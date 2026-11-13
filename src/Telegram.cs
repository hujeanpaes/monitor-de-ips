using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MonitorIPs
{
    /// <summary>
    /// Avisos por Telegram: cola propia con agrupación (10 s), "sólo si dura más de X min", horas de silencio
    /// (sin sonido), reintentos sin conexión (10 s → 1 min) y respeto del límite de Telegram (429 / retry_after).
    /// El token del bot se guarda cifrado con DPAPI y nunca sale hacia la interfaz.
    /// Sólo se habla con api.telegram.org (MONITORIPS_TG_API permite apuntar a un servidor simulado en pruebas).
    /// </summary>
    public static class TelegramNotifier
    {
        public class Item
        {
            public string Type, Id, Name, Sub, Kind, Group, Detail;
            public long T;            // cuándo ocurrió (ms)
            public long QueuedAt;     // cuándo entró en la cola de envío (ms)
            public double Duration;   // "up": segundos caída
        }

        class Batch { public List<Item> Items; public HashSet<string> Done = new HashSet<string>(); public bool Silent; }

        const int BatchWindowMs = 10000, MaxQueue = 200, MaxPerMessage = 25;
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MonitorIPs.Telegram.v1");
        static readonly object sync = new object();
        static readonly List<Item> queue = new List<Item>();
        static readonly Dictionary<string, Item> pendingDown = new Dictionary<string, Item>();
        static Batch current;
        static long batchStart, nextTry, lastSent;
        static int failures, dropped;
        static string lastError;
        static AppConfig cfg;
        static Func<string, bool> isDown;
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        static readonly JavaScriptSerializer json = new JavaScriptSerializer();

        static string ApiBase { get { return (Util.TestHook("TG_API") ?? "https://api.telegram.org").TrimEnd('/'); } }

        public static void Start(AppConfig c, Func<string, bool> downNow)
        {
            cfg = c; isDown = downNow;
            new Thread(Loop) { IsBackground = true, Name = "telegram" }.Start();
        }

        // ------------------------------------------------------------------ token (DPAPI)

        public static string Protect(string token)
        {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
        }

        /// <summary>Token en claro, o null si no hay o no se puede descifrar (otro equipo u otro usuario de Windows).</summary>
        static string Token()
        {
            if (cfg == null || string.IsNullOrEmpty(cfg.TelegramTokenEnc)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cfg.TelegramTokenEnc), Entropy, DataProtectionScope.CurrentUser)); }
            catch { return null; }
        }

        static bool Active() { return cfg != null && cfg.TelegramEnabled && cfg.TelegramChats.Count > 0 && Token() != null; }

        // ------------------------------------------------------------------ entrada de eventos

        public static void Enqueue(Item e)
        {
            if (!Active()) return;
            if (!cfg.TelegramAllGroups && !cfg.TelegramGroups.Contains(e.Group ?? "")) return;
            lock (sync)
            {
                if (e.Type == "up")
                {
                    if (pendingDown.Remove(e.Id)) return;   // caída corta: no se avisa ni de la caída ni de la recuperación
                    if (!cfg.TelegramUp) return;
                }
                else if (e.Type == "down")
                {
                    if (cfg.TelegramMinDownMin > 0) { pendingDown[e.Id] = e; return; }
                    if (!cfg.TelegramDown) return;
                }
                else if (e.Type == "cert") { if (!cfg.TelegramCert) return; }
                else return;
                Add(e);
            }
            wake.Set();
        }

        static void Add(Item e)
        {
            e.QueuedAt = Util.NowMs();
            if (queue.Count == 0) batchStart = e.QueuedAt;
            queue.Add(e);
            while (queue.Count > MaxQueue) { queue.RemoveAt(0); dropped++; }
        }

        // ------------------------------------------------------------------ bucle de envío

        static void Loop()
        {
            while (true)
            {
                wake.WaitOne(1000);
                try { Tick(); } catch (Exception ex) { lock (sync) lastError = ex.Message; }
            }
        }

        static void Tick()
        {
            long now = Util.NowMs();
            lock (sync)
            {
                // caídas que ya cumplen el mínimo y siguen caídas
                if (pendingDown.Count > 0)
                {
                    long min = cfg.TelegramMinDownMin * 60000L;
                    foreach (var kv in pendingDown.Where(kv => now - kv.Value.T >= min).ToList())
                    {
                        pendingDown.Remove(kv.Key);
                        if (cfg.TelegramDown && isDown(kv.Key)) Add(kv.Value);
                    }
                }
                if (!cfg.TelegramEnabled) { queue.Clear(); pendingDown.Clear(); current = null; return; }
                if (current == null)
                {
                    if (queue.Count == 0 || now - batchStart < BatchWindowMs) return;
                    var items = queue.Take(MaxPerMessage).ToList();
                    queue.RemoveRange(0, items.Count);
                    if (queue.Count > 0) batchStart = now - BatchWindowMs;   // el resto sale en el siguiente ciclo
                    current = new Batch { Items = items, Silent = InQuiet(DateTime.Now) };
                }
                if (now < nextTry) return;
            }

            string token = Token();
            if (token == null) { lock (sync) { lastError = "El token no se puede usar en este equipo: vuelve a conectarlo."; nextTry = now + 60000; } return; }
            if (current == null) return;
            string text = Compose(current.Items, now);
            var batch = current;
            bool retry = false, sent = false; int wait = 0; string error = null;
            foreach (var chat in cfg.TelegramChats.ToList())
            {
                if (batch.Done.Contains(chat.Id)) continue;
                var r = Send(token, chat.Id, text, batch.Silent);
                if (r.Ok) { batch.Done.Add(chat.Id); sent = true; continue; }
                error = (string.IsNullOrEmpty(chat.Name) ? chat.Id : chat.Name) + ": " + r.Error;
                if (r.Permanent) { batch.Done.Add(chat.Id); continue; }    // p. ej. el bot fue expulsado del grupo: no reintentar
                retry = true; wait = Math.Max(wait, r.RetryAfter);
            }
            lock (sync)
            {
                if (sent) lastSent = Util.NowMs();
                lastError = error;
                if (retry && wait > 0) nextTry = Util.NowMs() + wait * 1000L + 500;   // 429: lo que pide Telegram
                else if (retry)
                {
                    failures++;   // sin conexión: 10 s, 20 s, 40 s y luego cada minuto
                    nextTry = Util.NowMs() + Math.Min(60000L, 10000L << Math.Min(3, failures - 1));
                }
                else { failures = 0; nextTry = 0; if (current == batch) current = null; }
            }
        }

        static bool InQuiet(DateTime t)
        {
            if (string.IsNullOrEmpty(cfg.TelegramQuietFrom) || string.IsNullOrEmpty(cfg.TelegramQuietTo)) return false;
            int a = Minutes(cfg.TelegramQuietFrom), b = Minutes(cfg.TelegramQuietTo), m = t.Hour * 60 + t.Minute;
            if (a == b) return false;
            return a < b ? m >= a && m < b : m >= a || m < b;   // cruza la medianoche (22:00 → 07:00)
        }

        static int Minutes(string hhmm) { var p = hhmm.Split(':'); return int.Parse(p[0]) * 60 + int.Parse(p[1]); }

        // ------------------------------------------------------------------ mensajes

        static string Compose(List<Item> items, long now)
        {
            var sb = new StringBuilder();
            if (items.Count == 1) sb.Append(Single(items[0], now));
            else
            {
                int d = items.Count(i => i.Type == "down"), u = items.Count(i => i.Type == "up"), c = items.Count(i => i.Type == "cert");
                var head = new List<string>();
                if (d > 0) head.Add("🔴 " + d + (d == 1 ? " caída" : " caídas"));
                if (u > 0) head.Add("🟢 " + u + (u == 1 ? " recuperada" : " recuperadas"));
                if (c > 0) head.Add("🟠 " + c + (c == 1 ? " certificado" : " certificados"));
                sb.Append("<b>").Append(string.Join(" · ", head)).Append("</b>\n");
                foreach (var i in items) sb.Append('\n').Append(Line(i, now));
            }
            sb.Append("\n\n<i>Monitor de IPs · ").Append(Util.Html(Environment.MachineName)).Append("</i>");
            return sb.ToString();
        }

        static string Single(Item i, long now)
        {
            string where = "<code>" + Util.Html(i.Sub) + "</code> · " + Util.Html(i.Kind) + (string.IsNullOrEmpty(i.Group) ? "" : " · " + Util.Html(i.Group));
            string late = Late(i, now);
            switch (i.Type)
            {
                case "down":
                    return "🔴 <b>" + Util.Html(i.Name) + "</b> sin respuesta\n" + where + " · desde las " + Hm(i.T)
                         + (string.IsNullOrEmpty(i.Detail) ? "" : "\n" + Util.Html(i.Detail)) + late;
                case "up":
                    return "🟢 <b>" + Util.Html(i.Name) + "</b> recuperada · estuvo caída <b>" + Util.FormatDuration(i.Duration) + "</b>\n" + where + late;
                default:
                    return "🟠 <b>" + Util.Html(i.Name) + "</b> · " + Util.Html(i.Detail) + "\n" + where + late;
            }
        }

        static string Line(Item i, long now)
        {
            string tail = i.Type == "down" ? " · desde las " + Hm(i.T) : i.Type == "up" ? " · caída " + Util.FormatDuration(i.Duration) : " · " + Util.Html(i.Detail);
            string ico = i.Type == "down" ? "🔴" : i.Type == "up" ? "🟢" : "🟠";
            return ico + " <b>" + Util.Html(i.Name) + "</b> · <code>" + Util.Html(i.Sub) + "</code>" + tail + Late(i, now);
        }

        /// <summary>Si el aviso sale tarde (sin conexión), lo indica con la hora real.</summary>
        static string Late(Item i, long now) { return now - i.QueuedAt > 60000 ? " <i>(con retraso, ocurrió a las " + Hm(i.Type == "down" ? i.T : i.QueuedAt) + ")</i>" : ""; }

        static string Hm(long ms) { return Util.FromUnixMs(ms).ToString("HH:mm", CultureInfo.InvariantCulture); }


        // ------------------------------------------------------------------ API de Telegram

        class Res { public bool Ok, Permanent; public string Error; public int RetryAfter; public Dictionary<string, object> Body; }

        static Res Call(string token, string method, Dictionary<string, object> payload)
        {
            var res = new Res();
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(ApiBase + "/bot" + token + "/" + method);
                req.Method = "POST"; req.ContentType = "application/json; charset=utf-8";
                req.Timeout = 15000; req.ReadWriteTimeout = 15000; req.UserAgent = "MonitorIPs";
                byte[] data = Encoding.UTF8.GetBytes(json.Serialize(payload ?? new Dictionary<string, object>()));
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (var resp = (HttpWebResponse)req.GetResponse()) res.Body = Read(resp);
                res.Ok = res.Body != null && res.Body.ContainsKey("ok") && true.Equals(res.Body["ok"]);
                if (!res.Ok) { res.Error = Desc(res.Body) ?? "respuesta inesperada"; }
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r == null) { res.Error = "Sin conexión con Telegram (" + ex.Status + ")"; return res; }
                res.Body = Read(r);
                int code = (int)r.StatusCode;
                res.Error = "Telegram respondió " + code + (Desc(res.Body) != null ? ": " + Desc(res.Body) : "");
                if (code == 429)
                {
                    object p, ra;
                    if (res.Body != null && res.Body.TryGetValue("parameters", out p) && p is Dictionary<string, object> && ((Dictionary<string, object>)p).TryGetValue("retry_after", out ra))
                        res.RetryAfter = Convert.ToInt32(ra);
                }
                else if (code == 400 || code == 401 || code == 403 || code == 404) res.Permanent = true;
            }
            catch (Exception ex) { res.Error = ex.Message; }
            return res;
        }

        static Dictionary<string, object> Read(HttpWebResponse r)
        {
            try { using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) return json.Deserialize<Dictionary<string, object>>(sr.ReadToEnd()); }
            catch { return null; }
        }

        static string Desc(Dictionary<string, object> b) { object d; return b != null && b.TryGetValue("description", out d) ? Convert.ToString(d) : null; }

        static Res Send(string token, string chatId, string text, bool silent)
        {
            return Call(token, "sendMessage", new Dictionary<string, object> {
                { "chat_id", chatId }, { "text", text }, { "parse_mode", "HTML" },
                { "disable_web_page_preview", true }, { "disable_notification", silent },
            });
        }

        /// <summary>Valida un token y devuelve el @usuario del bot (excepción con el motivo si no vale).</summary>
        public static string GetMe(string token)
        {
            token = (token ?? "").Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(token, @"^\d{5,}:[A-Za-z0-9_-]{30,}$"))
                throw new Exception("El token no tiene el formato de BotFather (números, dos puntos y una clave larga).");
            var r = Call(token, "getMe", null);
            if (!r.Ok) throw new Exception(r.Error.Contains("401") ? "Telegram no reconoce ese token (401). Cópialo de nuevo desde @BotFather." : r.Error);
            var u = r.Body["result"] as Dictionary<string, object>;
            return u != null && u.ContainsKey("username") ? "@" + u["username"] : "bot";
        }

        /// <summary>Chats que han escrito al bot o donde lo han añadido (getUpdates).</summary>
        public static List<Dictionary<string, object>> DetectChats()
        {
            string token = Token();
            if (token == null) throw new Exception("Primero conecta el bot con su token.");
            var r = Call(token, "getUpdates", new Dictionary<string, object> { { "limit", 100 }, { "timeout", 0 } });
            if (!r.Ok) throw new Exception(r.Error);
            var found = new Dictionary<string, Dictionary<string, object>>();
            var list = r.Body["result"] as IEnumerable;
            if (list != null)
                foreach (var up in list.OfType<Dictionary<string, object>>())
                    foreach (var key in new[] { "message", "edited_message", "channel_post", "my_chat_member", "chat_member" })
                    {
                        object m; if (!up.TryGetValue(key, out m) || !(m is Dictionary<string, object>)) continue;
                        object c; if (!((Dictionary<string, object>)m).TryGetValue("chat", out c) || !(c is Dictionary<string, object>)) continue;
                        var chat = (Dictionary<string, object>)c;
                        string id = Convert.ToString(chat["id"], CultureInfo.InvariantCulture);
                        string type = chat.ContainsKey("type") ? Convert.ToString(chat["type"]) : "";
                        string name = chat.ContainsKey("title") ? Convert.ToString(chat["title"])
                                    : (Get(chat, "first_name") + " " + Get(chat, "last_name")).Trim();
                        if (name == "" && chat.ContainsKey("username")) name = "@" + chat["username"];
                        found[id] = new Dictionary<string, object> { { "id", id }, { "name", name }, { "type", type == "private" ? "Chat privado" : type == "channel" ? "Canal" : "Grupo" } };
                    }
            return found.Values.ToList();
        }

        static string Get(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : ""; }

        /// <summary>Mensaje de prueba a los chats indicados (o a los guardados).</summary>
        public static Dictionary<string, object> SendTest(List<TelegramChat> chats)
        {
            string token = Token();
            if (token == null) throw new Exception("Primero conecta el bot con su token.");
            if (chats == null || chats.Count == 0) chats = cfg.TelegramChats;
            if (chats.Count == 0) throw new Exception("Elige al menos un chat.");
            string text = "✅ <b>Mensaje de prueba</b>\nLos avisos de caídas y recuperaciones llegarán a este chat." +
                          "\n\n<i>Monitor de IPs · " + Util.Html(Environment.MachineName) + "</i>";
            var errors = new List<string>(); int ok = 0;
            foreach (var c in chats)
            {
                var r = Send(token, c.Id, text, false);
                if (r.Ok) ok++; else errors.Add((string.IsNullOrEmpty(c.Name) ? c.Id : c.Name) + ": " + r.Error);
            }
            lock (sync) { if (ok > 0) lastSent = Util.NowMs(); lastError = errors.Count > 0 ? errors[0] : null; }
            return new Dictionary<string, object> { { "sent", ok }, { "errors", errors } };
        }

        public static void ResetState() { lock (sync) { queue.Clear(); pendingDown.Clear(); current = null; failures = 0; nextTry = 0; lastError = null; } }

        /// <summary>Ajustes y estado para la interfaz (sin el token).</summary>
        public static Dictionary<string, object> Status()
        {
            string enc = cfg.TelegramTokenEnc ?? "";
            string token = Token();
            lock (sync)
                return new Dictionary<string, object> {
                    { "enabled", cfg.TelegramEnabled }, { "hasToken", enc != "" }, { "tokenOk", token != null },
                    { "tokenHint", token != null && token.Length > 4 ? "••••" + token.Substring(token.Length - 4) : "" },
                    { "bot", cfg.TelegramBot }, { "chats", cfg.TelegramChats },
                    { "down", cfg.TelegramDown }, { "up", cfg.TelegramUp }, { "cert", cfg.TelegramCert },
                    { "minDown", cfg.TelegramMinDownMin }, { "allGroups", cfg.TelegramAllGroups }, { "groups", cfg.TelegramGroups },
                    { "quietFrom", cfg.TelegramQuietFrom }, { "quietTo", cfg.TelegramQuietTo },
                    { "lastSent", lastSent > 0 ? (object)lastSent : null }, { "lastError", lastError },
                    { "pending", queue.Count + pendingDown.Count + (current != null ? current.Items.Count : 0) }, { "dropped", dropped },
                };
        }
    }
}

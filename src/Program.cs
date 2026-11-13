using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

[assembly: AssemblyTitle("Monitor de IPs")]
[assembly: AssemblyProduct("Monitor de IPs")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

namespace MonitorIPs
{
    static class Program
    {
        const string Version = "1.2.0";
        static string token;
        static int port;
        static HttpListener listener;
        static AppConfig config;
        static MonitorEngine engine;
        static long lastBeat;     // ms de la última llamada a la API; 0 = la UI aún no se conectó
        static long byeAt;        // ms en que la ventana avisó de que se cerraba (pagehide)
        static volatile bool quitRequested;
        static readonly long startedAt = Util.NowMs();
        static string htmlTemplate;
        static byte[] favicon;
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static string PortFile { get { return Path.Combine(Path.GetTempPath(), "MonitorIPs.port"); } }

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            var mutex = new Mutex(true, "Local\\MonitorIPs_SingleInstance_v1", out created);
            if (!created)
            {
                // Ya está corriendo: sólo abrir otra ventana hacia la instancia existente.
                try { OpenWindow(int.Parse(File.ReadAllText(PortFile).Trim())); } catch { }
                return;
            }

            try
            {
                Store.Init();
                config = Store.Load();
                Store.Save(config);
                LoadResources();
                token = Guid.NewGuid().ToString("N");
                StartServer();
                File.WriteAllText(PortFile, port.ToString());
                engine = new MonitorEngine(config);
                engine.Start();
                OpenWindow(port);
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("No se pudo iniciar el Monitor de IPs:\n\n" + ex.Message,
                    "Monitor de IPs", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
                return;
            }

            // Vigilante: la app se cierra sola cuando se cierra la ventana (la UI deja de hacer polling),
            // o al pulsar "Salir del monitor".
            while (true)
            {
                Thread.Sleep(1000);
                long now = Util.NowMs();
                long beat = Interlocked.Read(ref lastBeat);
                bool exit;
                if (quitRequested) exit = true;
                else if (beat == 0) exit = now - startedAt > 120000;                 // la ventana nunca llegó a conectar
                else if (Interlocked.Read(ref byeAt) > beat) exit = now - beat > 6000; // se cerró y no volvió (no fue recarga)
                else exit = now - beat > 75000;                                     // sin señales de vida
                if (exit) break;
            }

            engine.StopAll();
            Store.Save(config);
            try { listener.Stop(); } catch { }
            try { File.Delete(PortFile); } catch { }
            GC.KeepAlive(mutex);
        }

        static void LoadResources()
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream("index.html"))
            using (var r = new StreamReader(s, Encoding.UTF8)) htmlTemplate = r.ReadToEnd();
            using (var s = asm.GetManifestResourceStream("app.ico"))
            {
                if (s != null) { var ms = new MemoryStream(); s.CopyTo(ms); favicon = ms.ToArray(); }
            }
        }

        // ------------------------------------------------------------------ ventana

        static void OpenWindow(int p)
        {
            string url = "http://localhost:" + p + "/";
            string browser = FindBrowser();
            if (browser != null)
            {
                string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorIPs", "edge-profile");
                string argsLine = "--app=\"" + url + "\" --user-data-dir=\"" + profile + "\" --window-size=1400,880 " +
                                  "--no-first-run --no-default-browser-check --disable-sync --disable-extensions " +
                                  "--disable-background-timer-throttling --disable-renderer-backgrounding " +
                                  "--disable-backgrounding-occluded-windows --autoplay-policy=no-user-gesture-required " +
                                  "--disable-features=Translate,msEdgeSidebarV2,msHubApps";
                Process.Start(new ProcessStartInfo(browser, argsLine) { UseShellExecute = false });
            }
            else Process.Start(url);
        }

        static string FindBrowser()
        {
            var roots = new[] {
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            };
            var rel = new[] { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe", @"BraveSoftware\Brave-Browser\Application\brave.exe" };
            foreach (var r in rel)
                foreach (var root in roots)
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    string p = Path.Combine(root, r);
                    if (File.Exists(p)) return p;
                }
            return null;
        }

        // ------------------------------------------------------------------ servidor HTTP

        static void StartServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:" + port + "/");
            listener.Start();
            new Thread(() =>
            {
                while (listener.IsListening)
                {
                    try
                    {
                        var ctx = listener.GetContext();
                        ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
                    }
                    catch { if (!listener.IsListening) break; }
                }
            }) { IsBackground = true, Name = "http" }.Start();
        }

        static void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            string path = req.Url.AbsolutePath;
            try
            {
                if (path == "/" || path == "/index.html")
                {
                    Send(res, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(htmlTemplate.Replace("__TOKEN__", token)));
                    return;
                }
                if (path == "/favicon.ico")
                {
                    if (favicon != null) Send(res, 200, "image/x-icon", favicon); else Send(res, 404, "text/plain", new byte[0]);
                    return;
                }
                if (path.StartsWith("/api/"))
                {
                    string tk = req.Headers["X-Token"] ?? req.QueryString["token"];
                    if (tk != token) { SendJson(res, 403, new Dictionary<string, object> { { "error", "token inválido" } }); return; }
                    Interlocked.Exchange(ref lastBeat, Util.NowMs());
                    Dictionary<string, object> body = null;
                    if (req.HasEntityBody)
                    {
                        using (var r = new StreamReader(req.InputStream, Encoding.UTF8))
                        {
                            string text = r.ReadToEnd();
                            if (text.Trim().Length > 0) body = json.Deserialize<Dictionary<string, object>>(text);
                        }
                    }
                    object result = Api(path, req.QueryString, body ?? new Dictionary<string, object>());
                    SendJson(res, 200, result);
                    return;
                }
                Send(res, 404, "text/plain", Encoding.UTF8.GetBytes("404"));
            }
            catch (Exception ex)
            {
                try { SendJson(res, 400, new Dictionary<string, object> { { "error", ex.Message } }); } catch { }
            }
        }

        static void Send(HttpListenerResponse res, int code, string type, byte[] data)
        {
            res.StatusCode = code;
            res.ContentType = type;
            res.Headers["Cache-Control"] = "no-store";
            res.ContentLength64 = data.Length;
            res.OutputStream.Write(data, 0, data.Length);
            res.OutputStream.Close();
        }

        static void SendJson(HttpListenerResponse res, int code, object obj)
        {
            Send(res, code, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json.Serialize(obj ?? new Dictionary<string, object>())));
        }

        // ------------------------------------------------------------------ API

        /// <summary>Todas las rutas /api/*. Cualquier llamada cuenta como señal de vida de la ventana.</summary>
        static object Api(string path, System.Collections.Specialized.NameValueCollection q, Dictionary<string, object> b)
        {
            switch (path)
            {
                case "/api/state":
                {
                    long since, ev;
                    long.TryParse(q["since"], out since);
                    long.TryParse(q["ev"], out ev);
                    var snap = engine.Snapshot(since, ev);
                    snap["settings"] = Settings();
                    snap["trashCount"] = engine.TrashCount();
                    return snap;
                }
                case "/api/bye":
                    Interlocked.Exchange(ref byeAt, Util.NowMs() + 1);
                    return Ok();
                case "/api/targets/save":
                    return engine.AddOrUpdate(Str(b, "id"), Str(b, "name"), Str(b, "host"), Str(b, "group"), (int)(Num(b, "interval", 1) * 1000));
                case "/api/targets/bulk":
                    return new Dictionary<string, object> { { "added", Bulk(Str(b, "text"), Str(b, "group"), (int)(Num(b, "interval", 1) * 1000)) } };
                case "/api/targets/delete":
                    engine.Remove(Str(b, "id"));
                    return Ok();
                case "/api/scan/subnets":
                    return new Dictionary<string, object> { { "subnets", Scanner.Subnets() }, { "max", Scanner.MaxIps } };
                case "/api/scan/start":
                {
                    var job = Scanner.Start(Str(b, "range"), j =>
                    {
                        if (j.Status == "done" || j.Status == "cancelled")
                            engine.InfoEvent("scan", "Escaneo " + j.Label, j.Found.Count + " activas de " + j.Total + (j.Status == "cancelled" ? " (detenido)" : ""));
                    });
                    return Scanner.Describe(job.Id, engine.HasHost);
                }
                case "/api/scan":
                    return Scanner.Describe(q["id"], engine.HasHost);
                case "/api/scan/cancel":
                    Scanner.CancelJob(Str(b, "id"));
                    return Ok();
                case "/api/targets/addmany":
                {
                    int added = 0, skipped = 0;
                    object raw;
                    var items = b.TryGetValue("items", out raw) && raw is IEnumerable && !(raw is string) ? ((IEnumerable)raw).Cast<object>() : new object[0];
                    string group = Str(b, "group");
                    int interval = (int)(Num(b, "interval", 1) * 1000);
                    foreach (var o in items)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d == null) continue;
                        if (TryAdd(Str(d, "host"), Str(d, "name"), group, interval)) added++; else skipped++;
                    }
                    if (added > 0) engine.SaveConfig();
                    return new Dictionary<string, object> { { "added", added }, { "skipped", skipped } };
                }
                case "/api/targets/clearall":
                    return new Dictionary<string, object> { { "removed", engine.RemoveAll() } };
                case "/api/trash":
                    return new Dictionary<string, object> { { "items", engine.TrashList() }, { "days", AppConfig.TrashDays } };
                case "/api/trash/restore":
                    return new Dictionary<string, object> { { "restored", engine.Restore(Ids(b)) } };
                case "/api/trash/delete":
                    return new Dictionary<string, object> { { "deleted", engine.PurgeTrash(Ids(b)) } };
                case "/api/targets/pause":
                    engine.SetPaused(Str(b, "id"), Bool(b, "paused"));
                    return Ok();
                case "/api/targets/pauseall":
                    engine.SetAllPaused(Bool(b, "paused"));
                    return Ok();
                case "/api/targets/reset":
                    engine.ResetStats(Str(b, "id"));
                    return Ok();
                case "/api/settings":
                    if (b.ContainsKey("timeoutMs")) config.TimeoutMs = (int)Num(b, "timeoutMs", 1000);
                    if (b.ContainsKey("downAfter")) config.DownAfter = (int)Num(b, "downAfter", 3);
                    if (b.ContainsKey("highLatencyMs")) config.HighLatencyMs = (int)Num(b, "highLatencyMs", 100);
                    if (b.ContainsKey("sound")) config.Sound = Bool(b, "sound");
                    if (b.ContainsKey("notify")) config.Notify = Bool(b, "notify");
                    if (b.ContainsKey("showEvents")) config.ShowEvents = Bool(b, "showEvents");
                    if (b.ContainsKey("theme")) config.Theme = Str(b, "theme");
                    config.Normalize();
                    Store.Save(config);
                    return Settings();
                case "/api/trace/start":
                    return Tracer.Describe(Tracer.Start(Str(b, "host"), !b.ContainsKey("resolve") || Bool(b, "resolve")).Id);
                case "/api/trace":
                    return Tracer.Describe(q["id"]);
                case "/api/trace/cancel":
                    Tracer.CancelJob(Str(b, "id"));
                    return Ok();
                case "/api/export":
                    return new Dictionary<string, object> { { "path", Export(Str(b, "kind")) } };
                case "/api/open":
                {
                    string kind = Str(b, "kind");
                    string dir = kind == "logs" ? Store.LogsDir : kind == "exports" ? Store.ExportsDir : Store.DataDir;
                    Directory.CreateDirectory(dir);
                    Process.Start("explorer.exe", "\"" + dir + "\"");
                    return Ok();
                }
                case "/api/quit":
                    quitRequested = true; // se cierra aunque haya otras ventanas abiertas
                    return Ok();
            }
            throw new ArgumentException("Ruta desconocida: " + path);
        }

        /// <summary>"ids": [..] en el cuerpo. Sólo "all": true explícito devuelve null (= todos).</summary>
        static List<string> Ids(Dictionary<string, object> b)
        {
            if (Bool(b, "all")) return null;
            object v;
            if (!b.TryGetValue("ids", out v) || v is string || !(v is IEnumerable)) return new List<string>();
            return ((IEnumerable)v).Cast<object>().Select(o => Convert.ToString(o)).ToList();
        }

        static Dictionary<string, object> Ok() { return new Dictionary<string, object> { { "ok", true } }; }

        static Dictionary<string, object> Settings()
        {
            return new Dictionary<string, object> {
                { "timeoutMs", config.TimeoutMs }, { "downAfter", config.DownAfter }, { "highLatencyMs", config.HighLatencyMs },
                { "sound", config.Sound }, { "notify", config.Notify }, { "theme", config.Theme }, { "showEvents", config.ShowEvents },
                { "dataDir", Store.DataDir }, { "version", Version }, { "installed", Store.Installed },
            };
        }

        static string Str(Dictionary<string, object> b, string k) { object v; return b.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : ""; }
        static bool Bool(Dictionary<string, object> b, string k) { object v; return b.TryGetValue(k, out v) && v is bool && (bool)v; }
        static double Num(Dictionary<string, object> b, string k, double def)
        {
            object v;
            if (!b.TryGetValue(k, out v) || v == null) return def;
            double d;
            return double.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : def;
        }

        /// <summary>Una línea por equipo: "10.0.0.1", "10.0.0.1 Router sede", "Router sede;10.0.0.1", etc.</summary>
        static int Bulk(string text, string group, int interval)
        {
            int added = 0;
            foreach (var raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string host, name = "";
                var parts = line.Split(new[] { ';', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                IPAddress tmp;
                if (parts.Length >= 2)
                {
                    if (!IPAddress.TryParse(parts[0], out tmp) && IPAddress.TryParse(parts[1], out tmp)) { host = parts[1]; name = parts[0]; }
                    else { host = parts[0]; name = parts[1]; }
                }
                else
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"\b\d{1,3}(\.\d{1,3}){3}\b");
                    var ws = line.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (IPAddress.TryParse(ws[0], out tmp) || !m.Success) { host = ws[0]; name = ws.Length > 1 ? ws[1].Trim() : ""; }
                    else
                    {
                        host = m.Value;
                        bool pingOutput = System.Text.RegularExpressions.Regex.IsMatch(line, @"bytes=|TTL=|Respuesta desde|Reply from|Haciendo ping|Pinging", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        name = pingOutput ? "" : line.Replace(m.Value, "").Trim(' ', '-', ':', '|');
                    }
                }
                if (TryAdd(host, name, group, interval)) added++;
            }
            if (added > 0) engine.SaveConfig();
            return added;
        }

        /// <summary>Alta sin guardar (las altas en bloque guardan una vez al final). Omite hosts ya monitoreados.</summary>
        static bool TryAdd(string host, string name, string group, int interval)
        {
            if (string.IsNullOrWhiteSpace(host) || engine.HasHost(host)) return false;
            try { engine.AddOrUpdate(null, name, host, group, interval, false); return true; }
            catch { return false; }
        }

        static string Export(string kind)
        {
            Directory.CreateDirectory(Store.ExportsDir);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            string file;
            var sb = new StringBuilder();
            if (kind == "samples")
            {
                file = "latencias_" + stamp + ".csv";
                sb.AppendLine("Fecha;Hora;Nombre;Host;Grupo;Latencia_ms;TTL;Resultado");
                foreach (var kv in engine.AllSamples())
                    foreach (var s in kv.Value)
                    {
                        var d = Util.FromUnixMs(s[1]);
                        sb.AppendLine(Util.Csv(d.ToString("yyyy-MM-dd"), d.ToString("HH:mm:ss.fff"), kv.Key.Name, kv.Key.Host, kv.Key.Group,
                            s[2] >= 0 ? s[2].ToString() : "", s[2] >= 0 ? s[3].ToString() : "", s[2] >= 0 ? "OK" : "SIN RESPUESTA"));
                    }
            }
            else if (kind == "summary")
            {
                file = "resumen_" + stamp + ".csv";
                sb.AppendLine("Nombre;Host;Grupo;Estado;Enviados;Recibidos;Perdida_%;Min_ms;Prom_ms;Max_ms;Jitter_ms");
                foreach (var t in engine.SummaryAll())
                    sb.AppendLine(Util.Csv(t["name"], t["host"], t["group"], StateLabel((string)t["state"]), t["sent"], t["recv"], t["loss"],
                        Neg(t["min"]), Neg(t["avg"]), Neg(t["max"]), t["jitter"]));
            }
            else
            {
                file = "eventos_" + stamp + ".csv";
                sb.AppendLine("Fecha;Hora;Nombre;Host;Evento;Duracion_s;Detalle");
                foreach (var e in engine.EventsSnapshot())
                {
                    var d = Util.FromUnixMs(e.T);
                    sb.AppendLine(Util.Csv(d.ToString("yyyy-MM-dd"), d.ToString("HH:mm:ss"), e.Name, e.Host, Util.EventLabel(e.Type),
                        e.Duration > 0 ? ((long)e.Duration).ToString() : "", e.Detail));
                }
            }
            string path = Path.Combine(Store.ExportsDir, file);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            Process.Start("explorer.exe", "/select,\"" + path + "\"");
            return path;
        }

        static object Neg(object v) { return Convert.ToDouble(v) < 0 ? "" : v; }

        static string StateLabel(string s)
        {
            switch (s) { case "up": return "En linea"; case "down": return "Caida"; case "paused": return "Pausada"; default: return "Sin datos"; }
        }
    }
}

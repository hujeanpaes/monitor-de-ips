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
[assembly: AssemblyVersion("1.9.0.0")]
[assembly: AssemblyFileVersion("1.9.0.0")]

namespace MonitorIPs
{
    static class Program
    {
        const string Version = "1.9.0";
        static string token;
        static int port;
        static HttpListener listener;
        static AppConfig config;
        static MonitorEngine engine;
        static long lastBeat;     // ms de la última llamada a la API; 0 = la UI aún no se conectó
        static long byeAt;        // ms en que la ventana avisó de que se cerraba (pagehide)
        static volatile bool quitRequested;
        static readonly long startedAt = Util.NowMs();
        static TrayApp tray;
        static bool trayMode;          // arrancado con --tray (inicio con Windows): sin abrir ventana
        static bool windowWasOpen;     // para detectar el cierre de la ventana
        static int trayAlerts;         // avisos mostrados desde la bandeja (diagnóstico, visible en Ajustes/API)
        static string htmlTemplate;
        static byte[] favicon;
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static string PortFile { get { return Path.Combine(Path.GetTempPath(), "MonitorIPs.port"); } }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            trayMode = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
            bool created;
            var mutex = new Mutex(true, "Local\\MonitorIPs_SingleInstance_v1", out created);
            if (!created)
            {
                // Ya está corriendo: traer su ventana al frente o abrir una nueva hacia esa instancia.
                if (!trayMode && !WindowFocus.TryFocus())
                    try { OpenWindow(int.Parse(File.ReadAllText(PortFile).Trim())); } catch { }
                return;
            }

            SetProcessDPIAware(); // iconos y menú de la bandeja nítidos con escalado de Windows (125 %, 150 %…)
            System.Windows.Forms.Application.EnableVisualStyles();
            try
            {
                Store.Init();
                config = Store.Load();
                Store.Save(config);
                LoadResources();
                Probes.Init(Version);
                token = Guid.NewGuid().ToString("N");
                StartServer();
                File.WriteAllText(PortFile, port.ToString());
                engine = new MonitorEngine(config);
                engine.EventRaised += OnEngineEvent;
                engine.Start();
                History.Start(Store.HistoryDir, () => config.HistoryDays);
                Updater.Start(Version, config, OnNewVersion);
                TelegramNotifier.Start(config, engine.IsDown);
                Autostart.RepairPath();
                tray = new TrayApp();
                if (!trayMode) OpenWindow(port);
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("No se pudo iniciar el Monitor de IPs:\n\n" + ex.Message,
                    "Monitor de IPs", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
                return;
            }

            // Bucle de la bandeja; Tick() decide cuándo terminar.
            System.Windows.Forms.Application.Run(tray);

            engine.StopAll();
            History.Stop();
            Store.Save(config);
            try { listener.Stop(); } catch { }
            try { File.Delete(PortFile); } catch { }
            GC.KeepAlive(mutex);
        }

        // ------------------------------------------------------------------ ciclo de vida (llamado por la bandeja)

        /// <summary>¿Hay una ventana del monitor abierta? (la UI consulta la API cada segundo)</summary>
        static bool WindowOpen()
        {
            long beat = Interlocked.Read(ref lastBeat);
            if (beat == 0) return false;
            long since = Util.NowMs() - beat;
            return Interlocked.Read(ref byeAt) > beat ? since < 3000 : since < 10000;
        }

        /// <summary>Cada segundo, en el hilo de la bandeja. Devuelve true para cerrar la aplicación.</summary>
        internal static bool Tick()
        {
            if (quitRequested) return true;
            bool open = WindowOpen();
            if (windowWasOpen && !open)
            {
                if (!config.CloseToTray) return true; // comportamiento clásico: cerrar la ventana = salir
                if (!config.TrayHintShown)
                {
                    config.TrayHintShown = true;
                    Store.Save(config);
                    tray.ShowHint("Monitor de IPs sigue vigilando",
                        "Está en la bandeja, junto al reloj. Haz clic en el icono para abrirlo; para cerrarlo del todo: clic derecho → Salir del monitor.");
                }
            }
            windowWasOpen = open;
            // la ventana nunca llegó a conectar (y no se arrancó en la bandeja ni se quiere quedar en ella)
            if (!config.CloseToTray && !trayMode && Interlocked.Read(ref lastBeat) == 0 && Util.NowMs() - startedAt > 120000) return true;
            return false;
        }

        /// <summary>Con la ventana cerrada, las caídas y recuperaciones se avisan desde la bandeja.</summary>
        static void OnEngineEvent(MonitorEvent ev)
        {
            if (ev.Type != "down" && ev.Type != "up" && ev.Type != "cert") return;
            // Telegram: siempre, con la ventana abierta o cerrada
            try
            {
                var c = engine.ConfigOf(ev.TargetId);
                TelegramNotifier.Enqueue(new TelegramNotifier.Item {
                    Type = ev.Type, Id = ev.TargetId, Name = string.IsNullOrEmpty(ev.Name) ? ev.Host : ev.Name,
                    Sub = c == null ? ev.Host : c.Check == "http" ? c.Url : c.Check == "tcp" ? c.Host + ":" + c.Port : c.Host,
                    Kind = c == null ? "" : c.Check == "http" ? "Web" : c.Check == "tcp" ? "TCP" : "Ping", Group = c == null ? "" : c.Group,
                    Detail = ev.Detail, T = ev.T, Duration = ev.Duration,
                });
            }
            catch { }
            if (tray == null || WindowOpen()) return;
            Interlocked.Increment(ref trayAlerts);
            string name = string.IsNullOrEmpty(ev.Name) ? ev.Host : ev.Name;
            if (ev.Type == "cert") { tray.Post(() => tray.ShowAlert("Certificado por caducar: " + name, ev.Host + " · " + ev.Detail, false)); return; }
            if (ev.Type == "down")
                tray.Post(() => tray.ShowAlert("⚠ " + name + " sin respuesta", ev.Host + (string.IsNullOrEmpty(ev.Detail) ? "" : " · " + ev.Detail), true));
            else
                tray.Post(() => tray.ShowAlert("✔ " + name + " recuperada", ev.Host + " · estuvo caída " + Duration(ev.Duration), false));
        }

        /// <summary>Versión nueva encontrada: con la ventana cerrada se avisa desde la bandeja (una vez por versión).</summary>
        static void OnNewVersion(Updater.Info info)
        {
            if (tray == null || WindowOpen()) return; // con la ventana abierta lo muestra la franja de la UI
            tray.Post(() => tray.ShowAlert("Nueva versión disponible: " + info.Version,
                "Abre el monitor para ver las novedades y descargarla.", false));
        }

        static string Duration(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (t.TotalHours >= 1) return (int)t.TotalHours + "h " + t.Minutes + "m";
            if (t.TotalMinutes >= 1) return t.Minutes + "m " + t.Seconds + "s";
            return t.Seconds + "s";
        }

        internal static void OpenOrFocusWindow() { if (!WindowFocus.TryFocus()) OpenWindow(port); }
        internal static bool SoundEnabled { get { return config.Sound; } }
        internal static void ToggleSound() { config.Sound = !config.Sound; Store.Save(config); }
        internal static void PauseAll(bool paused) { engine.SetAllPaused(paused); }
        internal static int[] CountStates() { return engine.CountStates(); }

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
                    snap["update"] = Updater.Brief();
                    return snap;
                }
                case "/api/bye":
                    Interlocked.Exchange(ref byeAt, Util.NowMs() + 1);
                    return Ok();
                case "/api/targets/save":
                    return engine.AddOrUpdate(Str(b, "id"), SpecFrom(b));
                case "/api/ports/detect":
                    return new Dictionary<string, object> { { "open", Probes.DetectPorts(Str(b, "host")) }, { "tested", Ports.Common.Length } };
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
                        if (TryAdd(new TargetConfig { Host = Str(d, "host"), Name = Str(d, "name"), Group = group, IntervalMs = interval })) added++; else skipped++;
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
                    if (b.ContainsKey("view")) config.View = Str(b, "view");
                    if (b.ContainsKey("listSort")) config.ListSort = Str(b, "listSort");
                    if (b.ContainsKey("screenMode")) config.ScreenMode = Bool(b, "screenMode");
                    if (b.ContainsKey("closeToTray")) config.CloseToTray = Bool(b, "closeToTray");
                    if (b.ContainsKey("updateCheck")) config.UpdateCheck = Bool(b, "updateCheck");
                    if (b.ContainsKey("historyDays")) { config.HistoryDays = (int)Num(b, "historyDays", 90); config.Normalize(); History.Purge(); }
                    if (b.ContainsKey("autostart")) Autostart.Set(Bool(b, "autostart"));
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
                    return new Dictionary<string, object> { { "path", Export(Str(b, "kind"), Str(b, "range")) } };
                case "/api/report":
                    return MakeReport(b);
                case "/api/telegram":
                    return TelegramNotifier.Status();
                case "/api/telegram/token":
                {
                    string tk = Str(b, "token").Trim();
                    string bot = TelegramNotifier.GetMe(tk);           // lanza con el motivo si no vale
                    config.TelegramTokenEnc = TelegramNotifier.Protect(tk);
                    config.TelegramBot = bot;
                    Store.Save(config);
                    TelegramNotifier.ResetState();
                    return TelegramNotifier.Status();
                }
                case "/api/telegram/forget":
                    config.TelegramTokenEnc = ""; config.TelegramBot = ""; config.TelegramEnabled = false;
                    Store.Save(config);
                    TelegramNotifier.ResetState();
                    return TelegramNotifier.Status();
                case "/api/telegram/detect":
                    return new Dictionary<string, object> { { "chats", TelegramNotifier.DetectChats() } };
                case "/api/telegram/test":
                    return TelegramNotifier.SendTest(b.ContainsKey("chats") ? Chats(b["chats"]) : null);
                case "/api/telegram/save":
                    SaveTelegram(b);
                    return TelegramNotifier.Status();
                case "/api/update":
                    return Updater.Status();
                case "/api/update/check":
                    Updater.Check();
                    return Updater.Status();
                case "/api/update/open":
                {
                    string url = Updater.UrlFor(Str(b, "kind"));
                    if (url == null) throw new ArgumentException("No hay información de la versión nueva");
                    Process.Start(url); // sólo enlaces de github.com/hujeanpaes/monitor-de-ips (validados en Updater)
                    return Ok();
                }
                case "/api/update/skip":
                    config.SkippedVersion = Str(b, "version");
                    Store.Save(config);
                    return Updater.Status();
                case "/api/history":
                    return History.ForTarget(q["id"] ?? "", Range(q["range"]), engine.IsDown(q["id"] ?? ""));
                case "/api/availability":
                    return new Dictionary<string, object> { { "range", Range(q["range"]) }, { "items", Availability(Range(q["range"])) } };
                case "/api/open":
                {
                    string kind = Str(b, "kind");
                    if (kind == "botfather") { Process.Start("https://t.me/BotFather"); return Ok(); }   // enlace fijo, nunca una URL recibida
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
                { "closeToTray", config.CloseToTray }, { "autostart", Autostart.IsEnabled() }, { "trayAlerts", trayAlerts },
                { "historyDays", config.HistoryDays }, { "historySize", History.SizeBytes() }, { "updateCheck", config.UpdateCheck },
                { "view", config.View }, { "listSort", config.ListSort }, { "screenMode", config.ScreenMode },
                { "reportTitle", config.ReportTitle },
                { "telegram", new Dictionary<string, object> { { "enabled", config.TelegramEnabled }, { "chats", config.TelegramChats.Count }, { "bot", config.TelegramBot } } },
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
                if (parts.Length >= 2)
                {
                    if (!LooksLikeTarget(parts[0]) && LooksLikeTarget(parts[1])) { host = parts[1]; name = parts[0]; }
                    else { host = parts[0]; name = parts[1]; }
                }
                else
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"\b\d{1,3}(\.\d{1,3}){3}\b");
                    var ws = line.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (LooksLikeTarget(ws[0]) || !m.Success) { host = ws[0]; name = ws.Length > 1 ? ws[1].Trim() : ""; }
                    else
                    {
                        host = m.Value;
                        bool pingOutput = System.Text.RegularExpressions.Regex.IsMatch(line, @"bytes=|TTL=|Respuesta desde|Reply from|Haciendo ping|Pinging", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        name = pingOutput ? "" : line.Replace(m.Value, "").Trim(' ', '-', ':', '|');
                    }
                }
                if (TryAdd(SpecFromToken(host, name, group, interval))) added++;
            }
            if (added > 0) engine.SaveConfig();
            return added;
        }

        static bool IsUrl(string s) { return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase); }

        /// <summary>"host:puerto" (no IPv6) → (host, puerto); si no, null.</summary>
        static Tuple<string, int> HostPort(string s)
        {
            int i = s.LastIndexOf(':'), p;
            if (i > 0 && s.IndexOf(':') == i && int.TryParse(s.Substring(i + 1), out p) && p > 0 && p < 65536) return Tuple.Create(s.Substring(0, i), p);
            return null;
        }

        static bool LooksLikeTarget(string s) { IPAddress a; return IsUrl(s) || HostPort(s) != null || IPAddress.TryParse(s, out a); }

        /// <summary>Convierte un texto pegado en una comprobación: URL → web, host:puerto → TCP, si no → ping.</summary>
        static TargetConfig SpecFromToken(string token, string name, string group, int interval)
        {
            var s = new TargetConfig { Name = name, Group = group, IntervalMs = interval };
            var hp = HostPort(token);
            if (IsUrl(token)) { s.Check = "http"; s.Url = token; if (interval <= 1000) s.IntervalMs = 30000; }
            else if (hp != null) { s.Check = "tcp"; s.Host = hp.Item1; s.Port = hp.Item2; if (interval <= 1000) s.IntervalMs = 10000; }
            else s.Host = token;
            return s;
        }

        /// <summary>Comprobación a partir del cuerpo de /api/targets/save (intervalo en segundos).</summary>
        static TargetConfig SpecFrom(Dictionary<string, object> b)
        {
            string check = Str(b, "check");
            if (check != "tcp" && check != "http") check = "ping";
            double defInterval = check == "http" ? 30 : check == "tcp" ? 10 : 1;
            return new TargetConfig
            {
                Name = Str(b, "name"), Host = Str(b, "host"), Group = Str(b, "group"),
                IntervalMs = (int)(Num(b, "interval", defInterval) * 1000),
                Check = check, Port = (int)Num(b, "port", 0), Url = Str(b, "url"),
                Expect = Str(b, "expect"), Keyword = Str(b, "keyword"),
                Insecure = !b.ContainsKey("insecure") || Bool(b, "insecure"),
                TimeoutMs = (int)Num(b, "timeoutMs", 0),
                HighLatencyMs = (int)Num(b, "highLatencyMs", 0),
                DownAfter = (int)Num(b, "downAfter", 0),
            };
        }

        /// <summary>Alta sin guardar (las altas en bloque guardan una vez al final). Omite hosts ya monitoreados.</summary>
        static bool TryAdd(TargetConfig spec)
        {
            try
            {
                if (spec.Check != "http" && string.IsNullOrWhiteSpace(spec.Host)) return false;
                spec.NormalizeCheck();
                if (engine.HasKey(spec.Key)) return false;
                engine.AddOrUpdate(null, spec, false);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Informe PDF: resuelve el periodo en hora local, filtra por grupo y genera el archivo en exports\.</summary>
        static Dictionary<string, object> MakeReport(Dictionary<string, object> b)
        {
            var es = System.Globalization.CultureInfo.GetCultureInfo("es-ES");
            string period = Str(b, "period");
            string title = (Str(b, "title") ?? "").Trim();
            if (title.Length > 120) title = title.Substring(0, 120);
            if (title != config.ReportTitle) { config.ReportTitle = title; Store.Save(config); }

            DateTime today = DateTime.Today;
            long now = Util.NowMs() / 60000, from, to;
            bool daily = true;
            string label, tag;
            Func<long, string> day = m => Util.FromUnixMs(m * 60000).ToString("d MMM yyyy", es);
            Func<long, string> dayTime = m => Util.FromUnixMs(m * 60000).ToString("d MMM yyyy, HH:mm", es);
            switch (period)
            {
                case "24h":
                    to = now; from = to - 1440; daily = false; tag = "24h";
                    label = "Últimas 24 horas (" + dayTime(from) + " – " + dayTime(to) + ")";
                    break;
                case "7d":
                case "30d":
                {
                    // mismo cálculo que la vista Disponibilidad, para que las cifras coincidan
                    int span = period == "7d" ? 7 * 1440 : 30 * 1440, bucket = period == "7d" ? 60 : 240;
                    to = now; from = (to - span) / bucket * bucket; tag = period;
                    label = (period == "7d" ? "Últimos 7 días" : "Últimos 30 días") + " (" + dayTime(from) + " – " + dayTime(to) + ")";
                    break;
                }
                case "thisMonth":
                {
                    var first = new DateTime(today.Year, today.Month, 1);
                    from = History.DayStartMinute(first); to = now; tag = first.ToString("yyyy-MM");
                    label = Cap(first.ToString("MMMM 'de' yyyy", es)) + " (del 1 al " + today.Day + ", hasta ahora)";
                    break;
                }
                case "custom":
                {
                    DateTime a, z;
                    if (!DateTime.TryParseExact(Str(b, "from"), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out a) ||
                        !DateTime.TryParseExact(Str(b, "to"), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out z))
                        throw new Exception("Indica las fechas «desde» y «hasta».");
                    if (z < a) throw new Exception("La fecha «desde» debe ser anterior o igual a «hasta».");
                    if (a > today) throw new Exception("La fecha «desde» no puede ser futura.");
                    var limit = today.AddDays(-config.HistoryDays);
                    if (a < limit) throw new Exception("Solo se guarda historial de los últimos " + config.HistoryDays + " días (desde el " + limit.ToString("d MMM yyyy", es) + "). Puedes ampliarlo en Ajustes.");
                    from = History.DayStartMinute(a); to = Math.Min(now, History.DayStartMinute(z.AddDays(1)));
                    tag = a.ToString("yyyy-MM-dd") + "_a_" + z.ToString("yyyy-MM-dd");
                    label = a == z ? Cap(a.ToString("dddd d 'de' MMMM 'de' yyyy", es)) : "Del " + a.ToString("d MMM yyyy", es) + " al " + z.ToString("d MMM yyyy", es);
                    if ((to - from) <= 2 * 1440) daily = false;
                    break;
                }
                default: // mes anterior
                {
                    var first = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
                    from = History.DayStartMinute(first); to = History.DayStartMinute(first.AddMonths(1)); tag = first.ToString("yyyy-MM");
                    label = Cap(first.ToString("MMMM 'de' yyyy", es)) + " (del 1 al " + DateTime.DaysInMonth(first.Year, first.Month) + ")";
                    break;
                }
            }
            if (to <= from) throw new Exception("El periodo elegido está vacío.");

            string group = b.ContainsKey("group") && b["group"] != null ? Str(b, "group") : null;   // null = todas
            var targets = engine.SummaryAll().Where(t => group == null || (string)t["group"] == group).ToList();
            if (targets.Count == 0) throw new Exception("No hay comprobaciones en ese alcance.");

            var res = Report.Generate(new Report.Options
            {
                Title = title != "" ? title : "Informe de disponibilidad",
                PeriodLabel = label,
                Scope = group == null ? "Todas las comprobaciones" : (group == "" ? "Sin grupo" : "Grupo «" + group + "»"),
                Version = Version, OutDir = Store.ExportsDir,
                Browser = Environment.GetEnvironmentVariable("MONITORIPS_NOPDF") == "1" ? null : FindBrowser(),   // la variable permite probar el plan B (HTML)
                FileBase = "informe_" + tag + "_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss"),
                FromMin = from, ToMin = to, Daily = daily, Targets = targets, DownNow = engine.IsDown,
            });
            if (!b.ContainsKey("open") || Bool(b, "open")) try { Process.Start(res.Path); } catch { }
            return new Dictionary<string, object> { { "path", res.Path }, { "pdf", res.Pdf } };
        }

        static List<TelegramChat> Chats(object o)
        {
            var res = new List<TelegramChat>();
            var list = o as System.Collections.IEnumerable;
            if (list == null) return res;
            foreach (var x in list.OfType<Dictionary<string, object>>())
            {
                string id = Str(x, "id"); if (id == "") id = Str(x, "Id");
                string name = Str(x, "name"); if (name == "") name = Str(x, "Name");
                if (System.Text.RegularExpressions.Regex.IsMatch(id, @"^-?\d{1,20}$")) res.Add(new TelegramChat { Id = id, Name = name.Length > 80 ? name.Substring(0, 80) : name });
            }
            return res;
        }

        static void SaveTelegram(Dictionary<string, object> b)
        {
            if (b.ContainsKey("chats")) config.TelegramChats = Chats(b["chats"]);
            if (b.ContainsKey("down")) config.TelegramDown = Bool(b, "down");
            if (b.ContainsKey("up")) config.TelegramUp = Bool(b, "up");
            if (b.ContainsKey("cert")) config.TelegramCert = Bool(b, "cert");
            if (b.ContainsKey("minDown")) config.TelegramMinDownMin = (int)Num(b, "minDown", 0);
            if (b.ContainsKey("allGroups")) config.TelegramAllGroups = Bool(b, "allGroups");
            if (b.ContainsKey("groups"))
            {
                var g = b["groups"] as System.Collections.IEnumerable;
                config.TelegramGroups = g == null ? new List<string>() : g.Cast<object>().Select(x => Convert.ToString(x) ?? "").Distinct().ToList();
            }
            if (b.ContainsKey("quietFrom")) config.TelegramQuietFrom = Str(b, "quietFrom");
            if (b.ContainsKey("quietTo")) config.TelegramQuietTo = Str(b, "quietTo");
            if (b.ContainsKey("enabled"))
            {
                bool on = Bool(b, "enabled");
                if (on && (string.IsNullOrEmpty(config.TelegramTokenEnc) || config.TelegramChats.Count == 0))
                    throw new Exception("Para activar los avisos conecta el bot y elige al menos un chat.");
                config.TelegramEnabled = on;
            }
            config.Normalize();
            Store.Save(config);
        }

        static string Cap(string s) { return string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1); }

        static string Range(string r) { return r == "7d" || r == "30d" ? r : "24h"; }
        static string RangeLabel(string r) { return r == "30d" ? "Ultimos 30 dias" : r == "7d" ? "Ultimos 7 dias" : "Ultimas 24 horas"; }

        /// <summary>Disponibilidad de todas las IPs monitoreadas en el rango (vista "Disponibilidad" y exporte).</summary>
        static List<Dictionary<string, object>> Availability(string range)
        {
            var targets = engine.SummaryAll();
            var stats = History.ForTargets(targets.Select(t => (string)t["id"]), range, engine.IsDown);
            return targets.Select(t =>
            {
                var d = new Dictionary<string, object>(stats[(string)t["id"]]);
                d["id"] = t["id"]; d["name"] = t["name"]; d["host"] = t["host"]; d["group"] = t["group"]; d["state"] = t["state"];
                d["label"] = t["label"]; d["check"] = t["check"]; d["url"] = t["url"];
                return d;
            }).ToList();
        }

        static string Export(string kind, string range)
        {
            Directory.CreateDirectory(Store.ExportsDir);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            string file;
            var sb = new StringBuilder();
            if (kind == "samples")
            {
                file = "latencias_" + stamp + ".csv";
                sb.AppendLine("Fecha;Hora;Nombre;Host;Comprobacion;Grupo;Latencia_ms;TTL_o_HTTP;Resultado");
                foreach (var kv in engine.AllSamples())
                    foreach (var s in kv.Value)
                    {
                        var d = Util.FromUnixMs(s[1]);
                        sb.AppendLine(Util.Csv(d.ToString("yyyy-MM-dd"), d.ToString("HH:mm:ss.fff"), kv.Key.Name, kv.Key.Check == "http" ? kv.Key.Url : kv.Key.Host, kv.Key.CheckLabel, kv.Key.Group,
                            s[2] >= 0 ? s[2].ToString() : "", s[2] >= 0 ? s[3].ToString() : "", s[2] >= 0 ? "OK" : "SIN RESPUESTA"));
                    }
            }
            else if (kind == "availability")
            {
                range = Range(range);
                file = "disponibilidad_" + range + "_" + stamp + ".csv";
                sb.AppendLine("Nombre;Host;Comprobacion;Grupo;Disponibilidad_%;Caidas;Tiempo_caido_min;Caida_mas_larga_min;Latencia_media_ms;Latencia_max_ms;Perdida_%;Minutos_vigilados;Periodo");
                foreach (var t in Availability(range))
                    sb.AppendLine(Util.Csv(t["name"], (string)t["check"] == "http" ? t["url"] : t["host"], t["label"], t["group"], t["availability"] ?? "", t["outages"], t["downtime"], t["longest"],
                        Neg(t["avg"]), Neg(t["max"]), t["loss"], t["minutes"], RangeLabel(range)));
            }
            else if (kind == "summary")
            {
                file = "resumen_" + stamp + ".csv";
                sb.AppendLine("Nombre;Host;Comprobacion;Grupo;Estado;Enviados;Recibidos;Perdida_%;Min_ms;Prom_ms;Max_ms;Jitter_ms");
                foreach (var t in engine.SummaryAll())
                    sb.AppendLine(Util.Csv(t["name"], (string)t["check"] == "http" ? t["url"] : t["host"], t["label"], t["group"], StateLabel((string)t["state"]), t["sent"], t["recv"], t["loss"],
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

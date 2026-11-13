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
[assembly: AssemblyVersion("1.5.0.0")]
[assembly: AssemblyFileVersion("1.5.0.0")]

namespace MonitorIPs
{
    static class Program
    {
        const string Version = "1.5.0";
        static string token;
        static int port;
        static HttpListener listener;
        static AppConfig config;
        static MonitorEngine engine;
        static long lastBeat;     // ms de la Ãºltima llamada a la API; 0 = la UI aÃºn no se conectÃ³
        static long byeAt;        // ms en que la ventana avisÃ³ de que se cerraba (pagehide)
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
                // Ya estÃ¡ corriendo: traer su ventana al frente o abrir una nueva hacia esa instancia.
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
                token = Guid.NewGuid().ToString("N");
                StartServer();
                File.WriteAllText(PortFile, port.ToString());
                engine = new MonitorEngine(config);
                engine.EventRaised += OnEngineEvent;
                engine.Start();
                History.Start(Store.HistoryDir, () => config.HistoryDays);
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

            // Bucle de la bandeja; Tick() decide cuÃ¡ndo terminar.
            System.Windows.Forms.Application.Run(tray);

            engine.StopAll();
            History.Stop();
            Store.Save(config);
            try { listener.Stop(); } catch { }
            try { File.Delete(PortFile); } catch { }
            GC.KeepAlive(mutex);
        }

        // ------------------------------------------------------------------ ciclo de vida (llamado por la bandeja)

        /// <summary>Â¿Hay una ventana del monitor abierta? (la UI consulta la API cada segundo)</summary>
        static bool WindowOpen()
        {
            long beat = Interlocked.Read(ref lastBeat);
            if (beat == 0) return false;
            long since = Util.NowMs() - beat;
            return Interlocked.Read(ref byeAt) > beat ? since < 3000 : since < 10000;
        }

        /// <summary>Cada segundo, en el hilo de la bandeja. Devuelve true para cerrar la aplicaciÃ³n.</summary>
        internal static bool Tick()
        {
            if (quitRequested) return true;
            bool open = WindowOpen();
            if (windowWasOpen && !open)
            {
                if (!config.CloseToTray) return true; // comportamiento clÃ¡sico: cerrar la ventana = salir
                if (!config.TrayHintShown)
                {
                    config.TrayHintShown = true;
                    Store.Save(config);
                    tray.ShowHint("Monitor de IPs sigue vigilando",
                        "EstÃ¡ en la bandeja, junto al reloj. Haz clic en el icono para abrirlo; para cerrarlo del todo: clic derecho â†’ Salir del monitor.");
                }
            }
            windowWasOpen = open;
            // la ventana nunca llegÃ³ a conectar (y no se arrancÃ³ en la bandeja ni se quiere quedar en ella)
            if (!config.CloseToTray && !trayMode && Interlocked.Read(ref lastBeat) == 0 && Util.NowMs() - startedAt > 120000) return true;
            return false;
        }

        /// <summary>Con la ventana cerrada, las caÃ­das y recuperaciones se avisan desde la bandeja.</summary>
        static void OnEngineEvent(MonitorEvent ev)
        {
            if ((ev.Type != "down" && ev.Type != "up") || tray == null || WindowOpen()) return;
            Interlocked.Increment(ref trayAlerts);
            string name = string.IsNullOrEmpty(ev.Name) ? ev.Host : ev.Name;
            if (ev.Type == "down")
                tray.Post(() => tray.ShowAlert("âš  " + name + " sin respuesta", ev.Host + (string.IsNullOrEmpty(ev.Detail) ? "" : " Â· " + ev.Detail), true));
            else
                tray.Post(() => tray.ShowAlert("âœ” " + name + " recuperada", ev.Host + " Â· estuvo caÃ­da " + Duration(ev.Duration), false));
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
                    if (tk != token) { SendJson(res, 403, new Dictionary<string, object> { { "error", "token invÃ¡lido" } }); return; }
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

        /// <summary>Todas las rutas /api/*. Cualquier llamada cuenta como seÃ±al de vida de la ventana.</summary>
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
                    if (b.ContainsKey("closeToTray")) config.CloseToTray = Bool(b, "closeToTray");
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
                case "/api/history":
                    return History.ForTarget(q["id"] ?? "", Range(q["range"]), engine.IsDown(q["id"] ?? ""));
                case "/api/availability":
                    return new Dictionary<string, object> { { "range", Range(q["range"]) }, { "items", Availability(Range(q["range"])) } };
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

        /// <summary>"ids": [..] en el cuerpo. SÃ³lo "all": true explÃ­cito devuelve null (= todos).</summary>
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
                { "historyDays", config.HistoryDays }, { "historySize", History.SizeBytes() },
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

        /// <summary>Una lÃ­nea por equipo: "10.0.0.1", "10.0.0.1 Router sede", "Router sede;10.0.0.1", etc.</summary>
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
                sb.AppendLine("Fecha;Hora;Nombre;Host;Grupo;Latencia_ms;TTL;Resultado");
                foreach (var kv in engine.AllSamples())
                    foreach (var s in kv.Value)
                    {
                        var d = Util.FromUnixMs(s[1]);
                        sb.AppendLine(Util.Csv(d.ToString("yyyy-MM-dd"), d.ToString("HH:mm:ss.fff"), kv.Key.Name, kv.Key.Host, kv.Key.Group,
                            s[2] >= 0 ? s[2].ToString() : "", s[2] >= 0 ? s[3].ToString() : "", s[2] >= 0 ? "OK" : "SIN RESPUESTA"));
                    }
            }
            else if (kind == "availability")
            {
                range = Range(range);
                file = "disponibilidad_" + range + "_" + stamp + ".csv";
                sb.AppendLine("Nombre;Host;Grupo;Disponibilidad_%;Caidas;Tiempo_caido_min;Caida_mas_larga_min;Latencia_media_ms;Latencia_max_ms;Perdida_%;Minutos_vigilados;Periodo");
                foreach (var t in Availability(range))
                    sb.AppendLine(Util.Csv(t["name"], t["host"], t["group"], t["availability"] ?? "", t["outages"], t["downtime"], t["longest"],
                        Neg(t["avg"]), Neg(t["max"]), t["loss"], t["minutes"], RangeLabel(range)));
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

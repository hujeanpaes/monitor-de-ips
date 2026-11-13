using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MonitorIPs
{
    public class TargetConfig
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Host { get; set; }
        public string Group { get; set; }
        public int IntervalMs { get; set; }
        public bool Paused { get; set; }

        // ---- tipo de comprobación (v1.6). Las configuraciones antiguas quedan como "ping".
        /// <summary>"ping" | "tcp" | "http"</summary>
        public string Check { get; set; }
        /// <summary>Puerto para "tcp".</summary>
        public int Port { get; set; }
        /// <summary>URL completa para "http" (http:// o https://). Host guarda su nombre de equipo.</summary>
        public string Url { get; set; }
        /// <summary>Códigos HTTP válidos, p. ej. "200-399" o "200,301-302".</summary>
        public string Expect { get; set; }
        /// <summary>Texto que debe aparecer en la respuesta web (opcional).</summary>
        public string Keyword { get; set; }
        /// <summary>Aceptar certificados HTTPS no válidos (equipos internos con certificado propio).</summary>
        public bool Insecure { get; set; }
        /// <summary>Tiempo de espera propio en ms; 0 = predeterminado según el tipo.</summary>
        public int TimeoutMs { get; set; }

        public TargetConfig()
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 10);
            Name = "";
            Host = "";
            Group = "";
            IntervalMs = 1000;
            Check = "ping";
            Url = "";
            Expect = "200-399";
            Keyword = "";
            Insecure = true;
        }

        /// <summary>Corrige valores y deja Host coherente con la URL. Lanza ArgumentException si no es válido.</summary>
        public void NormalizeCheck()
        {
            Check = (Check ?? "ping").Trim().ToLowerInvariant();
            if (Check != "tcp" && Check != "http") Check = "ping";
            if (Name == null) Name = "";
            if (Group == null) Group = "";
            if (Keyword == null) Keyword = "";
            if (string.IsNullOrWhiteSpace(Expect)) Expect = "200-399";
            if (TimeoutMs < 0) TimeoutMs = 0;
            if (TimeoutMs > 60000) TimeoutMs = 60000;
            if (Check == "http")
            {
                Uri u;
                string url = (Url ?? "").Trim();
                if (url.Length > 0 && url.IndexOf("://", StringComparison.Ordinal) < 0) url = "http://" + url;
                if (!Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https"))
                    throw new ArgumentException("URL no válida (usa http://… o https://…)");
                Url = u.ToString();
                Host = u.Host;
                Port = u.Port;
                if (IntervalMs < 5000) IntervalMs = 5000;
            }
            else
            {
                Url = Url ?? "";
                if (Check == "tcp")
                {
                    if (Port < 1 || Port > 65535) throw new ArgumentException("Puerto no válido (1-65535)");
                    if (IntervalMs < 2000) IntervalMs = 2000;
                }
                else Port = 0;
            }
        }

        /// <summary>Clave para detectar duplicados: el mismo host puede tener ping y varios puertos.</summary>
        [ScriptIgnore]
        public string Key
        {
            get
            {
                if (Check == "http") return "http|" + (Url ?? "").ToLowerInvariant();
                if (Check == "tcp") return "tcp|" + (Host ?? "").ToLowerInvariant() + "|" + Port;
                return "ping|" + (Host ?? "").ToLowerInvariant();
            }
        }

        /// <summary>Descripción corta: "Ping", "RDP 3389", "HTTPS".</summary>
        [ScriptIgnore]
        public string CheckLabel
        {
            get
            {
                if (Check == "http") return Url.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "HTTPS" : "HTTP";
                if (Check == "tcp") { string n = Ports.Name(Port); return n == null ? "TCP " + Port : n + " " + Port; }
                return "Ping";
            }
        }
    }

    /// <summary>Puertos TCP conocidos (nombres para mostrar y lista de detección).</summary>
    public static class Ports
    {
        public static readonly int[] Common = { 21, 22, 23, 25, 53, 80, 110, 135, 139, 143, 443, 445, 1433, 3306, 3389, 5432, 5900, 8080, 8443, 9100 };

        public static string Name(int port)
        {
            switch (port)
            {
                case 21: return "FTP"; case 22: return "SSH"; case 23: return "Telnet"; case 25: return "SMTP";
                case 53: return "DNS"; case 80: return "HTTP"; case 110: return "POP3"; case 135: return "RPC";
                case 139: return "NetBIOS"; case 143: return "IMAP"; case 443: return "HTTPS"; case 445: return "SMB";
                case 1433: return "SQL Server"; case 3306: return "MySQL"; case 3389: return "RDP"; case 5432: return "PostgreSQL";
                case 5900: return "VNC"; case 8080: return "HTTP-alt"; case 8443: return "HTTPS-alt"; case 9100: return "Impresora";
                default: return null;
            }
        }
    }

    public class TrashItem
    {
        public TargetConfig Target { get; set; }
        public long DeletedAt { get; set; } // unix ms
    }

    public class AppConfig
    {
        public const int TrashDays = 30;
        public List<TargetConfig> Targets { get; set; }
        public List<TrashItem> Trash { get; set; }
        public int TimeoutMs { get; set; }
        public int DownAfter { get; set; }
        public int HighLatencyMs { get; set; }
        public bool Sound { get; set; }
        public bool Notify { get; set; }
        public string Theme { get; set; }
        public bool ShowEvents { get; set; }
        /// <summary>Al cerrar la ventana el monitor sigue en la bandeja (si es false, cerrar = salir).</summary>
        public bool CloseToTray { get; set; }
        /// <summary>Ya se mostró el aviso "sigue vigilando en la bandeja".</summary>
        public bool TrayHintShown { get; set; }
        /// <summary>Días de historial por minuto que se conservan (history\).</summary>
        public int HistoryDays { get; set; }

        public AppConfig()
        {
            Targets = new List<TargetConfig>();
            Trash = new List<TrashItem>();
            TimeoutMs = 1000;
            DownAfter = 3;
            HighLatencyMs = 100;
            Sound = true;
            Notify = true;
            Theme = "dark";
            ShowEvents = true;
            CloseToTray = true;
            HistoryDays = 90;
        }

        public void Normalize()
        {
            if (Targets == null) Targets = new List<TargetConfig>();
            if (Trash == null) Trash = new List<TrashItem>();
            PurgeExpiredTrash();
            if (TimeoutMs < 200) TimeoutMs = 200;
            if (TimeoutMs > 10000) TimeoutMs = 10000;
            if (DownAfter < 1) DownAfter = 1;
            if (DownAfter > 50) DownAfter = 50;
            if (HighLatencyMs < 1) HighLatencyMs = 1;
            if (HistoryDays == 0) HistoryDays = 90;          // config antigua sin el campo
            if (HistoryDays < 7) HistoryDays = 7;
            if (HistoryDays > 365) HistoryDays = 365;
            if (Theme != "light") Theme = "dark";
            foreach (var t in Targets)
            {
                if (string.IsNullOrEmpty(t.Id)) t.Id = Guid.NewGuid().ToString("N").Substring(0, 10);
                if (t.Name == null) t.Name = "";
                if (t.Group == null) t.Group = "";
                if (t.Host == null) t.Host = "";
                if (t.IntervalMs < 250) t.IntervalMs = 250;
                if (t.IntervalMs > 600000) t.IntervalMs = 600000;
                try { t.NormalizeCheck(); } catch { t.Check = "ping"; t.Port = 0; } // dato corrupto: se vigila por ping
            }
        }

        /// <summary>Borra definitivamente lo que lleva más de 30 días en la papelera. Devuelve true si quitó algo.</summary>
        public bool PurgeExpiredTrash()
        {
            long limit = Util.NowMs() - TrashDays * 86400000L;
            return Trash.RemoveAll(x => x == null || x.Target == null || x.DeletedAt < limit) > 0;
        }
    }

    /// <summary>Carga/guarda config.json y escribe los logs CSV. Todo junto al .exe (portable).</summary>
    public static class Store
    {
        public static string DataDir;
        public static string ConfigPath;
        public static string LogsDir;
        public static string ExportsDir;
        public static string HistoryDir;
        static readonly object fileLock = new object();
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        /// <summary>Marcador que deja el instalador junto al .exe: indica versión instalada (no portable).</summary>
        public const string InstalledMarker = "installed.mode";

        public static bool Installed { get; private set; }

        /// <summary>
        /// Portable: datos junto al .exe. Instalado (existe installed.mode) o carpeta sin permiso de escritura:
        /// datos en %APPDATA%\MonitorIPs, para que sobrevivan a actualizaciones y desinstalación.
        /// </summary>
        public static void Init()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            Installed = File.Exists(Path.Combine(exeDir, InstalledMarker));
            DataDir = !Installed && IsWritable(exeDir)
                ? exeDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorIPs");
            Directory.CreateDirectory(DataDir);
            ConfigPath = Path.Combine(DataDir, "config.json");
            LogsDir = Path.Combine(DataDir, "logs");
            ExportsDir = Path.Combine(DataDir, "exports");
            HistoryDir = Path.Combine(DataDir, "history");
        }

        static bool IsWritable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static AppConfig Load()
        {
            AppConfig cfg = null;
            try
            {
                if (File.Exists(ConfigPath))
                    cfg = json.Deserialize<AppConfig>(File.ReadAllText(ConfigPath, Encoding.UTF8));
            }
            catch
            {
                try { File.Copy(ConfigPath, ConfigPath + ".bad", true); } catch { }
            }
            if (cfg == null)
            {
                cfg = new AppConfig();
                // Primer arranque: las IPs que se vigilaban con ping -t.
                foreach (var ip in new[] { "172.17.16.205", "10.64.12.1", "10.225.146.130" })
                    cfg.Targets.Add(new TargetConfig { Host = ip, Group = "Seguimiento diario" });
            }
            cfg.Normalize();
            return cfg;
        }

        /// <summary>
        /// Guarda config.json de forma segura: serializa con cfg bloqueado (otros hilos la modifican)
        /// y escribe a un .tmp que luego reemplaza al original de forma atómica.
        /// Orden de bloqueo siempre cfg -> fileLock (nunca al revés) para evitar interbloqueos.
        /// </summary>
        public static void Save(AppConfig cfg)
        {
            string text;
            try
            {
                lock (cfg) text = PrettyJson(json.Serialize(cfg));
            }
            catch { return; }
            lock (fileLock)
            {
                try
                {
                    string tmp = ConfigPath + ".tmp";
                    File.WriteAllText(tmp, text, new UTF8Encoding(false));
                    if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, null);
                    else File.Move(tmp, ConfigPath);
                }
                catch { }
            }
        }

        public static void LogEvent(MonitorEvent ev)
        {
            lock (fileLock)
            {
                try
                {
                    Directory.CreateDirectory(LogsDir);
                    DateTime local = Util.FromUnixMs(ev.T);
                    string path = Path.Combine(LogsDir, "eventos-" + local.ToString("yyyy-MM-dd") + ".csv");
                    bool isNew = !File.Exists(path);
                    using (var w = new StreamWriter(path, true, new UTF8Encoding(true)))
                    {
                        if (isNew) w.WriteLine("Fecha;Hora;Nombre;Host;Evento;Duracion_s;Detalle");
                        w.WriteLine(Util.Csv(local.ToString("yyyy-MM-dd"), local.ToString("HH:mm:ss"), ev.Name, ev.Host,
                            Util.EventLabel(ev.Type), ev.Duration > 0 ? ((long)ev.Duration).ToString() : "", ev.Detail));
                    }
                }
                catch { }
            }
        }

        /// <summary>Indentado simple para que config.json sea editable a mano.</summary>
        static string PrettyJson(string src)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inStr = false;
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (inStr)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < src.Length) { sb.Append(src[++i]); continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inStr = true; sb.Append(c); break;
                    case '{':
                    case '[':
                        sb.Append(c);
                        if (i + 1 < src.Length && (src[i + 1] == '}' || src[i + 1] == ']')) { sb.Append(src[++i]); break; }
                        indent++; sb.Append("\r\n").Append(' ', indent * 2); break;
                    case '}':
                    case ']':
                        indent--; sb.Append("\r\n").Append(' ', indent * 2).Append(c); break;
                    case ',': sb.Append(c).Append("\r\n").Append(' ', indent * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }

    public static class Util
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static readonly System.Globalization.CultureInfo CsvCulture = System.Globalization.CultureInfo.GetCultureInfo("es-ES");
        static readonly char[] CsvSpecial = { ';', '"', '\n', '\r' };

        /// <summary>32 bytes de datos, igual que ping.exe de Windows (abcdefghijklmnopqrstuvwabcdefghi).</summary>
        public static readonly byte[] PingPayload = Enumerable.Range(0, 32).Select(i => (byte)('a' + i % 23)).ToArray();

        public static long NowMs() { return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds; }
        public static DateTime FromUnixMs(long ms) { return Epoch.AddMilliseconds(ms).ToLocalTime(); }

        public static string Csv(params object[] cells)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < cells.Length; i++)
            {
                if (i > 0) sb.Append(';');
                string s = cells[i] == null ? "" : Convert.ToString(cells[i], CsvCulture);
                if (s.IndexOfAny(CsvSpecial) >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
                sb.Append(s);
            }
            return sb.ToString();
        }

        /// <summary>DNS inverso con límite de tiempo (null si no hay nombre o tarda demasiado).</summary>
        public static string ReverseDns(string ip, int timeoutMs)
        {
            try
            {
                var ar = System.Net.Dns.BeginGetHostEntry(ip, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return null;
                var entry = System.Net.Dns.EndGetHostEntry(ar);
                return string.IsNullOrEmpty(entry.HostName) || entry.HostName == ip ? null : entry.HostName;
            }
            catch { return null; }
        }

        public static string EventLabel(string type)
        {
            switch (type)
            {
                case "down": return "CAIDA";
                case "up": return "RECUPERADA";
                case "added": return "AGREGADA";
                case "removed": return "ELIMINADA";
                case "paused": return "PAUSADA";
                case "resumed": return "REANUDADA";
                case "restored": return "RESTAURADA";
                case "cleared": return "LIMPIEZA";
                case "scan": return "ESCANEO";
                default: return type;
            }
        }
    }
}

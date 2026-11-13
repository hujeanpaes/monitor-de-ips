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

        public TargetConfig()
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 10);
            Name = "";
            Host = "";
            Group = "";
            IntervalMs = 1000;
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
            if (Theme != "light") Theme = "dark";
            foreach (var t in Targets)
            {
                if (string.IsNullOrEmpty(t.Id)) t.Id = Guid.NewGuid().ToString("N").Substring(0, 10);
                if (t.Name == null) t.Name = "";
                if (t.Group == null) t.Group = "";
                if (t.Host == null) t.Host = "";
                if (t.IntervalMs < 250) t.IntervalMs = 250;
                if (t.IntervalMs > 600000) t.IntervalMs = 600000;
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

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MonitorIPs
{
    /// <summary>
    /// Aviso de versión nueva: consulta la última Release pública en GitHub (sólo lectura, sin credenciales,
    /// sin enviar datos del usuario). No descarga ni ejecuta nada: sólo informa y abre el enlace en el navegador.
    /// </summary>
    public static class Updater
    {
        const string ApiUrl = "https://api.github.com/repos/hujeanpaes/monitor-de-ips/releases/latest";
        const string AllowedPrefix = "https://github.com/hujeanpaes/monitor-de-ips/";
        const long Day = 24L * 3600 * 1000;

        public class Info
        {
            public string Version, Title, Notes, PageUrl, SetupUrl, PortableUrl;
            public long Published;
        }

        static readonly object sync = new object();
        static Info latest;
        static long checkedAt, nextCheck;
        static string lastError;
        static volatile bool checking;
        static string current = "0.0.0";
        static AppConfig config;
        static Action<Info> onNewVersion;
        static string notifiedVersion;

        public static void Start(string version, AppConfig cfg, Action<Info> onNew)
        {
            current = version; config = cfg; onNewVersion = onNew;
            nextCheck = Util.NowMs() + 60000; // 1 minuto después de arrancar
            new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(30000);
                    if (config.UpdateCheck && Util.NowMs() >= nextCheck) { try { Check(); } catch { } }
                }
            }) { IsBackground = true, Name = "updater" }.Start();
        }

        /// <summary>Consulta GitHub ahora. Devuelve true si hay una versión más nueva que la actual.</summary>
        public static bool Check()
        {
            if (checking) return Newer();
            checking = true;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(ApiUrl);
                req.UserAgent = "MonitorIPs/" + current;
                req.Accept = "application/vnd.github+json";
                req.Timeout = 15000; req.ReadWriteTimeout = 15000;
                string json;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) json = sr.ReadToEnd();

                var d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json);
                var info = new Info
                {
                    Version = Clean(Str(d, "tag_name")),
                    Title = Str(d, "name"),
                    Notes = Str(d, "body"),
                    PageUrl = Safe(Str(d, "html_url")),
                };
                DateTime pub;
                if (DateTime.TryParse(Str(d, "published_at"), null, System.Globalization.DateTimeStyles.RoundtripKind, out pub))
                    info.Published = (long)(pub.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                object assets;
                if (d.TryGetValue("assets", out assets) && assets is IEnumerable)
                    foreach (var o in ((IEnumerable)assets).OfType<Dictionary<string, object>>())
                    {
                        string name = Str(o, "name"), url = Safe(Str(o, "browser_download_url"));
                        if (url == null) continue;
                        if (name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase)) info.SetupUrl = url;
                        else if (name.EndsWith("-Portable.exe", StringComparison.OrdinalIgnoreCase)) info.PortableUrl = url;
                    }
                if (string.IsNullOrEmpty(info.Version)) throw new Exception("Respuesta de GitHub sin versión");

                lock (sync) { latest = info; lastError = null; }
                bool newer = Compare(info.Version, current) > 0;
                if (newer && info.Version != notifiedVersion && info.Version != config.SkippedVersion && onNewVersion != null)
                {
                    notifiedVersion = info.Version;
                    try { onNewVersion(info); } catch { }
                }
                return newer;
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                lock (sync) lastError = r != null ? "GitHub respondió " + (int)r.StatusCode : "Sin conexión con GitHub";
                return Newer();
            }
            catch (Exception ex) { lock (sync) lastError = ex.Message; return Newer(); }
            finally
            {
                checkedAt = Util.NowMs();
                nextCheck = checkedAt + Day;
                checking = false;
            }
        }

        static bool Newer() { lock (sync) return latest != null && Compare(latest.Version, current) > 0; }

        /// <summary>Resumen corto para /api/state (la franja de la UI).</summary>
        public static Dictionary<string, object> Brief()
        {
            lock (sync)
            {
                bool show = latest != null && Compare(latest.Version, current) > 0 && latest.Version != config.SkippedVersion;
                return new Dictionary<string, object> { { "available", show ? latest.Version : null } };
            }
        }

        public static Dictionary<string, object> Status()
        {
            lock (sync)
            {
                bool newer = latest != null && Compare(latest.Version, current) > 0;
                return new Dictionary<string, object> {
                    { "current", current }, { "latest", latest != null ? latest.Version : null }, { "newer", newer },
                    { "skipped", newer && latest.Version == config.SkippedVersion },
                    { "title", latest != null ? latest.Title : null }, { "notes", latest != null ? latest.Notes : null },
                    { "published", latest != null ? (object)latest.Published : null },
                    { "checkedAt", checkedAt > 0 ? (object)checkedAt : null }, { "error", lastError }, { "checking", checking },
                    { "auto", config.UpdateCheck },
                };
            }
        }

        /// <summary>Enlace a abrir: "download" (Setup si está instalada, Portable si no) o "page".</summary>
        public static string UrlFor(string kind)
        {
            lock (sync)
            {
                if (latest == null) return null;
                if (kind == "download")
                    return (Store.Installed ? latest.SetupUrl : latest.PortableUrl) ?? latest.SetupUrl ?? latest.PortableUrl ?? latest.PageUrl;
                return latest.PageUrl;
            }
        }

        /// <summary>Compara versiones numéricas "1.7.10" vs "1.7.9".</summary>
        public static int Compare(string a, string b)
        {
            var pa = (Clean(a) ?? "0").Split('.'); var pb = (Clean(b) ?? "0").Split('.');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int x = 0, y = 0;
                if (i < pa.Length) int.TryParse(pa[i], out x);
                if (i < pb.Length) int.TryParse(pb[i], out y);
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        static string Clean(string v) { return string.IsNullOrEmpty(v) ? v : v.Trim().TrimStart('v', 'V'); }
        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : ""; }

        /// <summary>Sólo se aceptan enlaces del propio repositorio.</summary>
        static string Safe(string url) { return !string.IsNullOrEmpty(url) && url.StartsWith(AllowedPrefix, StringComparison.OrdinalIgnoreCase) ? url : null; }
    }
}

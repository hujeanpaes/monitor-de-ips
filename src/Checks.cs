using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace MonitorIPs
{
    /// <summary>Resultado de una comprobación. Ms &gt;= 0 = correcta.</summary>
    public class ProbeResult
    {
        public int Ms = -1;
        /// <summary>Ping: TTL. Web: código HTTP. TCP: 0.</summary>
        public int Code;
        /// <summary>Causa completa del fallo (para tooltip, detalle y eventos).</summary>
        public string Error;
        /// <summary>Causa breve en mayúsculas para la tarjeta: SIN RESPUESTA, PUERTO CERRADO, HTTP 503…</summary>
        public string Short;
        public DateTime? CertExpires;
        public string CertSubject;

        public static ProbeResult Fail(string shortText, string error) { return new ProbeResult { Short = shortText, Error = error }; }
    }

    /// <summary>Sondas de comprobación: ping (ICMP), puerto TCP y web (HTTP/HTTPS).</summary>
    public static class Probes
    {
        public const int DefaultTcpTimeout = 3000;
        public const int DefaultHttpTimeout = 10000;
        static readonly PingOptions PingOpts = new PingOptions(128, false);
        static string userAgent = "MonitorIPs";

        /// <summary>Al arrancar: TLS 1.2/1.3 para HTTPS y agente de usuario con la versión.</summary>
        public static void Init(string version)
        {
            userAgent = "MonitorIPs/" + version;
            try
            {
                var p = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                try { p |= (SecurityProtocolType)12288; } catch { } // TLS 1.3 si el sistema lo admite
                ServicePointManager.SecurityProtocol = p;
            }
            catch { try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls; } catch { } }
            ServicePointManager.Expect100Continue = false;
        }

        /// <summary>Ejecuta la comprobación configurada. ping se reutiliza entre llamadas del mismo hilo.</summary>
        public static ProbeResult Run(TargetConfig c, Ping ping, int globalTimeout)
        {
            switch (c.Check)
            {
                case "tcp": return Tcp(c.Host, c.Port, c.TimeoutMs > 0 ? c.TimeoutMs : DefaultTcpTimeout);
                case "http": return Http(c, c.TimeoutMs > 0 ? c.TimeoutMs : DefaultHttpTimeout);
                default: return Ping(ping, c.Host, c.TimeoutMs > 0 ? c.TimeoutMs : globalTimeout);
            }
        }

        // ------------------------------------------------------------------ ping

        public static ProbeResult Ping(Ping ping, string host, int timeout)
        {
            try
            {
                PingReply reply = ping.Send(host, timeout, Util.PingPayload, PingOpts);
                if (reply.Status == IPStatus.Success)
                    return new ProbeResult { Ms = (int)reply.RoundtripTime, Code = reply.Options != null ? reply.Options.Ttl : 0 };
                return ProbeResult.Fail(reply.Status == IPStatus.TimedOut ? "SIN RESPUESTA" : "INACCESIBLE", StatusText(reply.Status));
            }
            catch (PingException ex)
            {
                var inner = ex.InnerException as SocketException;
                if (inner != null && (inner.SocketErrorCode == SocketError.HostNotFound || inner.SocketErrorCode == SocketError.NoData))
                    return ProbeResult.Fail("DNS", "No se pudo resolver el nombre \"" + host + "\".");
                return ProbeResult.Fail("ERROR", ex.InnerException != null ? ex.InnerException.Message : ex.Message);
            }
            catch (Exception ex) { return ProbeResult.Fail("ERROR", ex.Message); }
        }

        static string StatusText(IPStatus st)
        {
            switch (st)
            {
                case IPStatus.TimedOut: return "Tiempo de espera agotado para esta solicitud.";
                case IPStatus.DestinationHostUnreachable: return "Host de destino inaccesible.";
                case IPStatus.DestinationNetworkUnreachable: return "Red de destino inaccesible.";
                case IPStatus.TtlExpired: return "TTL expirado en tránsito.";
                case IPStatus.DestinationUnreachable: return "Destino inaccesible.";
                case IPStatus.BadRoute: return "Ruta incorrecta.";
                default: return "Error: " + st;
            }
        }

        // ------------------------------------------------------------------ TCP

        static IPAddress Resolve(string host)
        {
            IPAddress a;
            if (IPAddress.TryParse(host, out a)) return a;
            var all = Dns.GetHostAddresses(host);
            return all.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? all.First();
        }

        public static ProbeResult Tcp(string host, int port, int timeout)
        {
            IPAddress addr;
            try { addr = Resolve(host); }
            catch { return ProbeResult.Fail("DNS", "No se pudo resolver el nombre \"" + host + "\"."); }

            var client = new TcpClient(addr.AddressFamily);
            try
            {
                var sw = Stopwatch.StartNew();
                var ar = client.BeginConnect(addr, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeout))
                    return ProbeResult.Fail("SIN RESPUESTA", "Tiempo de espera agotado al conectar con " + host + ":" + port + ".");
                client.EndConnect(ar);
                return new ProbeResult { Ms = (int)sw.ElapsedMilliseconds };
            }
            catch (SocketException ex) { return SocketFail(ex, host, port); }
            catch (Exception ex) { return ProbeResult.Fail("ERROR", ex.Message); }
            finally { try { client.Close(); } catch { } }
        }

        static ProbeResult SocketFail(SocketException ex, string host, int port)
        {
            switch (ex.SocketErrorCode)
            {
                case SocketError.ConnectionRefused:
                    return ProbeResult.Fail("PUERTO CERRADO", "Conexión rechazada en " + host + ":" + port + " (el equipo responde, pero el servicio no).");
                case SocketError.TimedOut:
                    return ProbeResult.Fail("SIN RESPUESTA", "Tiempo de espera agotado al conectar con " + host + ":" + port + ".");
                case SocketError.HostUnreachable:
                case SocketError.NetworkUnreachable:
                case SocketError.HostDown:
                    return ProbeResult.Fail("INACCESIBLE", "Equipo o red inaccesible (" + host + ").");
                case SocketError.HostNotFound:
                case SocketError.NoData:
                    return ProbeResult.Fail("DNS", "No se pudo resolver el nombre \"" + host + "\".");
                default:
                    return ProbeResult.Fail("ERROR", ex.Message);
            }
        }

        // ------------------------------------------------------------------ web

        public static ProbeResult Http(TargetConfig c, int timeout)
        {
            X509Certificate2 cert = null;
            bool insecure = c.Insecure;
            HttpWebResponse resp = null;
            var sw = Stopwatch.StartNew();
            ProbeResult res;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(c.Url);
                req.Method = "GET";
                req.Timeout = timeout;
                req.ReadWriteTimeout = timeout;
                req.KeepAlive = false;                      // cada comprobación es una conexión real
                req.AllowAutoRedirect = true;
                req.MaximumAutomaticRedirections = 5;
                req.UserAgent = userAgent;
                req.Accept = "*/*";
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                req.ServerCertificateValidationCallback = (s, certificate, chain, errors) =>
                {
                    try { if (certificate != null) cert = new X509Certificate2(certificate); } catch { }
                    return insecure || errors == SslPolicyErrors.None;
                };
                try { resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException ex)
                {
                    resp = ex.Response as HttpWebResponse;
                    if (resp == null) { res = WebFail(ex, c); AttachCert(res, cert); return res; }
                }

                int code = (int)resp.StatusCode;
                string body = null;
                if (!string.IsNullOrEmpty(c.Keyword)) body = ReadBody(resp, 1024 * 1024);
                int ms = (int)sw.ElapsedMilliseconds;

                if (!CodeMatches(code, c.Expect))
                    res = new ProbeResult { Code = code, Short = "HTTP " + code, Error = "HTTP " + code + " " + resp.StatusDescription + " (se esperaba " + c.Expect + ")." };
                else if (body != null && body.IndexOf(c.Keyword, StringComparison.OrdinalIgnoreCase) < 0)
                    res = new ProbeResult { Code = code, Short = "TEXTO NO ENCONTRADO", Error = "La página respondió HTTP " + code + " pero no contiene \"" + c.Keyword + "\"." };
                else
                    res = new ProbeResult { Ms = ms, Code = code };
            }
            catch (Exception ex) { res = ProbeResult.Fail("ERROR", ex.Message); }
            finally { if (resp != null) try { resp.Close(); } catch { } }
            AttachCert(res, cert);
            return res;
        }

        static void AttachCert(ProbeResult r, X509Certificate2 cert)
        {
            if (cert == null) return;
            try
            {
                r.CertExpires = cert.NotAfter;
                r.CertSubject = cert.GetNameInfo(X509NameType.SimpleName, false);
            }
            catch { }
        }

        static ProbeResult WebFail(WebException ex, TargetConfig c)
        {
            string host = c.Host + (c.Port > 0 ? ":" + c.Port : "");
            switch (ex.Status)
            {
                case WebExceptionStatus.Timeout:
                    return ProbeResult.Fail("SIN RESPUESTA", "Tiempo de espera agotado esperando la respuesta de " + c.Url + ".");
                case WebExceptionStatus.NameResolutionFailure:
                    return ProbeResult.Fail("DNS", "No se pudo resolver el nombre \"" + c.Host + "\".");
                case WebExceptionStatus.TrustFailure:
                    return ProbeResult.Fail("CERTIFICADO", "El certificado HTTPS no es válido. Si es un equipo interno, activa \"Aceptar certificados no válidos\".");
                case WebExceptionStatus.SecureChannelFailure:
                    return ProbeResult.Fail("ERROR TLS", "No se pudo establecer la conexión segura (TLS) con " + host + ".");
                case WebExceptionStatus.ConnectFailure:
                    var se = ex.InnerException as SocketException;
                    if (se == null && ex.InnerException != null) se = ex.InnerException.InnerException as SocketException;
                    if (se != null) return SocketFail(se, c.Host, c.Port);
                    return ProbeResult.Fail("SIN CONEXIÓN", "No se pudo conectar con " + host + ".");
                default:
                    return ProbeResult.Fail("ERROR", ex.Message);
            }
        }

        static string ReadBody(HttpWebResponse resp, int max)
        {
            Encoding enc = Encoding.UTF8;
            try { if (!string.IsNullOrEmpty(resp.CharacterSet)) enc = Encoding.GetEncoding(resp.CharacterSet); } catch { }
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                var buf = new byte[16384];
                int n;
                while (ms.Length < max && (n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                return enc.GetString(ms.ToArray());
            }
        }

        /// <summary>"200-399", "200,301-302", "2xx", "2xx,3xx" …</summary>
        public static bool CodeMatches(int code, string expect)
        {
            if (string.IsNullOrWhiteSpace(expect)) expect = "200-399";
            foreach (var raw in expect.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = raw.Trim().ToLowerInvariant();
                int a, b;
                if (p.Length == 3 && p.EndsWith("xx") && char.IsDigit(p[0])) { if (code / 100 == p[0] - '0') return true; continue; }
                int dash = p.IndexOf('-');
                if (dash > 0 && int.TryParse(p.Substring(0, dash), out a) && int.TryParse(p.Substring(dash + 1), out b)) { if (code >= a && code <= b) return true; continue; }
                if (int.TryParse(p, out a) && code == a) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ detección de puertos

        /// <summary>Prueba en paralelo los puertos comunes y devuelve los abiertos.</summary>
        public static List<object> DetectPorts(string host)
        {
            IPAddress addr;
            try { addr = Resolve((host ?? "").Trim()); }
            catch { throw new ArgumentException("No se pudo resolver \"" + host + "\""); }
            var open = new List<object>();
            var threads = Ports.Common.Select(p => new Thread(() =>
            {
                var r = Tcp(addr.ToString(), p, 800);
                if (r.Ms >= 0) lock (open) open.Add(new Dictionary<string, object> { { "port", p }, { "name", Ports.Name(p) }, { "ms", r.Ms } });
            }) { IsBackground = true }).ToList();
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join(3000);
            lock (open) return open.OrderBy(o => (int)((Dictionary<string, object>)o)["port"]).ToList();
        }
    }
}

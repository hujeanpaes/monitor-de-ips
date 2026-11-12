using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace MonitorIPs
{
    public class ScanHit
    {
        public uint N;
        public string Ip;
        public int Ms = -1;     // -1: no respondió a ping (detectada sólo por ARP)
        public int Ttl;
        public string Mac;
        public string Name;
    }

    public class ScanJob
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Label;
        public string Status = "running"; // running | done | cancelled | error
        public string Error;
        public int Total, Done;
        public long Started = Util.NowMs();
        public volatile bool Cancel;
        public readonly List<ScanHit> Found = new List<ScanHit>();
    }

    /// <summary>Escáner de rangos IPv4: ping en paralelo + ARP en la red local (MAC y equipos que bloquean ping).</summary>
    public static class Scanner
    {
        public const int MaxIps = 4096;
        const int Workers = 64;
        static readonly Dictionary<string, ScanJob> jobs = new Dictionary<string, ScanJob>();

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref int physAddrLen);

        // ------------------------------------------------------------ rangos

        public static uint ToNum(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        public static string ToIp(uint n)
        {
            return (n >> 24) + "." + ((n >> 16) & 255) + "." + ((n >> 8) & 255) + "." + (n & 255);
        }

        static bool TryIp(string s, out uint n)
        {
            n = 0;
            IPAddress a;
            s = (s ?? "").Trim();
            if (s.Count(c => c == '.') != 3 || !IPAddress.TryParse(s, out a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
            n = ToNum(a);
            return true;
        }

        /// <summary>"a.b.c.d - e.f.g.h", "a.b.c.d-254", "a.b.c.0/24" o una IP.</summary>
        public static void ParseRange(string text, out uint first, out uint last, out string label)
        {
            string t = (text ?? "").Trim();
            if (t.Length == 0) throw new ArgumentException("Indica un rango, por ejemplo 192.168.3.1 - 192.168.3.254");
            uint a, b;
            if (t.Contains("/"))
            {
                var p = t.Split('/');
                int bits;
                if (p.Length != 2 || !TryIp(p[0], out a) || !int.TryParse(p[1].Trim(), out bits) || bits < 0 || bits > 32)
                    throw new ArgumentException("Rango CIDR no válido: " + t);
                uint mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
                uint net = a & mask, bc = net | ~mask;
                first = bits <= 30 ? net + 1 : net;
                last = bits <= 30 ? bc - 1 : bc;
                label = ToIp(net) + "/" + bits;
            }
            else if (t.Contains("-"))
            {
                var p = t.Split(new[] { '-' }, 2);
                if (!TryIp(p[0], out a)) throw new ArgumentException("IP inicial no válida: " + p[0].Trim());
                string r = p[1].Trim();
                int oct;
                if (TryIp(r, out b)) { }
                else if (int.TryParse(r, out oct) && oct >= 0 && oct <= 255) b = (a & 0xFFFFFF00u) | (uint)oct;
                else throw new ArgumentException("IP final no válida: " + r);
                first = Math.Min(a, b); last = Math.Max(a, b);
                label = ToIp(first) + " - " + ToIp(last);
            }
            else
            {
                if (!TryIp(t, out a)) throw new ArgumentException("No es una IP o rango válido: " + t);
                first = last = a;
                label = ToIp(a);
            }
            if ((ulong)last - first + 1 > MaxIps)
                throw new ArgumentException("El rango tiene " + ((ulong)last - first + 1) + " IPs; el máximo por escaneo es " + MaxIps + " (por ejemplo, un /20).");
        }

        // ------------------------------------------------------------ redes locales

        class LocalNet { public uint Net, Mask, Ip; public string Name; }

        static List<LocalNet> LocalNets()
        {
            var res = new List<LocalNet>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        uint ip = ToNum(ua.Address), mask = ToNum(ua.IPv4Mask);
                        if ((ip >> 16) == 0xA9FE) continue; // 169.254.x.x (APIPA)
                        res.Add(new LocalNet { Ip = ip, Mask = mask, Net = ip & mask, Name = ni.Name });
                    }
                }
            }
            catch { }
            return res;
        }

        /// <summary>Subredes de este equipo, para los botones rápidos del diálogo.</summary>
        public static List<object> Subnets()
        {
            var list = new List<object>();
            foreach (var n in LocalNets())
            {
                int bits = 0;
                for (uint m = n.Mask; m != 0; m <<= 1) bits++;
                if (bits < 20) bits = 24; // red muy grande: proponer la /24 propia
                uint mask = uint.MaxValue << (32 - bits);
                list.Add(new Dictionary<string, object> {
                    { "cidr", ToIp(n.Ip & mask) + "/" + bits }, { "ip", ToIp(n.Ip) }, { "name", n.Name } });
            }
            return list;
        }

        // ------------------------------------------------------------ escaneo

        public static ScanJob Start(string range, Action<ScanJob> onDone)
        {
            uint first, last;
            string label;
            ParseRange(range, out first, out last, out label);
            var job = new ScanJob { Label = label, Total = (int)(last - first + 1) };
            lock (jobs)
            {
                jobs[job.Id] = job;
                if (jobs.Count > 10)
                    foreach (var old in jobs.Values.OrderBy(j => j.Started).Take(jobs.Count - 10).ToList()) jobs.Remove(old.Id);
            }
            new Thread(() => Run(job, first, last, onDone)) { IsBackground = true, Name = "scan " + label }.Start();
            return job;
        }

        public static void CancelJob(string id)
        {
            ScanJob j;
            lock (jobs) jobs.TryGetValue(id ?? "", out j);
            if (j != null) j.Cancel = true;
        }

        public static Dictionary<string, object> Describe(string id, Func<string, bool> isMonitored)
        {
            ScanJob j;
            lock (jobs) jobs.TryGetValue(id ?? "", out j);
            if (j == null) throw new ArgumentException("Escaneo no encontrado");
            List<ScanHit> found;
            lock (j.Found) found = j.Found.OrderBy(h => h.N).ToList();
            return new Dictionary<string, object> {
                { "id", j.Id }, { "label", j.Label }, { "status", j.Status }, { "error", j.Error },
                { "total", j.Total }, { "done", j.Done },
                { "found", found.Select(h => (object)new Dictionary<string, object> {
                    { "ip", h.Ip }, { "ms", h.Ms }, { "ttl", h.Ttl }, { "mac", h.Mac }, { "name", h.Name },
                    { "monitored", isMonitored(h.Ip) } }).ToList() },
            };
        }

        static void Run(ScanJob job, uint first, uint last, Action<ScanJob> onDone)
        {
            try
            {
                var locals = LocalNets();
                long next = first;
                int workers = Math.Min(Workers, job.Total);
                var threads = new List<Thread>();
                for (int w = 0; w < workers; w++)
                {
                    var th = new Thread(() =>
                    {
                        using (var ping = new Ping())
                        {
                            while (!job.Cancel)
                            {
                                long cur = Interlocked.Increment(ref next) - 1;
                                if (cur > last) break;
                                var hit = Probe(ping, (uint)cur, locals);
                                if (hit != null)
                                {
                                    hit.Name = Util.ReverseDns(hit.Ip, 1200);
                                    lock (job.Found) job.Found.Add(hit);
                                }
                                Interlocked.Increment(ref job.Done);
                            }
                        }
                    }) { IsBackground = true };
                    threads.Add(th);
                    th.Start();
                }
                foreach (var th in threads) th.Join();
                job.Status = job.Cancel ? "cancelled" : "done";
            }
            catch (Exception ex)
            {
                job.Status = "error";
                job.Error = ex.Message;
            }
            try { if (onDone != null) onDone(job); } catch { }
        }

        static readonly PingOptions ScanPingOpts = new PingOptions(128, false);

        /// <summary>
        /// Comprueba una IP. En la red local se pregunta primero por ARP: si nadie responde, el equipo no existe
        /// y se ahorran los pings; si responde, un ping mide la latencia (o queda como "sin ping").
        /// Fuera de la red local: ping con un reintento.
        /// </summary>
        static ScanHit Probe(Ping ping, uint n, List<LocalNet> locals)
        {
            string ip = ToIp(n);
            var hit = new ScanHit { N = n, Ip = ip };
            var addr = IPAddress.Parse(ip);
            bool isSelf = locals.Any(l => l.Ip == n);
            bool isLocal = !isSelf && locals.Any(l => (n & l.Mask) == l.Net && n != l.Net && n != (l.Net | ~l.Mask));
            int attempts = 2;
            if (isLocal)
            {
                hit.Mac = Arp(addr);
                if (hit.Mac == null) return null; // misma red y sin respuesta ARP: no hay equipo
                attempts = 1;
            }
            for (int attempt = 0; attempt < attempts && hit.Ms < 0; attempt++)
            {
                try
                {
                    var r = ping.Send(addr, 1000, Util.PingPayload, ScanPingOpts);
                    if (r.Status == IPStatus.Success)
                    {
                        hit.Ms = (int)r.RoundtripTime;
                        hit.Ttl = r.Options != null ? r.Options.Ttl : 0;
                    }
                }
                catch { }
            }
            if (isSelf && hit.Ms >= 0) hit.Mac = "(este equipo)";
            return hit.Ms >= 0 || hit.Mac != null ? hit : null;
        }

        static string Arp(IPAddress addr)
        {
            try
            {
                var mac = new byte[6];
                int len = mac.Length;
                uint dest = BitConverter.ToUInt32(addr.GetAddressBytes(), 0);
                if (SendARP(dest, 0, mac, ref len) != 0 || len == 0) return null;
                if (mac.All(b => b == 0)) return null;
                return string.Join("-", mac.Take(len).Select(b => b.ToString("X2")).ToArray());
            }
            catch { return null; }
        }
    }
}

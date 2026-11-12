using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace MonitorIPs
{
    public class TraceHop
    {
        public int N;
        public string Ip;
        public string Name;
        public int[] Times = { -2, -2, -2 }; // -2 pendiente, -1 sin respuesta (*)
    }

    public class TraceJob
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Host;
        public string Ip;
        public string Status = "running"; // running | done | error | cancelled
        public string Error;
        public bool Resolve = true;
        public long Started = Util.NowMs();
        public volatile bool Cancel;
        public readonly List<TraceHop> Hops = new List<TraceHop>();
    }

    /// <summary>Tracert implementado con Ping + TTL creciente (como tracert.exe, sin parsear su salida).</summary>
    public static class Tracer
    {
        const int MaxHops = 30;
        const int ProbeTimeout = 2000;
        static readonly Dictionary<string, TraceJob> jobs = new Dictionary<string, TraceJob>();

        public static TraceJob Start(string host, bool resolve)
        {
            host = (host ?? "").Trim();
            if (host.Length == 0) throw new ArgumentException("Indica una IP o host");
            var job = new TraceJob { Host = host, Resolve = resolve };
            lock (jobs)
            {
                jobs[job.Id] = job;
                if (jobs.Count > 30)
                    foreach (var old in jobs.Values.OrderBy(j => j.Started).Take(jobs.Count - 30).ToList()) jobs.Remove(old.Id);
            }
            new Thread(() => Run(job)) { IsBackground = true, Name = "trace " + host }.Start();
            return job;
        }

        public static void CancelJob(string id)
        {
            TraceJob j;
            lock (jobs) jobs.TryGetValue(id ?? "", out j);
            if (j != null) j.Cancel = true;
        }

        public static Dictionary<string, object> Describe(string id)
        {
            TraceJob j;
            lock (jobs) jobs.TryGetValue(id ?? "", out j);
            if (j == null) throw new ArgumentException("Traza no encontrada");
            lock (j.Hops)
            {
                return new Dictionary<string, object> {
                    { "id", j.Id }, { "host", j.Host }, { "ip", j.Ip }, { "status", j.Status }, { "error", j.Error },
                    { "hops", j.Hops.Select(h => (object)new Dictionary<string, object> {
                        { "n", h.N }, { "ip", h.Ip }, { "name", h.Name }, { "times", h.Times.ToArray() } }).ToList() },
                };
            }
        }

        static void Run(TraceJob job)
        {
            try
            {
                IPAddress addr;
                if (!IPAddress.TryParse(job.Host, out addr))
                {
                    var all = Dns.GetHostAddresses(job.Host);
                    addr = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.First();
                }
                job.Ip = addr.ToString();
                bool reached = false;
                using (var ping = new Ping())
                {
                    for (int ttl = 1; ttl <= MaxHops && !reached && !job.Cancel; ttl++)
                    {
                        var hop = new TraceHop { N = ttl };
                        lock (job.Hops) job.Hops.Add(hop);
                        for (int p = 0; p < 3 && !job.Cancel; p++)
                        {
                            var sw = Stopwatch.StartNew();
                            int t = -1;
                            try
                            {
                                var reply = ping.Send(addr, ProbeTimeout, Util.PingPayload, new PingOptions(ttl, true));
                                sw.Stop();
                                if (reply.Status == IPStatus.Success || reply.Status == IPStatus.TtlExpired || reply.Status == IPStatus.TimeExceeded)
                                {
                                    t = reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : (int)sw.ElapsedMilliseconds;
                                    if (reply.Address != null && hop.Ip == null) hop.Ip = reply.Address.ToString();
                                    if (reply.Status == IPStatus.Success) reached = true;
                                }
                                else if (reply.Address != null && !reply.Address.Equals(IPAddress.Any) && hop.Ip == null &&
                                         (reply.Status == IPStatus.DestinationHostUnreachable || reply.Status == IPStatus.DestinationNetworkUnreachable))
                                {
                                    hop.Ip = reply.Address.ToString();
                                }
                            }
                            catch { }
                            lock (job.Hops) hop.Times[p] = t;
                        }
                        if (hop.Ip != null && job.Resolve) hop.Name = Util.ReverseDns(hop.Ip, 1200);
                    }
                }
                job.Status = job.Cancel ? "cancelled" : "done";
                if (!reached && !job.Cancel) job.Error = "No se alcanzó el destino en " + MaxHops + " saltos";
            }
            catch (Exception ex)
            {
                job.Status = "error";
                job.Error = ex is SocketException ? "No se pudo resolver \"" + job.Host + "\"" : ex.Message;
            }
        }
    }
}

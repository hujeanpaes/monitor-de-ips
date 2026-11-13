using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;

namespace MonitorIPs
{
    public class MonitorEvent
    {
        public long Seq;
        public long T;
        public string TargetId;
        public string Name;
        public string Host;
        public string Type;     // down | up | added | removed | paused | resumed | restored | cleared | scan
        public string Detail;
        public double Duration; // segundos (para "up": cuánto estuvo caída)
    }

    public class TargetRuntime
    {
        public const int Capacity = 900; // ~15 min a 1 s
        public TargetConfig Cfg;
        public readonly object Lock = new object();
        public long[][] Ring = new long[Capacity][]; // {seq, t, ms, ttl}  ms = -1 -> sin respuesta
        public int Head, Count;
        public string State = "unknown"; // unknown | up | down
        public long StateSince;
        public long FirstFailT;
        public int ConsecFail;
        public long Sent, Recv;
        public int LastMs = -2;
        public string LastError;
        public int Epoch;
        public volatile bool Stop;
        public readonly AutoResetEvent Wake = new AutoResetEvent(false);
        public Thread Thread;

        public void Reset()
        {
            lock (Lock)
            {
                Ring = new long[Capacity][];
                Head = 0; Count = 0;
                State = "unknown"; StateSince = Util.NowMs();
                ConsecFail = 0; Sent = 0; Recv = 0; LastMs = -2; LastError = null;
                Epoch++;
            }
        }

        public void Push(long[] s)
        {
            Ring[Head] = s;
            Head = (Head + 1) % Capacity;
            if (Count < Capacity) Count++;
        }

        /// <summary>Muestra i-ésima en orden cronológico (0 = la más antigua). Llamar con Lock tomado.</summary>
        public long[] At(int i) { return Ring[(Head - Count + i + Capacity) % Capacity]; }

        public List<long[]> Samples()
        {
            var list = new List<long[]>(Count);
            for (int i = 0; i < Count; i++) list.Add(At(i));
            return list;
        }
    }

    public class MonitorEngine
    {
        readonly AppConfig cfg;
        readonly List<TargetRuntime> targets = new List<TargetRuntime>();
        readonly List<MonitorEvent> events = new List<MonitorEvent>();
        long seq, evSeq;
        static readonly PingOptions PingOpts = new PingOptions(128, false);

        public MonitorEngine(AppConfig cfg) { this.cfg = cfg; }

        public void SaveConfig() { Store.Save(cfg); }

        public void Start()
        {
            lock (targets) foreach (var t in cfg.Targets) StartRuntime(t);
        }

        public void StopAll()
        {
            lock (targets) foreach (var rt in targets) { rt.Stop = true; rt.Wake.Set(); }
        }

        TargetRuntime StartRuntime(TargetConfig t)
        {
            var rt = new TargetRuntime { Cfg = t, StateSince = Util.NowMs() };
            rt.Thread = new Thread(() => Loop(rt)) { IsBackground = true, Name = "ping " + t.Host };
            targets.Add(rt);
            rt.Thread.Start();
            return rt;
        }

        TargetRuntime Find(string id)
        {
            lock (targets) return targets.FirstOrDefault(x => x.Cfg.Id == id);
        }

        // ---------- operaciones (llamadas desde la API) ----------

        /// <summary>Añade (id vacío) o edita una IP. save=false para altas en bloque (se guarda una vez al final).</summary>
        public TargetConfig AddOrUpdate(string id, string name, string host, string group, int intervalMs, bool save = true)
        {
            host = (host ?? "").Trim();
            if (host.Length == 0 || host.Length > 253 || host.Contains(" ")) throw new ArgumentException("IP o nombre de host no válido");
            if (intervalMs < 250) intervalMs = 250;
            TargetConfig result;
            lock (targets)
            {
                var rt = string.IsNullOrEmpty(id) ? null : Find(id);
                if (rt != null)
                {
                    bool hostChanged = !string.Equals(rt.Cfg.Host, host, StringComparison.OrdinalIgnoreCase);
                    rt.Cfg.Name = (name ?? "").Trim();
                    rt.Cfg.Group = (group ?? "").Trim();
                    rt.Cfg.IntervalMs = intervalMs;
                    rt.Cfg.Host = host;
                    if (hostChanged) rt.Reset();
                    rt.Wake.Set();
                    result = rt.Cfg;
                }
                else
                {
                    var t = new TargetConfig { Name = (name ?? "").Trim(), Host = host, Group = (group ?? "").Trim(), IntervalMs = intervalMs };
                    lock (cfg) cfg.Targets.Add(t);
                    StartRuntime(t);
                    AddEvent(t, "added", null, 0);
                    result = t;
                }
            }
            if (save) Store.Save(cfg);
            return result;
        }

        /// <summary>Quita una IP del monitoreo y la manda a la papelera (recuperable 30 días).</summary>
        public void Remove(string id)
        {
            TargetRuntime rt;
            lock (targets)
            {
                rt = Find(id);
                if (rt == null) return;
                targets.Remove(rt);
                lock (cfg)
                {
                    cfg.Targets.Remove(rt.Cfg);
                    cfg.Trash.Add(new TrashItem { Target = rt.Cfg, DeletedAt = Util.NowMs() });
                }
            }
            rt.Stop = true; rt.Wake.Set();
            AddEvent(rt.Cfg, "removed", null, 0);
            Store.Save(cfg);
        }

        /// <summary>Manda TODAS las IPs a la papelera.</summary>
        public int RemoveAll()
        {
            List<TargetRuntime> all;
            long now = Util.NowMs();
            lock (targets)
            {
                all = new List<TargetRuntime>(targets);
                targets.Clear();
                lock (cfg)
                {
                    cfg.Targets.Clear();
                    foreach (var rt in all) cfg.Trash.Add(new TrashItem { Target = rt.Cfg, DeletedAt = now });
                }
            }
            foreach (var rt in all) { rt.Stop = true; rt.Wake.Set(); }
            if (all.Count > 0)
                AddEvent(new TargetConfig { Name = "Todas las IPs", Host = all.Count + " IPs enviadas a la papelera" }, "cleared", null, 0);
            Store.Save(cfg);
            return all.Count;
        }

        public List<object> TrashList()
        {
            lock (cfg)
            {
                if (cfg.PurgeExpiredTrash()) Store.Save(cfg);
                long now = Util.NowMs();
                return cfg.Trash.OrderByDescending(x => x.DeletedAt).Select(x => (object)new Dictionary<string, object> {
                    { "id", x.Target.Id }, { "name", x.Target.Name }, { "host", x.Target.Host }, { "group", x.Target.Group },
                    { "deletedAt", x.DeletedAt },
                    { "expiresAt", x.DeletedAt + AppConfig.TrashDays * 86400000L },
                }).ToList();
            }
        }

        public int TrashCount()
        {
            lock (cfg) return cfg.Trash.Count;
        }

        /// <summary>Recupera de la papelera (ids == null: todo).</summary>
        public int Restore(List<string> ids)
        {
            var restored = new List<TargetConfig>();
            lock (targets)
            {
                lock (cfg)
                {
                    var items = cfg.Trash.Where(x => ids == null || ids.Contains(x.Target.Id)).OrderBy(x => x.DeletedAt).ToList();
                    foreach (var it in items)
                    {
                        cfg.Trash.Remove(it);
                        var t = it.Target;
                        if (cfg.Targets.Any(x => x.Id == t.Id)) t.Id = new TargetConfig().Id;
                        cfg.Targets.Add(t);
                        restored.Add(t);
                    }
                }
                foreach (var t in restored) StartRuntime(t);
            }
            foreach (var t in restored) AddEvent(t, "restored", null, 0);
            if (restored.Count > 0) Store.Save(cfg);
            return restored.Count;
        }

        /// <summary>Borra definitivamente de la papelera (ids == null: vaciar).</summary>
        public int PurgeTrash(List<string> ids)
        {
            int n;
            lock (cfg) n = cfg.Trash.RemoveAll(x => ids == null || ids.Contains(x.Target.Id));
            if (n > 0) Store.Save(cfg);
            return n;
        }

        public void SetPaused(string id, bool paused)
        {
            var rt = Find(id);
            if (rt == null || rt.Cfg.Paused == paused) return;
            rt.Cfg.Paused = paused;
            lock (rt.Lock) { rt.State = "unknown"; rt.ConsecFail = 0; rt.StateSince = Util.NowMs(); }
            rt.Wake.Set();
            AddEvent(rt.Cfg, paused ? "paused" : "resumed", null, 0);
            Store.Save(cfg);
        }

        public void SetAllPaused(bool paused)
        {
            List<string> ids;
            lock (targets) ids = targets.Select(t => t.Cfg.Id).ToList();
            foreach (var id in ids) SetPaused(id, paused);
        }

        public void ResetStats(string id)
        {
            var rt = Find(id);
            if (rt != null) rt.Reset();
        }

        public bool IsDown(string id)
        {
            var rt = Find(id);
            return rt != null && !rt.Cfg.Paused && rt.State == "down";
        }

        public bool HasHost(string host)
        {
            host = (host ?? "").Trim();
            lock (targets) return targets.Any(t => string.Equals(t.Cfg.Host, host, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Evento informativo que no pertenece a una IP concreta (escaneos, etc.).</summary>
        public void InfoEvent(string type, string name, string host)
        {
            AddEvent(new TargetConfig { Id = "", Name = name, Host = host }, type, null, 0);
        }

        // ---------- bucle de ping ----------

        void Loop(TargetRuntime rt)
        {
            using (var ping = new Ping())
            {
                while (!rt.Stop)
                {
                    var c = rt.Cfg;
                    if (c.Paused) { rt.Wake.WaitOne(500); continue; }
                    var sw = Stopwatch.StartNew();
                    int ms = -1, ttl = 0;
                    string err = null;
                    int epoch = rt.Epoch;
                    try
                    {
                        PingReply reply = ping.Send(c.Host, cfg.TimeoutMs, Util.PingPayload, PingOpts);
                        if (reply.Status == IPStatus.Success)
                        {
                            ms = (int)reply.RoundtripTime;
                            ttl = reply.Options != null ? reply.Options.Ttl : 0;
                        }
                        else err = StatusText(reply.Status);
                    }
                    catch (PingException ex)
                    {
                        err = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                    }
                    catch (Exception ex) { err = ex.Message; }

                    if (rt.Stop) break;
                    if (!c.Paused && epoch == rt.Epoch) Record(rt, ms, ttl, err);

                    int wait = c.IntervalMs - (int)sw.ElapsedMilliseconds;
                    if (wait < 50) wait = 50;
                    rt.Wake.WaitOne(wait);
                }
            }
        }

        void Record(TargetRuntime rt, int ms, int ttl, string err)
        {
            long now = Util.NowMs();
            MonitorEvent pending = null;
            bool isDown;
            lock (rt.Lock)
            {
                long s = Interlocked.Increment(ref seq);
                rt.Push(new long[] { s, now, ms, ttl });
                rt.Sent++;
                rt.LastMs = ms;
                if (ms >= 0)
                {
                    rt.Recv++;
                    rt.ConsecFail = 0;
                    rt.LastError = null;
                    if (rt.State != "up")
                    {
                        if (rt.State == "down")
                            pending = MakeEvent(rt.Cfg, "up", null, (now - rt.StateSince) / 1000.0);
                        rt.State = "up";
                        rt.StateSince = now;
                    }
                }
                else
                {
                    rt.LastError = err;
                    rt.ConsecFail++;
                    if (rt.ConsecFail == 1) rt.FirstFailT = now;
                    if (rt.ConsecFail >= cfg.DownAfter && rt.State != "down")
                    {
                        rt.State = "down";
                        rt.StateSince = rt.FirstFailT;
                        pending = MakeEvent(rt.Cfg, "down", err, 0);
                    }
                }
                isDown = rt.State == "down";
            }
            History.Add(rt.Cfg.Id, ms, isDown);
            if (pending != null) PushEvent(pending);
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

        // ---------- eventos ----------

        MonitorEvent MakeEvent(TargetConfig t, string type, string detail, double dur)
        {
            return new MonitorEvent { T = Util.NowMs(), TargetId = t.Id, Name = t.Name, Host = t.Host, Type = type, Detail = detail, Duration = dur };
        }

        void AddEvent(TargetConfig t, string type, string detail, double dur) { PushEvent(MakeEvent(t, type, detail, dur)); }

        void PushEvent(MonitorEvent ev)
        {
            lock (events)
            {
                ev.Seq = ++evSeq;
                events.Add(ev);
                if (events.Count > 2000) events.RemoveRange(0, events.Count - 2000);
            }
            Store.LogEvent(ev);
            var handler = EventRaised;
            if (handler != null) try { handler(ev); } catch { }
        }

        /// <summary>Se dispara con cada evento nuevo (fuera de los bloqueos). La bandeja lo usa para avisar.</summary>
        public event Action<MonitorEvent> EventRaised;

        /// <summary>Recuento rápido para la bandeja: { en línea, caídas, pausadas, total }.</summary>
        public int[] CountStates()
        {
            int up = 0, down = 0, paused = 0, total;
            lock (targets)
            {
                total = targets.Count;
                foreach (var rt in targets)
                {
                    if (rt.Cfg.Paused) paused++;
                    else if (rt.State == "up") up++;
                    else if (rt.State == "down") down++;
                }
            }
            return new[] { up, down, paused, total };
        }

        public List<MonitorEvent> EventsSnapshot()
        {
            lock (events) return new List<MonitorEvent>(events);
        }

        // ---------- snapshot para la UI ----------

        public Dictionary<string, object> Snapshot(long since, long evSince)
        {
            var list = new List<object>();
            List<TargetRuntime> copy;
            lock (targets) copy = new List<TargetRuntime>(targets);
            long maxSeq = Interlocked.Read(ref seq);
            // sólo muestras en (since, maxSeq]: las que lleguen mientras tanto irán en el siguiente poll, sin duplicarse
            foreach (var rt in copy) list.Add(Describe(rt, since, maxSeq));

            List<object> evs;
            lock (events)
                evs = events.Where(e => e.Seq > evSince).Select(e => (object)new Dictionary<string, object> {
                    { "seq", e.Seq }, { "t", e.T }, { "id", e.TargetId }, { "name", e.Name }, { "host", e.Host },
                    { "type", e.Type }, { "detail", e.Detail }, { "dur", e.Duration } }).ToList();

            return new Dictionary<string, object> {
                { "seq", maxSeq }, { "targets", list }, { "events", evs },
            };
        }

        /// <summary>Estado + estadísticas de la ventana (una sola pasada) + muestras con since &lt; seq &lt;= upTo.</summary>
        public Dictionary<string, object> Describe(TargetRuntime rt, long since, long upTo)
        {
            lock (rt.Lock)
            {
                var c = rt.Cfg;
                int n = rt.Count, okCount = 0;
                long min = long.MaxValue, max = -1, sum = 0, prevOk = -1;
                double jitterSum = 0;
                var fresh = new List<long[]>();
                for (int i = 0; i < n; i++)
                {
                    var s = rt.At(i);
                    if (s[0] > since && s[0] <= upTo) fresh.Add(s);
                    long ms = s[2];
                    if (ms < 0) continue;
                    okCount++; sum += ms;
                    if (ms < min) min = ms;
                    if (ms > max) max = ms;
                    if (prevOk >= 0) jitterSum += Math.Abs(ms - prevOk);
                    prevOk = ms;
                }
                double jitter = okCount > 1 ? jitterSum / (okCount - 1) : 0;
                return new Dictionary<string, object> {
                    { "id", c.Id }, { "name", c.Name }, { "host", c.Host }, { "group", c.Group },
                    { "interval", c.IntervalMs }, { "paused", c.Paused },
                    { "state", c.Paused ? "paused" : rt.State }, { "since", rt.StateSince },
                    { "consec", rt.ConsecFail }, { "last", rt.LastMs }, { "error", rt.LastError },
                    { "sent", rt.Sent }, { "recv", rt.Recv }, { "epoch", rt.Epoch },
                    { "min", okCount > 0 ? min : -1 }, { "max", okCount > 0 ? max : -1 },
                    { "avg", okCount > 0 ? Math.Round((double)sum / okCount, 1) : -1 },
                    { "jitter", Math.Round(jitter, 1) },
                    { "loss", n > 0 ? Math.Round(100.0 * (n - okCount) / n, 1) : 0 },
                    { "window", n },
                    { "samples", fresh },
                };
            }
        }

        public List<Dictionary<string, object>> SummaryAll()
        {
            List<TargetRuntime> copy;
            lock (targets) copy = new List<TargetRuntime>(targets);
            return copy.Select(rt => Describe(rt, long.MaxValue, long.MaxValue)).ToList();
        }

        public List<KeyValuePair<TargetConfig, List<long[]>>> AllSamples()
        {
            List<TargetRuntime> copy;
            lock (targets) copy = new List<TargetRuntime>(targets);
            var res = new List<KeyValuePair<TargetConfig, List<long[]>>>();
            foreach (var rt in copy)
                lock (rt.Lock) res.Add(new KeyValuePair<TargetConfig, List<long[]>>(rt.Cfg, rt.Samples()));
            return res;
        }
    }
}

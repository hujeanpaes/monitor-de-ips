using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace MonitorIPs
{
    /// <summary>
    /// Historial largo: agregados por minuto e IP en history\AAAA-MM-DD.csv (fecha local).
    /// Línea: minutoUnix;id;enviados;recibidos;sumaMs;minMs;maxMs;caída(0/1)
    /// Disponibilidad = minutos sin caída / minutos vigilados (los minutos en pausa o con el programa cerrado no cuentan).
    /// </summary>
    public static class History
    {
        // ------------------------------------------------------------------ tipos

        class Acc { public long Minute; public int Sent, Recv, Min = int.MaxValue, Max = -1; public long Sum; public bool Down; }

        struct Row { public long Minute; public int Sent, Recv, Min, Max; public long Sum; public bool Down; }

        /// <summary>Agregado de un tramo (minuto, hora o bucket).</summary>
        class Agg
        {
            public int Mins, DownMins, Min = int.MaxValue, Max = -1;
            public long Sent, Recv, Sum;
            public void Add(Row r)
            {
                Mins++; if (r.Down) DownMins++;
                Sent += r.Sent; Recv += r.Recv; Sum += r.Sum;
                if (r.Recv > 0) { if (r.Min < Min) Min = r.Min; if (r.Max > Max) Max = r.Max; }
            }
            public void Add(Agg o)
            {
                Mins += o.Mins; DownMins += o.DownMins; Sent += o.Sent; Recv += o.Recv; Sum += o.Sum;
                if (o.Min < Min) Min = o.Min; if (o.Max > Max) Max = o.Max;
            }
        }

        /// <summary>Un día ya cerrado de una IP: agregados por hora + tramos de caída (para no releer minutos).</summary>
        class IdDay { public readonly Agg[] Hours = new Agg[24]; public readonly List<long[]> Runs = new List<long[]>(); }

        // ------------------------------------------------------------------ estado

        static readonly object sync = new object();
        static readonly object fileLock = new object();
        static readonly Dictionary<string, Acc> open = new Dictionary<string, Acc>();
        static readonly List<KeyValuePair<string, Acc>> closed = new List<KeyValuePair<string, Acc>>();
        static readonly Dictionary<DateTime, Dictionary<string, IdDay>> cache = new Dictionary<DateTime, Dictionary<string, IdDay>>();
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static Thread writer;
        static volatile bool stopping;
        static string dir;
        static Func<int> retentionDays;
        static DateTime lastPurge = DateTime.MinValue;

        public static void Start(string historyDir, Func<int> days)
        {
            dir = historyDir;
            retentionDays = days;
            Directory.CreateDirectory(dir);
            Purge();
            writer = new Thread(() =>
            {
                while (!stopping)
                {
                    // justo después de cada cambio de minuto
                    int wait = 60000 - (int)(Util.NowMs() % 60000) + 1500;
                    for (int t = 0; t < wait && !stopping; t += 250) Thread.Sleep(250);
                    if (stopping) break;
                    try { Flush(false); } catch { }
                    if (DateTime.Today != lastPurge) try { Purge(); } catch { }
                }
            }) { IsBackground = true, Name = "history" };
            writer.Start();
        }

        /// <summary>Al salir: escribe también el minuto en curso.</summary>
        public static void Stop()
        {
            stopping = true;
            try { Flush(true); } catch { }
        }

        /// <summary>Llamado por el motor con cada ping (ms = -1 sin respuesta; down = la IP está en estado caída).</summary>
        public static void Add(string id, int ms, bool down)
        {
            long minute = Util.NowMs() / 60000;
            lock (sync)
            {
                Acc a;
                if (open.TryGetValue(id, out a) && a.Minute != minute) { closed.Add(new KeyValuePair<string, Acc>(id, a)); a = null; }
                if (a == null) { a = new Acc { Minute = minute }; open[id] = a; }
                a.Sent++;
                if (ms >= 0)
                {
                    a.Recv++; a.Sum += ms;
                    if (ms < a.Min) a.Min = ms;
                    if (ms > a.Max) a.Max = ms;
                }
                if (down) a.Down = true;
            }
        }

        /// <summary>Escribe los minutos cerrados (o todos si all) en el archivo de su día.</summary>
        public static void Flush(bool all)
        {
            long cur = Util.NowMs() / 60000;
            List<KeyValuePair<string, Acc>> rows;
            lock (sync)
            {
                foreach (var id in open.Keys.ToList())
                    if (all || open[id].Minute < cur) { closed.Add(new KeyValuePair<string, Acc>(id, open[id])); open.Remove(id); }
                rows = new List<KeyValuePair<string, Acc>>(closed);
                closed.Clear();
            }
            if (rows.Count == 0 || dir == null) return;
            lock (fileLock)
            {
                foreach (var day in rows.GroupBy(r => LocalDate(r.Value.Minute)))
                {
                    var sb = new StringBuilder();
                    foreach (var r in day.OrderBy(x => x.Value.Minute))
                    {
                        var a = r.Value;
                        sb.Append(a.Minute.ToString(Inv)).Append(';').Append(r.Key).Append(';')
                          .Append(a.Sent.ToString(Inv)).Append(';').Append(a.Recv.ToString(Inv)).Append(';')
                          .Append(a.Sum.ToString(Inv)).Append(';')
                          .Append((a.Recv > 0 ? a.Min : -1).ToString(Inv)).Append(';')
                          .Append(a.Max.ToString(Inv)).Append(';')
                          .Append(a.Down ? '1' : '0').Append("\r\n");
                    }
                    try { File.AppendAllText(PathFor(day.Key), sb.ToString(), Encoding.ASCII); } catch { }
                }
            }
        }

        // ------------------------------------------------------------------ archivos

        static DateTime LocalDate(long minute) { return Util.FromUnixMs(minute * 60000).Date; }
        static string PathFor(DateTime day) { return Path.Combine(dir, day.ToString("yyyy-MM-dd", Inv) + ".csv"); }
        public static long DayStartMinute(DateTime day) { return (long)(day.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMinutes; }

        /// <summary>Filas de un día agrupadas por IP y ordenadas por minuto.</summary>
        static Dictionary<string, List<Row>> ReadDay(DateTime day, HashSet<string> ids)
        {
            var res = new Dictionary<string, List<Row>>();
            string path = PathFor(day);
            if (!File.Exists(path)) return res;
            try
            {
                lock (fileLock)
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.ASCII))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        var p = line.Split(';');
                        if (p.Length < 8) continue;
                        if (ids != null && !ids.Contains(p[1])) continue;
                        var r = new Row();
                        if (!long.TryParse(p[0], NumberStyles.Integer, Inv, out r.Minute)) continue;
                        int.TryParse(p[2], NumberStyles.Integer, Inv, out r.Sent);
                        int.TryParse(p[3], NumberStyles.Integer, Inv, out r.Recv);
                        long.TryParse(p[4], NumberStyles.Integer, Inv, out r.Sum);
                        int.TryParse(p[5], NumberStyles.Integer, Inv, out r.Min);
                        int.TryParse(p[6], NumberStyles.Integer, Inv, out r.Max);
                        r.Down = p[7] == "1";
                        List<Row> list;
                        if (!res.TryGetValue(p[1], out list)) res[p[1]] = list = new List<Row>();
                        list.Add(r);
                    }
                }
            }
            catch { }
            foreach (var l in res.Values) l.Sort((a, b) => a.Minute.CompareTo(b.Minute));
            return res;
        }

        static IdDay ToIdDay(List<Row> rows, long dayStart)
        {
            var d = new IdDay();
            long[] run = null;
            foreach (var r in rows)
            {
                int h = (int)Math.Max(0, Math.Min(23, (r.Minute - dayStart) / 60));
                if (d.Hours[h] == null) d.Hours[h] = new Agg();
                d.Hours[h].Add(r);
                if (r.Down)
                {
                    if (run != null && run[1] == r.Minute) run[1] = r.Minute + 1;
                    else { run = new long[] { r.Minute, r.Minute + 1 }; d.Runs.Add(run); }
                }
            }
            return d;
        }

        /// <summary>Días anteriores a ayer: no cambian, se cachean agregados por hora (todas las IPs).</summary>
        static Dictionary<string, IdDay> CachedDay(DateTime day)
        {
            lock (cache)
            {
                Dictionary<string, IdDay> d;
                if (cache.TryGetValue(day, out d)) return d;
                d = new Dictionary<string, IdDay>();
                long start = DayStartMinute(day);
                foreach (var kv in ReadDay(day, null)) d[kv.Key] = ToIdDay(kv.Value, start);
                cache[day] = d;
                return d;
            }
        }

        public static void Purge()
        {
            lastPurge = DateTime.Today;
            if (dir == null || !Directory.Exists(dir)) return;
            DateTime limit = DateTime.Today.AddDays(-Math.Max(1, retentionDays()));
            lock (fileLock)
                foreach (var f in Directory.GetFiles(dir, "????-??-??.csv"))
                {
                    DateTime d;
                    if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", Inv, DateTimeStyles.None, out d) && d < limit)
                        try { File.Delete(f); } catch { }
                }
            lock (cache) foreach (var k in cache.Keys.Where(k => k < limit).ToList()) cache.Remove(k);
        }

        public static long SizeBytes()
        {
            try { return dir == null || !Directory.Exists(dir) ? 0 : new DirectoryInfo(dir).GetFiles("*.csv").Sum(f => f.Length); }
            catch { return 0; }
        }

        // ------------------------------------------------------------------ consultas

        /// <summary>24h → tramos de 5 min; 7d → 1 h; 30d → 4 h.</summary>
        static void RangeParams(string range, out int spanMin, out int bucketMin)
        {
            switch (range)
            {
                case "30d": spanMin = 30 * 1440; bucketMin = 240; break;
                case "7d": spanMin = 7 * 1440; bucketMin = 60; break;
                default: spanMin = 1440; bucketMin = 5; break;
            }
        }

        class IdResult { public Agg Total = new Agg(); public Agg[] Buckets; public List<long[]> Runs = new List<long[]>(); }

        static Dictionary<string, IdResult> Compute(HashSet<string> ids, string range, bool buckets, out long fromMin, out long toMin, out int bucketMin)
        {
            int span;
            RangeParams(range, out span, out bucketMin);
            toMin = Util.NowMs() / 60000;                      // minuto en curso (excluido: aún abierto)
            fromMin = (toMin - span) / bucketMin * bucketMin;  // alineado al tramo
            int nb = (int)((toMin - fromMin + bucketMin - 1) / bucketMin);
            long f = fromMin; int bm = bucketMin;
            return Compute(ids, fromMin, toMin, buckets ? nb : 0, m => (int)((m - f) / bm), bucketMin >= 60);
        }

        /// <summary>
        /// Agrega [fromMin, toMin) para cada IP. nb = número de tramos (0 = sin tramos) e idx = tramo de un minuto.
        /// hourly = los días cerrados se pueden leer por horas (tramos de 1 h o más).
        /// </summary>
        static Dictionary<string, IdResult> Compute(HashSet<string> ids, long fromMin, long toMin, int nb, Func<long, int> idx, bool hourly)
        {
            Flush(false); // que el archivo de hoy tenga todos los minutos cerrados
            bool buckets = nb > 0;
            var res = new Dictionary<string, IdResult>();
            foreach (var id in ids) res[id] = new IdResult { Buckets = buckets ? new Agg[nb] : null };

            DateTime yesterday = DateTime.Today.AddDays(-1);
            DateTime lastDay = LocalDate(Math.Max(fromMin, toMin - 1));
            for (DateTime day = LocalDate(fromMin); day <= lastDay; day = day.AddDays(1))
            {
                long dayStart = DayStartMinute(day);
                if (day < yesterday && hourly)
                {
                    // día cerrado: horas cacheadas
                    var cd = CachedDay(day);
                    foreach (var id in ids)
                    {
                        IdDay idd;
                        if (!cd.TryGetValue(id, out idd)) continue;
                        var r = res[id];
                        for (int h = 0; h < 24; h++)
                        {
                            var a = idd.Hours[h];
                            long start = dayStart + h * 60;
                            if (a == null || start < fromMin || start >= toMin) continue;
                            r.Total.Add(a);
                            if (r.Buckets != null) AddTo(r.Buckets, idx(start), a);
                        }
                        foreach (var run in idd.Runs) AddRun(r.Runs, run);
                    }
                }
                else
                {
                    // hoy, ayer o rango de 24 h: minuto a minuto
                    foreach (var kv in ReadDay(day, ids))
                    {
                        var r = res[kv.Key];
                        foreach (var row in kv.Value)
                        {
                            if (row.Minute < fromMin || row.Minute >= toMin) continue;
                            r.Total.Add(row);
                            if (r.Buckets != null)
                            {
                                var one = new Agg(); one.Add(row);
                                AddTo(r.Buckets, idx(row.Minute), one);
                            }
                            if (row.Down) AddRun(r.Runs, new long[] { row.Minute, row.Minute + 1 });
                        }
                    }
                }
            }
            // recorta los tramos de caída al rango
            long f = fromMin, t = toMin;
            foreach (var r in res.Values)
                r.Runs = r.Runs.Where(x => x[1] > f && x[0] < t).Select(x => new[] { Math.Max(x[0], f), Math.Min(x[1], t) }).ToList();
            return res;
        }

        static void AddTo(Agg[] b, int i, Agg a)
        {
            if (i < 0 || i >= b.Length) return;
            if (b[i] == null) b[i] = new Agg();
            b[i].Add(a);
        }

        /// <summary>Añade un tramo de caída fusionándolo con el anterior si son contiguos (también entre días).</summary>
        static void AddRun(List<long[]> runs, long[] run)
        {
            if (runs.Count > 0 && runs[runs.Count - 1][1] >= run[0]) runs[runs.Count - 1][1] = Math.Max(runs[runs.Count - 1][1], run[1]);
            else runs.Add(new[] { run[0], run[1] });
        }

        static Dictionary<string, object> SummaryOf(IdResult r, long toMin, bool downNow)
        {
            var t = r.Total;
            long downtime = r.Runs.Sum(x => x[1] - x[0]);
            long longest = r.Runs.Count > 0 ? r.Runs.Max(x => x[1] - x[0]) : 0;
            return new Dictionary<string, object> {
                { "minutes", t.Mins }, { "downMinutes", t.DownMins },
                { "availability", t.Mins > 0 ? (object)Math.Round(100.0 * (t.Mins - t.DownMins) / t.Mins, 3) : null },
                { "outages", r.Runs.Count }, { "downtime", downtime }, { "longest", longest },
                { "avg", t.Recv > 0 ? Math.Round((double)t.Sum / t.Recv, 1) : -1 },
                { "max", t.Max }, { "min", t.Recv > 0 ? t.Min : -1 },
                { "loss", t.Sent > 0 ? Math.Round(100.0 * (t.Sent - t.Recv) / t.Sent, 2) : 0 },
                { "ongoing", downNow && r.Runs.Count > 0 && r.Runs[r.Runs.Count - 1][1] >= toMin - 1 },
            };
        }

        /// <summary>Historial de una IP: tramos para la gráfica, resumen y lista de caídas.</summary>
        public static Dictionary<string, object> ForTarget(string id, string range, bool downNow)
        {
            long fromMin, toMin; int bucketMin;
            var r = Compute(new HashSet<string> { id }, range, true, out fromMin, out toMin, out bucketMin)[id];
            var summary = SummaryOf(r, toMin, downNow);
            bool ongoing = (bool)summary["ongoing"];
            var buckets = new List<object>();
            for (int i = 0; i < r.Buckets.Length; i++)
            {
                var b = r.Buckets[i];
                long start = (fromMin + (long)i * bucketMin) * 60000;
                if (b == null) { buckets.Add(new object[] { start, 0, 0, -1, -1, 0 }); continue; }
                buckets.Add(new object[] { start, b.Mins, b.DownMins,
                    b.Recv > 0 ? Math.Round((double)b.Sum / b.Recv, 1) : -1, b.Max,
                    b.Sent > 0 ? Math.Round(100.0 * (b.Sent - b.Recv) / b.Sent, 1) : 0 });
            }
            var outages = new List<object>();
            for (int i = r.Runs.Count - 1; i >= 0; i--)
            {
                var x = r.Runs[i];
                bool live = ongoing && i == r.Runs.Count - 1;
                outages.Add(new Dictionary<string, object> { { "start", x[0] * 60000 }, { "end", live ? (object)null : x[1] * 60000 }, { "minutes", x[1] - x[0] } });
            }
            return new Dictionary<string, object> {
                { "range", range }, { "from", fromMin * 60000 }, { "to", toMin * 60000 }, { "bucket", bucketMin },
                { "summary", summary }, { "buckets", buckets }, { "outages", outages },
            };
        }

        /// <summary>Resumen de disponibilidad de varias IPs (vista "Disponibilidad" y exporte).</summary>
        public static Dictionary<string, Dictionary<string, object>> ForTargets(IEnumerable<string> ids, string range, Func<string, bool> downNow)
        {
            long fromMin, toMin; int bucketMin;
            var set = new HashSet<string>(ids);
            var all = Compute(set, range, false, out fromMin, out toMin, out bucketMin);
            return all.ToDictionary(kv => kv.Key, kv => SummaryOf(kv.Value, toMin, downNow(kv.Key)));
        }

        /// <summary>Datos del informe: resumen y caídas por IP, más la disponibilidad global por día (o por hora).</summary>
        public class ReportData
        {
            public long FromMin, ToMin;
            public bool Daily;
            public Dictionary<string, Dictionary<string, object>> Summary = new Dictionary<string, Dictionary<string, object>>();
            public Dictionary<string, List<long[]>> Runs = new Dictionary<string, List<long[]>>();
            /// <summary>Por tramo: inicio (minuto Unix), minutos vigilados y minutos caídos (todas las IPs).</summary>
            public long[] BucketStart; public long[] BucketMins, BucketDown;
        }

        /// <summary>Informe de [fromMin, toMin): tramos diarios alineados a la medianoche local, u horarios si daily = false.</summary>
        public static ReportData Report(IEnumerable<string> ids, long fromMin, long toMin, bool daily, Func<string, bool> downNow)
        {
            var set = new HashSet<string>(ids);
            int nb; Func<long, int> idx; long[] starts;
            if (daily)
            {
                DateTime first = LocalDate(fromMin), last = LocalDate(Math.Max(fromMin, toMin - 1));
                nb = (int)(last - first).TotalDays + 1;
                starts = Enumerable.Range(0, nb).Select(i => DayStartMinute(first.AddDays(i))).ToArray();
                idx = m => (int)(LocalDate(m) - first).TotalDays;
            }
            else
            {
                long h0 = fromMin / 60 * 60;
                nb = (int)((toMin - h0 + 59) / 60);
                starts = Enumerable.Range(0, nb).Select(i => h0 + i * 60L).ToArray();
                idx = m => (int)((m - h0) / 60);
            }
            var all = Compute(set, fromMin, toMin, Math.Max(1, nb), idx, true);
            var rep = new ReportData { FromMin = fromMin, ToMin = toMin, Daily = daily, BucketStart = starts, BucketMins = new long[nb], BucketDown = new long[nb] };
            foreach (var kv in all)
            {
                rep.Summary[kv.Key] = SummaryOf(kv.Value, toMin, downNow(kv.Key));
                rep.Runs[kv.Key] = kv.Value.Runs;
                for (int i = 0; i < nb && i < kv.Value.Buckets.Length; i++)
                {
                    var b = kv.Value.Buckets[i]; if (b == null) continue;
                    rep.BucketMins[i] += b.Mins; rep.BucketDown[i] += b.DownMins;
                }
            }
            return rep;
        }
    }
}

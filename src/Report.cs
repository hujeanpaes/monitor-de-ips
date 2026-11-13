using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MonitorIPs
{
    /// <summary>
    /// Informe de disponibilidad en PDF: se construye un HTML autónomo (A4, tema claro) y se imprime a PDF con el
    /// Edge/Chrome que ya usa la aplicación en modo headless. Si no hay navegador o falla, se deja el HTML para
    /// imprimirlo a mano (Ctrl+P → Guardar como PDF). Sin librerías externas.
    /// </summary>
    public static class Report
    {
        public class Options
        {
            public string Title, PeriodLabel, Scope, Version, Browser, OutDir, FileBase;
            public long FromMin, ToMin;
            public bool Daily;
            public List<Dictionary<string, object>> Targets;
            public Func<string, bool> DownNow;
        }

        public class Result { public string Path; public bool Pdf; }

        const int MaxOutages = 300;
        static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

        public static Result Generate(Options o)
        {
            var data = History.Report(o.Targets.Select(t => (string)t["id"]), o.FromMin, o.ToMin, o.Daily, o.DownNow);
            string html = BuildHtml(o, data);
            Directory.CreateDirectory(o.OutDir);

            string pdf = Path.Combine(o.OutDir, o.FileBase + ".pdf");
            if (o.Browser != null && ToPdf(o.Browser, html, pdf)) return new Result { Path = pdf, Pdf = true };

            string file = Path.Combine(o.OutDir, o.FileBase + ".html");
            File.WriteAllText(file, html, new UTF8Encoding(false));
            return new Result { Path = file, Pdf = false };
        }

        /// <summary>Imprime el HTML a PDF con el navegador en modo headless (perfil temporal propio, máx. 60 s).</summary>
        static bool ToPdf(string browser, string html, string pdf)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "MonitorIPs-informe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmp);
                string src = Path.Combine(tmp, "informe.html");
                File.WriteAllText(src, html, new UTF8Encoding(false));
                if (File.Exists(pdf)) File.Delete(pdf);
                string args = "--headless=new --disable-gpu --no-first-run --no-default-browser-check --disable-extensions " +
                              "--disable-sync --no-pdf-header-footer --user-data-dir=\"" + Path.Combine(tmp, "perfil") + "\" " +
                              "--print-to-pdf=\"" + pdf + "\" \"" + new Uri(src).AbsoluteUri + "\"";
                using (var p = Process.Start(new ProcessStartInfo(browser, args) { UseShellExecute = false, CreateNoWindow = true }))
                {
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return false; }
                }
                return File.Exists(pdf) && new FileInfo(pdf).Length > 1000;
            }
            catch { return false; }
            finally
            {
                for (int i = 0; i < 5; i++)   // el navegador puede tardar un instante en soltar el perfil
                {
                    try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); break; }
                    catch { System.Threading.Thread.Sleep(400); }
                }
            }
        }

        // ------------------------------------------------------------------ HTML

        class Line
        {
            public string Id, Name, Sub, Kind, Group;
            public long Minutes, DownMinutes, Outages, Downtime, Longest;
            public double? Avail; public double Avg, Max, Loss;
        }

        static string BuildHtml(Options o, History.ReportData d)
        {
            long span = Math.Max(1, o.ToMin - o.FromMin);
            var lines = o.Targets.Select(t =>
            {
                var s = d.Summary[(string)t["id"]];
                string check = (string)t["check"];
                return new Line
                {
                    Id = (string)t["id"],
                    Name = Str(t["name"]) != "" ? Str(t["name"]) : (check == "http" ? Str(t["url"]) : Str(t["host"])),
                    Sub = check == "http" ? Str(t["url"]) : check == "tcp" ? Str(t["host"]) + ":" + Str(t["port"]) : Str(t["host"]),
                    Kind = check == "http" ? "Web" : check == "tcp" ? "TCP" : "Ping",
                    Group = Str(t["group"]),
                    Minutes = Convert.ToInt64(s["minutes"]), DownMinutes = Convert.ToInt64(s["downMinutes"]),
                    Outages = Convert.ToInt64(s["outages"]), Downtime = Convert.ToInt64(s["downtime"]), Longest = Convert.ToInt64(s["longest"]),
                    Avail = s["availability"] == null ? (double?)null : Convert.ToDouble(s["availability"]),
                    Avg = Convert.ToDouble(s["avg"]), Max = Convert.ToDouble(s["max"]), Loss = Convert.ToDouble(s["loss"]),
                };
            }).ToList();

            long totMin = lines.Sum(l => l.Minutes), totDown = lines.Sum(l => l.DownMinutes);
            double? global = totMin > 0 ? 100.0 * (totMin - totDown) / totMin : (double?)null;
            long outages = lines.Sum(l => l.Outages), downtime = lines.Sum(l => l.Downtime);
            var withData = lines.Where(l => l.Avail != null).ToList();
            var worst = withData.OrderBy(l => l.Avail).ThenByDescending(l => l.Downtime).FirstOrDefault();
            var lat = withData.Where(l => l.Avg >= 0).ToList();
            double avgLat = lat.Sum(l => l.Minutes) > 0 ? lat.Sum(l => l.Avg * l.Minutes) / lat.Sum(l => l.Minutes) : -1;
            double coverage = lines.Count > 0 ? 100.0 * totMin / (span * lines.Count) : 0;

            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>").Append(H(o.Title)).Append("</title><style>").Append(Css).Append("</style></head><body>");

            // cabecera
            sb.Append("<header><div class=\"brand\"><div class=\"logo\"><svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"#fff\" stroke-width=\"2.6\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><path d=\"M2 12h4l3-8 4 16 3-8h6\"/></svg></div>")
              .Append("<div><div class=\"kicker\">Informe de disponibilidad</div><h1>").Append(H(o.Title)).Append("</h1></div></div>")
              .Append("<div class=\"meta\"><div><b>Periodo:</b> ").Append(H(o.PeriodLabel)).Append("</div>")
              .Append("<div><b>Alcance:</b> ").Append(H(o.Scope)).Append(" · ").Append(lines.Count).Append(lines.Count == 1 ? " comprobación" : " comprobaciones").Append("</div>")
              .Append("<div><b>Generado:</b> ").Append(H(DateTime.Now.ToString("d 'de' MMMM 'de' yyyy, HH:mm", Es))).Append(" · Monitor de IPs ").Append(H(o.Version)).Append("</div></div></header>");

            if (totMin == 0)
            {
                sb.Append("<div class=\"nodata\">Sin datos en este periodo.<small>No hay historial guardado para estas comprobaciones entre las fechas elegidas (el monitor estaba cerrado, en pausa o las comprobaciones aún no existían).</small></div>");
                sb.Append(Notes(coverage)).Append("</body></html>");
                return sb.ToString();
            }

            // cifras
            sb.Append("<section class=\"kpis\">")
              .Append(Kpi(Pct(global), "Disponibilidad global", Tone(global), "ponderada por minutos vigilados"))
              .Append(Kpi(outages.ToString("N0", Es), outages == 1 ? "Caída" : "Caídas", outages == 0 ? "ok" : "", ""))
              .Append(Kpi(Dur(downtime), "Tiempo caído total", downtime == 0 ? "ok" : "", "suma de todas las comprobaciones"))
              .Append(Kpi(worst != null ? Pct(worst.Avail) : "—", "Peor comprobación", Tone(worst != null ? worst.Avail : null), worst != null ? worst.Name : ""))
              .Append(Kpi(avgLat >= 0 ? Ms(avgLat) : "—", "Latencia media", "", "respuestas recibidas"))
              .Append("</section>");

            // gráfica
            sb.Append("<section class=\"block\"><h2>Disponibilidad ").Append(d.Daily ? "por día" : "por hora").Append("</h2>").Append(Chart(d)).Append("</section>");

            // tabla por comprobación (por grupo, de peor a mejor)
            sb.Append("<section class=\"block\"><h2>Detalle por comprobación</h2><table class=\"tbl\"><thead><tr>")
              .Append("<th>Comprobación</th><th>Tipo</th><th class=\"av\">Disponibilidad</th><th class=\"r\">Caídas</th><th class=\"r\">Tiempo caído</th><th class=\"r\">Más larga</th><th class=\"r\">Lat. media</th><th class=\"r\">Lat. máx.</th><th class=\"r\">Pérdida</th></tr></thead><tbody>");
            var groups = lines.GroupBy(l => l.Group).OrderBy(g => g.Key == "" ? "￿" : g.Key, StringComparer.Create(Es, true)).ToList();
            bool multi = groups.Count > 1 || (groups.Count == 1 && groups[0].Key != "");
            foreach (var g in groups)
            {
                if (multi)
                {
                    long gm = g.Sum(l => l.Minutes), gd = g.Sum(l => l.DownMinutes);
                    sb.Append("<tr class=\"grp\"><td colspan=\"9\">").Append(H(g.Key == "" ? "Sin grupo" : g.Key))
                      .Append("<span>").Append(g.Count()).Append(g.Count() == 1 ? " comprobación" : " comprobaciones")
                      .Append(gm > 0 ? " · " + Pct(100.0 * (gm - gd) / gm) : "").Append("</span></td></tr>");
                }
                foreach (var l in g.OrderBy(l => l.Avail ?? 101).ThenByDescending(l => l.Downtime).ThenBy(l => l.Name, StringComparer.Create(Es, true)))
                {
                    double cov = 100.0 * l.Minutes / span;
                    sb.Append("<tr><td><b>").Append(H(l.Name)).Append("</b><small>").Append(H(l.Sub)).Append("</small></td>")
                      .Append("<td>").Append(l.Kind).Append("</td>");
                    if (l.Avail == null) sb.Append("<td class=\"av\"><span class=\"muted\">sin datos</span></td>");
                    else
                    {
                        double w = Math.Max(2, Math.Min(100, l.Avail.Value));
                        sb.Append("<td class=\"av\"><div class=\"bar\"><i class=\"").Append(Tone(l.Avail)).Append("\" style=\"width:").Append(w.ToString("0.##", CultureInfo.InvariantCulture)).Append("%\"></i></div><b class=\"")
                          .Append(Tone(l.Avail)).Append("\">").Append(Pct(l.Avail)).Append("</b>");
                        if (cov < 95) sb.Append("<small>cobertura ").Append(Math.Floor(cov).ToString("0", Es)).Append(" %</small>");
                        sb.Append("</td>");
                    }
                    sb.Append("<td class=\"r\">").Append(l.Outages == 0 ? "—" : l.Outages.ToString("N0", Es)).Append("</td>")
                      .Append("<td class=\"r\">").Append(Dur(l.Downtime)).Append("</td>")
                      .Append("<td class=\"r\">").Append(Dur(l.Longest)).Append("</td>")
                      .Append("<td class=\"r\">").Append(l.Avg >= 0 ? Ms(l.Avg) : "—").Append("</td>")
                      .Append("<td class=\"r\">").Append(l.Max >= 0 ? Ms(l.Max) : "—").Append("</td>")
                      .Append("<td class=\"r\">").Append(l.Minutes > 0 ? l.Loss.ToString("0.##", Es) + " %" : "—").Append("</td></tr>");
                }
            }
            sb.Append("</tbody></table></section>");

            // registro de caídas
            var all = new List<Tuple<Line, long, long, bool>>();
            foreach (var l in lines)
            {
                var runs = d.Runs[l.Id];
                bool ongoing = Convert.ToBoolean(d.Summary[l.Id]["ongoing"]);
                for (int i = 0; i < runs.Count; i++)
                    all.Add(Tuple.Create(l, runs[i][0], runs[i][1], ongoing && i == runs.Count - 1));
            }
            sb.Append("<section class=\"block\"><h2>Registro de caídas</h2>");
            if (all.Count == 0) sb.Append("<p class=\"muted\">No hubo caídas en este periodo.</p>");
            else
            {
                int total = all.Count;
                if (total > MaxOutages)
                {
                    all = all.OrderByDescending(x => x.Item3 - x.Item2).Take(MaxOutages).ToList();
                    sb.Append("<p class=\"muted\">Hubo ").Append(total.ToString("N0", Es)).Append(" caídas; se muestran las ").Append(MaxOutages).Append(" más largas.</p>");
                }
                sb.Append("<table class=\"tbl log\"><thead><tr><th>Inicio</th><th>Fin</th><th class=\"r\">Duración</th><th>Comprobación</th></tr></thead><tbody>");
                foreach (var x in all.OrderBy(x => x.Item2).ThenBy(x => x.Item1.Name))
                {
                    sb.Append("<tr><td>").Append(When(x.Item2)).Append("</td><td>").Append(x.Item4 ? "<b class=\"bad\">en curso</b>" : When(x.Item3))
                      .Append("</td><td class=\"r\">").Append(Dur(x.Item3 - x.Item2)).Append("</td><td><b>").Append(H(x.Item1.Name)).Append("</b> <small class=\"inl\">")
                      .Append(H(x.Item1.Sub)).Append("</small></td></tr>");
                }
                sb.Append("</tbody></table>");
            }
            sb.Append("</section>");

            sb.Append(Notes(coverage)).Append("</body></html>");
            return sb.ToString();
        }

        static string Notes(double coverage)
        {
            return "<section class=\"notes\"><b>Cómo se calcula.</b> Disponibilidad = minutos sin caída ÷ minutos vigilados. Un minuto cuenta como caído si la comprobación estaba " +
                   "en estado <i>caída</i> (tras los fallos seguidos configurados). Los periodos en pausa o con el monitor cerrado no cuentan: se indican como " +
                   "<i>sin datos</i> o con su cobertura cuando es inferior al 95 %. Cobertura total de datos en el periodo: " + Math.Floor(coverage).ToString("0", Es) +
                   " %. Colores: verde ≥ 99,9 %, ámbar ≥ 99 %, rojo &lt; 99 %.</section>";
        }

        /// <summary>Barras SVG de disponibilidad por tramo, con escala adaptada y línea de objetivo en 99,9 %.</summary>
        static string Chart(History.ReportData d)
        {
            int n = d.BucketStart.Length;
            const double W = 700, Hh = 170, pl = 44, pr = 6, pt = 8, pb = 22;
            var av = new double?[n];
            double lo = 100;
            for (int i = 0; i < n; i++)
            {
                if (d.BucketMins[i] <= 0) continue;
                av[i] = 100.0 * (d.BucketMins[i] - d.BucketDown[i]) / d.BucketMins[i];
                lo = Math.Min(lo, av[i].Value);
            }
            double floor = new[] { 99.9, 99.5, 99, 98, 95, 90, 80, 50, 0 }.First(s => s < lo - 0.0001 || s == 0);
            if (lo >= 100) floor = 99;
            double cw = (W - pl - pr) / Math.Max(1, n), bw = Math.Max(1.5, cw * 0.72);
            Func<double, double> Y = v => pt + (1 - (v - floor) / (100 - floor)) * (Hh - pt - pb);
            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("<svg class=\"chart\" viewBox=\"0 0 ").Append(W.ToString(ic)).Append(" ").Append(Hh.ToString(ic)).Append("\" xmlns=\"http://www.w3.org/2000/svg\">");
            foreach (var v in new[] { floor, (floor + 100) / 2, 100.0 })
            {
                double y = Y(v);
                sb.AppendFormat(ic, "<line x1=\"{0}\" x2=\"{1}\" y1=\"{2:0.#}\" y2=\"{2:0.#}\" stroke=\"#e3e7ee\"/>", pl, W - pr, y)
                  .AppendFormat(ic, "<text x=\"{0}\" y=\"{1:0.#}\" text-anchor=\"end\" class=\"ax\">{2}</text>", pl - 6, y + 3, H(v.ToString(v >= 99 && v < 100 ? "0.#" : "0", Es) + " %"));
            }
            if (floor < 99.9)
            {
                double y = Y(99.9);
                sb.AppendFormat(ic, "<line x1=\"{0}\" x2=\"{1}\" y1=\"{2:0.#}\" y2=\"{2:0.#}\" stroke=\"#16a34a\" stroke-dasharray=\"4 3\" stroke-width=\"1\"/>", pl, W - pr, y)
                  .AppendFormat(ic, "<text x=\"{0}\" y=\"{1:0.#}\" text-anchor=\"end\" class=\"ax ok\">objetivo 99,9 %</text>", W - pr, y < pt + 12 ? y + 11 : y - 4);
            }
            int every = d.Daily ? (n <= 16 ? 1 : n <= 35 ? 2 : (int)Math.Ceiling(n / 16.0)) : (n <= 26 ? 2 : 3);
            for (int i = 0; i < n; i++)
            {
                double x = pl + i * cw + (cw - bw) / 2;
                if (av[i] == null)
                    sb.AppendFormat(ic, "<rect x=\"{0:0.##}\" y=\"{1:0.#}\" width=\"{2:0.##}\" height=\"2\" fill=\"#d5dae3\"/>", x, Hh - pb - 2, bw);
                else
                {
                    double y = Y(Math.Max(floor, av[i].Value));
                    string col = d.BucketDown[i] == 0 ? "#22c55e" : av[i] >= 99.9 ? "#f59e0b" : "#e11d48";
                    sb.AppendFormat(ic, "<rect x=\"{0:0.##}\" y=\"{1:0.#}\" width=\"{2:0.##}\" height=\"{3:0.#}\" rx=\"1.5\" fill=\"{4}\"/>", x, y, bw, Math.Max(1.5, Hh - pb - y), col);
                }
                if (i % every == 0)
                {
                    var t = Util.FromUnixMs(d.BucketStart[i] * 60000);
                    sb.AppendFormat(ic, "<text x=\"{0:0.#}\" y=\"{1}\" text-anchor=\"middle\" class=\"ax\">{2}</text>", x + bw / 2, Hh - 6,
                        d.Daily ? t.ToString(n > 31 ? "d/M" : "%d", Es) : t.ToString("HH", Es) + " h");
                }
            }
            sb.Append("</svg><div class=\"legend\"><span><i style=\"background:#22c55e\"></i>sin caídas</span><span><i style=\"background:#f59e0b\"></i>con caídas, ≥ 99,9 %</span><span><i style=\"background:#e11d48\"></i>&lt; 99,9 %</span><span><i style=\"background:#d5dae3\"></i>sin datos</span></div>");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ formato

        static string Kpi(string value, string label, string tone, string sub)
        {
            return "<div class=\"kpi\"><b class=\"" + tone + "\">" + H(value) + "</b><span>" + H(label) + "</span>" + (sub != "" ? "<small>" + H(sub) + "</small>" : "") + "</div>";
        }

        static string Tone(double? v) { return v == null ? "" : v >= 99.9 ? "ok" : v >= 99 ? "warn" : "bad"; }

        /// <summary>Porcentaje sin redondear hacia arriba (99,996 % no debe mostrarse como 100 % si hubo caídas).</summary>
        static string Pct(double? v)
        {
            if (v == null) return "—";
            double x = v.Value >= 100 ? 100 : Math.Floor(v.Value * 100) / 100;
            return x.ToString(x >= 100 ? "0" : "0.00", Es) + " %";
        }

        static string Ms(double v) { return v < 1 ? "<1 ms" : (v >= 100 ? Math.Round(v).ToString("N0", Es) : v.ToString("0.#", Es)) + " ms"; }

        static string Dur(long minutes)
        {
            if (minutes <= 0) return "—";
            long d = minutes / 1440, h = minutes % 1440 / 60, m = minutes % 60;
            if (d > 0) return d + " d " + h + " h";
            if (h > 0) return m == 0 ? h + " h" : h + " h " + m + " min";
            return m + " min";
        }

        static string When(long minute) { return H(Util.FromUnixMs(minute * 60000).ToString("ddd d MMM, HH:mm", Es)); }

        static string Str(object o) { return o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture); }

        static string H(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        const string Css = @"
@page{size:A4;margin:15mm 14mm 17mm;@bottom-right{content:'Página ' counter(page) ' de ' counter(pages);font:8.5pt 'Segoe UI',sans-serif;color:#7b8496}@bottom-left{content:'Monitor de IPs · Informe de disponibilidad';font:8.5pt 'Segoe UI',sans-serif;color:#7b8496}}
*{box-sizing:border-box;-webkit-print-color-adjust:exact;print-color-adjust:exact}
body{margin:0;font:9.5pt/1.45 'Segoe UI',system-ui,sans-serif;color:#121826}
header{display:flex;justify-content:space-between;align-items:flex-end;gap:20px;padding-bottom:12px;border-bottom:2px solid #4a67f5;margin-bottom:16px}
.brand{display:flex;align-items:center;gap:12px}
.logo{width:36px;height:36px;border-radius:10px;background:linear-gradient(135deg,#4a67f5,#7c5cf0);display:flex;align-items:center;justify-content:center;flex:none}
.kicker{font-size:8pt;text-transform:uppercase;letter-spacing:.09em;color:#4a67f5;font-weight:700}
h1{margin:0;font-size:17pt;font-weight:700;letter-spacing:-.01em}
.meta{text-align:right;font-size:8.5pt;color:#4b5567}
.meta b{color:#121826;font-weight:600}
h2{font-size:11.5pt;margin:0 0 8px;font-weight:700}
.block{margin-top:18px}
.kpis{display:grid;grid-template-columns:1.15fr .8fr 1.1fr 1.15fr .95fr;gap:8px}
.kpi{border:1px solid #e1e6ee;border-radius:10px;padding:9px 11px;background:#f8fafc;break-inside:avoid}
.kpi b{display:block;font-size:15pt;font-weight:750;line-height:1.15;color:#121826;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.kpi span{display:block;font-size:7.5pt;color:#4b5567;font-weight:600;text-transform:uppercase;letter-spacing:.02em;margin-top:2px;white-space:nowrap}
.kpi small{display:block;font-size:7.5pt;color:#7b8496;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.ok{color:#15803d !important}.warn{color:#b45309 !important}.bad{color:#be123c !important}
.chart{width:100%;height:auto;display:block}
.chart .ax{font:7.5pt 'Segoe UI',sans-serif;fill:#7b8496}
.chart .ax.ok{fill:#15803d}
.legend{display:flex;gap:14px;font-size:7.5pt;color:#4b5567;margin-top:4px}
.legend i{display:inline-block;width:9px;height:9px;border-radius:2px;margin-right:5px;vertical-align:-1px}
.tbl{width:100%;border-collapse:collapse;font-size:8.5pt}
.tbl th{text-align:left;font-size:7.5pt;text-transform:uppercase;letter-spacing:.05em;color:#4b5567;font-weight:650;border-bottom:1.5px solid #cfd6e2;padding:5px 6px}
.tbl td{border-bottom:1px solid #e9edf3;padding:5px 6px;vertical-align:middle}
.tbl tr{break-inside:avoid}
.tbl thead{display:table-header-group}
.tbl td small{display:block;color:#7b8496;font-family:Consolas,'Cascadia Mono',monospace;font-size:7.5pt;word-break:break-all}
.tbl td small.inl{display:inline}
.tbl .r{text-align:right;white-space:nowrap}
.tbl .av{width:21%}
.tbl th:first-child{width:27%}
.tbl tr.grp td{background:#eef1f7;font-weight:700;font-size:8.5pt;text-transform:uppercase;letter-spacing:.05em;color:#33405a;border-bottom:0}
.tbl tr.grp td span{font-weight:500;text-transform:none;letter-spacing:0;color:#4b5567;margin-left:8px}
.bar{display:inline-block;width:46%;height:6px;background:#e9edf3;border-radius:3px;overflow:hidden;vertical-align:middle;margin-right:7px}
.bar i{display:block;height:100%;background:#22c55e}
.bar i.warn{background:#f59e0b}.bar i.bad{background:#e11d48}
.av b{font-variant-numeric:tabular-nums}
.av small{font-family:inherit !important;font-size:7pt !important}
.log td{white-space:nowrap;padding-top:3px;padding-bottom:3px}
.log td:last-child{white-space:normal}
.muted{color:#7b8496}
.nodata{margin:40px 0;padding:30px;text-align:center;border:1px dashed #cfd6e2;border-radius:12px;font-size:13pt;font-weight:600}
.nodata small{display:block;font-size:9pt;font-weight:400;color:#7b8496;margin-top:6px}
.notes{margin-top:20px;padding:10px 12px;border-radius:8px;background:#f5f7fa;color:#4b5567;font-size:8pt;break-inside:avoid}
";
    }
}

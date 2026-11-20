using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkyShare.Planner.Core;

namespace SkyShare.Planner.Runtime {

    /// <summary>
    /// Draws the night-plan altitude chart as a PNG: every target's altitude curve on
    /// one plot, dotted where the target is not scheduled and solid while it is being
    /// imaged, vertical lines where the sequence changes target, twilight shading,
    /// the Moon's altitude and illumination, and the minimum-altitude line.
    /// Dark background to suit Discord.
    /// </summary>
    public static class NightChart {
        private const int W = 1200;
        private const double L = 56, R = 20, T = 64, PlotH = 440; // legend goes below the plot
        private const int MaxLegendRows = 12;

        private static readonly string[] Palette =
            { "#4FC3F7", "#FFB74D", "#81C784", "#E57373", "#BA68C8", "#FFF176", "#4DB6AC", "#F06292" };

        /// <summary>The SkyShare plan preview chart.</summary>
        public static byte[] RenderPng(NightPlan plan, NightEstimate est, Site site, double minAltitude, TimeZoneInfo? zone = null) =>
            RenderPng(ChartModels.FromEstimate(plan, est, minAltitude), site, zone);

        /// <summary>Renders on a private STA thread (WPF drawing needs one); safe to call from anywhere.</summary>
        public static byte[] RenderPng(ChartModel model, Site site, TimeZoneInfo? zone = null) {
            byte[]? png = null; Exception? error = null;
            var thread = new Thread(() => {
                try { png = Render(model, site, zone ?? TimeZoneInfo.Local); } catch (Exception e) { error = e; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            if (error != null) throw error;
            return png!;
        }

        private static byte[] Render(ChartModel m, Site site, TimeZoneInfo zone) {
            var est = m.Window;
            var series = m.Series;
            var from = est.Sunset.AddMinutes(-30);
            var to = est.Sunrise.AddMinutes(30);
            var rows = Math.Min(series.Count, MaxLegendRows + 1);
            var H = (int)(T + PlotH + 34 + rows * 18 + 12);
            double plotW = W - L - R, plotH = PlotH;
            double X(DateTime t) => L + (t - from).TotalMinutes / (to - from).TotalMinutes * plotW;
            double Y(double alt) => T + (1 - Math.Clamp(alt, 0, 90) / 90) * plotH;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) {
                dc.DrawRectangle(Brush("#16181f"), null, new Rect(0, 0, W, H));

                // Sky: shade by sun altitude, in 5-minute strips.
                for (var t = from; t < to; t = t.AddMinutes(5)) {
                    var sun = NightEstimator.SunAltitude(site, zone, t.AddMinutes(2.5));
                    var c = sun > -0.833 ? "#3a3f50" : sun > -6 ? "#30354a" : sun > -12 ? "#282c3f" : sun > -18 ? "#212435" : "#1a1c28";
                    dc.DrawRectangle(Brush(c), null, new Rect(X(t), T, X(t.AddMinutes(5)) - X(t) + 0.6, plotH));
                }

                // Grid: altitude every 15°, time every hour.
                var grid = new Pen(Brush("#3c4155"), 1) { DashStyle = new DashStyle(new double[] { 2, 4 }, 0) };
                for (int a = 0; a <= 90; a += 15) {
                    dc.DrawLine(grid, new Point(L, Y(a)), new Point(L + plotW, Y(a)));
                    Text(dc, $"{a}°", L - 6, Y(a), 12, "#9aa0b4", align: 1, vcenter: true);
                }
                for (var h = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0).AddHours(1); h < to; h = h.AddHours(1)) {
                    dc.DrawLine(grid, new Point(X(h), T), new Point(X(h), T + plotH));
                    Text(dc, h.ToString("HH:mm"), X(h), T + plotH + 6, 12, "#9aa0b4", align: 0);
                }

                // Twilight markers.
                Text(dc, $"dusk {est.Dusk:HH:mm}", X(est.Dusk) - 4, T + plotH - 18, 11, "#7d8398", align: 1);
                Text(dc, $"dawn {est.Dawn:HH:mm}", X(est.Dawn) + 4, T + plotH - 18, 11, "#7d8398");

                // Minimum altitude.
                if (m.MinAltitude is { } minAlt) {
                    var limit = new Pen(Brush("#E57373"), 1.2) { DashStyle = new DashStyle(new double[] { 6, 4 }, 0) };
                    dc.DrawLine(limit, new Point(L, Y(minAlt)), new Point(L + plotW, Y(minAlt)));
                    Text(dc, $"stop below {minAlt:0}°", L + plotW - 4, Y(minAlt) - 16, 11, "#E57373", align: 1);
                }

                // Moon: pale dash-dot curve, labelled with its illumination at its highest point.
                var moon = new List<(DateTime Time, double Altitude)>();
                for (var t = from; t <= to; t = t.AddMinutes(4)) moon.Add((t, NightEstimator.MoonAltitude(site, zone, t)));
                var moonPen = new Pen(Brush("#cfd3df"), 1.4) { DashStyle = new DashStyle(new double[] { 6, 3, 1, 3 }, 0), DashCap = PenLineCap.Round };
                Polyline(dc, moonPen, moon, X, Y);
                var top = moon.MaxBy(p => p.Altitude);
                if (top.Altitude > 0) {
                    var lit = NightEstimator.MoonIllumination(zone, top.Time);
                    var lx = Math.Clamp(X(top.Time), L + 30, L + plotW - 30);
                    Text(dc, $"Moon {lit * 100:0}%", lx, Y(top.Altitude) - 18, 11, "#cfd3df", align: 0);
                }

                // Altitude curves: dotted everywhere, solid while imaged.
                for (int i = 0; i < series.Count; i++) {
                    var sr = series[i];
                    var color = Brush(Palette[i % Palette.Length]);
                    var pts = NightEstimator.AltitudeCurve(sr.RaHours, sr.DecDeg, site, from, to, 4, zone);
                    var dotted = new Pen(color, 1.6) { DashStyle = new DashStyle(new double[] { 1, 2.5 }, 0), DashCap = PenLineCap.Round };
                    Polyline(dc, dotted, pts, X, Y);

                    var solid = new Pen(color, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    foreach (var g in sr.Imaging) {
                        var seg = NightEstimator.AltitudeCurve(sr.RaHours, sr.DecDeg, site, g.Start, g.End, 1, zone).ToList();
                        seg.Add((g.End, NightEstimator.Altitude(sr.RaHours, sr.DecDeg, site, zone, g.End)));
                        Polyline(dc, solid, seg, X, Y);
                    }
                }

                // Vertical lines where the sequence switches target.
                var change = new Pen(Brush("#c8ccd8"), 1) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
                var starts = series.SelectMany((sr, i) => sr.Imaging.Select(g => (g.Start, i))).OrderBy(x => x.Start).ToList();
                var rowEnds = new double[4];   // labels go in the first row they don't overlap
                var seen = new HashSet<int>();
                foreach (var (s0, i) in starts) {
                    dc.DrawLine(change, new Point(X(s0), T - 4), new Point(X(s0), T + plotH));
                    var label = seen.Add(i) ? $"{i + 1}. {series[i].Name}  {s0:HH:mm}" : $"{i + 1}  {s0:HH:mm}"; // name on first visit only
                    var x0 = X(s0) + 4;
                    var w = Measure(label, 12, false);
                    if (x0 + w > W - R) x0 = W - R - w;
                    var r = Array.FindIndex(rowEnds, e => e < x0);
                    if (r < 0) r = Array.IndexOf(rowEnds, rowEnds.Min());
                    rowEnds[r] = x0 + w + 6;
                    Text(dc, label, x0, T + 4 + r * 16, 12, Palette[i % Palette.Length]);
                }
                if (m.Finish is { } fin) {
                    dc.DrawLine(change, new Point(X(fin), T - 4), new Point(X(fin), T + plotH));
                    Text(dc, $"done {fin:HH:mm}", X(fin) + 4, T + plotH - 36, 11, "#c8ccd8");
                }

                // Title.
                Text(dc, m.Title, L, 14, 20, "#e8eaf0", bold: true);
                Text(dc, m.Subtitle, L, 40, 12, "#9aa0b4");

                // Legend: swatch, name, [completion bar], description.
                double ly = T + plotH + 30;
                var names = series.Take(MaxLegendRows + 1).Select((sr, i) => $"{i + 1}. {sr.Name}").ToList();
                var nameW = Math.Min(380, names.Select(n => Measure(n, 13, true)).DefaultIfEmpty(0).Max());
                var hasBars = series.Any(sr => sr.ProgressBefore != null);
                double barX = L + 26 + nameW + 14, barW = 100;
                double descX = hasBars ? barX + barW + 12 : Math.Max(L + 280, barX);
                for (int i = 0; i < series.Count; i++) {
                    var sr = series[i];
                    var y = ly + i * 18;
                    if (i == MaxLegendRows && series.Count > MaxLegendRows + 1) { Text(dc, $"… {series.Count - i} more", L, y, 12, "#9aa0b4"); break; }
                    var color = Palette[i % Palette.Length];
                    dc.DrawRectangle(Brush(color), null, new Rect(L, y + 4, 18, 8));
                    Text(dc, names[i], L + 26, y, 13, "#e8eaf0", bold: true);
                    if (sr.ProgressBefore is { } before) {
                        var after = Math.Max(before, sr.ProgressAfter ?? before);
                        var bar = new Rect(barX, y + 3, barW, 10);
                        dc.DrawRectangle(Brush("#2a2e3d"), new Pen(Brush("#4a4f63"), 1), bar);
                        dc.DrawRectangle(Brush(color), null, new Rect(barX, y + 3, barW * Math.Clamp(before, 0, 100) / 100, 10));
                        var gain = Brush(color).Clone(); gain.Opacity = 0.45; gain.Freeze();
                        dc.DrawRectangle(gain, null, new Rect(barX + barW * Math.Clamp(before, 0, 100) / 100, y + 3,
                                                              barW * (Math.Clamp(after, 0, 100) - Math.Clamp(before, 0, 100)) / 100, 10));
                    }
                    var maxW = W - R - descX;
                    Text(dc, Fit(sr.Legend, 13, maxW), descX, y, 13, sr.Warn ? "#E57373" : "#c8ccd8");
                }
            }

            var bmp = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        /// <summary>Draws the curve, breaking it where the target is below the horizon.</summary>
        private static void Polyline(DrawingContext dc, Pen pen, IEnumerable<(DateTime Time, double Altitude)> pts,
                                     Func<DateTime, double> x, Func<double, double> y) {
            // One path per stretch above the horizon, so dash patterns run on instead of restarting every point.
            var run = new List<Point>();
            void Flush() {
                if (run.Count >= 2) {
                    var g = new StreamGeometry();
                    using (var ctx = g.Open()) {
                        ctx.BeginFigure(run[0], false, false);
                        ctx.PolyLineTo(run.Skip(1).ToList(), true, true);
                    }
                    g.Freeze();
                    dc.DrawGeometry(null, pen, g);
                }
                run.Clear();
            }
            foreach (var (t, a) in pts) {
                if (a < 0) { Flush(); continue; }
                run.Add(new Point(x(t), y(a)));
            }
            Flush();
        }

        private static FormattedText Format(string s, double size, bool bold, string color = "#ffffff") =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                size, Brush(color), 1.0);

        private static double Measure(string s, double size, bool bold) => Format(s, size, bold).Width;

        /// <summary>Shortens text with "…" so it fits in maxWidth.</summary>
        private static string Fit(string s, double size, double maxWidth) {
            if (Measure(s, size, false) <= maxWidth) return s;
            var n = s.Length;
            while (n > 1 && Measure(s[..n] + "…", size, false) > maxWidth) n--;
            return s[..n] + "…";
        }

        private static void Text(DrawingContext dc, string s, double x, double y, double size, string color,
                                 int align = -1, bool bold = false, bool vcenter = false) {
            var ft = Format(s, size, bold, color);
            if (align == 0) x -= ft.Width / 2; else if (align == 1) x -= ft.Width;
            if (vcenter) y -= ft.Height / 2;
            dc.DrawText(ft, new Point(x, y));
        }

        private static SolidColorBrush Brush(string hex) {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }
}

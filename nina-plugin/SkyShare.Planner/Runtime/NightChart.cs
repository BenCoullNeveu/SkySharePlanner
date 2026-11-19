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
    /// and the minimum-altitude line. Dark background to suit Discord.
    /// </summary>
    public static class NightChart {
        private const int W = 1200;
        private const double L = 56, R = 20, T = 64, PlotH = 440; // legend goes below the plot
        private const int MaxLegendRows = 12;

        private static readonly string[] Palette =
            { "#4FC3F7", "#FFB74D", "#81C784", "#E57373", "#BA68C8", "#FFF176", "#4DB6AC", "#F06292" };

        /// <summary>Renders on a private STA thread (WPF drawing needs one); safe to call from anywhere.</summary>
        public static byte[] RenderPng(NightPlan plan, NightEstimate est, Site site, double minAltitude, TimeZoneInfo? zone = null) {
            byte[]? png = null; Exception? error = null;
            var thread = new Thread(() => {
                try { png = Render(plan, est, site, minAltitude, zone ?? TimeZoneInfo.Local); } catch (Exception e) { error = e; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            if (error != null) throw error;
            return png!;
        }

        private static byte[] Render(NightPlan plan, NightEstimate est, Site site, double minAlt, TimeZoneInfo zone) {
            var from = est.Sunset.AddMinutes(-30);
            var to = est.Sunrise.AddMinutes(30);
            var rows = Math.Min(est.Targets.Count, MaxLegendRows + 1);
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
                var limit = new Pen(Brush("#E57373"), 1.2) { DashStyle = new DashStyle(new double[] { 6, 4 }, 0) };
                dc.DrawLine(limit, new Point(L, Y(minAlt)), new Point(L + plotW, Y(minAlt)));
                Text(dc, $"stop below {minAlt:0}°", L + plotW - 4, Y(minAlt) - 16, 11, "#E57373", align: 1);

                // Altitude curves: dotted everywhere, solid while imaged.
                for (int i = 0; i < plan.Targets.Count; i++) {
                    var color = Brush(Palette[i % Palette.Length]);
                    var pts = NightEstimator.AltitudeCurve(plan.Targets[i], site, from, to, 4, zone);
                    var dotted = new Pen(color, 1.6) { DashStyle = new DashStyle(new double[] { 1, 2.5 }, 0), DashCap = PenLineCap.Round };
                    Polyline(dc, dotted, pts, X, Y);

                    var e = est.Targets[i];
                    if (!e.Skipped && e.ImagingStart is { } s0 && e.End is { } s1) {
                        var seg = NightEstimator.AltitudeCurve(plan.Targets[i], site, s0, s1, 1, zone).ToList();
                        seg.Add((s1, NightEstimator.AltitudeCurve(plan.Targets[i], site, s1, s1, 1, zone)[0].Altitude));
                        Polyline(dc, new Pen(color, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, seg, X, Y);
                    }
                }

                // Vertical lines where the sequence switches target.
                var change = new Pen(Brush("#c8ccd8"), 1) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
                var row = 0;
                for (int i = 0; i < est.Targets.Count; i++) {
                    var e = est.Targets[i];
                    if (e.Skipped || e.ImagingStart is not { } s0) continue;
                    dc.DrawLine(change, new Point(X(s0), T - 4), new Point(X(s0), T + plotH));
                    Text(dc, $"{i + 1}. {e.Name}  {s0:HH:mm}", X(s0) + 4, T + 4 + (row++ % 3) * 16, 12, Palette[i % Palette.Length]);
                }
                if (est.Finish is { } fin) {
                    dc.DrawLine(change, new Point(X(fin), T - 4), new Point(X(fin), T + plotH));
                    Text(dc, $"done {fin:HH:mm}", X(fin) + 4, T + plotH - 36, 11, "#c8ccd8");
                }

                // Title.
                var title = $"Night plan {est.Night:yyyy-MM-dd}" + (string.IsNullOrWhiteSpace(plan.Client) ? "" : $" — {plan.Client}");
                Text(dc, title, L, 14, 20, "#e8eaf0", bold: true);
                Text(dc, "clear-sky estimate · solid = imaging · dotted = not scheduled", L, 40, 12, "#9aa0b4");

                // Legend.
                double ly = T + plotH + 30;
                for (int i = 0; i < est.Targets.Count; i++) {
                    var e = est.Targets[i];
                    var y = ly + i * 18;
                    if (i == MaxLegendRows && est.Targets.Count > MaxLegendRows + 1) { Text(dc, $"… {est.Targets.Count - i} more", L, y, 12, "#9aa0b4"); break; }
                    dc.DrawRectangle(Brush(Palette[i % Palette.Length]), null, new Rect(L, y + 4, 18, 8));
                    Text(dc, $"{i + 1}. {e.Name}", L + 26, y, 13, "#e8eaf0", bold: true);
                    Text(dc, NightSummary.Describe(e), L + 280, y, 13, e.Skipped ? "#E57373" : "#c8ccd8");
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
            Point? prev = null;
            foreach (var (t, a) in pts) {
                if (a < 0) { prev = null; continue; }
                var p = new Point(x(t), y(a));
                if (prev is { } q) dc.DrawLine(pen, q, p);
                prev = p;
            }
        }

        private static void Text(DrawingContext dc, string s, double x, double y, double size, string color,
                                 int align = -1, bool bold = false, bool vcenter = false) {
            var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                size, Brush(color), 1.0);
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

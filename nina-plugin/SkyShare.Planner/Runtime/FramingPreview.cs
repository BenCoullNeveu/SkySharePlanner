using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using SkyShare.Planner.Core;

namespace SkyShare.Planner.Runtime {

    /// <summary>
    /// "What we should see": one sky-survey cutout per target (colour DSS2 from CDS
    /// hips2fits, falling back to its mirror and then NASA SkyView's DSS2 red; always
    /// north up, east left) with the camera's frame drawn at the planned
    /// rotation, laid out as a grid in one JPEG for Discord.
    ///
    /// Only target coordinates are sent to the survey services. Cutouts are cached under
    /// %LOCALAPPDATA%\SkyShare Planner\survey-cache, so a target is downloaded once.
    /// </summary>
    public static class FramingPreview {
        private const int Tile = 400, Label = 40, Gap = 10, Pad = 14, MaxColumns = 3, MaxTargets = 9;
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        private static readonly string CacheDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyShare Planner", "survey-cache");

        /// <summary>Field of view from the profile: focal length, pixel size, and the sensor size (camera if connected, else the framing assistant's).</summary>
        public static FieldOfView? FieldOfView(IProfileService profile, ICameraMediator? camera) {
            var p = profile.ActiveProfile;
            int w = p.FramingAssistantSettings.CameraWidth, h = p.FramingAssistantSettings.CameraHeight;
            var cam = camera?.GetInfo();
            if (cam is { Connected: true, XSize: > 0, YSize: > 0 }) { w = cam.XSize; h = cam.YSize; }
            return Framing.FieldOfView(p.TelescopeSettings.FocalLength, p.CameraSettings.PixelSize, w, h);
        }

        /// <summary>The grid as JPEG bytes, or null when there is nothing to show. Never throws.</summary>
        public static async Task<byte[]?> Render(IReadOnlyList<FramingTarget> targets, FieldOfView? fov) {
            try {
                if (fov is null) {
                    Logger.Warning("[SkyShare] framing preview skipped: focal length, pixel size or sensor size missing in the profile");
                    return null;
                }
                var list = targets.Take(MaxTargets).ToList();
                if (list.Count == 0) return null;
                var cutout = Framing.CutoutDeg(fov);
                var images = await Task.WhenAll(list.Select(t => Fetch(t, cutout)));
                return RenderOnSta(() => Draw(list, images, fov, cutout, targets.Count));
            } catch (Exception e) {
                Logger.Warning($"[SkyShare] framing preview failed: {e.Message}");
                return null;
            }
        }

        /// <summary>The first source that answers with an image (cache first); null if none do.</summary>
        private static async Task<(byte[] Data, string Survey)?> Fetch(FramingTarget t, double cutoutDeg) {
            var errors = new List<string>();
            foreach (var (survey, url) in Framing.SurveySources(t.RaHours, t.DecDeg, cutoutDeg, Tile)) {
                var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..16];
                var path = Path.Combine(CacheDir, key + ".jpg");
                try {
                    if (File.Exists(path)) return (await File.ReadAllBytesAsync(path), survey);
                    using var resp = await Http.GetAsync(url);
                    var type = resp.Content.Headers.ContentType?.MediaType ?? "";
                    if (!resp.IsSuccessStatusCode || !type.StartsWith("image/")) {
                        errors.Add($"{new Uri(url).Host}: {(int)resp.StatusCode} {type}");
                        continue;
                    }
                    var bytes = await resp.Content.ReadAsByteArrayAsync();
                    Directory.CreateDirectory(CacheDir);
                    await File.WriteAllBytesAsync(path, bytes);
                    return (bytes, survey);
                } catch (Exception e) {
                    errors.Add($"{new Uri(url).Host}: {e.Message}");
                }
            }
            Logger.Warning($"[SkyShare] no survey image for {t.Name}: {string.Join("; ", errors)}");
            return null;
        }

        private static byte[] RenderOnSta(Func<byte[]> draw) {
            byte[]? result = null; Exception? error = null;
            var thread = new Thread(() => { try { result = draw(); } catch (Exception e) { error = e; } });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            if (error != null) throw error;
            return result!;
        }

        private static byte[] Draw(IReadOnlyList<FramingTarget> list, (byte[] Data, string Survey)?[] images, FieldOfView fov, double cutout, int total) {
            int cols = Math.Min(MaxColumns, list.Count), rows = (list.Count + cols - 1) / cols;
            int header = 44;
            int W = Pad * 2 + cols * Tile + (cols - 1) * Gap;
            int H = header + Pad + rows * (Tile + Label) + (rows - 1) * Gap + Pad;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) {
                dc.DrawRectangle(Brush("#16181f"), null, new Rect(0, 0, W, H));
                Text(dc, "Framing preview", Pad, 10, 18, "#e8eaf0", bold: true);
                Text(dc, $"sky survey · north up, east left · frame {fov}" + (total > list.Count ? $" · first {list.Count} of {total} targets" : ""),
                     Pad + 170, 15, 12, "#9aa0b4");

                for (int i = 0; i < list.Count; i++) {
                    var t = list[i];
                    double x = Pad + (i % cols) * (Tile + Gap), y = header + Pad + (i / cols) * (Tile + Label + Gap);
                    var tile = new Rect(x, y, Tile, Tile);
                    if (images[i] is { } got && Decode(got.Data) is { } img) {
                        dc.DrawImage(img, tile);
                    } else {
                        dc.DrawRectangle(Brush("#22252f"), null, tile);
                        Text(dc, "no survey image", x + Tile / 2.0, y + Tile / 2.0 - 8, 13, "#9aa0b4", align: 0);
                    }

                    // Sensor outline at the planned rotation, with a tick on the sensor's top edge.
                    var c = Framing.SensorCorners(fov, t.RotationDeg, cutout, Tile);
                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open()) {
                        ctx.BeginFigure(new Point(x + c[0].X, y + c[0].Y), false, true);
                        ctx.PolyLineTo(c.Skip(1).Select(p => new Point(x + p.X, y + p.Y)).ToList(), true, true);
                    }
                    geo.Freeze();
                    dc.PushClip(new RectangleGeometry(tile));
                    dc.DrawGeometry(null, new Pen(Brush("#FFB74D"), 2), geo);
                    var (mx, my) = Framing.TopMid(c);
                    dc.DrawEllipse(Brush("#FFB74D"), null, new Point(x + mx, y + my), 4, 4);
                    foreach (var (dx, dy, col) in new[] { (1.0, 1.0, "#000000"), (0.0, 0.0, "#ffffff") }) { // shadow, then text
                        Text(dc, "N", x + Tile / 2.0 + dx, y + 4 + dy, 13, col, bold: true, align: 0);
                        Text(dc, "E", x + 6 + dx, y + Tile / 2.0 - 8 + dy, 13, col, bold: true);
                    }
                    dc.Pop();

                    Text(dc, $"{i + 1}. {t.Name}", x, y + Tile + 4, 14, "#e8eaf0", bold: true);
                    Text(dc, $"rotation {t.RotationDeg:0.#}°" + (string.IsNullOrWhiteSpace(t.Note) ? "" : $" · {t.Note}") +
                             (images[i] is { } src ? $" · {src.Survey}" : ""),
                         x, y + Tile + 22, 12, "#9aa0b4");
                }
            }

            var bmp = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new JpegBitmapEncoder { QualityLevel = 88 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        private static BitmapSource? Decode(byte[] bytes) {
            try {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = new MemoryStream(bytes);
                img.EndInit();
                img.Freeze();
                return img;
            } catch {
                return null;
            }
        }

        private static void Text(DrawingContext dc, string s, double x, double y, double size, string color, bool bold = false, int align = -1) {
            var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                size, Brush(color), 1.0);
            if (align == 0) x -= ft.Width / 2;
            dc.DrawText(ft, new Point(x, y));
        }

        private static SolidColorBrush Brush(string hex) {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }
}

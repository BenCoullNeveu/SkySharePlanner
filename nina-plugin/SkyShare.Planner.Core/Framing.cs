using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkyShare.Planner.Core {

    /// <summary>A target to show in the framing preview. Rotation is NINA's position angle (degrees, east of north).</summary>
    public sealed record FramingTarget(string Name, double RaHours, double DecDeg, double RotationDeg, string? Note = null);

    /// <summary>Camera field of view in degrees (width = sensor's long axis at rotation 0, east-west).</summary>
    public sealed record FieldOfView(double WidthDeg, double HeightDeg, double ArcsecPerPixel) {
        public override string ToString() =>
            $"{WidthDeg * 60:0.#}′ × {HeightDeg * 60:0.#}′ at {ArcsecPerPixel:0.##}″/px";
    }

    /// <summary>
    /// Geometry for framing previews: the camera's field of view from the profile,
    /// the sky-survey cutout to fetch, and where the sensor outline goes on it.
    /// Survey cutouts are north up, east left (standard sky view), like NINA's framing assistant.
    /// </summary>
    public static class Framing {
        /// <summary>The survey cutout is this many times the sensor diagonal, so every rotation fits with some context.</summary>
        public const double Margin = 1.35;

        public static FieldOfView? FieldOfView(double focalLengthMm, double pixelSizeUm, int widthPx, int heightPx) {
            if (focalLengthMm <= 0 || pixelSizeUm <= 0 || widthPx <= 0 || heightPx <= 0) return null;
            var scale = 206.265 * pixelSizeUm / focalLengthMm; // arcsec per pixel
            return new FieldOfView(widthPx * scale / 3600, heightPx * scale / 3600, scale);
        }

        /// <summary>Width of the survey cutout, in degrees.</summary>
        public static double CutoutDeg(FieldOfView fov) =>
            Math.Sqrt(fov.WidthDeg * fov.WidthDeg + fov.HeightDeg * fov.HeightDeg) * Margin;

        /// <summary>CDS hips2fits request for a square colour DSS2 cutout centred on the target.</summary>
        public static string SurveyUrl(double raHours, double decDeg, double cutoutDeg, int sizePx, string host = "alasky.cds.unistra.fr") {
            var c = CultureInfo.InvariantCulture;
            return $"https://{host}/hips-image-services/hips2fits" +
                   "?hips=CDS%2FP%2FDSS2%2Fcolor" +
                   $"&width={sizePx}&height={sizePx}&projection=TAN&coordsys=icrs&format=jpg" +
                   $"&fov={cutoutDeg.ToString("0.#####", c)}" +
                   $"&ra={(raHours * 15).ToString("0.######", c)}&dec={decDeg.ToString("0.######", c)}";
        }

        /// <summary>NASA SkyView request for the same cutout in DSS2 Red (black and white). Also north up, east left.</summary>
        public static string SkyViewUrl(double raHours, double decDeg, double cutoutDeg, int sizePx) {
            var c = CultureInfo.InvariantCulture;
            return "https://skyview.gsfc.nasa.gov/current/cgi/runquery.pl?Survey=DSS2+Red&Coordinates=J2000&Projection=Tan" +
                   $"&Position={(raHours * 15).ToString("0.######", c)},{decDeg.ToString("0.######", c)}" +
                   $"&Size={cutoutDeg.ToString("0.#####", c)}&Pixels={sizePx}&Return=JPEG&Scaling=HistEq";
        }

        /// <summary>Where to get a cutout, in order of preference: CDS colour, its mirror, then NASA SkyView.</summary>
        public static IReadOnlyList<(string Survey, string Url)> SurveySources(double raHours, double decDeg, double cutoutDeg, int sizePx) => new[] {
            ("DSS2 colour", SurveyUrl(raHours, decDeg, cutoutDeg, sizePx)),
            ("DSS2 colour", SurveyUrl(raHours, decDeg, cutoutDeg, sizePx, "alaskybis.cds.unistra.fr")),
            ("DSS2 red", SkyViewUrl(raHours, decDeg, cutoutDeg, sizePx)),
        };

        /// <summary>
        /// Sensor outline on a square cutout of sizePx showing cutoutDeg: four corners in
        /// image pixels (y down). Position angle turns the frame counter-clockwise on a
        /// north-up, east-left image.
        /// </summary>
        public static IReadOnlyList<(double X, double Y)> SensorCorners(FieldOfView fov, double rotationDeg, double cutoutDeg, int sizePx) {
            var pxPerDeg = sizePx / cutoutDeg;
            double hw = fov.WidthDeg * pxPerDeg / 2, hh = fov.HeightDeg * pxPerDeg / 2, c0 = sizePx / 2.0;
            var a = rotationDeg * Math.PI / 180;
            double cos = Math.Cos(a), sin = Math.Sin(a);
            var corners = new List<(double, double)>();
            foreach (var (x, y) in new[] { (-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh) }) {
                // counter-clockwise on screen (y down) = rotate by -a in screen coordinates
                corners.Add((c0 + x * cos + y * sin, c0 - x * sin + y * cos));
            }
            return corners;
        }

        /// <summary>Top edge midpoint of the rotated frame: where the "up" side of the sensor points.</summary>
        public static (double X, double Y) TopMid(IReadOnlyList<(double X, double Y)> corners) =>
            ((corners[0].X + corners[1].X) / 2, (corners[0].Y + corners[1].Y) / 2);
    }
}

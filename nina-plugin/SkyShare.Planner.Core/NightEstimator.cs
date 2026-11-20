using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SkyShare.Planner.Core {

    /// <summary>Observatory position. Longitude east-positive (NINA's convention).</summary>
    public sealed record Site(double LatitudeDeg, double LongitudeDeg);

    /// <summary>
    /// How the generated blocks run (mirrors BuildOptions) plus time costs the
    /// estimate adds on top of the exposures. Defaults are deliberately a little
    /// generous: a clear-sky estimate should not promise more than a real night gives.
    /// </summary>
    public sealed record EstimateSettings(
        double MinAltitude = 20,
        bool AutofocusAtStart = true,
        int DitherEvery = 1,
        double SlewCenterSeconds = 180,     // slew, plate solve, center, rotate
        double AutofocusSeconds = 240,
        double AutofocusEveryMinutes = 60,  // the template's AF triggers; 0 = only at target start
        double DitherSeconds = 30,          // dither + guiding settle
        double FrameOverheadSeconds = 5,    // download + save per frame
        double FilterChangeSeconds = 10,
        double MeridianFlipSeconds = 300);

    /// <summary>Planned frames for one exposure line of a target.</summary>
    public sealed record FrameEstimate(string Filter, double Exposure, int Frames);

    /// <summary>
    /// One target's estimated run. Skipped targets have no times. ArriveTime is when
    /// the sequence reaches the target (after any Wait for Time); ImagingStart is the
    /// first exposure, after slew/center/autofocus.
    /// </summary>
    public sealed record TargetEstimate(
        string Name,
        DateTime? ArriveTime,
        DateTime? ImagingStart,
        DateTime? End,
        IReadOnlyList<FrameEstimate> Frames,
        string Outcome,
        bool Skipped,
        MoonInfo? Moon = null) {
        public double IntegrationSeconds => Frames.Sum(f => f.Frames * f.Exposure);
        public int FrameCount => Frames.Sum(f => f.Frames);
    }

    /// <summary>
    /// The whole night. All times are local clock times of the observatory.
    /// Dusk/Dawn are astronomical twilight (sun at -18°); when the sun never gets
    /// that low they fall back to nautical, then civil twilight.
    /// </summary>
    public sealed record NightEstimate(
        DateOnly Night,
        DateTime Sunset, DateTime Dusk, DateTime Dawn, DateTime Sunrise,
        string DarknessLabel,
        DateTime ImagingStart,
        IReadOnlyList<TargetEstimate> Targets,
        DateTime? Finish,
        IReadOnlyList<string> Notes);

    /// <summary>
    /// The Moon relative to a target while it is imaged: closest separation (degrees,
    /// counted only while the Moon is up) and illuminated fraction (0..1).
    /// </summary>
    public sealed record MoonInfo(double SeparationDeg, double Illumination) {
        /// <summary>Bright and near enough to matter for broadband: over half lit and within 40°.</summary>
        public bool IsClose => Illumination > 0.5 && SeparationDeg < 40;

        public override string ToString() =>
            $"Moon {SeparationDeg:0}° away, {Illumination * 100:0}% lit" + (IsClose ? " (close)" : "");
    }

    /// <summary>Sun times of one night (local clock). DarknessLabel says which twilight Dusk/Dawn are.</summary>
    public sealed record NightWindow(DateOnly Night, DateTime Sunset, DateTime Dusk, DateTime Dawn, DateTime Sunrise, string DarknessLabel);

    /// <summary>
    /// Clear-sky simulation of the generated sequence, used for the night-plan preview.
    ///
    /// It walks the targets in plan order exactly like SkyShare Targets runs them:
    /// wait for "start", slew/center/rotate, optional autofocus, then exposures until
    /// any stop condition holds — altitude below the minimum, "end", max_frames,
    /// max_hours, the next target rising above next_target_altitude, or dawn. Like
    /// the real conditions, these are checked between frames, and a target that is
    /// already below the minimum altitude when its turn comes is skipped (the
    /// altitude condition does not wait for it to rise).
    ///
    /// Positions use J2000 coordinates without precession and a low-precision sun:
    /// good to a few minutes of time, which is plenty for a plan preview.
    /// </summary>
    public static class NightEstimator {
        public const string AstronomicalLabel = "astronomical";

        public static NightEstimate Estimate(NightPlan plan, Site site, EstimateSettings s,
                                             TimeZoneInfo? zone = null, DateTime? notBefore = null) {
            zone ??= TimeZoneInfo.Local;
            var night = DateOnly.ParseExact(plan.Night, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var notes = new List<string>();

            var w = Twilight(night, site, zone);
            var (sunset, dusk, dawn, sunrise, label) = (w.Sunset, w.Dusk, w.Dawn, w.Sunrise, w.DarknessLabel);
            if (label != AstronomicalLabel)
                notes.Add($"the sun never reaches -18° tonight; using {label} twilight as the dark window");

            var start = dusk;
            if (notBefore is { } nb && nb > start && nb < dawn) start = nb;
            var t = start;

            var results = new List<TargetEstimate>();
            DateTime? finish = null;
            for (int i = 0; i < plan.Targets.Count; i++) {
                var target = plan.Targets[i];
                var next = i + 1 < plan.Targets.Count ? plan.Targets[i + 1] : null;
                var r = RunTarget(target, next, site, zone, s, night, ref t, dawn);
                results.Add(r);
                if (!r.Skipped) finish = r.End;
            }
            return new NightEstimate(night, sunset, dusk, dawn, sunrise, label, start, results, finish, notes);
        }

        private static TargetEstimate RunTarget(PlanTarget tg, PlanTarget? next, Site site, TimeZoneInfo zone,
                                                EstimateSettings s, DateOnly night, ref DateTime t, DateTime dawn) {
            Sexagesimal.TryParseRaHours(tg.Ra, out var raH);
            Sexagesimal.TryParseDecDegrees(tg.Dec, out var decD);
            double nextRa = 0, nextDec = 0;
            if (next != null) { Sexagesimal.TryParseRaHours(next.Ra, out nextRa); Sexagesimal.TryParseDecDegrees(next.Dec, out nextDec); }
            DateTime? endClock = tg.End is { } e ? ClockInNight(night, e) : null;

            if (tg.Start is { } st) {
                var startClock = ClockInNight(night, st);
                if (startClock > t) t = startClock;
            }
            if (t >= dawn) return Skip(tg, "night is over before its turn");
            if (endClock is { } ec0 && t >= ec0) return Skip(tg, $"its end time {ec0:HH:mm} has passed before its turn");
            var arrive = t;

            // The altitude condition is checked when EXPOSURES starts: below the limit = skipped.
            var altNow = Altitude(raH, decD, site, zone, t);
            if (altNow < s.MinAltitude)
                return Skip(tg, $"at {altNow:0}° when its turn comes at {t:HH:mm} (below {s.MinAltitude:0}°)", arrive);

            t = t.AddSeconds(s.SlewCenterSeconds);
            if (s.AutofocusAtStart) t = t.AddSeconds(s.AutofocusSeconds);
            var lastAf = t;
            var imagingStart = t;
            var ha = HourAngle(raH, site, zone, t);

            var counts = tg.Exposures.Select(_ => 0).ToArray();
            int total = 0;
            string? outcome = null;
            var tLocal = t; // ref locals can't be captured

            string? Stop() {
                if (tLocal >= dawn) return "dawn";
                if (endClock is { } ec && tLocal >= ec) return $"end time {ec:HH:mm}";
                var a = Altitude(raH, decD, site, zone, tLocal);
                if (a < s.MinAltitude) return $"dropped below {s.MinAltitude:0}°";
                if (tg.MaxFrames is { } mf && total >= mf) return $"{mf} frames (max_frames)";
                if (tg.MaxHours is { } mh && (tLocal - imagingStart).TotalHours >= mh) return $"{mh:0.##} h (max_hours)";
                if (next != null && tg.NextTargetAltitude is { } nta && Altitude(nextRa, nextDec, site, zone, tLocal) >= nta)
                    return $"{next.Name} above {nta:0}°";
                return null;
            }

            void Frame(int idx, double exposure) {
                tLocal = tLocal.AddSeconds(exposure + s.FrameOverheadSeconds);
                counts[idx]++; total++;
                var h = HourAngle(raH, site, zone, tLocal);
                if (ha < 0 && h >= 0) tLocal = tLocal.AddSeconds(s.MeridianFlipSeconds); // flip once past the meridian
                ha = h;
                if (s.AutofocusEveryMinutes > 0 && (tLocal - lastAf).TotalMinutes >= s.AutofocusEveryMinutes) {
                    tLocal = tLocal.AddSeconds(s.AutofocusSeconds);
                    lastAf = tLocal;
                }
            }

            if (!tg.Cycle) {
                for (int i = 0; i < tg.Exposures.Count && outcome is null; i++) {
                    var ex = tg.Exposures[i];
                    tLocal = tLocal.AddSeconds(s.FilterChangeSeconds);
                    for (int k = 0; k < ex.Count; k++) {
                        if ((outcome = Stop()) != null) break;
                        Frame(i, ex.Exposure);
                        if (s.DitherEvery > 0 && counts[i] % s.DitherEvery == 0 && k < ex.Count - 1)
                            tLocal = tLocal.AddSeconds(s.DitherSeconds);
                    }
                }
                outcome ??= "all planned frames taken";
            } else if (tg.Exposures.Sum(x => x.Count) > 0) {
                while (outcome is null) {
                    for (int i = 0; i < tg.Exposures.Count && outcome is null; i++) {
                        var ex = tg.Exposures[i];
                        tLocal = tLocal.AddSeconds(s.FilterChangeSeconds);
                        for (int k = 0; k < ex.Count; k++) {
                            if ((outcome = Stop()) != null) break;
                            Frame(i, ex.Exposure);
                        }
                    }
                    if (outcome is null) tLocal = tLocal.AddSeconds(s.DitherSeconds); // once per cycle
                }
            } else outcome = "no frames in the plan";

            t = tLocal;
            var frames = tg.Exposures.Select((x, i) => new FrameEstimate(x.Filter, x.Exposure, counts[i])).ToList();
            if (total == 0) return Skip(tg, $"stopped before the first frame: {outcome}", arrive);
            return new TargetEstimate(tg.Name, arrive, imagingStart, t, frames, outcome, false,
                                      MoonDuring(raH, decD, site, imagingStart, t, zone));
        }

        private static TargetEstimate Skip(PlanTarget tg, string why, DateTime? arrive = null) =>
            new(tg.Name, arrive, null, null,
                tg.Exposures.Select(x => new FrameEstimate(x.Filter, x.Exposure, 0)).ToList(), "skipped: " + why, true);

        /// <summary>"HH:MM" from the plan as a time in this night (before noon = the next morning).</summary>
        public static DateTime ClockInNight(DateOnly night, string hhmm) {
            Sexagesimal.TryParseHhMm(hhmm, out var time);
            var day = time.Hour < 12 ? night.AddDays(1) : night;
            return day.ToDateTime(time);
        }

        /// <summary>
        /// Sunset, dark window and sunrise of the night starting on this date. The dark
        /// window is astronomical twilight, falling back to nautical then civil when the
        /// sun never gets that low.
        /// </summary>
        public static NightWindow Twilight(DateOnly night, Site site, TimeZoneInfo? zone = null) {
            zone ??= TimeZoneInfo.Local;
            var noon = night.ToDateTime(new TimeOnly(12, 0));
            var (sunset, sunrise) = Crossing(site, zone, noon, -0.833) ?? (noon.AddHours(6), noon.AddHours(18));
            foreach (var (alt, name) in new[] { (-18.0, AstronomicalLabel), (-12.0, "nautical"), (-6.0, "civil") })
                if (Crossing(site, zone, noon, alt) is { } dark)
                    return new NightWindow(night, sunset, dark.Down, dark.Up, sunrise, name);
            return new NightWindow(night, sunset, sunset, sunrise, sunrise, "none");
        }

        /// <summary>The night a local time belongs to: before noon counts as the previous evening's night.</summary>
        public static DateOnly NightOf(DateTime local) =>
            DateOnly.FromDateTime(local.Hour < 12 ? local.Date.AddDays(-1) : local.Date);

        /// <summary>Altitude of RA/Dec at local times from..to every stepMinutes.</summary>
        public static IReadOnlyList<(DateTime Time, double Altitude)> AltitudeCurve(
            double raHours, double decDeg, Site site, DateTime from, DateTime to, double stepMinutes = 5, TimeZoneInfo? zone = null) {
            zone ??= TimeZoneInfo.Local;
            var pts = new List<(DateTime, double)>();
            for (var x = from; x <= to; x = x.AddMinutes(stepMinutes)) pts.Add((x, Altitude(raHours, decDeg, site, zone, x)));
            return pts;
        }

        // ---- altitude curves for the chart -----------------------------------

        /// <summary>Altitude of a plan target at local times from..to every stepMinutes.</summary>
        public static IReadOnlyList<(DateTime Time, double Altitude)> AltitudeCurve(
            PlanTarget tg, Site site, DateTime from, DateTime to, double stepMinutes = 5, TimeZoneInfo? zone = null) {
            Sexagesimal.TryParseRaHours(tg.Ra, out var raH);
            Sexagesimal.TryParseDecDegrees(tg.Dec, out var decD);
            return AltitudeCurve(raH, decD, site, from, to, stepMinutes, zone);
        }

        // ---- astronomy --------------------------------------------------------

        private static double JulianDate(DateTime utc) => utc.ToOADate() + 2415018.5;

        private static DateTime ToUtc(DateTime local, TimeZoneInfo zone) =>
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);

        /// <summary>Local sidereal time in degrees.</summary>
        private static double Lst(DateTime utc, double lonDeg) {
            var d = JulianDate(utc) - 2451545.0;
            return Norm360(280.46061837 + 360.98564736629 * d + lonDeg);
        }

        public static double Altitude(double raHours, double decDeg, Site site, TimeZoneInfo zone, DateTime local) {
            var utc = ToUtc(local, zone);
            var ha = Rad(Lst(utc, site.LongitudeDeg) - raHours * 15);
            var lat = Rad(site.LatitudeDeg); var dec = Rad(decDeg);
            var sinAlt = Math.Sin(dec) * Math.Sin(lat) + Math.Cos(dec) * Math.Cos(lat) * Math.Cos(ha);
            return Deg(Math.Asin(Math.Clamp(sinAlt, -1, 1)));
        }

        /// <summary>Hour angle in hours, -12..12 (negative = east of the meridian).</summary>
        private static double HourAngle(double raHours, Site site, TimeZoneInfo zone, DateTime local) {
            var h = (Lst(ToUtc(local, zone), site.LongitudeDeg) / 15 - raHours) % 24;
            if (h < -12) h += 24; if (h >= 12) h -= 24;
            return h;
        }

        /// <summary>Sun's ecliptic longitude in radians, n days from J2000.</summary>
        private static double SunLongitude(double n) {
            var l = Rad(Norm360(280.460 + 0.9856474 * n));
            var g = Rad(Norm360(357.528 + 0.9856003 * n));
            return l + Rad(1.915) * Math.Sin(g) + Rad(0.020) * Math.Sin(2 * g);
        }

        /// <summary>
        /// Moon's geocentric ecliptic longitude/latitude (radians) and horizontal parallax
        /// (degrees): the Astronomical Almanac's low-precision series, good to ~0.3°.
        /// </summary>
        private static (double Lambda, double Beta, double Parallax) MoonEcliptic(DateTime utc) {
            var T = (JulianDate(utc) - 2451545.0) / 36525;
            double S(double a, double b) => Math.Sin(Rad(Norm360(a + b * T)));
            double C(double a, double b) => Math.Cos(Rad(Norm360(a + b * T)));
            var lambda = 218.32 + 481267.881 * T
                + 6.29 * S(135.0, 477198.87) - 1.27 * S(259.3, -413335.36) + 0.66 * S(235.7, 890534.22)
                + 0.21 * S(269.9, 954397.74) - 0.19 * S(357.5, 35999.05) - 0.11 * S(186.5, 966404.03);
            var beta = 5.13 * S(93.3, 483202.02) + 0.28 * S(228.2, 960400.89)
                - 0.28 * S(318.3, 6003.15) - 0.17 * S(217.6, -407332.21);
            var parallax = 0.9508 + 0.0518 * C(135.0, 477198.87) + 0.0095 * C(259.3, -413335.36)
                + 0.0078 * C(235.7, 890534.22) + 0.0028 * C(269.9, 954397.74);
            return (Rad(Norm360(lambda)), Rad(beta), parallax);
        }

        /// <summary>The Moon's geocentric RA (hours) and Dec (degrees).</summary>
        public static (double RaHours, double DecDeg) MoonRaDec(TimeZoneInfo zone, DateTime local) {
            var (lambda, beta, _) = MoonEcliptic(ToUtc(local, zone));
            double l = Math.Cos(beta) * Math.Cos(lambda);
            double m = 0.9175 * Math.Cos(beta) * Math.Sin(lambda) - 0.3978 * Math.Sin(beta);
            double n = 0.3978 * Math.Cos(beta) * Math.Sin(lambda) + 0.9175 * Math.Sin(beta);
            return (Norm360(Deg(Math.Atan2(m, l))) / 15, Deg(Math.Asin(n)));
        }

        /// <summary>The Moon's altitude as seen from the site (parallax included: up to ~1° lower than geocentric).</summary>
        public static double MoonAltitude(Site site, TimeZoneInfo zone, DateTime local) {
            var (ra, dec) = MoonRaDec(zone, local);
            var (_, _, parallax) = MoonEcliptic(ToUtc(local, zone));
            var geo = Altitude(ra, dec, site, zone, local);
            return geo - parallax * Math.Cos(Rad(geo));
        }

        /// <summary>Angle between two RA/Dec positions, in degrees.</summary>
        public static double Separation(double ra1Hours, double dec1Deg, double ra2Hours, double dec2Deg) {
            double d1 = Rad(dec1Deg), d2 = Rad(dec2Deg), dra = Rad((ra1Hours - ra2Hours) * 15);
            var c = Math.Sin(d1) * Math.Sin(d2) + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(dra);
            return Deg(Math.Acos(Math.Clamp(c, -1, 1)));
        }

        /// <summary>
        /// The Moon while a target is imaged from..to: the closest it gets (only while
        /// the Moon is above the horizon) and how lit it is. Null when the Moon stays down.
        /// </summary>
        public static MoonInfo? MoonDuring(double raHours, double decDeg, Site site, DateTime from, DateTime to, TimeZoneInfo? zone = null) {
            zone ??= TimeZoneInfo.Local;
            MoonInfo? closest = null;
            for (var t = from; t <= to || t == from; t = t.AddMinutes(15)) {
                if (MoonAltitude(site, zone, t) > 0) {
                    var (mra, mdec) = MoonRaDec(zone, t);
                    var sep = Separation(raHours, decDeg, mra, mdec);
                    if (closest is null || sep < closest.SeparationDeg)
                        closest = new MoonInfo(sep, MoonIllumination(zone, t));
                }
                if (t >= to) break;
            }
            return closest;
        }

        /// <summary>Illuminated fraction of the Moon (0 = new, 1 = full).</summary>
        public static double MoonIllumination(TimeZoneInfo zone, DateTime local) {
            var utc = ToUtc(local, zone);
            var (lambda, beta, _) = MoonEcliptic(utc);
            var sun = SunLongitude(JulianDate(utc) - 2451545.0);
            var cosElongation = Math.Cos(beta) * Math.Cos(lambda - sun);
            return (1 - cosElongation) / 2;
        }

        public static double SunAltitude(Site site, TimeZoneInfo zone, DateTime local) {
            var n = JulianDate(ToUtc(local, zone)) - 2451545.0;
            var lambda = SunLongitude(n);
            var eps = Rad(23.439 - 0.0000004 * n);
            var ra = Math.Atan2(Math.Cos(eps) * Math.Sin(lambda), Math.Cos(lambda));
            var dec = Math.Asin(Math.Sin(eps) * Math.Sin(lambda));
            return Altitude(Deg(ra) / 15, Deg(dec), site, zone, local);
        }

        /// <summary>When the sun goes below / comes back above altDeg between noon and the next noon; null if it never does.</summary>
        private static (DateTime Down, DateTime Up)? Crossing(Site site, TimeZoneInfo zone, DateTime noon, double altDeg) {
            DateTime? down = null, up = null;
            var prev = SunAltitude(site, zone, noon);
            for (int m = 1; m <= 24 * 60; m++) {
                var x = noon.AddMinutes(m);
                var a = SunAltitude(site, zone, x);
                if (down is null && prev >= altDeg && a < altDeg) down = x;
                else if (down != null && prev < altDeg && a >= altDeg) { up = x; break; }
                prev = a;
            }
            return down is { } d && up is { } u ? (d, u) : null;
        }

        private static double Norm360(double x) => (x % 360 + 360) % 360;
        private static double Rad(double d) => d * Math.PI / 180;
        private static double Deg(double r) => r * 180 / Math.PI;
    }
}

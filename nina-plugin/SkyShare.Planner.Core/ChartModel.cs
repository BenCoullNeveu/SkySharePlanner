using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyShare.Planner.Core {

    /// <summary>A stretch of time a target is being imaged.</summary>
    public sealed record ChartSegment(DateTime Start, DateTime End);

    /// <summary>
    /// One target on the altitude chart: its curve (dotted), the stretches it is
    /// imaged (solid), and its legend line. ProgressBefore/After (0..100), when set,
    /// draw a completion bar in the legend: done before tonight, and what tonight adds.
    /// </summary>
    public sealed record ChartSeries(
        string Name, double RaHours, double DecDeg,
        IReadOnlyList<ChartSegment> Imaging,
        string Legend, bool Warn = false,
        double? ProgressBefore = null, double? ProgressAfter = null);

    /// <summary>Everything the night chart draws. MinAltitude null = no limit line.</summary>
    public sealed record ChartModel(
        string Title, string Subtitle, NightWindow Window, double? MinAltitude,
        IReadOnlyList<ChartSeries> Series, DateTime? Finish);

    public static class ChartModels {

        /// <summary>The SkyShare plan preview: one series per plan target, in plan order.</summary>
        public static ChartModel FromEstimate(NightPlan plan, NightEstimate est, double minAltitude) {
            var series = new List<ChartSeries>();
            for (int i = 0; i < plan.Targets.Count; i++) {
                var t = plan.Targets[i];
                var e = est.Targets[i];
                Sexagesimal.TryParseRaHours(t.Ra, out var ra);
                Sexagesimal.TryParseDecDegrees(t.Dec, out var dec);
                var imaging = !e.Skipped && e.ImagingStart is { } s && e.End is { } f
                    ? new[] { new ChartSegment(s, f) } : Array.Empty<ChartSegment>();
                series.Add(new ChartSeries(e.Name, ra, dec, imaging, NightSummary.Describe(e), e.Skipped));
            }
            var title = $"Night plan {est.Night:yyyy-MM-dd}" + (string.IsNullOrWhiteSpace(plan.Client) ? "" : $" — {plan.Client}");
            var window = new NightWindow(est.Night, est.Sunset, est.Dusk, est.Dawn, est.Sunrise, est.DarknessLabel);
            return new ChartModel(title, "clear-sky estimate · solid = imaging · dotted = not scheduled",
                                  window, minAltitude, series, est.Finish);
        }
    }
}

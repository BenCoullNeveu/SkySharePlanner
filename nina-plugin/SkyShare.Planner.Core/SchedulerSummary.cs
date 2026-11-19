using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyShare.Planner.Core {

    /// <summary>One exposure in Target Scheduler's plan preview. TargetKey identifies the TS target.</summary>
    public sealed record TsFrame(string TargetKey, string Filter, double Exposure, DateTime Start, DateTime End);

    /// <summary>
    /// A TS target with its completion (0..100, TS's own percent-complete) before the
    /// preview and after it, i.e. as it would be at the end of a perfect night.
    /// </summary>
    public sealed record TsTarget(
        string TargetKey, string Name, string Project, double RaHours, double DecDeg, double MinAltitude,
        double PercentBefore, double PercentAfter);

    /// <summary>What Target Scheduler's preview planner says it would do from From onwards.</summary>
    public sealed record TsPreview(DateTime From, string Profile, IReadOnlyList<TsFrame> Frames, IReadOnlyList<TsTarget> Targets);

    /// <summary>A run of consecutive exposures on one target.</summary>
    public sealed record TsBlock(string TargetKey, DateTime Start, DateTime End, IReadOnlyList<FrameEstimate> Frames) {
        public double IntegrationSeconds => Frames.Sum(f => f.Frames * f.Exposure);
    }

    /// <summary>
    /// Turns Target Scheduler's plan preview into the night chart and a Discord message,
    /// with each scheduled target's project completion before and after tonight.
    ///
    /// TS's preview is "perfect": slews, autofocus, dithers and flips take no time and
    /// every frame is accepted. The text says so; real nights get less done.
    /// </summary>
    public static class SchedulerSummary {
        public const string PerfectNote =
            "TS preview: no time for slews, autofocus, dithers or flips, and every frame counts as accepted, so a real clear night gets somewhat less done.";

        /// <summary>Consecutive frames on the same target, split where TS switches target or waits.</summary>
        public static IReadOnlyList<TsBlock> Blocks(IEnumerable<TsFrame> frames) {
            var blocks = new List<TsBlock>();
            var run = new List<TsFrame>();
            void Close() {
                if (run.Count == 0) return;
                var lines = run.GroupBy(f => (f.Filter, f.Exposure))
                               .Select(g => new FrameEstimate(g.Key.Filter, g.Key.Exposure, g.Count())).ToList();
                blocks.Add(new TsBlock(run[0].TargetKey, run[0].Start, run[^1].End, lines));
                run.Clear();
            }
            foreach (var f in frames.OrderBy(f => f.Start)) {
                if (run.Count > 0 && (f.TargetKey != run[^1].TargetKey || (f.Start - run[^1].End).TotalMinutes > 1)) Close();
                run.Add(f);
            }
            Close();
            return blocks;
        }

        /// <summary>Targets with frames tonight, in the order TS first visits them.</summary>
        public static IReadOnlyList<TsTarget> Scheduled(TsPreview p, IReadOnlyList<TsBlock> blocks) {
            var byKey = p.Targets.GroupBy(t => t.TargetKey).ToDictionary(g => g.Key, g => g.First());
            return blocks.Select(b => b.TargetKey).Distinct()
                         .Where(byKey.ContainsKey).Select(k => byKey[k]).ToList();
        }

        public static string Describe(TsTarget t, IReadOnlyList<TsBlock> blocks) {
            var mine = blocks.Where(b => b.TargetKey == t.TargetKey).ToList();
            var times = string.Join(", ", mine.Select(b => $"{b.Start:HH:mm}–{b.End:HH:mm}"));
            var frames = string.Join(", ", mine.SelectMany(b => b.Frames)
                .GroupBy(f => (f.Filter, f.Exposure))
                .Select(g => $"{g.Key.Filter} {g.Key.Exposure:0.##}s×{g.Sum(f => f.Frames)}"));
            return $"{times} · {frames} · {NightSummary.Hours(mine.Sum(b => b.IntegrationSeconds))} · {Progress(t)}";
        }

        public static string Progress(TsTarget t) =>
            Math.Round(t.PercentAfter) > Math.Round(t.PercentBefore)
                ? $"{t.PercentBefore:0}% → {t.PercentAfter:0}%"
                : $"{t.PercentBefore:0}%";

        public static ChartModel Chart(TsPreview p, NightWindow window) {
            var blocks = Blocks(p.Frames);
            var scheduled = Scheduled(p, blocks);
            var series = scheduled.Select(t => new ChartSeries(
                $"{t.Name} ({t.Project})", t.RaHours, t.DecDeg,
                blocks.Where(b => b.TargetKey == t.TargetKey).Select(b => new ChartSegment(b.Start, b.End)).ToList(),
                Describe(t, blocks), false, t.PercentBefore, t.PercentAfter)).ToList();
            var limits = scheduled.Select(t => t.MinAltitude).Distinct().ToList();
            return new ChartModel(
                $"Target Scheduler {window.Night:yyyy-MM-dd}" + (string.IsNullOrWhiteSpace(p.Profile) ? "" : $" — {p.Profile}"),
                "TS plan preview (no overheads) · solid = imaging · dotted = not scheduled · bar = project target done / +tonight",
                window, limits.Count == 1 ? limits[0] : null, series, blocks.Count > 0 ? blocks[^1].End : null);
        }

        /// <summary>Discord-flavoured markdown (under Discord's 2000-character limit).</summary>
        public static string Markdown(TsPreview p, NightWindow window) {
            var blocks = Blocks(p.Frames);
            var scheduled = Scheduled(p, blocks);
            var lines = new List<string> {
                $"**Target Scheduler {window.Night:yyyy-MM-dd}**" + (string.IsNullOrWhiteSpace(p.Profile) ? "" : $" — {p.Profile}") + " · plan preview",
                $"Dark ({window.DarknessLabel}) {window.Dusk:HH:mm} → {window.Dawn:HH:mm}" +
                    (blocks.Count > 0 ? $" · first exposure {blocks[0].Start:HH:mm}" : ""),
            };
            if (blocks.Count == 0) {
                lines.Add(p.Targets.Count == 0
                    ? "⚠️ No active Target Scheduler projects with exposures left."
                    : "⚠️ Target Scheduler would not image anything tonight.");
            } else {
                for (int i = 0; i < scheduled.Count; i++)
                    lines.Add($"{i + 1}. **{scheduled[i].Name}** ({scheduled[i].Project}) {Describe(scheduled[i], blocks)}");
                if (blocks.Count > 1 && blocks.Select(b => b.TargetKey).Distinct().Count() < blocks.Count)
                    lines.Add($"Order: {string.Join(" → ", blocks.Select(b => $"{Name(p, b.TargetKey)} {b.Start:HH:mm}"))}");

                lines.Add("Projects tonight:");
                foreach (var proj in scheduled.Select(t => t.Project).Distinct()) {
                    var targets = p.Targets.Where(t => t.Project == proj).ToList();
                    double before = targets.Average(t => t.PercentBefore), after = targets.Average(t => t.PercentAfter);
                    var parts = targets.Select(t => $"{t.Name} {Progress(t)}" + (scheduled.Contains(t) ? "" : " (not tonight)"));
                    lines.Add($"• **{proj}** {Bar(before, after)} {before:0}% → {after:0}%" +
                              (targets.Count > 1 ? $" ({string.Join(", ", parts)})" : ""));
                }
                var end = blocks[^1].End;
                lines.Add($"Total {NightSummary.Hours(blocks.Sum(b => b.IntegrationSeconds))} of exposures · last frame ends ~{end:HH:mm}" +
                          (end < window.Dawn.AddMinutes(-15) ? $" · {NightSummary.Hours((window.Dawn - end).TotalSeconds)} idle before dawn" : ""));
            }
            var others = p.Targets.Count(t => !scheduled.Contains(t) && t.PercentBefore < 100);
            if (others > 0) lines.Add($"-# {others} other active target(s) not scheduled tonight.");
            lines.Add($"-# {PerfectNote}");

            var text = string.Join("\n", lines);
            return text.Length <= 1900 ? text : text[..1890] + "\n…";
        }

        /// <summary>Ten-step text bar: █ done before tonight, ▓ added tonight, ░ still to do.</summary>
        public static string Bar(double before, double after) {
            int b = (int)Math.Round(Math.Clamp(before, 0, 100) / 10), a = (int)Math.Round(Math.Clamp(after, 0, 100) / 10);
            a = Math.Max(a, b);
            return "`" + new string('█', b) + new string('▓', a - b) + new string('░', 10 - a) + "`";
        }

        private static string Name(TsPreview p, string key) => p.Targets.FirstOrDefault(t => t.TargetKey == key)?.Name ?? key;
    }
}

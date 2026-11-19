using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyShare.Planner.Core {

    /// <summary>Text for the night-plan preview (Discord message, chart legend, status file).</summary>
    public static class NightSummary {

        /// <summary>One line for a target: when, what, how much, and why it stops.</summary>
        public static string Describe(TargetEstimate e) {
            if (e.Skipped) return e.Outcome;
            var frames = string.Join(", ", e.Frames.Where(f => f.Frames > 0).Select(f => $"{f.Filter} {f.Exposure:0.##}s×{f.Frames}"));
            return $"{e.ImagingStart:HH:mm}–{e.End:HH:mm} · {frames} · {Hours(e.IntegrationSeconds)} · stops: {e.Outcome}";
        }

        /// <summary>Discord-flavoured markdown for the whole night (under Discord's 2000-character limit).</summary>
        public static string Markdown(NightPlan plan, NightEstimate est, EstimateSettings s) {
            var lines = new List<string> {
                $"**Night plan {est.Night:yyyy-MM-dd}**" + (string.IsNullOrWhiteSpace(plan.Client) ? "" : $" — {plan.Client}") + " · clear-sky estimate",
                $"Dark ({est.DarknessLabel}) {est.Dusk:HH:mm} → {est.Dawn:HH:mm} · imaging from {est.ImagingStart:HH:mm}",
            };
            for (int i = 0; i < est.Targets.Count; i++) {
                var e = est.Targets[i];
                lines.Add(e.Skipped
                    ? $"{i + 1}. ~~{e.Name}~~ — ⚠️ {e.Outcome}"
                    : $"{i + 1}. **{e.Name}** {Describe(e)}");
            }
            var total = est.Targets.Sum(t => t.IntegrationSeconds);
            if (est.Targets.Count == 0)
                lines.Add("No targets (calibration-only plan).");
            else if (est.Finish is { } fin)
                lines.Add($"Total {Hours(total)} of exposures · done ~{fin:HH:mm}" +
                          (fin < est.Dawn ? $" · {Hours((est.Dawn - fin).TotalSeconds)} left before dawn" : ""));
            else
                lines.Add("⚠️ Nothing would be imaged tonight with this plan.");
            lines.AddRange(est.Notes.Select(n => $"Note: {n}"));
            lines.Add($"-# Overheads: slew/center {s.SlewCenterSeconds / 60:0.#} min, autofocus {s.AutofocusSeconds / 60:0.#} min" +
                      (s.AutofocusSeconds > 0 && s.AutofocusEveryMinutes > 0 ? $" (+ every {s.AutofocusEveryMinutes:0} min)" : "") +
                      $", dither {s.DitherSeconds:0}s, {s.FrameOverheadSeconds:0}s per frame, flip {s.MeridianFlipSeconds / 60:0.#} min");

            var text = string.Join("\n", lines);
            return text.Length <= 1900 ? text : text[..1890] + "\n…";
        }

        public static string Hours(double seconds) {
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h{ts.Minutes:00}" : $"{ts.Minutes} min";
        }
    }
}

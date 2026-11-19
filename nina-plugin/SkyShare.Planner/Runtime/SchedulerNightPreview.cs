using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NINA.Profile.Interfaces;
using SkyShare.Planner.Core;

namespace SkyShare.Planner.Runtime {

    /// <summary>
    /// Target Scheduler's plan preview for tonight, drawn as the SkyShare night chart
    /// (with each scheduled target's completion bar) and posted to Discord. Optionally
    /// saves the PNG to a folder. A preview only: never fails the sequence.
    /// </summary>
    public static class SchedulerNightPreview {

        /// <summary>Returns a one-line summary for the sequence item, or why there is none.</summary>
        public static async Task<string> Publish(IProfileService profile, string? saveFolder) {
            try {
                if (!TargetSchedulerBridge.IsInstalled) {
                    StatusReporter.Warn("ts_preview_unavailable", "Target Scheduler is not installed, so there is no scheduler preview.");
                    return "Target Scheduler not installed";
                }
                var astro = profile.ActiveProfile.AstrometrySettings;
                var site = new Site(astro.Latitude, astro.Longitude);
                var now = DateTime.Now;
                var preview = await Task.Run(() => TargetSchedulerBridge.Preview(profile, now));
                var window = NightEstimator.Twilight(NightEstimator.NightOf(now), site);
                var markdown = SchedulerSummary.Markdown(preview, window);

                var blocks = SchedulerSummary.Blocks(preview.Frames);
                byte[]? png = null;
                var imageName = $"{window.Night:yyyy-MM-dd}_target-scheduler.night.png";
                if (blocks.Count > 0) {
                    try {
                        png = await Task.Run(() => NightChart.RenderPng(SchedulerSummary.Chart(preview, window), site));
                        if (!string.IsNullOrWhiteSpace(saveFolder)) {
                            Directory.CreateDirectory(saveFolder);
                            await File.WriteAllBytesAsync(Path.Combine(saveFolder, imageName), png);
                        }
                    } catch (Exception e) {
                        StatusReporter.Warn("night_chart_failed", $"could not draw the scheduler chart: {e.Message}");
                    }
                }

                var scheduled = SchedulerSummary.Scheduled(preview, blocks);
                var summary = blocks.Count == 0
                    ? "TS preview: nothing scheduled tonight"
                    : $"TS preview: {scheduled.Count} target(s), {NightSummary.Hours(blocks.Sum(b => b.IntegrationSeconds))} of exposures, " +
                      $"{blocks[0].Start:HH:mm}–{blocks[^1].End:HH:mm}";
                StatusReporter.ReportWithImage("ts_preview", summary, markdown, png, imageName, new {
                    image = png is null || string.IsNullOrWhiteSpace(saveFolder) ? null : imageName,
                    dusk = window.Dusk, dawn = window.Dawn,
                    blocks = blocks.Select(b => new {
                        target = b.TargetKey, start = b.Start, end = b.End,
                        frames = b.Frames.Sum(f => f.Frames), integration_s = b.IntegrationSeconds,
                    }),
                    targets = scheduled.Select(t => new {
                        name = t.Name, project = t.Project, percent_before = t.PercentBefore, percent_after = t.PercentAfter,
                    }),
                    markdown,
                });
                return summary;
            } catch (Exception e) {
                var inner = e is System.Reflection.TargetInvocationException { InnerException: { } ie } ? ie : e;
                NINA.Core.Utility.Logger.Error($"[SkyShare] scheduler preview failed: {inner}");
                StatusReporter.Warn("ts_preview_failed", $"could not run the Target Scheduler preview: {inner.Message}");
                return $"failed: {inner.Message}";
            }
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NINA.Profile.Interfaces;
using SkyShare.Planner.Core;

namespace SkyShare.Planner.Runtime {

    /// <summary>
    /// After Load Plan: estimate the night on a clear sky, draw the altitude chart, save
    /// it next to the plan ("x.plan.night.png", synced by Dropbox for the bot) and post
    /// both to the Discord webhook. A preview only: never fails the sequence.
    /// </summary>
    public static class NightPreview {

        public static async Task Publish(NightPlan plan, string planPath, IProfileService profile, EstimateSettings settings) {
            try {
                var astro = profile.ActiveProfile.AstrometrySettings;
                var site = new Site(astro.Latitude, astro.Longitude);
                var est = await Task.Run(() => NightEstimator.Estimate(plan, site, settings, notBefore: DateTime.Now));
                var markdown = NightSummary.Markdown(plan, est, settings);

                byte[]? png = null;
                string? imageName = null;
                if (plan.Targets.Count > 0) {
                    try {
                        png = await Task.Run(() => NightChart.RenderPng(plan, est, site, settings.MinAltitude));
                        var imagePath = Path.ChangeExtension(planPath, null) + ".night.png"; // x.plan.json -> x.plan.night.png
                        await File.WriteAllBytesAsync(imagePath, png);
                        imageName = Path.GetFileName(imagePath);
                    } catch (Exception e) {
                        StatusReporter.Warn("night_chart_failed", $"could not draw the night chart: {e.Message}");
                    }
                }

                var imaged = est.Targets.Where(t => !t.Skipped).ToList();
                var summary = $"clear-sky estimate: {imaged.Count}/{est.Targets.Count} target(s), " +
                              $"{NightSummary.Hours(est.Targets.Sum(t => t.IntegrationSeconds))} of exposures" +
                              (est.Finish is { } f ? $", done ~{f:HH:mm}" : "");
                StatusReporter.ReportWithImage("night_estimate", summary, markdown, png, imageName ?? "night.png", new {
                    image = imageName,
                    dusk = est.Dusk, dawn = est.Dawn, imaging_start = est.ImagingStart, finish = est.Finish,
                    targets = est.Targets.Select(t => new {
                        name = t.Name, skipped = t.Skipped, outcome = t.Outcome,
                        start = t.ImagingStart, end = t.End, frames = t.FrameCount,
                        integration_s = t.IntegrationSeconds,
                    }),
                    markdown,
                });
                foreach (var t in est.Targets.Where(t => t.Skipped))
                    StatusReporter.Warn("target_not_reached", $"{t.Name}: {t.Outcome} (clear-sky estimate)");
            } catch (Exception e) {
                StatusReporter.Warn("night_estimate_failed", $"could not estimate the night: {e.Message}");
            }
        }
    }
}

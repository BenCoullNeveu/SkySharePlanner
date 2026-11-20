using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using SkyShare.Planner.Core;

namespace SkyShare.Planner.Runtime {

    /// <summary>
    /// After Load Plan: estimate the night on a clear sky, draw the altitude chart, save
    /// it next to the plan ("x.plan.night.png", synced by Dropbox for the bot) and post
    /// both to the Discord webhook. A preview only: never fails the sequence.
    /// </summary>
    public static class NightPreview {

        /// <param name="framingCamera">Camera for the framing preview's sensor size; null = no framing preview.</param>
        public static async Task Publish(NightPlan plan, string planPath, IProfileService profile, EstimateSettings settings,
                                         ICameraMediator? framingCamera = null) {
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

                var files = new System.Collections.Generic.List<StatusReporter.Attachment>();
                if (png != null) files.Add(new StatusReporter.Attachment(png, imageName ?? "night.png"));
                if (framingCamera != null && plan.Targets.Count > 0) {
                    var framing = plan.Targets.Select((t, i) => {
                        Sexagesimal.TryParseRaHours(t.Ra, out var ra);
                        Sexagesimal.TryParseDecDegrees(t.Dec, out var dec);
                        var e = est.Targets[i];
                        return new FramingTarget(t.Name, ra, dec, t.Rotation,
                            e.Skipped ? "skipped tonight" : $"{e.ImagingStart:HH:mm}–{e.End:HH:mm}");
                    }).ToList();
                    var jpg = await FramingPreview.Render(framing, FramingPreview.FieldOfView(profile, framingCamera));
                    if (jpg != null) files.Add(new StatusReporter.Attachment(jpg, Path.GetFileName(Path.ChangeExtension(planPath, null)) + ".framing.jpg", "image/jpeg"));
                }

                var imaged = est.Targets.Where(t => !t.Skipped).ToList();
                var summary = $"clear-sky estimate: {imaged.Count}/{est.Targets.Count} target(s), " +
                              $"{NightSummary.Hours(est.Targets.Sum(t => t.IntegrationSeconds))} of exposures" +
                              (est.Finish is { } f ? $", done ~{f:HH:mm}" : "");
                StatusReporter.ReportWithImages("night_estimate", summary, markdown, files, new {
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

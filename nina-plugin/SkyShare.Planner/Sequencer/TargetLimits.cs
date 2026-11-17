using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.SequenceItem;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner.Sequencer {

    /// <summary>
    /// Stops a generated target when any of its plan limits is reached:
    ///   max_frames            - saved light frames for this target
    ///   max_hours             - wall-clock time since this target's exposures began
    ///   next_target_altitude  - the next plan target has risen above this altitude
    ///
    /// SkyShare Targets puts one on the target's EXPOSURES container and one on each
    /// Smart Exposure, so the limits are checked between frames, not only between filters.
    /// (Plan "end" and the altitude limit use NINA's own conditions.)
    ///
    /// Exported so NINA can save and reload sequences that contain generated targets.
    /// </summary>
    [ExportMetadata("Name", "SkyShare: Target Limits")]
    [ExportMetadata("Description", "Stops a SkyShare target at its plan's frame, hour or next-target-altitude limit. Added automatically.")]
    [ExportMetadata("Icon", "ClockSVG")]
    [ExportMetadata("Category", "SkyShare")]
    [Export(typeof(ISequenceCondition))]
    [JsonObject(MemberSerialization.OptIn)]
    public class TargetLimits : SequenceCondition {
        private readonly IProfileService profileService;
        private bool altitudeWarned;

        [ImportingConstructor]
        public TargetLimits(IProfileService profileService) {
            this.profileService = profileService;
        }

        private TargetLimits(TargetLimits copyMe) : this(copyMe.profileService) {
            CopyMetaData(copyMe);
            TargetName = copyMe.TargetName;
            MaxFrames = copyMe.MaxFrames;
            MaxHours = copyMe.MaxHours;
            NextTargetName = copyMe.NextTargetName;
            NextRaHours = copyMe.NextRaHours;
            NextDecDegrees = copyMe.NextDecDegrees;
            NextTargetAltitude = copyMe.NextTargetAltitude;
        }

        [JsonProperty] public string TargetName { get; set; } = "";
        [JsonProperty] public int? MaxFrames { get; set; }
        [JsonProperty] public double? MaxHours { get; set; }
        [JsonProperty] public string? NextTargetName { get; set; }
        [JsonProperty] public double NextRaHours { get; set; }
        [JsonProperty] public double NextDecDegrees { get; set; }
        [JsonProperty] public double? NextTargetAltitude { get; set; }

        /// <summary>Shown in the sequencer.</summary>
        public string Summary {
            get {
                var parts = new List<string>();
                if (MaxFrames is { } f) parts.Add($"{f} frames");
                if (MaxHours is { } h) parts.Add($"{h:0.##} h");
                if (NextTargetAltitude is { } a) parts.Add($"{NextTargetName} above {a:0.#}°");
                return parts.Count == 0 ? "no limits" : "Stop at: " + string.Join(" or ", parts);
            }
        }

        public override bool Check(ISequenceItem previousItem, ISequenceItem nextItem) {
            if (PlanStore.TargetIndex(TargetName) is not { } index) return true; // not tonight's plan: don't interfere

            string? reason = null;
            var frames = PlanStore.LightsSaved(index);
            var started = PlanStore.ExposuresStarted(index);

            if (MaxFrames is { } maxFrames && frames >= maxFrames)
                reason = $"{frames} of {maxFrames} frames taken";
            else if (MaxHours is { } maxHours && DateTime.Now - started >= TimeSpan.FromHours(maxHours))
                reason = $"{maxHours:0.##} h on target";
            else if (NextTargetAltitude is { } limit && NextAltitude() is { } alt && alt >= limit)
                reason = $"next target {NextTargetName} is at {alt:0.#}° (limit {limit:0.#}°)";

            if (reason is null) return true;
            if (PlanStore.FirstLimitReport(index))
                StatusReporter.Report("target_limit", $"{TargetName}: stopping, {reason}", new { target = TargetName, frames });
            return false;
        }

        private double? NextAltitude() {
            try {
                var site = profileService.ActiveProfile.AstrometrySettings;
                var coords = new Coordinates(Angle.ByHours(NextRaHours), Angle.ByDegree(NextDecDegrees), Epoch.J2000);
                return coords.Transform(Angle.ByDegree(site.Latitude), Angle.ByDegree(site.Longitude), site.Elevation).Altitude.Degree;
            } catch (Exception e) {
                if (!altitudeWarned)
                    StatusReporter.Warn("next_altitude_failed", $"could not compute the altitude of {NextTargetName}, ignoring that limit: {e.Message}");
                altitudeWarned = true;
                return null;
            }
        }

        public override object Clone() => new TargetLimits(this);

        public override string ToString() => $"Condition: {nameof(TargetLimits)}, {TargetName}: {Summary}";
    }
}

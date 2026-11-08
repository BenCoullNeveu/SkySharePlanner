using System;
using System.Linq;
using NINA.Astrometry;
using NINA.Core.Model.Equipment;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Utility;
using SkyShare.Planner.Core;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner.Sequencer {

    /// <summary>
    /// Turns one plan target into a real DeepSkyObjectContainer by cloning the
    /// PROTOTYPE and setting typed properties. The prototype decides the *shape*
    /// (slew/center, autofocus triggers, guiding, altitude limits, dither...);
    /// the plan decides only the *values* (where, which way up, what to shoot, when).
    ///
    /// Prototype contract (see README "Template contract"):
    ///   DeepSkyObjectContainer "PROTOTYPE"
    ///     ├─ [optional] Wait for Time            ← plan.start   (removed if null)
    ///     ├─ ... anything you like (Slew, Center and Rotate, SkyShare: Record Rotation, ...)
    ///     └─ Sequential "EXPOSURES"
    ///          ├─ [optional] Loop until Time cond ← plan.end    (removed if null)
    ///          └─ Smart Exposure (exactly one)     ← cloned once per plan exposure
    /// </summary>
    public static class TargetBuilder {
        public const string ExposuresName = "EXPOSURES";

        public static DeepSkyObjectContainer Build(
            DeepSkyObjectContainer prototype, PlanTarget t, IProfileService profile) {

            var block = SequenceTree.CloneFresh(prototype, t.Name);

            // --- where and which way up ---------------------------------------
            Sexagesimal.TryParseRaHours(t.Ra, out var raH);
            Sexagesimal.TryParseDecDegrees(t.Dec, out var decD);
            block.Target.TargetName = t.Name;
            block.Target.InputCoordinates.Coordinates =
                new Coordinates(Angle.ByHours(raH), Angle.ByDegree(decD), Epoch.J2000);
            block.Target.PositionAngle = t.Rotation;
            // Items with "HasDsoParent" (Center, Altitude/Above Horizon conditions...)
            // pick these coordinates up from the container automatically.

            // --- when ---------------------------------------------------------
            var wait = SequenceTree.FirstDescendant<WaitForTime>(block);
            if (wait != null) {
                if (t.Start is null) wait.Parent?.Remove(wait);
                else SetClock(t.Start, (h, m) => { wait.Hours = h; wait.Minutes = m; wait.Seconds = 0; });
                // VERIFY: the prototype's Wait for Time must use the plain "Time" provider
                // (not Dusk/Dawn), otherwise Hours/Minutes are recomputed by the provider.
            }

            var exposures = SequenceTree.FindContainer(block, ExposuresName)
                ?? throw new InvalidOperationException($"PROTOTYPE needs a container named '{ExposuresName}'.");

            var until = SequenceTree.Conditions(exposures).OfType<TimeCondition>().FirstOrDefault();
            if (until != null) {
                if (t.End is null) exposures.Remove(until);
                else SetClock(t.End, (h, m) => { until.Hours = h; until.Minutes = m; until.Seconds = 0; });
            }

            // --- what ---------------------------------------------------------
            var smartProto = exposures.Items.OfType<SmartExposure>().FirstOrDefault()
                ?? throw new InvalidOperationException($"'{ExposuresName}' needs one Smart Exposure to clone.");
            int insertAt = exposures.Items.IndexOf(smartProto);
            foreach (var se in exposures.Items.OfType<SmartExposure>().ToList()) exposures.Remove(se);

            var profileFilters = profile.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
            foreach (var e in t.Exposures) {
                var se = (SmartExposure)smartProto.Clone();
                se.Name = $"{e.Filter} {e.Exposure:g}s × {e.Count}";

                var filter = profileFilters.FirstOrDefault(f => string.Equals(f.Name, e.Filter, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Filter '{e.Filter}' not in profile"); // validated earlier
                Single<SwitchFilter>(se).Filter = filter;

                var take = Single<TakeExposure>(se);
                take.ExposureTime = e.Exposure;
                take.Gain = e.Gain;
                take.Offset = e.Offset;
                take.Binning = new BinningMode((short)e.Binning, (short)e.Binning);
                take.ImageType = "LIGHT";

                se.Conditions.OfType<LoopCondition>().Single().Iterations = e.Count;

                // Insert at the prototype's position so items after it (e.g. a final Dither) stay last.
                SequenceTree.Insert(exposures, insertAt++, se);
            }
            return block;
        }

        private static T Single<T>(ISequenceContainer c) where T : class, ISequenceItem =>
            c.Items.OfType<T>().SingleOrDefault()
            ?? throw new InvalidOperationException($"Smart Exposure prototype must contain exactly one {typeof(T).Name}");

        private static void SetClock(string hhmm, Action<int, int> set) {
            Sexagesimal.TryParseHhMm(hhmm, out var time);
            set(time.Hour, time.Minute);
        }
    }
}

using System;
using System.Linq;
using NINA.Core.Model.Equipment;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.FlatDevice;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Rotator;
using SkyShare.Planner.Core;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner.Sequencer {

    /// <summary>
    /// Turns one FlatGroup (one camera orientation) into a container cloned from the
    /// flats PROTOTYPE.
    ///
    /// Prototype contract:
    ///   Sequential "PROTOTYPE"   (disabled)
    ///     ├─ [optional] Move Rotator Mechanical  ← set to the recorded position, removed if not needed
    ///     ├─ ... anything you like (e.g. park / point at panel)
    ///     └─ Trained Flat Exposure (exactly one) ← cloned once per FlatSpec
    ///
    /// Trained Flat Exposure looks up exposure time and panel brightness from the
    /// profile's trained-flat table for filter + gain + binning at run time, so the
    /// old hard-coded FLAT_LIBRARY moves into NINA (Flat Wizard / Flat Panel tab).
    /// </summary>
    public static class FlatsBuilder {

        public static SequentialContainer Build(
            SequentialContainer prototype, FlatGroup group, int count, IProfileService profile,
            System.Collections.Generic.List<(TrainedFlatExposure item, FlatSpec spec, double rotation)> created) {

            var name = group.MechanicalPosition is { } m
                ? $"Flats @ {group.SkyRotation:0.#}° (rotator {m:0.0}°) - {string.Join(", ", group.Targets)}"
                : $"Flats @ {group.SkyRotation:0.#}° - {string.Join(", ", group.Targets)}";
            var block = SequenceTree.CloneFresh(prototype, name);

            var rotate = SequenceTree.FirstDescendant<MoveRotatorMechanical>(block);
            if (rotate != null) {
                if (group.MechanicalPosition is { } pos) rotate.MechanicalPosition = (float)pos; // VERIFY property name/type
                else rotate.Parent?.Remove(rotate);
            }

            var flatProto = SequenceTree.FirstDescendant<TrainedFlatExposure>(block)
                ?? throw new InvalidOperationException("Flats PROTOTYPE needs one Trained Flat Exposure.");
            var parent = flatProto.Parent!;
            int insertAt = parent.Items.IndexOf(flatProto);
            parent.Remove(flatProto);

            var profileFilters = profile.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
            foreach (var spec in group.Specs) {
                var flat = (TrainedFlatExposure)flatProto.Clone();
                flat.Name = $"Flat {spec}";

                var filter = profileFilters.First(f => string.Equals(f.Name, spec.Filter, StringComparison.OrdinalIgnoreCase));
                foreach (var sw in SequenceTree.Descendants<SwitchFilter>(flat)) sw.Filter = filter;

                foreach (var take in SequenceTree.Descendants<TakeExposure>(flat)) {
                    take.Gain = spec.Gain;
                    take.Offset = spec.Offset;
                    take.Binning = new BinningMode((short)spec.Binning, (short)spec.Binning);
                    take.ImageType = "FLAT";
                }
                foreach (var loop in DescendantConditions<LoopCondition>(flat)) loop.Iterations = count;

                SequenceTree.Insert(parent, insertAt++, flat);
                created.Add((flat, spec, group.SkyRotation));
            }
            return block;
        }

        private static System.Collections.Generic.IEnumerable<T> DescendantConditions<T>(ISequenceContainer root) where T : class {
            foreach (var c in SequenceTree.Conditions(root).OfType<T>()) yield return c;
            foreach (var child in SequenceTree.Descendants<ISequenceContainer>(root))
                foreach (var c in SequenceTree.Conditions(child).OfType<T>()) yield return c;
        }
    }
}

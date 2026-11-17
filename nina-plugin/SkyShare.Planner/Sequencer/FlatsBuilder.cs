using System;
using System.Collections.Generic;
using System.Linq;
using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.FlatDevice;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Rotator;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using SkyShare.Planner.Core;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner.Sequencer {

    /// <summary>
    /// Builds one FlatGroup (one camera orientation) from scratch:
    ///
    ///   Sequential "▶ Flats @ 35° - M 31"
    ///     ├─ Move Rotator Mechanical   ← if the group has a recorded rotator position
    ///     │                              (preview: disabled placeholder until imaged)
    ///     └─ Trained Flat Exposure per FlatSpec (filter, gain, offset, binning, plan flats.count)
    ///
    /// Trained Flat Exposure looks up exposure time and panel brightness from the
    /// profile's trained-flat table for filter + gain + binning at run time.
    /// Panel / park steps around the flats stay in the template's FLATS container.
    /// </summary>
    public sealed class FlatsBuilder {
        private readonly IProfileService profile;
        private readonly ICameraMediator camera;
        private readonly IImagingMediator imaging;
        private readonly IImageSaveMediator imageSave;
        private readonly IImageHistoryVM imageHistory;
        private readonly IFilterWheelMediator filterWheel;
        private readonly IFlatDeviceMediator flatDevice;
        private readonly IRotatorMediator rotator;

        public FlatsBuilder(
            IProfileService profile, ICameraMediator camera, IImagingMediator imaging,
            IImageSaveMediator imageSave, IImageHistoryVM imageHistory, IFilterWheelMediator filterWheel,
            IFlatDeviceMediator flatDevice, IRotatorMediator rotator) {
            this.profile = profile;
            this.camera = camera;
            this.imaging = imaging;
            this.imageSave = imageSave;
            this.imageHistory = imageHistory;
            this.filterWheel = filterWheel;
            this.flatDevice = flatDevice;
            this.rotator = rotator;
        }

        public SequentialContainer Build(
            FlatGroup group, int count,
            List<(TrainedFlatExposure item, FlatSpec spec, double rotation)> created) {

            var rotatorLabel = group switch {
                { MechanicalPosition: { } m } => $" (rotator {m:0.0}°)",
                { RotatorPending: true } => " (rotator: set after imaging)",
                _ => "",
            };
            var name = $"Flats @ {group.SkyRotation:0.#}°{rotatorLabel} - {string.Join(", ", group.Targets)}";
            var block = new SequentialContainer { Name = SequenceTree.GeneratedPrefix + name };

            if (group.MechanicalPosition is { } pos) {
                block.Add(new MoveRotatorMechanical(rotator) { MechanicalPosition = (float)pos });
            } else if (group.RotatorPending) {
                // Preview: the position is recorded while imaging. Disabled so it can never
                // run with a made-up angle; SkyShare Flats rebuilds this block before running.
                var move = new MoveRotatorMechanical(rotator) { Name = "Move Rotator Mechanical (set after imaging)" };
                block.Add(move);
                move.Status = SequenceEntityStatus.DISABLED;
            }

            var profileFilters = profile.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
            foreach (var spec in group.Specs) {
                var flat = new TrainedFlatExposure(profile, camera, imaging, imageSave, imageHistory, filterWheel, flatDevice) {
                    Name = $"Flat {spec}",
                    KeepPanelClosed = true, // as in the old template; the End area closes the cover anyway
                };

                var filter = profileFilters.First(f => string.Equals(f.Name, spec.Filter, StringComparison.OrdinalIgnoreCase));
                var switches = SequenceTree.Descendants<SwitchFilter>(flat).ToList();
                var takes = SequenceTree.Descendants<TakeExposure>(flat).ToList();
                var loops = LoopConditions(flat).ToList();
                if (switches.Count == 0 || takes.Count == 0 || loops.Count == 0)
                    throw new InvalidOperationException(
                        "NINA's Trained Flat Exposure no longer has the expected filter / exposure / loop items; the SkyShare plugin needs updating.");

                foreach (var sw in switches) sw.Filter = filter;
                foreach (var take in takes) {
                    take.Gain = spec.Gain;
                    take.Offset = spec.Offset;
                    take.Binning = new BinningMode((short)spec.Binning, (short)spec.Binning);
                    take.ImageType = "FLAT";
                }
                foreach (var loop in loops) loop.Iterations = count;

                block.Add(flat);
                created.Add((flat, spec, group.SkyRotation));
            }
            SequenceTree.ApplyNinaMetadata(block); // names + icons, as if NINA had created the items
            return block;
        }

        // Trained Flat Exposure keeps its loop on an inner container.
        private static IEnumerable<LoopCondition> LoopConditions(ISequenceContainer root) =>
            SequenceTree.Descendants<ISequenceContainer>(root).Prepend(root)
                .SelectMany(c => SequenceTree.Conditions(c).OfType<LoopCondition>());
    }
}

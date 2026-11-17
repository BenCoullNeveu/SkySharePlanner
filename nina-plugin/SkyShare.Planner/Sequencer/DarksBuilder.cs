using System;
using System.Collections.Generic;
using NINA.Astrometry.Interfaces;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Camera;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.Utility.DateTimeProvider;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using SkyShare.Planner.Core;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner.Sequencer {

    /// <summary>
    /// Builds the plan's dark session from NINA's own instructions:
    ///
    ///   ▶ Cool Camera to T °C                 ← only if darks.temperature
    ///   ▶ DARKS (fixed)                        cond: Loop 1×
    ///       └─ Take Many Exposures per fixed set (DARK, count frames)
    ///   ▶ DARKS (until astronomical dawn)      cond: Loop until Time (dawn / HH:MM)
    ///       └─ Take Many Exposures per repeating set (DARK, count frames per round)
    ///            + the same time condition, so it's checked between frames
    ///
    /// The two phases swap places with darks.order = repeat_first (DarkPlanner).
    /// The repeat container has no loop condition, so it repeats its rounds until
    /// the time condition ends it.
    /// </summary>
    public sealed class DarksBuilder {
        private readonly IProfileService profile;
        private readonly ICameraMediator camera;
        private readonly IImagingMediator imaging;
        private readonly IImageSaveMediator imageSave;
        private readonly IImageHistoryVM imageHistory;
        private readonly INighttimeCalculator nighttimeCalculator;

        public DarksBuilder(IProfileService profile, ICameraMediator camera, IImagingMediator imaging,
                            IImageSaveMediator imageSave, IImageHistoryVM imageHistory,
                            INighttimeCalculator nighttimeCalculator) {
            this.profile = profile;
            this.camera = camera;
            this.imaging = imaging;
            this.imageSave = imageSave;
            this.imageHistory = imageHistory;
            this.nighttimeCalculator = nighttimeCalculator;
        }

        /// <param name="created">Filled with (plan set index, item) so frames can be counted per set.</param>
        public List<ISequenceItem> Build(DarkSettings darks, List<(int index, TakeManyExposures item)> created) {
            var items = new List<ISequenceItem>();
            if (darks.Sets.Count == 0) return items;

            if (darks.Temperature is { } temp) {
                items.Add(new CoolCamera(camera) {
                    Name = SequenceTree.GeneratedPrefix + $"Cool Camera to {temp:0.#} °C",
                    Temperature = temp,
                });
            }

            foreach (var phase in DarkPlanner.Phases(darks)) {
                var repeat = phase.Kind == DarkPhaseKind.Repeat;
                var container = new SequentialContainer {
                    Name = SequenceTree.GeneratedPrefix + (repeat
                        ? $"DARKS (until {DarkPlanner.UntilLabel(darks.Until)})"
                        : "DARKS (fixed)"),
                };

                // Fixed: run the list once. Repeat: no loop condition, so rounds repeat
                // while the time condition holds.
                TimeCondition? until = null;
                if (repeat) container.Add(until = Until(darks.Until));
                else container.Add(new LoopCondition { Iterations = 1 });

                foreach (var i in phase.SetIndices) {
                    var set = darks.Sets[i];
                    var tme = new TakeManyExposures(profile, camera, imaging, imageSave, imageHistory) {
                        Name = $"Dark {set}",
                    };
                    var take = tme.GetTakeExposure()
                        ?? throw new InvalidOperationException("NINA's Take Many Exposures has no Take Exposure; the SkyShare plugin needs updating.");
                    take.ExposureTime = set.Exposure;
                    take.Gain = set.Gain;
                    take.Offset = set.Offset;
                    take.Binning = new BinningMode((short)set.Binning, (short)set.Binning);
                    take.ImageType = "DARK";
                    (tme.GetLoopCondition()
                        ?? throw new InvalidOperationException("NINA's Take Many Exposures has no Loop condition; the SkyShare plugin needs updating."))
                        .Iterations = set.Count;
                    if (until != null) tme.Add((ISequenceCondition)until.Clone()); // stop between frames, not only between rounds

                    container.Add(tme);
                    created.Add((i, tme));
                }
                items.Add(container);
            }

            foreach (var item in items) SequenceTree.ApplyNinaMetadata(item); // names + icons
            return items;
        }

        /// <summary>Loop until a twilight event (NINA's providers) or a clock time.</summary>
        private TimeCondition Until(string until) {
            IDateTimeProvider provider = until switch {
                DarkPlanner.AstronomicalDawn => new DawnProvider(nighttimeCalculator), // NINA's "Dawn" = astronomical
                DarkPlanner.NauticalDawn => new NauticalDawnProvider(nighttimeCalculator),
                DarkPlanner.CivilDawn => new CivilDawnProvider(nighttimeCalculator),
                DarkPlanner.Sunrise => new SunriseProvider(nighttimeCalculator),
                _ => new NINA.Sequencer.Utility.DateTimeProvider.TimeProvider(nighttimeCalculator),
            };
            var condition = new TimeCondition(new List<IDateTimeProvider> { provider }, provider);
            if (Sexagesimal.TryParseHhMm(until, out var time)) {
                condition.Hours = time.Hour;
                condition.Minutes = time.Minute;
                condition.Seconds = 0;
            }
            return condition;
        }
    }
}

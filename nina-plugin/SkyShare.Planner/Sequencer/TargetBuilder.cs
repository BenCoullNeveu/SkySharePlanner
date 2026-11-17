using System;
using System.Collections.Generic;
using System.Linq;
using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Autofocus;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.Guider;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.SequenceItem.Utility;
using NINA.Sequencer.Trigger.Guider;
using NINA.Sequencer.Utility.DateTimeProvider;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using SkyShare.Planner.Core;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner.Sequencer {

    /// <summary>
    /// Builds one plan target as a Deep Sky Object container, from scratch:
    ///
    ///   DSO container "▶ name"        ← name, J2000 coords, position angle
    ///     ├─ Wait for Time             ← only if plan "start"
    ///     ├─ Center and Rotate         (plain Center if no rotator is connected)
    ///     ├─ SkyShare: Record Rotation
    ///     ├─ Run Autofocus             ← if BuildOptions.AutofocusAtStart
    ///     └─ EXPOSURES
    ///          ├─ cond: Loop 1×             ← by-filter mode only: run the list once
    ///          ├─ cond: Altitude below BuildOptions.MinAltitude
    ///          ├─ cond: Loop until Time     ← only if plan "end"
    ///          ├─ cond: SkyShare: Target Limits ← only if max_frames / max_hours / next_target_altitude
    ///          ├─ Smart Exposure per plan exposure
    ///          │    by-filter: count = total frames, dither every N frames
    ///          │    cycle:     count = frames per cycle, no dither inside
    ///          │    (+ Target Limits, so limits are checked between frames)
    ///          └─ Dither                    ← cycle mode only: once per cycle
    ///
    /// Cycle mode (plan "cycle": true) repeats EXPOSURES until a limit, the altitude
    /// limit, the end time or dawn stops it. E.g. L×3 R×1 G×1 B×1 = 6 frames, dither, repeat.
    ///
    /// Anything site-specific (safety, meridian flip, guiding recovery, alerts,
    /// autofocus triggers) belongs on the containers around SkyShare Targets in the
    /// template; their triggers apply to these blocks too.
    ///
    /// NINA doesn't expose its item factory to plugins, so items are constructed
    /// directly with the same services NINA's own items import. These constructor
    /// signatures are NINA 3.2's; check them when moving to a new NINA version.
    /// </summary>
    public sealed class TargetBuilder {
        public const string ExposuresName = "EXPOSURES";

        private readonly IProfileService profile;
        private readonly ITelescopeMediator telescope;
        private readonly IImagingMediator imaging;
        private readonly IRotatorMediator rotator;
        private readonly IFilterWheelMediator filterWheel;
        private readonly IGuiderMediator guider;
        private readonly IDomeMediator dome;
        private readonly IDomeFollower domeFollower;
        private readonly IPlateSolverFactory plateSolverFactory;
        private readonly IWindowServiceFactory windowServiceFactory;
        private readonly IImageHistoryVM imageHistory;
        private readonly ICameraMediator camera;
        private readonly IFocuserMediator focuser;
        private readonly IAutoFocusVMFactory autoFocusVMFactory;
        private readonly IImageSaveMediator imageSave;
        private readonly INighttimeCalculator nighttimeCalculator;
        private readonly IFramingAssistantVM framingAssistant;
        private readonly IApplicationMediator application;
        private readonly IPlanetariumFactory planetariumFactory;

        public TargetBuilder(
            IProfileService profile, ITelescopeMediator telescope, IImagingMediator imaging,
            IRotatorMediator rotator, IFilterWheelMediator filterWheel, IGuiderMediator guider,
            IDomeMediator dome, IDomeFollower domeFollower, IPlateSolverFactory plateSolverFactory,
            IWindowServiceFactory windowServiceFactory, IImageHistoryVM imageHistory, ICameraMediator camera,
            IFocuserMediator focuser, IAutoFocusVMFactory autoFocusVMFactory, IImageSaveMediator imageSave,
            INighttimeCalculator nighttimeCalculator, IFramingAssistantVM framingAssistant,
            IApplicationMediator application, IPlanetariumFactory planetariumFactory) {
            this.profile = profile;
            this.telescope = telescope;
            this.imaging = imaging;
            this.rotator = rotator;
            this.filterWheel = filterWheel;
            this.guider = guider;
            this.dome = dome;
            this.domeFollower = domeFollower;
            this.plateSolverFactory = plateSolverFactory;
            this.windowServiceFactory = windowServiceFactory;
            this.imageHistory = imageHistory;
            this.camera = camera;
            this.focuser = focuser;
            this.autoFocusVMFactory = autoFocusVMFactory;
            this.imageSave = imageSave;
            this.nighttimeCalculator = nighttimeCalculator;
            this.framingAssistant = framingAssistant;
            this.application = application;
            this.planetariumFactory = planetariumFactory;
        }

        /// <param name="next">The following plan target, for next_target_altitude; null for the last one.</param>
        public DeepSkyObjectContainer Build(PlanTarget t, PlanTarget? next, BuildOptions options) {
            var block = new DeepSkyObjectContainer(profile, nighttimeCalculator, framingAssistant,
                application, planetariumFactory, camera, filterWheel) {
                Name = SequenceTree.GeneratedPrefix + t.Name,
            };

            // Set the target before adding children: Center and the altitude
            // condition copy the DSO container's coordinates when they're attached.
            Sexagesimal.TryParseRaHours(t.Ra, out var raH);
            Sexagesimal.TryParseDecDegrees(t.Dec, out var decD);
            block.Target.TargetName = t.Name;
            block.Target.InputCoordinates.Coordinates =
                new Coordinates(Angle.ByHours(raH), Angle.ByDegree(decD), Epoch.J2000);
            block.Target.PositionAngle = t.Rotation;

            // --- when ---------------------------------------------------------
            if (t.Start != null) {
                var wait = new WaitForTime(TimeProviders(out var time), time);
                SetClock(t.Start, (h, m) => { wait.Hours = h; wait.Minutes = m; wait.Seconds = 0; });
                block.Add(wait);
            }

            // --- where --------------------------------------------------------
            ISequenceItem center = rotator.GetInfo().Connected
                ? new CenterAndRotate(profile, telescope, imaging, rotator, filterWheel, guider,
                                      dome, domeFollower, plateSolverFactory, windowServiceFactory)
                : new Center(profile, telescope, imaging, filterWheel, guider,
                             dome, domeFollower, plateSolverFactory, windowServiceFactory);
            block.Add(center);
            block.Add(new RecordRotation(rotator)); // no-op without a rotator

            if (options.AutofocusAtStart)
                block.Add(new RunAutofocus(profile, imageHistory, camera, filterWheel, focuser, autoFocusVMFactory));

            // --- what ---------------------------------------------------------
            var exposures = new SequentialContainer { Name = t.Cycle ? ExposuresName + " (cycle)" : ExposuresName };
            block.Add(exposures); // attach first so the altitude condition finds the DSO parent

            // Without a loop condition a container repeats while its other conditions
            // hold. By filter: Loop 1× = run the plan's list once, limits cut it short.
            // Cycle: no loop condition, so cycles repeat until a limit stops them.
            if (!t.Cycle) exposures.Add(new LoopCondition { Iterations = 1 });

            var altitude = new AltitudeCondition(profile) { HasDsoParent = true };
            altitude.Data.Offset = options.MinAltitude;
            altitude.Data.Comparator = ComparisonOperatorEnum.LESS_THAN; // "until altitude below", as in the old template
            exposures.Add(altitude);

            if (t.End != null) {
                var until = new TimeCondition(TimeProviders(out var time), time);
                SetClock(t.End, (h, m) => { until.Hours = h; until.Minutes = m; until.Seconds = 0; });
                exposures.Add(until);
            }

            var limits = Limits(t, next);
            if (limits != null) exposures.Add(limits);

            var profileFilters = profile.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
            foreach (var e in t.Exposures) {
                var se = new SmartExposure(profile, camera, imaging, imageSave, imageHistory, filterWheel, guider) {
                    Name = t.Cycle ? $"{e.Filter} {e.Exposure:g}s × {e.Count} per cycle" : $"{e.Filter} {e.Exposure:g}s × {e.Count}",
                };

                var filter = profileFilters.FirstOrDefault(f => string.Equals(f.Name, e.Filter, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Filter '{e.Filter}' not in profile"); // validated by Load Plan
                Single(se.Items.OfType<SwitchFilter>()).Filter = filter;

                var take = Single(se.Items.OfType<TakeExposure>());
                take.ExposureTime = e.Exposure;
                take.Gain = e.Gain;
                take.Offset = e.Offset;
                take.Binning = new BinningMode((short)e.Binning, (short)e.Binning);
                take.ImageType = "LIGHT";

                Single(se.Conditions.OfType<LoopCondition>()).Iterations = e.Count;

                // Cycle mode dithers once per cycle (below), never between frames.
                var dither = se.Triggers.OfType<DitherAfterExposures>().FirstOrDefault();
                if (!t.Cycle && options.DitherEvery > 0 && dither != null) dither.AfterExposures = options.DitherEvery;
                else if (dither != null) se.Remove(dither);

                if (limits != null) se.Add((ISequenceCondition)limits.Clone()); // checked between frames

                exposures.Add(se);
            }

            if (t.Cycle) exposures.Add(new Dither(guider, profile));
            SequenceTree.ApplyNinaMetadata(block); // names + icons, as if NINA had created the items
            return block;
        }

        private TargetLimits? Limits(PlanTarget t, PlanTarget? next) {
            if (t.MaxFrames is null && t.MaxHours is null && t.NextTargetAltitude is null) return null;
            var limits = new TargetLimits(profile) {
                TargetName = t.Name,
                MaxFrames = t.MaxFrames,
                MaxHours = t.MaxHours,
            };
            if (t.NextTargetAltitude is { } alt && next != null) {
                Sexagesimal.TryParseRaHours(next.Ra, out var ra);
                Sexagesimal.TryParseDecDegrees(next.Dec, out var dec);
                limits.NextTargetName = next.Name;
                limits.NextRaHours = ra;
                limits.NextDecDegrees = dec;
                limits.NextTargetAltitude = alt;
            }
            return limits;
        }

        /// <summary>Wait for Time / Loop until Time with the plain clock-time provider only.</summary>
        private IList<IDateTimeProvider> TimeProviders(out IDateTimeProvider time) {
            time = new NINA.Sequencer.Utility.DateTimeProvider.TimeProvider(nighttimeCalculator);
            return new List<IDateTimeProvider> { time };
        }

        // SmartExposure's constructor creates its own SwitchFilter, TakeExposure,
        // LoopCondition and Dither trigger. Fail clearly if a NINA update changes that.
        private static T Single<T>(IEnumerable<T> items) =>
            items.SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"NINA's Smart Exposure no longer contains exactly one {typeof(T).Name}; the SkyShare plugin needs updating.");

        private static void SetClock(string hhmm, Action<int, int> set) {
            Sexagesimal.TryParseHhMm(hhmm, out var time);
            set(time.Hour, time.Minute);
        }
    }
}

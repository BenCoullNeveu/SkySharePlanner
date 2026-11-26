using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Astrometry.Interfaces;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.Trigger;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using SkyShare.Planner.Core;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner.Sequencer {

    /// <summary>
    /// Holds the body of the night: put your "When Safe" and "When Unsafe" containers inside.
    ///
    /// Imaging night (the plan has targets): runs them as they are.
    /// Calibration night (no targets): takes them out and puts the plan's calibration
    /// sequence (its darks, see DarksBuilder) in their place, so no safety wait or dawn
    /// wait holds the darks up. Any SkyShare Darks container elsewhere then skips, so
    /// darks are taken once. Loading an imaging plan later in the same NINA session puts
    /// the original containers back.
    ///
    /// The removed containers are only kept in memory: don't save the sequence as a
    /// template while a calibration plan is loaded.
    /// </summary>
    [ExportMetadata("Name", "SkyShareAstro Session")]
    [ExportMetadata("Description", "Holds the night's When Safe / When Unsafe containers; on calibration nights replaces them with the plan's calibration sequence.")]
    [ExportMetadata("Icon", "SkyShareLogoSVG")]
    [ExportMetadata("Category", "SkyShare")]
    [Export(typeof(ISequenceItem))]       // the instruction list only shows ISequenceItem exports
    [Export(typeof(ISequenceContainer))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SkyShareSession : SequentialContainer {
        private readonly DarksBuilder builder;
        private readonly IProfileService profileService;
        private readonly ISwitchMediator switches;
        private readonly IFlatDeviceMediator flat;
        private string? builtForPlan;
        /// <summary>The template's own items, set aside while a calibration plan is loaded.</summary>
        private List<ISequenceItem>? setAside;

        [ImportingConstructor]
        public SkyShareSession(
            IProfileService profileService, ICameraMediator cameraMediator, IImagingMediator imagingMediator,
            IImageSaveMediator imageSaveMediator, IImageHistoryVM imageHistoryVM, INighttimeCalculator nighttimeCalculator,
            ISwitchMediator switchMediator, IFlatDeviceMediator flatDeviceMediator)
            : this(new DarksBuilder(profileService, cameraMediator, imagingMediator, imageSaveMediator,
                imageHistoryVM, nighttimeCalculator), profileService, switchMediator, flatDeviceMediator) {
        }

        private SkyShareSession(DarksBuilder builder, IProfileService profileService, ISwitchMediator switches, IFlatDeviceMediator flat) : base() {
            this.builder = builder;
            this.profileService = profileService;
            this.switches = switches;
            this.flat = flat;
        }

        private string mode = "";
        /// <summary>Shown under the container: what it will run tonight.</summary>
        public string Mode { get => mode; private set { mode = value; RaisePropertyChanged(); } }

        /// <summary>Swaps the body for the loaded plan, once per plan load. Load Plan calls it first; Execute is the fallback.</summary>
        public async Task EnsureBuilt() {
            var plan = PlanStore.Require();
            var key = $"{PlanStore.PlanPath}|{PlanStore.LoadedAt:o}";
            if (builtForPlan == key) return;
            var calibration = PlanStore.IsCalibrationNight;
            try {
                await SequenceTree.OnUiThread(() => {
                    SequenceTree.RemoveGenerated(this);
                    if (calibration) {
                        // Set the template's containers aside (once), then add the calibration sequence.
                        if (setAside is null) {
                            setAside = Items.ToList();
                            foreach (var item in setAside) Remove(item);
                        }
                        var created = new List<(int, TakeManyExposures)>();
                        foreach (var item in builder.Build(plan.Darks, created)) Add(item);
                        PlanStore.SetDarks(created);
                        PlanStore.DarksInSession = true;
                    } else {
                        Restore();
                    }
                });
            } catch (Exception e) {
                StatusReporter.Error("session_build_failed", $"could not set up the session: {e.Message}");
                throw;
            }
            builtForPlan = key;
            Mode = calibration
                ? "Calibration night: " + (plan.Darks.Sets.Count > 0 ? DarkPlanner.Describe(plan.Darks) : "no darks in the plan")
                : "Imaging night: runs the containers inside";
            if (calibration)
                StatusReporter.Report("calibration_night", $"calibration night: {Mode}");
        }

        /// <summary>Puts the template's own containers back (imaging plan, or no plan).</summary>
        private void Restore() {
            if (setAside is null) return;
            foreach (var item in setAside) Add(item);
            setAside = null;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (PlanStore.Plan is null) {
                await SequenceTree.OnUiThread(() => { SequenceTree.RemoveGenerated(this); Restore(); });
                Mode = "No plan loaded: runs the containers inside";
            } else {
                await EnsureBuilt();
                if (PlanStore.IsCalibrationNight) {
                    StatusReporter.Report("calibration_started", Mode);
                    // The panel is normally off and disconnected here: power up, connect, close (plugin options).
                    try {
                        await FlatPanelPrep.Run(switches, flat, SkyShareOptions.Get(profileService), progress, token);
                    } catch (OperationCanceledException) {
                        throw;
                    } catch (Exception e) {
                        var msg = $"calibration stopped before the darks: {e.Message}";
                        StatusReporter.Error("calibration_cover_failed", msg);
                        throw new SequenceEntityFailedException(msg);
                    }
                }
            }
            await base.Execute(progress, token);
            if (PlanStore.Plan is not null && PlanStore.IsCalibrationNight)
                StatusReporter.Report("calibration_done", $"{PlanStore.DarkResults().Sum(d => d.FramesTaken)} dark frame(s) taken");
        }

        public override object Clone() {
            // A clone is a fresh template copy: the original containers, never the generated ones.
            var source = setAside ?? Items.Where(i => i.Name?.StartsWith(SequenceTree.GeneratedPrefix) != true).ToList();
            var clone = new SkyShareSession(builder, profileService, switches, flat) {
                Items = new ObservableCollection<ISequenceItem>(source.Select(i => (ISequenceItem)i.Clone())),
                Conditions = new ObservableCollection<ISequenceCondition>(Conditions.Select(c => (ISequenceCondition)c.Clone())),
                Triggers = new ObservableCollection<ISequenceTrigger>(Triggers.Select(t => (ISequenceTrigger)t.Clone())),
            };
            clone.CopyMetaData(this);
            foreach (var i in clone.Items) i.AttachNewParent(clone);
            foreach (var c in clone.Conditions) c.AttachNewParent(clone);
            foreach (var t in clone.Triggers) t.AttachNewParent(clone);
            return clone;
        }

        public override string ToString() => $"Category: {Category}, Container: {nameof(SkyShareSession)}";
    }
}

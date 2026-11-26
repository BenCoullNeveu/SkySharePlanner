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
    /// Runs the plan's "darks" section: fixed sets first (by default), then rounds of
    /// the repeating sets until dawn or a set time. See DarksBuilder for the layout.
    /// Nothing needs to go inside it. Put it where the camera is covered (after your
    /// park / close-cover steps). With no darks in the plan it does nothing.
    /// </summary>
    [ExportMetadata("Name", "SkyShare Darks")]
    [ExportMetadata("Description", "Takes tonight's dark frames: fixed sets, then repeating sets until dawn.")]
    [ExportMetadata("Icon", "CameraSVG")]
    [ExportMetadata("Category", "SkyShare")]
    [Export(typeof(ISequenceItem))]       // the instruction list only shows ISequenceItem exports
    [Export(typeof(ISequenceContainer))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SkyShareDarks : SequentialContainer {
        private readonly DarksBuilder builder;
        private string? builtForPlan;

        [ImportingConstructor]
        public SkyShareDarks(
            IProfileService profileService, ICameraMediator cameraMediator, IImagingMediator imagingMediator,
            IImageSaveMediator imageSaveMediator, IImageHistoryVM imageHistoryVM, INighttimeCalculator nighttimeCalculator)
            : this(new DarksBuilder(profileService, cameraMediator, imagingMediator, imageSaveMediator,
                imageHistoryVM, nighttimeCalculator)) {
        }

        private SkyShareDarks(DarksBuilder builder) : base() {
            this.builder = builder;
        }

        /// <summary>Generates the dark blocks once per plan load. Load Plan calls it; Execute is the fallback.</summary>
        public async Task EnsureBuilt() {
            var plan = PlanStore.Require();
            var key = $"{PlanStore.PlanPath}|{PlanStore.LoadedAt:o}";
            if (builtForPlan == key) return;
            if (PlanStore.DarksInSession) {
                // Calibration night: SkyShareAstro Session takes the darks.
                await SequenceTree.OnUiThread(() => SequenceTree.RemoveGenerated(this));
                builtForPlan = key;
                return;
            }
            try {
                await SequenceTree.OnUiThread(() => {
                    SequenceTree.RemoveGenerated(this);
                    var created = new List<(int, TakeManyExposures)>();
                    foreach (var item in builder.Build(plan.Darks, created)) Add(item);
                    PlanStore.SetDarks(created);
                });
            } catch (Exception e) {
                StatusReporter.Error("darks_build_failed", $"could not build dark blocks: {e.Message}");
                throw;
            }
            builtForPlan = key;
            if (plan.Darks.Sets.Count > 0)
                StatusReporter.Report("darks_built", DarkPlanner.Describe(plan.Darks));
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (PlanStore.Plan is null) {
                StatusReporter.Report("darks_skipped", "no plan loaded");
                return;
            }
            if (PlanStore.Plan.Darks.Sets.Count == 0) return; // normal imaging night
            if (PlanStore.DarksInSession) {
                StatusReporter.Report("darks_skipped", "already taken by SkyShareAstro Session (calibration night)");
                return;
            }

            await EnsureBuilt();
            StatusReporter.Report("darks_started", DarkPlanner.Describe(PlanStore.Plan.Darks));
            await base.Execute(progress, token);
            StatusReporter.Report("darks_done", $"{PlanStore.DarkResults().Sum(d => d.FramesTaken)} dark frame(s) taken");
        }

        public override object Clone() {
            var clone = new SkyShareDarks(builder) {
                Items = new ObservableCollection<ISequenceItem>(Items.Select(i => (ISequenceItem)i.Clone())),
                Conditions = new ObservableCollection<ISequenceCondition>(Conditions.Select(c => (ISequenceCondition)c.Clone())),
                Triggers = new ObservableCollection<ISequenceTrigger>(Triggers.Select(t => (ISequenceTrigger)t.Clone())),
            };
            clone.CopyMetaData(this);
            foreach (var i in clone.Items) i.AttachNewParent(clone);
            foreach (var c in clone.Conditions) c.AttachNewParent(clone);
            foreach (var t in clone.Triggers) t.AttachNewParent(clone);
            return clone;
        }

        public override string ToString() => $"Category: {Category}, Container: {nameof(SkyShareDarks)}";
    }
}

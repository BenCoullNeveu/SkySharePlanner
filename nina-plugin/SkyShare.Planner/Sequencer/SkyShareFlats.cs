using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.FlatDevice;
using NINA.Sequencer.Trigger;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using SkyShare.Planner.Core;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner.Sequencer {

    /// <summary>
    /// Put this where FLAT INSTRUCTIONS used to be (End area). Nothing needs to go
    /// inside it. Builds flats at run time - i.e. AFTER imaging - so it knows which
    /// targets actually ran and where the rotator was for each. See FlatPlanner for rules.
    /// </summary>
    [ExportMetadata("Name", "SkyShare Flats")]
    [ExportMetadata("Description", "Builds the minimum set of trained flats for tonight's targets, grouped by orientation.")]
    [ExportMetadata("Icon", "BrainBulbSVG")]
    [ExportMetadata("Category", "SkyShare")]
    [Export(typeof(ISequenceItem))]       // the instruction list only shows ISequenceItem exports
    [Export(typeof(ISequenceContainer))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SkyShareFlats : SequentialContainer {
        private readonly FlatsBuilder builder;
        private readonly IRotatorMediator rotatorMediator;
        private readonly IProfileService profileService;

        [ImportingConstructor]
        public SkyShareFlats(
            IProfileService profileService, ICameraMediator cameraMediator, IImagingMediator imagingMediator,
            IImageSaveMediator imageSaveMediator, IImageHistoryVM imageHistoryVM, IFilterWheelMediator filterWheelMediator,
            IFlatDeviceMediator flatDeviceMediator, IRotatorMediator rotatorMediator)
            : this(new FlatsBuilder(profileService, cameraMediator, imagingMediator, imageSaveMediator,
                imageHistoryVM, filterWheelMediator, flatDeviceMediator, rotatorMediator), rotatorMediator, profileService) {
        }

        private SkyShareFlats(FlatsBuilder builder, IRotatorMediator rotatorMediator, IProfileService profileService) : base() {
            this.builder = builder;
            this.rotatorMediator = rotatorMediator;
            this.profileService = profileService;
        }

        /// <summary>
        /// Called by Load Plan: shows the flats we expect, assuming every target runs.
        /// Rotator moves to other orientations are disabled placeholders, because the
        /// rotator position is only recorded while imaging. Execute replaces all of this
        /// with the real flats once imaging is done.
        /// </summary>
        public async Task BuildPreview() {
            var plan = PlanStore.Require();
            // Load Plan runs before STARTUP connects equipment, so also count a rotator
            // that's selected in the profile but not connected yet.
            var hasRotator = rotatorMediator.GetInfo().Connected
                || profileService.ActiveProfile.RotatorSettings.Id != "No_Device";
            var preview = FlatPlanner.Preview(plan, hasRotator);
            await SequenceTree.OnUiThread(() => {
                SequenceTree.RemoveGenerated(this);
                var unused = new List<(TrainedFlatExposure, FlatSpec, double)>();
                foreach (var g in preview.Groups) Add(builder.Build(g, plan.Flats.Count, unused));
            });
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (PlanStore.Plan is null) {
                // No plan = nothing imaged by us tonight. Don't fail the End area.
                StatusReporter.Report("flats_skipped", "no plan loaded");
                return;
            }
            var plan = PlanStore.Plan;
            var flatPlan = FlatPlanner.Plan(plan, PlanStore.RunRecords(), rotatorMediator.GetInfo().Connected);

            foreach (var s in flatPlan.Skipped) StatusReporter.Warn("flats_skipped", s);

            try {
                await SequenceTree.OnUiThread(() => {
                    SequenceTree.RemoveGenerated(this);
                    var created = new List<(TrainedFlatExposure, FlatSpec, double)>();
                    foreach (var g in flatPlan.Groups)
                        Add(builder.Build(g, plan.Flats.Count, created));
                    PlanStore.SetFlats(created, flatPlan.Skipped); // read back by Session Complete
                });
            } catch (Exception e) {
                StatusReporter.Error("flats_build_failed", $"could not build flats: {e.Message}");
                throw;
            }

            StatusReporter.Report("flats_built",
                $"{flatPlan.Groups.Sum(g => g.Specs.Count)} flat set(s) in {flatPlan.Groups.Count} orientation(s)",
                new { groups = flatPlan.Groups.Select(g => new { g.SkyRotation, g.MechanicalPosition, specs = g.Specs.Select(s => s.ToString()), g.Targets }),
                      skipped = flatPlan.Skipped });

            await base.Execute(progress, token);
            StatusReporter.Report("flats_done", "flats finished");
        }

        public override object Clone() {
            var clone = new SkyShareFlats(builder, rotatorMediator, profileService) {
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

        public override string ToString() => $"Category: {Category}, Container: {nameof(SkyShareFlats)}";
    }
}

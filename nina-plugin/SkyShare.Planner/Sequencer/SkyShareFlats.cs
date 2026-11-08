using System;
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
using NINA.Sequencer.Trigger;
using SkyShare.Planner.Core;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner.Sequencer {

    /// <summary>
    /// Put this where FLAT INSTRUCTIONS used to be (End area).
    /// Builds flats at run time - i.e. AFTER imaging - so it knows which targets
    /// actually ran and where the rotator was for each. See FlatPlanner for rules.
    /// </summary>
    [ExportMetadata("Name", "SkyShare Flats")]
    [ExportMetadata("Description", "Builds the minimum set of trained flats for tonight's targets, grouped by orientation.")]
    [ExportMetadata("Icon", "BrainBulbSVG")]     // VERIFY icon key
    [ExportMetadata("Category", "SkyShare")]
    [Export(typeof(ISequenceContainer))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SkyShareFlats : SequentialContainer {
        private readonly IProfileService profileService;
        private readonly IRotatorMediator rotatorMediator;

        [ImportingConstructor]
        public SkyShareFlats(IProfileService profileService, IRotatorMediator rotatorMediator) : base() {
            this.profileService = profileService;
            this.rotatorMediator = rotatorMediator;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (PlanStore.Plan is null) {
                // No plan = nothing imaged by us tonight. Don't fail the End area.
                StatusReporter.Report("flats_skipped", "no plan loaded");
                return;
            }
            var plan = PlanStore.Plan;
            var flatPlan = FlatPlanner.Plan(plan, PlanStore.RunRecords(), rotatorMediator.GetInfo().Connected);

            foreach (var s in flatPlan.Skipped) Notification.ShowWarning($"SkyShare flats skipped: {s}");

            await SequenceTree.OnUiThread(() => {
                var proto = SequenceTree.RequirePrototype<SequentialContainer>(this);
                SequenceTree.RemoveGenerated(this);
                var created = new System.Collections.Generic.List<(NINA.Sequencer.SequenceItem.FlatDevice.TrainedFlatExposure, FlatSpec, double)>();
                foreach (var g in flatPlan.Groups)
                    Add(FlatsBuilder.Build(proto, g, plan.Flats.Count, profileService, created));
                PlanStore.SetFlats(created, flatPlan.Skipped); // read back by Session Complete
            });

            StatusReporter.Report("flats_built",
                $"{flatPlan.Groups.Sum(g => g.Specs.Count)} flat set(s) in {flatPlan.Groups.Count} orientation(s)",
                new { groups = flatPlan.Groups.Select(g => new { g.SkyRotation, g.MechanicalPosition, specs = g.Specs.Select(s => s.ToString()), g.Targets }),
                      skipped = flatPlan.Skipped });

            await base.Execute(progress, token);
            StatusReporter.Report("flats_done", "flats finished");
        }

        public override object Clone() {
            var clone = new SkyShareFlats(profileService, rotatorMediator) {
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

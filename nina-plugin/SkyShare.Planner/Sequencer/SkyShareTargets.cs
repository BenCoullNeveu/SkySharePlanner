using System;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner.Sequencer {

    /// <summary>
    /// Put this where TEMPLATE DSO used to be (inside "When Safe").
    /// It holds a disabled DeepSkyObjectContainer named PROTOTYPE. When the
    /// container starts, it clones the prototype once per plan target, in plan
    /// order, then runs them like a normal Sequential Instruction Set.
    ///
    /// Re-entrant: if "When Safe" loops (weather interruption), already-finished
    /// targets keep their status, so NINA's sequential strategy skips them and
    /// resumes at the interrupted target.
    /// </summary>
    [ExportMetadata("Name", "SkyShare Targets")]
    [ExportMetadata("Description", "Builds one target block per entry in tonight's plan, from the PROTOTYPE inside it.")]
    [ExportMetadata("Icon", "TelescopeSVG")]     // VERIFY icon key
    [ExportMetadata("Category", "SkyShare")]
    [Export(typeof(ISequenceContainer))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SkyShareTargets : SequentialContainer {
        private readonly IProfileService profileService;
        private string? builtForPlan; // plan path + load time we last built from

        [ImportingConstructor]
        public SkyShareTargets(IProfileService profileService) : base() {
            this.profileService = profileService;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var plan = PlanStore.Require();
            var key = $"{PlanStore.PlanPath}|{PlanStore.LoadedAt:o}";

            // Build once per loaded plan; on a weather loop just continue.
            if (builtForPlan != key) {
                await SequenceTree.OnUiThread(() => {
                    var proto = SequenceTree.RequirePrototype<DeepSkyObjectContainer>(this);
                    SequenceTree.RemoveGenerated(this);
                    PlanStore.ClearTargetBlocks();
                    for (int i = 0; i < plan.Targets.Count; i++) {
                        var block = TargetBuilder.Build(proto, plan.Targets[i], profileService);
                        Add(block);
                        PlanStore.RegisterTargetBlock(block, i);
                    }
                });
                builtForPlan = key;
                StatusReporter.Report("targets_built", $"{plan.Targets.Count} target block(s) generated");
            }

            await base.Execute(progress, token);
            StatusReporter.Report("targets_done", "imaging block finished");
        }

        public override object Clone() {
            var clone = new SkyShareTargets(profileService) {
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

        public override string ToString() => $"Category: {Category}, Container: {nameof(SkyShareTargets)}";
    }
}

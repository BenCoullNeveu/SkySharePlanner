using System;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Astrometry.Interfaces;
using NINA.Core.Model;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner.Sequencer {

    /// <summary>
    /// Put this where TEMPLATE DSO used to be (inside "When Safe"). When Load Plan
    /// runs (or, failing that, when this container starts), it generates one target
    /// block per plan target, in plan order (see
    /// TargetBuilder for what a block contains), then runs them like a normal
    /// Sequential Instruction Set. Nothing needs to go inside it.
    ///
    /// Re-entrant: if "When Safe" loops (weather interruption), already-finished
    /// targets keep their status, so NINA's sequential strategy skips them and
    /// resumes at the interrupted target.
    /// </summary>
    [ExportMetadata("Name", "SkyShare Targets")]
    [ExportMetadata("Description", "Generates one target block per entry in tonight's plan.")]
    [ExportMetadata("Icon", "TelescopeSVG")]
    [ExportMetadata("Category", "SkyShare")]
    [Export(typeof(ISequenceItem))]       // the instruction list only shows ISequenceItem exports
    [Export(typeof(ISequenceContainer))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SkyShareTargets : SequentialContainer {
        private readonly TargetBuilder builder;
        private string? builtForPlan; // plan path + load time we last built from

        [ImportingConstructor]
        public SkyShareTargets(
            IProfileService profileService, ITelescopeMediator telescopeMediator, IImagingMediator imagingMediator,
            IRotatorMediator rotatorMediator, IFilterWheelMediator filterWheelMediator, IGuiderMediator guiderMediator,
            IDomeMediator domeMediator, IDomeFollower domeFollower, IPlateSolverFactory plateSolverFactory,
            IWindowServiceFactory windowServiceFactory, IImageHistoryVM imageHistoryVM, ICameraMediator cameraMediator,
            IFocuserMediator focuserMediator, IAutoFocusVMFactory autoFocusVMFactory, IImageSaveMediator imageSaveMediator,
            INighttimeCalculator nighttimeCalculator, IFramingAssistantVM framingAssistantVM,
            IApplicationMediator applicationMediator, IPlanetariumFactory planetariumFactory)
            : this(new TargetBuilder(profileService, telescopeMediator, imagingMediator, rotatorMediator,
                filterWheelMediator, guiderMediator, domeMediator, domeFollower, plateSolverFactory,
                windowServiceFactory, imageHistoryVM, cameraMediator, focuserMediator, autoFocusVMFactory,
                imageSaveMediator, nighttimeCalculator, framingAssistantVM, applicationMediator, planetariumFactory)) {
            FrameCounter.Attach(imageSaveMediator); // counts saved light frames per target
        }

        private SkyShareTargets(TargetBuilder builder) : base() {
            this.builder = builder;
        }

        /// <summary>
        /// Generates the target blocks for the loaded plan, once per plan load.
        /// Load Plan calls this as soon as the plan is loaded, so the blocks are visible
        /// from the start of the night; Execute calls it again as a fallback (no-op if built).
        /// </summary>
        public async Task EnsureBuilt() {
            var plan = PlanStore.Require();
            var key = $"{PlanStore.PlanPath}|{PlanStore.LoadedAt:o}";

            // Build once per loaded plan; on a weather loop just continue.
            if (builtForPlan == key) return;
            try {
                await SequenceTree.OnUiThread(() => {
                    SequenceTree.RemoveGenerated(this);
                    PlanStore.ClearTargetBlocks();
                    for (int i = 0; i < plan.Targets.Count; i++) {
                        var next = i + 1 < plan.Targets.Count ? plan.Targets[i + 1] : null;
                        var block = builder.Build(plan.Targets[i], next, PlanStore.Options);
                        Add(block);
                        PlanStore.RegisterTargetBlock(block, i);
                    }
                });
            } catch (Exception e) {
                StatusReporter.Error("targets_build_failed", $"could not build target blocks: {e.Message}");
                throw;
            }
            builtForPlan = key;
            StatusReporter.Report("targets_built", $"{plan.Targets.Count} target block(s) generated");
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (PlanStore.Plan is null) {
                StatusReporter.Error("targets_no_plan", "SkyShare Targets ran but no plan is loaded. Put 'SkyShare: Load Plan' at the start of the sequence.");
                throw new SequenceEntityFailedException("No SkyShare plan loaded.");
            }
            await EnsureBuilt();

            await base.Execute(progress, token);
            StatusReporter.Report("targets_done", "imaging block finished");
        }

        public override object Clone() {
            var clone = new SkyShareTargets(builder) {
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

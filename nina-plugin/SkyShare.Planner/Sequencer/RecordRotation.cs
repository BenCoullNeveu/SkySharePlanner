using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner.Sequencer {

    /// <summary>
    /// SkyShare Targets adds this to every generated target, right after centering.
    /// Records the rotator's MECHANICAL position for this target so SkyShare Flats
    /// can return to exactly the same physical orientation later.
    /// (The plan stores sky position angle; mechanical position depends on the
    /// rotator sync, so it has to be measured, not computed.)
    /// Does nothing if no rotator is connected.
    /// </summary>
    [ExportMetadata("Name", "SkyShare: Record Rotation")]
    [ExportMetadata("Description", "Remembers the rotator position for this target's flats. Added automatically by SkyShare Targets.")]
    [ExportMetadata("Icon", "RotatorSVG")]       // VERIFY icon key
    [ExportMetadata("Category", "SkyShare")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class RecordRotation : SequenceItem {
        private readonly IRotatorMediator rotatorMediator;

        [ImportingConstructor]
        public RecordRotation(IRotatorMediator rotatorMediator) {
            this.rotatorMediator = rotatorMediator;
        }

        private RecordRotation(RecordRotation copyMe) : this(copyMe.rotatorMediator) {
            CopyMetaData(copyMe);
        }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var info = rotatorMediator.GetInfo();
            if (!info.Connected) return Task.CompletedTask;

            var index = PlanStore.FindTargetIndex(this);
            if (index is null) {
                StatusReporter.Warn("rotation_not_recorded", "Record Rotation is not inside a SkyShare-generated target; ignoring.");
                return Task.CompletedTask;
            }
            PlanStore.RecordMechanicalPosition(index.Value, info.MechanicalPosition); // VERIFY: RotatorInfo.MechanicalPosition
            Logger.Info($"[SkyShare] target {index + 1}: rotator mechanical {info.MechanicalPosition:0.00}°");
            return Task.CompletedTask;
        }

        public override object Clone() => new RecordRotation(this);

        public override string ToString() => $"Category: {Category}, Item: {nameof(RecordRotation)}";
    }
}

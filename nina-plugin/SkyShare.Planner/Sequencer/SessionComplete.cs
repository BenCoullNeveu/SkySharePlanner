using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.SequenceItem;
using SkyShare.Planner.Core;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner.Sequencer {

    /// <summary>
    /// Last instruction of the night (End area, after SkyShare Flats).
    /// Writes "&lt;night&gt;_&lt;client&gt;.session.json" next to the plan: what was
    /// captured, and the actions the compute server should run once the files
    /// have arrived through Dropbox. SkyStack's session watcher does the rest.
    ///
    /// Never fails the sequence: if something goes wrong it logs and reports.
    /// </summary>
    [ExportMetadata("Name", "SkyShare: Session Complete")]
    [ExportMetadata("Description", "Writes the session manifest that tells SkyStack to process tonight's data.")]
    [ExportMetadata("Icon", "CheckedCircledSVG")]  // VERIFY icon key
    [ExportMetadata("Category", "SkyShare")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SessionComplete : SequenceItem {

        [ImportingConstructor]
        public SessionComplete() { }

        private SessionComplete(SessionComplete copyMe) : this() { CopyMetaData(copyMe); }

        private string lastManifest = "";
        public string LastManifest { get => lastManifest; private set { lastManifest = value; RaisePropertyChanged(); } }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var plan = PlanStore.Plan;
            var planPath = PlanStore.PlanPath;
            if (plan is null || planPath is null) {
                Logger.Warning("[SkyShare] Session Complete: no plan loaded, no manifest written.");
                return Task.CompletedTask;
            }

            try {
                var (flatSets, flatsSkipped) = PlanStore.FlatResults();
                var manifest = SessionManifestBuilder.Build(
                    plan, Path.GetFileName(planPath), PlanStore.LightCounts(),
                    flatSets, flatsSkipped, DateTime.UtcNow);

                var dir = Path.GetDirectoryName(planPath)!;
                var final = Path.Combine(dir, SessionManifestBuilder.ManifestFileName(Path.GetFileName(planPath)));
                var tmp = Path.Combine(dir, "." + Path.GetFileName(final) + ".tmp");
                File.WriteAllText(tmp, manifest.ToJson());
                File.Move(tmp, final, overwrite: true);   // atomic on NTFS: the watcher never sees half a file

                LastManifest = Path.GetFileName(final);
                var captured = string.Join(", ", manifest.Targets.Where(t => t.Ran).Select(t => $"{t.Name} ({t.LightsTotal})"));
                StatusReporter.Report("session_complete",
                    $"{manifest.Outcome}: {(captured.Length > 0 ? captured : "no lights")}; " +
                    $"{manifest.Flats.FramesTotal} flats; {manifest.Actions.Count} job(s) for SkyStack",
                    new { manifest = LastManifest, manifest.Outcome, actions = manifest.Actions.Count });
            } catch (Exception e) {
                Logger.Error($"[SkyShare] Session Complete failed: {e}");
                StatusReporter.Report("session_manifest_failed", e.Message);
            }
            return Task.CompletedTask;
        }

        public override object Clone() => new SessionComplete(this);

        public override string ToString() => $"Category: {Category}, Item: {nameof(SessionComplete)}";
    }
}

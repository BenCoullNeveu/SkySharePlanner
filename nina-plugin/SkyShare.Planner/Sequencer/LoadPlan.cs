using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using SkyShare.Planner.Core;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner.Sequencer {

    /// <summary>
    /// First instruction of the night. Finds "&lt;night&gt;_*.plan.json" in the plans
    /// folder, validates it against the live NINA profile, and stores it for the
    /// SkyShare Targets / Flats containers.
    ///
    /// Fails the instruction (and therefore whatever error behaviour you set on it -
    /// typically "Abort") if there is no valid plan, so the scope never images a
    /// stale or wrong reservation.
    /// </summary>
    [ExportMetadata("Name", "SkyShare: Load Plan")]
    [ExportMetadata("Description", "Loads and validates tonight's SkyShare plan file from the plans folder.")]
    [ExportMetadata("Icon", "FileSVG")]          // VERIFY: any icon key from NINA's resource dictionary
    [ExportMetadata("Category", "SkyShare")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class LoadPlan : SequenceItem, IValidatable {
        private readonly IProfileService profileService;
        private readonly IRotatorMediator rotatorMediator;

        [ImportingConstructor]
        public LoadPlan(IProfileService profileService, IRotatorMediator rotatorMediator) {
            this.profileService = profileService;
            this.rotatorMediator = rotatorMediator;
        }

        private LoadPlan(LoadPlan copyMe) : this(copyMe.profileService, copyMe.rotatorMediator) {
            CopyMetaData(copyMe);
            PlansDirectory = copyMe.PlansDirectory;
            RolloverHour = copyMe.RolloverHour;
            WaitForPlanMinutes = copyMe.WaitForPlanMinutes;
        }

        // ---- settings (saved in the template) --------------------------------

        private string plansDirectory = @"D:\Users\SkyShare\Dropbox\SkyShare Data\Plans";
        /// <summary>Dropbox folder the bot / dashboard write *.plan.json into.</summary>
        [JsonProperty]
        public string PlansDirectory { get => plansDirectory; set { plansDirectory = value; RaisePropertyChanged(); } }

        private int rolloverHour = 12;
        /// <summary>Before this local hour we are still in last evening's night.</summary>
        [JsonProperty]
        public int RolloverHour { get => rolloverHour; set { rolloverHour = Math.Clamp(value, 0, 23); RaisePropertyChanged(); } }

        private int waitForPlanMinutes = 10;
        /// <summary>If the plan hasn't synced yet, keep checking this long before failing.</summary>
        [JsonProperty]
        public int WaitForPlanMinutes { get => waitForPlanMinutes; set { waitForPlanMinutes = Math.Max(0, value); RaisePropertyChanged(); } }

        // ---- shown in the UI after running ------------------------------------

        private string loadedSummary = "";
        public string LoadedSummary { get => loadedSummary; private set { loadedSummary = value; RaisePropertyChanged(); } }

        // ---- run --------------------------------------------------------------

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var night = PlanLocator.NightOf(DateTime.Now, RolloverHour);
            var deadline = DateTime.Now.AddMinutes(WaitForPlanMinutes);
            string path;

            while (true) {
                try {
                    path = PlanLocator.Find(PlansDirectory, night);
                    break;
                } catch (PlanException e) when (DateTime.Now < deadline && e.Message.StartsWith("No plan")) {
                    progress?.Report(new ApplicationStatus { Status = $"Waiting for plan {PlanLocator.NightKey(night)}…" });
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                } catch (PlanException e) {
                    Fail("plan_missing", e.Message);
                    throw; // unreachable, Fail throws
                }
            }

            NightPlan plan;
            try {
                plan = PlanParser.ParseFile(path);
            } catch (PlanException e) {
                Fail("plan_invalid", $"{Path.GetFileName(path)}: {e.Message}");
                throw;
            }

            var filters = profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters.Select(f => f.Name);
            var hasRotator = rotatorMediator.GetInfo().Connected;
            var issues = PlanValidator.Validate(plan, filters, hasRotator, night);

            foreach (var w in issues.Where(i => i.Severity == Severity.Warning)) {
                Logger.Warning($"[SkyShare] {w.Message}");
                Notification.ShowWarning($"SkyShare plan: {w.Message}");
            }
            var errors = issues.Where(i => i.Severity == Severity.Error).ToList();
            if (errors.Count > 0) {
                PlanStore.Set(plan, path); // so the status file lands next to the bad plan
                Fail("plan_invalid", $"{Path.GetFileName(path)}:\n- " + string.Join("\n- ", errors.Select(e => e.Message)));
            }

            PlanStore.Set(plan, path);
            LoadedSummary = $"{Path.GetFileName(path)} - {plan.Targets.Count} target(s): " +
                            string.Join(", ", plan.Targets.Select(t => t.Name));
            StatusReporter.Report("plan_loaded", LoadedSummary, new {
                night = plan.Night, client = plan.Client,
                targets = plan.Targets.Select(t => t.Name),
                warnings = issues.Where(i => i.Severity == Severity.Warning).Select(i => i.Message),
            });
        }

        private static void Fail(string @event, string message) {
            StatusReporter.Report(@event, message);
            throw new SequenceEntityFailedException(message); // VERIFY namespace: NINA.Core.Model
        }

        // ---- validation shown in the sequencer before start --------------------

        private IList<string> issues = new List<string>();
        public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }

        public bool Validate() {
            var i = new List<string>();
            if (string.IsNullOrWhiteSpace(PlansDirectory) || !Directory.Exists(PlansDirectory))
                i.Add($"Plans folder not found: {PlansDirectory}");
            Issues = i;
            return i.Count == 0;
        }

        public override object Clone() => new LoadPlan(this);

        public override string ToString() =>
            $"Category: {Category}, Item: {nameof(LoadPlan)}, Folder: {PlansDirectory}";
    }
}

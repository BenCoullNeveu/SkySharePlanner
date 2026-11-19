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
    /// SkyShare Targets / Flats / Darks containers.
    ///
    /// Fails the instruction (and therefore whatever error behaviour you set on it -
    /// typically "Abort") if there is no valid plan, so the scope never images a
    /// stale or wrong reservation.
    /// </summary>
    [ExportMetadata("Name", "SkyShare: Load Plan")]
    [ExportMetadata("Description", "Loads and validates tonight's SkyShare plan file from the plans folder.")]
    [ExportMetadata("Icon", "SkyShareLogoSVG")]  // Resources/DataTemplates.xaml
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
            MinAltitude = copyMe.MinAltitude;
            AutofocusAtStart = copyMe.AutofocusAtStart;
            DitherEvery = copyMe.DitherEvery;
            CenterDriftThreshold = copyMe.CenterDriftThreshold;
            SendNightPreview = copyMe.SendNightPreview;
            SlewCenterMinutes = copyMe.SlewCenterMinutes;
            AutofocusMinutes = copyMe.AutofocusMinutes;
            AutofocusEveryMinutes = copyMe.AutofocusEveryMinutes;
            DitherSeconds = copyMe.DitherSeconds;
            FrameOverheadSeconds = copyMe.FrameOverheadSeconds;
            MeridianFlipMinutes = copyMe.MeridianFlipMinutes;
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

        // How generated target blocks run (see TargetBuilder).

        private double minAltitude = 20;
        /// <summary>A target's exposures stop when it drops below this altitude (degrees).</summary>
        [JsonProperty]
        public double MinAltitude { get => minAltitude; set { minAltitude = Math.Clamp(value, 0, 90); RaisePropertyChanged(); } }

        private bool autofocusAtStart = true;
        /// <summary>Run autofocus after centering each target.</summary>
        [JsonProperty]
        public bool AutofocusAtStart { get => autofocusAtStart; set { autofocusAtStart = value; RaisePropertyChanged(); } }

        private int ditherEvery = 1;
        /// <summary>
        /// Fixed-count targets only: dither after this many frames of each filter (0 = never).
        /// Cycle targets always use dither-after-0 on their exposures and dither once per cycle.
        /// </summary>
        [JsonProperty]
        public int DitherEvery { get => ditherEvery; set { ditherEvery = Math.Max(0, value); RaisePropertyChanged(); } }

        private double centerDriftThreshold = 5;
        /// <summary>Center After Drift on every target: re-center beyond this drift (arcminutes).</summary>
        [JsonProperty]
        public double CenterDriftThreshold { get => centerDriftThreshold; set { centerDriftThreshold = Math.Clamp(value, 0.1, 600); RaisePropertyChanged(); } }

        // Night preview: a clear-sky estimate of the night, posted with an altitude chart.
        // These overheads only affect the estimate, not the sequence.

        private bool sendNightPreview = true;
        [JsonProperty]
        public bool SendNightPreview { get => sendNightPreview; set { sendNightPreview = value; RaisePropertyChanged(); } }

        private double slewCenterMinutes = 3;
        /// <summary>Slew, plate solve, center and rotate, per target.</summary>
        [JsonProperty]
        public double SlewCenterMinutes { get => slewCenterMinutes; set { slewCenterMinutes = Math.Clamp(value, 0, 60); RaisePropertyChanged(); } }

        private double autofocusMinutes = 4;
        [JsonProperty]
        public double AutofocusMinutes { get => autofocusMinutes; set { autofocusMinutes = Math.Clamp(value, 0, 60); RaisePropertyChanged(); } }

        private double autofocusEveryMinutes = 60;
        /// <summary>How often the template's autofocus triggers fire on average (0 = only at target start).</summary>
        [JsonProperty]
        public double AutofocusEveryMinutes { get => autofocusEveryMinutes; set { autofocusEveryMinutes = Math.Clamp(value, 0, 1440); RaisePropertyChanged(); } }

        private double ditherSeconds = 30;
        /// <summary>Dither plus guiding settle.</summary>
        [JsonProperty]
        public double DitherSeconds { get => ditherSeconds; set { ditherSeconds = Math.Clamp(value, 0, 600); RaisePropertyChanged(); } }

        private double frameOverheadSeconds = 5;
        /// <summary>Download and save, per frame.</summary>
        [JsonProperty]
        public double FrameOverheadSeconds { get => frameOverheadSeconds; set { frameOverheadSeconds = Math.Clamp(value, 0, 120); RaisePropertyChanged(); } }

        private double meridianFlipMinutes = 5;
        [JsonProperty]
        public double MeridianFlipMinutes { get => meridianFlipMinutes; set { meridianFlipMinutes = Math.Clamp(value, 0, 60); RaisePropertyChanged(); } }

        private EstimateSettings EstimateSettings => new(
            MinAltitude, AutofocusAtStart, DitherEvery,
            SlewCenterSeconds: SlewCenterMinutes * 60, AutofocusSeconds: AutofocusMinutes * 60,
            AutofocusEveryMinutes: AutofocusEveryMinutes, DitherSeconds: DitherSeconds,
            FrameOverheadSeconds: FrameOverheadSeconds, MeridianFlipSeconds: MeridianFlipMinutes * 60);

        private BuildOptions Options => new(MinAltitude, AutofocusAtStart, DitherEvery, CenterDriftThreshold);

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

            var filters = profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters.Select(f => f.Name).ToList();
            var hasRotator = rotatorMediator.GetInfo().Connected;
            var filterResolver = FilterResolver.Load(PlansDirectory); // + Plansilter_aliases.json
            var issues = PlanValidator.Validate(plan, filters, hasRotator, night, filterResolver);

            foreach (var w in issues.Where(i => i.Severity == Severity.Warning))
                StatusReporter.Warn("plan_warning", w.Message);
            var errors = issues.Where(i => i.Severity == Severity.Error).ToList();
            if (errors.Count > 0) {
                PlanStore.Set(plan, path, Options); // so the status file lands next to the bad plan
                Fail("plan_invalid", $"{Path.GetFileName(path)}:\n- " + string.Join("\n- ", errors.Select(e => e.Message)));
            }

            // Plan filters -> this profile's names (e.g. "H" -> "Ha 7nm"), so target blocks, flats
            // and the session manifest all use the real filter names.
            var renames = filterResolver.ResolveAll(plan, filters);

            PlanStore.Set(plan, path, Options); // status events go next to this plan from here on
            if (renames.Count > 0)
                StatusReporter.Report("filters_mapped", "filters matched to the NINA profile: " +
                                      string.Join(", ", renames.Select(r => $"{r.From} → {r.To}")));
            LoadedSummary = $"{Path.GetFileName(path)} - {plan.Targets.Count} target(s)" +
                            (plan.Targets.Count > 0 ? ": " + string.Join(", ", plan.Targets.Select(t => t.Name)) : "") +
                            (plan.Darks.Sets.Count > 0 ? "; " + DarkPlanner.Describe(plan.Darks) : "");

            // Fill in SkyShare Targets and a flats preview now, so tonight's blocks are
            // visible from the start. The flats preview assumes every target runs; the
            // real flats replace it when SkyShare Flats runs, after imaging.
            var root = SequenceTree.Root(this);
            var targetContainers = root is null
                ? new List<SkyShareTargets>()
                : SequenceTree.Descendants<SkyShareTargets>(root).ToList();
            foreach (var targets in targetContainers) await targets.EnsureBuilt(); // reports + rethrows on failure
            if (targetContainers.Count == 0 && plan.Targets.Count > 0)
                StatusReporter.Warn("targets_missing", "no 'SkyShare Targets' container found in this sequence; nothing will be imaged from the plan.");

            var darkContainers = root is null
                ? new List<SkyShareDarks>()
                : SequenceTree.Descendants<SkyShareDarks>(root).ToList();
            foreach (var darks in darkContainers) await darks.EnsureBuilt(); // reports + rethrows on failure
            if (darkContainers.Count == 0 && plan.Darks.Sets.Count > 0)
                StatusReporter.Warn("darks_missing", "the plan has darks but no 'SkyShare Darks' container is in this sequence; no darks will be taken.");
            if (darkContainers.Count > 1)
                StatusReporter.Warn("darks_twice", $"{darkContainers.Count} 'SkyShare Darks' containers in this sequence; each will take the plan's darks.");

            if (root != null)
                foreach (var flats in SequenceTree.Descendants<SkyShareFlats>(root)) {
                    // Only a preview: a failure here must not stop the night.
                    try { await flats.BuildPreview(); }
                    catch (Exception e) { StatusReporter.Warn("flats_preview_failed", $"could not preview flats: {e.Message}"); }
                }
            StatusReporter.Report("plan_loaded", LoadedSummary, new {
                night = plan.Night, client = plan.Client,
                targets = plan.Targets.Select(t => t.Name),
                darks = plan.Darks.Sets.Count,
                warnings = issues.Where(i => i.Severity == Severity.Warning).Select(i => i.Message),
            });

            if (SendNightPreview) await NightPreview.Publish(plan, path, profileService, EstimateSettings);
        }

        private static void Fail(string @event, string message) {
            StatusReporter.Error(@event, message);
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

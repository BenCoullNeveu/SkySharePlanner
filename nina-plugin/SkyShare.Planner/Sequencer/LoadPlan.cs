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
        private readonly ICameraMediator cameraMediator;

        [ImportingConstructor]
        public LoadPlan(IProfileService profileService, IRotatorMediator rotatorMediator, ICameraMediator cameraMediator) {
            this.profileService = profileService;
            this.rotatorMediator = rotatorMediator;
            this.cameraMediator = cameraMediator;
        }

        private LoadPlan(LoadPlan copyMe) : this(copyMe.profileService, copyMe.rotatorMediator, copyMe.cameraMediator) {
            CopyMetaData(copyMe);
        }

        // ---- settings ------------------------------------------------------------
        // None in the template: the plans folder, how targets run and the estimate
        // overheads are on Options > Plugins > SkyShare Planner (SkyShareOptions),
        // stored in the NINA profile, so every sequence uses the same values.

        /// <summary>The plugin-wide settings, for the instruction's view.</summary>
        public SkyShareOptions Settings => SkyShareOptions.Get(profileService);

        // ---- shown in the UI after running ------------------------------------

        private string loadedSummary = "";
        public string LoadedSummary { get => loadedSummary; private set { loadedSummary = value; RaisePropertyChanged(); } }

        // ---- run --------------------------------------------------------------

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var o = Settings;
            var plansDirectory = o.PlansDirectory;
            var options = o.BuildOptions;
            var night = PlanLocator.NightOf(DateTime.Now, o.RolloverHour);
            var deadline = DateTime.Now.AddMinutes(o.WaitForPlanMinutes);
            string path;

            while (true) {
                try {
                    path = PlanLocator.Find(plansDirectory, night);
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
            var filterResolver = FilterResolver.Load(plansDirectory); // + <plans folder>/filter_aliases.json
            var issues = PlanValidator.Validate(plan, filters, hasRotator, night, filterResolver);

            foreach (var w in issues.Where(i => i.Severity == Severity.Warning))
                StatusReporter.Warn("plan_warning", w.Message);
            var errors = issues.Where(i => i.Severity == Severity.Error).ToList();
            if (errors.Count > 0) {
                PlanStore.Set(plan, path, options); // so the status file lands next to the bad plan
                Fail("plan_invalid", $"{Path.GetFileName(path)}:\n- " + string.Join("\n- ", errors.Select(e => e.Message)));
            }

            // Plan filters -> this profile's names (e.g. "H" -> "Ha 7nm"), so target blocks, flats
            // and the session manifest all use the real filter names.
            var renames = filterResolver.ResolveAll(plan, filters);

            PlanStore.Set(plan, path, options); // status events go next to this plan from here on
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

            // The session container first: on a calibration night it swaps out When Safe /
            // When Unsafe (and the SkyShare Targets inside), on an imaging night it puts them back.
            var sessions = root is null
                ? new List<SkyShareSession>()
                : SequenceTree.Descendants<SkyShareSession>(root).ToList();
            foreach (var session in sessions) await session.EnsureBuilt(); // reports + rethrows on failure
            if (sessions.Count > 1)
                StatusReporter.Warn("session_twice", $"{sessions.Count} 'SkyShareAstro Session' containers in this sequence; use one.");

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
            if (darkContainers.Count == 0 && plan.Darks.Sets.Count > 0 && !PlanStore.DarksInSession)
                StatusReporter.Warn("darks_missing", "the plan has darks but no 'SkyShare Darks' container is in this sequence; no darks will be taken.");
            if (darkContainers.Count > 1 && !PlanStore.DarksInSession)
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

            if (o.SendNightPreview) await NightPreview.Publish(plan, path, profileService, o.EstimateSettings,
                                                           o.SendFramingPreview ? cameraMediator : null);
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
            var folder = Settings.PlansDirectory;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                i.Add($"Plans folder not found: {folder} (set it in Options > Plugins > SkyShare Planner)");
            Issues = i;
            return i.Count == 0;
        }

        public override object Clone() => new LoadPlan(this);

        public override string ToString() =>
            $"Category: {Category}, Item: {nameof(LoadPlan)}, Folder: {Settings.PlansDirectory}";
    }
}

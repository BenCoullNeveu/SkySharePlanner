using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NINA.Profile.Interfaces;
using SkyShare.Planner.Core;

namespace SkyShare.Planner.Runtime {

    /// <summary>
    /// Runs Target Scheduler's own plan preview (the one in TS's "Plan Preview" panel)
    /// and turns it into plain SkyShare records.
    ///
    /// TS is found by type name among the loaded assemblies, so SkyShare has no hard
    /// dependency on it: without TS this reports "not installed" and does nothing.
    /// Everything used here is public TS API (5.x):
    ///   SchedulerDatabaseInteraction, SchedulerPlanLoader, PreviewPlanner,
    ///   SchedulerPlan / ITarget / IExposure / ExposureCompletionHelper.
    ///
    /// The preview works on in-memory copies of the projects; it never writes to the
    /// TS database. Completion "before" is read before the preview runs, "after" from
    /// the same objects once the preview has counted its frames.
    /// </summary>
    public static class TargetSchedulerBridge {
        private const string Ns = "NINA.Plugin.TargetScheduler.";

        public static bool IsInstalled => FindType("Planning.PreviewPlanner") != null;

        public static TsPreview Preview(IProfileService profileService, DateTime at) {
            var previewType = FindType("Planning.PreviewPlanner")
                ?? throw new InvalidOperationException("Target Scheduler is not installed (or not loaded yet)");
            var dbType = FindType("Database.SchedulerDatabaseInteraction")!;
            var loaderType = FindType("Database.SchedulerPlanLoader")!;
            var profile = profileService.ActiveProfile;

            dynamic db = Activator.CreateInstance(dbType)!;
            dynamic loader = Activator.CreateInstance(loaderType, profile)!;
            // each call disposes its context, as in TS's own preview panel
            object? projects = loader.LoadActiveProjects(db.GetContext());
            object prefs = loader.GetProfilePreferences(db.GetContext());
            if (projects is not IList list || list.Count == 0)
                return new TsPreview(at, profile.Name, Array.Empty<TsFrame>(), Array.Empty<TsTarget>());

            // Snapshot targets and their completion before the preview counts frames.
            var targets = new List<(dynamic Target, dynamic Project, string Key, double Before)>();
            foreach (dynamic p in list) {
                foreach (dynamic t in p.Targets) {
                    t.IsPreview = true;
                    string key = $"{(int)p.DatabaseId}/{(int)t.DatabaseId}";
                    targets.Add((t, p, key, (double)p.ExposureCompletionHelper.PercentComplete(t)));
                }
            }

            var method = previewType.GetMethod("GetPlanPreview")!;
            var plans = (IEnumerable)method.Invoke(Activator.CreateInstance(previewType), new object?[] { at, profileService, prefs, projects })!;

            var frames = new List<TsFrame>();
            foreach (dynamic plan in plans) {
                if ((bool)plan.IsWait) continue;
                dynamic target = plan.PlanTarget;
                string key = $"{(int)target.Project.DatabaseId}/{(int)target.DatabaseId}";
                dynamic? exposure = null;
                foreach (dynamic ins in plan.PlanInstructions)
                    if (((object)ins).GetType().Name == "PlanTakeExposure") exposure = ins.exposure;
                DateTime start = plan.StartTime, end = plan.EndTime;
                frames.Add(new TsFrame(key,
                    exposure is null ? "?" : (string)exposure.FilterName,
                    exposure is null ? (end - start).TotalSeconds : (double)exposure.ExposureLength,
                    start, end));
            }

            var result = targets.Select(x => {
                dynamic coords = x.Target.Coordinates;
                return new TsTarget(x.Key, (string)x.Target.Name, (string)x.Project.Name,
                    (double)coords.RA, (double)coords.Dec, (double)x.Project.MinimumAltitude,
                    x.Before, (double)x.Project.ExposureCompletionHelper.PercentComplete(x.Target),
                    (double)x.Target.Rotation);
            }).ToList();
            return new TsPreview(at, profile.Name, frames, result);
        }

        private static Type? FindType(string name) =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.StartsWith("NINA.Plugin.TargetScheduler", StringComparison.Ordinal) == true)
                .Select(a => a.GetType(Ns + name, throwOnError: false))
                .FirstOrDefault(t => t != null);
    }
}

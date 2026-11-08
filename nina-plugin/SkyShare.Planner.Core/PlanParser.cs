using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SkyShare.Planner.Core {

    public static class PlanParser {
        public const int SupportedSchemaVersion = 1;

        private static readonly JsonSerializerOptions Options = new() {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public static NightPlan ParseFile(string path) => Parse(File.ReadAllText(path));

        public static NightPlan Parse(string json) {
            NightPlan? plan;
            try {
                plan = JsonSerializer.Deserialize<NightPlan>(json, Options);
            } catch (JsonException e) {
                throw new PlanException($"Plan is not valid JSON: {e.Message}");
            }
            if (plan is null) throw new PlanException("Plan file is empty");
            return plan;
        }
    }

    public enum Severity { Warning, Error }

    public sealed record PlanIssue(Severity Severity, string Message) {
        public override string ToString() => $"{Severity}: {Message}";
    }

    /// <summary>
    /// Semantic validation. Same rules as python/skyshare_plan.validate(),
    /// plus checks that need the acquisition PC (filter names, rotator).
    /// </summary>
    public static class PlanValidator {

        public static IReadOnlyList<PlanIssue> Validate(
            NightPlan plan,
            IEnumerable<string>? profileFilters = null,
            bool hasRotator = false,
            DateOnly? expectedNight = null) {

            var issues = new List<PlanIssue>();
            void Err(string m) => issues.Add(new PlanIssue(Severity.Error, m));
            void Warn(string m) => issues.Add(new PlanIssue(Severity.Warning, m));

            if (plan.SchemaVersion != PlanParser.SupportedSchemaVersion)
                Err($"schema_version {plan.SchemaVersion} not supported (plugin understands {PlanParser.SupportedSchemaVersion})");

            if (!DateOnly.TryParseExact(plan.Night, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var night))
                Err($"night '{plan.Night}' must be YYYY-MM-DD");
            else if (expectedNight is { } en && night != en)
                Err($"plan is for night {plan.Night} but tonight is {PlanLocator.NightKey(en)}");

            if (plan.Targets.Count == 0) Err("plan has no targets");
            if (plan.Flats.Count is < 1 or > 200) Err("flats.count must be 1-200");

            if (plan.Processing.Triage is not (null or "local" or "web" or "send" or "build"))
                Err($"processing.triage '{plan.Processing.Triage}' must be local, web, send, build or null");
            if (plan.Targets.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Targets.Count)
                Err("target names must be unique (they are SkyStack folder names)");

            var filters = profileFilters?.ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < plan.Targets.Count; i++) {
                var t = plan.Targets[i];
                var tag = $"target {i + 1} ({t.Name})";
                if (string.IsNullOrWhiteSpace(t.Name)) Err($"{tag}: name is empty");
                if (!Sexagesimal.TryParseRaHours(t.Ra, out _)) Err($"{tag}: bad ra '{t.Ra}'");
                if (!Sexagesimal.TryParseDecDegrees(t.Dec, out _)) Err($"{tag}: bad dec '{t.Dec}'");
                if (t.Rotation is < 0 or >= 360) Err($"{tag}: rotation must be 0-359.9");
                if (t.Start is not null && !Sexagesimal.TryParseHhMm(t.Start, out _)) Err($"{tag}: bad start '{t.Start}'");
                if (t.End is not null && !Sexagesimal.TryParseHhMm(t.End, out _)) Err($"{tag}: bad end '{t.End}'");
                if (t.Exposures.Count == 0) Err($"{tag}: no exposures");

                foreach (var e in t.Exposures) {
                    var et = $"{tag}/{e.Filter}";
                    if (e.Exposure is <= 0 or > 3600) Err($"{et}: exposure must be 0-3600 s");
                    if (e.Count is < 1 or > 2000) Err($"{et}: count must be 1-2000");
                    if (e.Gain < -1 || e.Offset < -1) Err($"{et}: gain/offset must be >= -1");
                    if (e.Binning is < 1 or > 4) Err($"{et}: binning must be 1-4");
                    if (filters is not null && !filters.Contains(e.Filter))
                        Err($"{et}: filter not in NINA profile (have: {string.Join(", ", filters)})");
                }
            }

            // Order sanity: a target's start should not be before the previous target's end.
            // (Times wrap at midnight, so only warn - the sequence still runs.)
            for (int i = 1; i < plan.Targets.Count; i++) {
                var prev = plan.Targets[i - 1];
                var cur = plan.Targets[i];
                if (prev.End is null && cur.Start is not null)
                    Warn($"target {i} has no end time; target {i + 1} will start only after it finishes, regardless of its start time");
            }

            var rotations = plan.Targets.Select(t => Math.Round(t.Rotation, 1)).Distinct().Count();
            if (!hasRotator && rotations > 1)
                Warn($"{rotations} different rotations but no rotator connected - only flats matching the final orientation can be taken");

            return issues;
        }
    }
}

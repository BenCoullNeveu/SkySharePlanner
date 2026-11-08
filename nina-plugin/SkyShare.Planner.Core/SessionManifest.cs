using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkyShare.Planner.Core {

    // =========================================================================
    // Session manifest (schema v1) - written by "SkyShare: Session Complete" at
    // the end of the night, read by SkyStack's session watcher on the compute
    // server. It says what was captured (so the server can wait for every file
    // to arrive through Dropbox) and what to do with it (explicit actions).
    // See schema/session.schema.json.
    // =========================================================================

    public sealed class SessionManifest {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 1;
        [JsonPropertyName("kind")] public string Kind { get; init; } = "skyshare_session";
        [JsonPropertyName("night")] public string Night { get; init; } = "";
        [JsonPropertyName("client")] public string? Client { get; init; }
        [JsonPropertyName("plan_file")] public string PlanFile { get; init; } = "";
        [JsonPropertyName("completed_utc")] public string CompletedUtc { get; init; } = "";
        /// <summary>complete | partial | nothing_captured</summary>
        [JsonPropertyName("outcome")] public string Outcome { get; init; } = "";
        [JsonPropertyName("targets")] public List<ManifestTarget> Targets { get; init; } = new();
        [JsonPropertyName("flats")] public ManifestFlats Flats { get; init; } = new();
        /// <summary>What the compute server must do, in order. Empty = nothing to process.</summary>
        [JsonPropertyName("actions")] public List<ManifestAction> Actions { get; init; } = new();

        private static readonly JsonSerializerOptions Json = new() {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        public string ToJson() => JsonSerializer.Serialize(this, Json);
        public static SessionManifest FromJson(string s) =>
            JsonSerializer.Deserialize<SessionManifest>(s, Json) ?? throw new PlanException("empty manifest");
    }

    public sealed class ManifestTarget {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        /// <summary>Session folder under SkyStack LIGHT/&lt;name&gt;/ (= night; NINA pattern $$DATEMINUS12$$).</summary>
        [JsonPropertyName("session")] public string Session { get; init; } = "";
        [JsonPropertyName("ran")] public bool Ran { get; init; }
        [JsonPropertyName("lights")] public Dictionary<string, int> Lights { get; init; } = new();
        [JsonPropertyName("lights_total")] public int LightsTotal { get; init; }
        [JsonPropertyName("planned")] public Dictionary<string, int> Planned { get; init; } = new();
        [JsonPropertyName("planned_total")] public int PlannedTotal { get; init; }
    }

    public sealed class ManifestFlats {
        [JsonPropertyName("sets")] public List<ManifestFlatSet> Sets { get; init; } = new();
        [JsonPropertyName("frames_total")] public int FramesTotal { get; init; }
        [JsonPropertyName("skipped")] public List<string> Skipped { get; init; } = new();
    }

    public sealed class ManifestFlatSet {
        [JsonPropertyName("filter")] public string Filter { get; init; } = "";
        [JsonPropertyName("gain")] public int Gain { get; init; }
        [JsonPropertyName("offset")] public int Offset { get; init; }
        [JsonPropertyName("binning")] public int Binning { get; init; }
        [JsonPropertyName("rotation")] public double Rotation { get; init; }
        [JsonPropertyName("frames")] public int Frames { get; init; }
    }

    /// <summary>
    /// One instruction for the compute server. v1 has a single type:
    ///   "skystack_run" = python run_routine.py run -t &lt;target&gt; -s &lt;session&gt; [options]
    /// Unknown types must be ignored (and reported) by the server, so new ones
    /// can be added without breaking older SkyStack versions.
    /// </summary>
    public sealed class ManifestAction {
        [JsonPropertyName("type")] public string Type { get; init; } = "skystack_run";
        [JsonPropertyName("target")] public string Target { get; init; } = "";
        [JsonPropertyName("sessions")] public List<string> Sessions { get; init; } = new();
        [JsonPropertyName("triage")] public string? Triage { get; init; }
        [JsonPropertyName("postprocess")] public bool? Postprocess { get; init; }
        [JsonPropertyName("export")] public bool? Export { get; init; }
    }

    /// <summary>Flat set as actually executed tonight.</summary>
    public sealed record FlatSetResult(FlatSpec Spec, double SkyRotation, int FramesTaken);

    public static class SessionManifestBuilder {

        /// <param name="lightsByTarget">plan target index → filter → frames completed (only targets that were built)</param>
        public static SessionManifest Build(
            NightPlan plan,
            string planFileName,
            IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>> lightsByTarget,
            IReadOnlyList<FlatSetResult> flats,
            IReadOnlyList<string> flatsSkipped,
            DateTime completedUtc) {

            var targets = new List<ManifestTarget>();
            for (int i = 0; i < plan.Targets.Count; i++) {
                var t = plan.Targets[i];
                var planned = t.Exposures
                    .GroupBy(e => e.Filter, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Sum(e => e.Count));
                var lights = lightsByTarget.TryGetValue(i, out var l)
                    ? l.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value)
                    : new Dictionary<string, int>();
                targets.Add(new ManifestTarget {
                    Name = t.Name,
                    Session = plan.Night,
                    Ran = lights.Values.Sum() > 0,
                    Lights = lights,
                    LightsTotal = lights.Values.Sum(),
                    Planned = planned,
                    PlannedTotal = planned.Values.Sum(),
                });
            }

            int captured = targets.Sum(t => t.LightsTotal);
            string outcome = captured == 0 ? "nothing_captured"
                : targets.All(t => t.LightsTotal >= t.PlannedTotal) ? "complete"
                : "partial";

            var actions = !plan.Processing.Enabled ? new List<ManifestAction>()
                : targets.Where(t => t.Ran).Select(t => new ManifestAction {
                    Type = "skystack_run",
                    Target = t.Name,
                    Sessions = new List<string> { t.Session },
                    Triage = plan.Processing.Triage,
                    Postprocess = plan.Processing.Postprocess,
                    Export = plan.Processing.Export,
                }).ToList();

            return new SessionManifest {
                Night = plan.Night,
                Client = plan.Client,
                PlanFile = planFileName,
                CompletedUtc = completedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                Outcome = outcome,
                Targets = targets,
                Flats = new ManifestFlats {
                    Sets = flats.Select(f => new ManifestFlatSet {
                        Filter = f.Spec.Filter, Gain = f.Spec.Gain, Offset = f.Spec.Offset,
                        Binning = f.Spec.Binning, Rotation = f.SkyRotation, Frames = f.FramesTaken,
                    }).ToList(),
                    FramesTotal = flats.Sum(f => f.FramesTaken),
                    Skipped = flatsSkipped.ToList(),
                },
                Actions = actions,
            };
        }

        /// <summary>"2026-11-07_Jane.plan.json" → "2026-11-07_Jane.session.json"</summary>
        public static string ManifestFileName(string planFileName) =>
            planFileName.EndsWith(PlanLocator.Suffix, StringComparison.OrdinalIgnoreCase)
                ? planFileName[..^PlanLocator.Suffix.Length] + ".session.json"
                : planFileName + ".session.json";
    }
}

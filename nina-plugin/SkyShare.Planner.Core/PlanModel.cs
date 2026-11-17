// Plan model - mirrors schema/plan.schema.json (v1).
// No NINA references: this project builds and tests on any OS.
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SkyShare.Planner.Core {

    public sealed class NightPlan {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyName("night")] public string Night { get; set; } = "";
        [JsonPropertyName("client")] public string? Client { get; set; }
        [JsonPropertyName("created_by")] public string? CreatedBy { get; set; }
        [JsonPropertyName("created_utc")] public string? CreatedUtc { get; set; }
        [JsonPropertyName("targets")] public List<PlanTarget> Targets { get; set; } = new();
        [JsonPropertyName("flats")] public FlatSettings Flats { get; set; } = new();
        /// <summary>Dark frames to capture (SkyShare Darks container). Empty sets = no darks.</summary>
        [JsonPropertyName("darks")] public DarkSettings Darks { get; set; } = new();
        [JsonPropertyName("processing")] public ProcessingSettings Processing { get; set; } = new();
    }

    public sealed class PlanTarget {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("ra")] public string Ra { get; set; } = "";
        [JsonPropertyName("dec")] public string Dec { get; set; } = "";
        [JsonPropertyName("rotation")] public double Rotation { get; set; }
        [JsonPropertyName("start")] public string? Start { get; set; }
        [JsonPropertyName("end")] public string? End { get; set; }

        /// <summary>
        /// false (default): shoot each filter's full count in turn.
        /// true: repeat cycles of exposures[].count frames per filter (e.g. L×3 R×1 G×1 B×1),
        /// dithering once per cycle, until a limit stops the target.
        /// </summary>
        [JsonPropertyName("cycle")] public bool Cycle { get; set; }

        // Limits: whichever is reached first stops the target (in either mode).
        [JsonPropertyName("max_frames")] public int? MaxFrames { get; set; }
        [JsonPropertyName("max_hours")] public double? MaxHours { get; set; }
        /// <summary>Stop when the next target in the plan rises above this altitude (degrees).</summary>
        [JsonPropertyName("next_target_altitude")] public double? NextTargetAltitude { get; set; }

        [JsonPropertyName("exposures")] public List<PlanExposure> Exposures { get; set; } = new();
    }

    public sealed class PlanExposure {
        [JsonPropertyName("filter")] public string Filter { get; set; } = "";
        [JsonPropertyName("exposure")] public double Exposure { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("gain")] public int Gain { get; set; } = -1;
        [JsonPropertyName("offset")] public int Offset { get; set; } = -1;
        [JsonPropertyName("binning")] public int Binning { get; set; } = 1;
    }

    /// <summary>What SkyStack should do after the night; copied into the session manifest as actions.</summary>
    public sealed class ProcessingSettings {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("triage")] public string? Triage { get; set; }          // local|web|send|build|null
        [JsonPropertyName("postprocess")] public bool? Postprocess { get; set; }
        [JsonPropertyName("export")] public bool? Export { get; set; }
    }

    /// <summary>
    /// A dark-frame session. Fixed sets take exactly `count` frames. Repeating sets
    /// ("repeat": true) take `count` frames each per round, round after round, until
    /// `until` (dawn or a clock time). By default the fixed sets go first.
    /// </summary>
    public sealed class DarkSettings {
        /// <summary>fixed_first (default) | repeat_first</summary>
        [JsonPropertyName("order")] public string Order { get; set; } = DarkPlanner.FixedFirst;
        /// <summary>astronomical_dawn (default) | nautical_dawn | civil_dawn | sunrise | HH:MM</summary>
        [JsonPropertyName("until")] public string Until { get; set; } = DarkPlanner.AstronomicalDawn;
        /// <summary>Cool the camera to this set point (°C) before the darks. null = leave as is.</summary>
        [JsonPropertyName("temperature")] public double? Temperature { get; set; }
        [JsonPropertyName("sets")] public List<DarkSet> Sets { get; set; } = new();
    }

    public sealed class DarkSet {
        [JsonPropertyName("exposure")] public double Exposure { get; set; }
        /// <summary>Total frames for a fixed set; frames per round for a repeating set.</summary>
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("gain")] public int Gain { get; set; } = -1;
        [JsonPropertyName("offset")] public int Offset { get; set; } = -1;
        [JsonPropertyName("binning")] public int Binning { get; set; } = 1;
        [JsonPropertyName("repeat")] public bool Repeat { get; set; }

        public override string ToString() =>
            $"{Exposure:0.###}s" + (Gain >= 0 ? $" g{Gain}" : "") + (Offset >= 0 ? $" o{Offset}" : "") +
            (Binning > 1 ? $" bin{Binning}" : "") + $" ×{Count}" + (Repeat ? " per round" : "");
    }

    public sealed class FlatSettings {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("count")] public int Count { get; set; } = 30;
    }
}

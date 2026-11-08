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
        [JsonPropertyName("processing")] public ProcessingSettings Processing { get; set; } = new();
    }

    public sealed class PlanTarget {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("ra")] public string Ra { get; set; } = "";
        [JsonPropertyName("dec")] public string Dec { get; set; } = "";
        [JsonPropertyName("rotation")] public double Rotation { get; set; }
        [JsonPropertyName("start")] public string? Start { get; set; }
        [JsonPropertyName("end")] public string? End { get; set; }
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

    public sealed class FlatSettings {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("count")] public int Count { get; set; } = 30;
    }
}

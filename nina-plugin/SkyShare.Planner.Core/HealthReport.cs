using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyShare.Planner.Core {

    public enum HealthLevel { Ok, Warn, Fail }

    /// <summary>One line of the start-of-night health check.</summary>
    public sealed record HealthCheck(string Name, HealthLevel Level, string Detail);

    /// <summary>Formats the health check for Discord and the sequence item. Plain text, no emojis.</summary>
    public static class HealthReport {

        public static HealthLevel Worst(IEnumerable<HealthCheck> checks) =>
            checks.Select(c => c.Level).DefaultIfEmpty(HealthLevel.Ok).Max();

        public static string Label(HealthLevel l) => l switch { HealthLevel.Ok => "OK", HealthLevel.Warn => "WARN", _ => "FAIL" };

        /// <summary>"All 9 checks OK" or "2 FAIL, 1 WARN of 9 checks".</summary>
        public static string Summary(IReadOnlyCollection<HealthCheck> checks) {
            int fail = checks.Count(c => c.Level == HealthLevel.Fail), warn = checks.Count(c => c.Level == HealthLevel.Warn);
            if (fail + warn == 0) return $"all {checks.Count} checks OK";
            var parts = new List<string>();
            if (fail > 0) parts.Add($"{fail} FAIL");
            if (warn > 0) parts.Add($"{warn} WARN");
            return $"{string.Join(", ", parts)} of {checks.Count} checks";
        }

        /// <summary>Discord message: a heading, then one aligned line per check in a code block.</summary>
        public static string Markdown(IReadOnlyCollection<HealthCheck> checks, DateTime at, string? profile = null) {
            var head = $"**Health check {at:yyyy-MM-dd HH:mm}**" + (string.IsNullOrWhiteSpace(profile) ? "" : $" — {profile}") +
                       $" · {Summary(checks)}";
            var width = checks.Select(c => c.Name.Length).DefaultIfEmpty(0).Max();
            var body = checks.OrderByDescending(c => c.Level)
                             .Select(c => $"{Label(c.Level),-4}  {c.Name.PadRight(width)}  {c.Detail}");
            var text = head + "\n```\n" + string.Join("\n", body) + "\n```";
            return text.Length <= 1900 ? text : text[..1880] + "\n…\n```";
        }

        /// <summary>Disk space check: estimated need (bytes) vs free space, with a fixed minimum on top.</summary>
        public static HealthCheck Disk(string folder, long freeBytes, long neededBytes, double minFreeGb) {
            double free = freeBytes / 1e9, need = neededBytes / 1e9;
            var detail = $"{free:0.#} GB free in {folder}" + (neededBytes > 0 ? $", tonight needs ~{need:0.#} GB" : "");
            if (free < need) return new HealthCheck("Disk space", HealthLevel.Fail, detail);
            if (free < need + minFreeGb) return new HealthCheck("Disk space", HealthLevel.Warn, detail + $" (less than {minFreeGb:0} GB spare)");
            return new HealthCheck("Disk space", HealthLevel.Ok, detail);
        }

        /// <summary>Clock offset against an internet time server, in seconds (positive = PC is ahead).</summary>
        public static HealthCheck Clock(double? offsetSeconds) {
            if (offsetSeconds is not { } o)
                return new HealthCheck("Clock", HealthLevel.Warn, "could not reach a time server (no internet?)");
            var detail = $"{o:+0.0;-0.0} s from internet time";
            var a = Math.Abs(o);
            return new HealthCheck("Clock", a > 30 ? HealthLevel.Fail : a > 2 ? HealthLevel.Warn : HealthLevel.Ok, detail);
        }

        /// <summary>Camera cooling: cooler on and within 1.5 °C of the set point.</summary>
        public static HealthCheck Cooling(bool canCool, bool coolerOn, double temperature, double setPoint) {
            if (!canCool) return new HealthCheck("Camera cooling", HealthLevel.Ok, "camera has no cooler");
            if (!coolerOn) return new HealthCheck("Camera cooling", HealthLevel.Warn, $"cooler off, sensor at {temperature:0.0}°C");
            var detail = $"{temperature:0.0}°C, set point {setPoint:0.0}°C";
            return new HealthCheck("Camera cooling", Math.Abs(temperature - setPoint) > 1.5 ? HealthLevel.Warn : HealthLevel.Ok,
                                   Math.Abs(temperature - setPoint) > 1.5 ? detail + " (not there yet)" : detail);
        }
    }
}

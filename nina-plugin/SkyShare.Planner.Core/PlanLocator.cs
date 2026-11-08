using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SkyShare.Planner.Core {

    /// <summary>Finds tonight's plan file in the Dropbox plans folder.</summary>
    public static class PlanLocator {
        public const string Suffix = ".plan.json";

        /// <summary>
        /// The "night" a moment belongs to. Before <paramref name="rolloverHour"/>
        /// (default noon) we are still in last evening's night, so 02:00 on the 8th
        /// resolves to night 2026-11-07.
        /// </summary>
        public static DateOnly NightOf(DateTime localNow, int rolloverHour = 12) {
            var d = DateOnly.FromDateTime(localNow);
            return localNow.Hour < rolloverHour ? d.AddDays(-1) : d;
        }

        public static string NightKey(DateOnly night) =>
            night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>
        /// Returns the single plan file for <paramref name="night"/>.
        /// Ignores temp files (Dropbox / atomic-write leftovers).
        /// </summary>
        /// <exception cref="PlanException">none found, or more than one (ambiguous reservation)</exception>
        public static string Find(string plansDirectory, DateOnly night) {
            if (!Directory.Exists(plansDirectory))
                throw new PlanException($"Plans folder not found: {plansDirectory}");

            var key = NightKey(night);
            var matches = Directory.EnumerateFiles(plansDirectory, $"{key}_*{Suffix}")
                .Where(p => !Path.GetFileName(p).StartsWith("."))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return matches.Count switch {
                0 => throw new PlanException($"No plan for night {key} in {plansDirectory}"),
                1 => matches[0],
                _ => throw new PlanException(
                    $"{matches.Count} plans for night {key} - expected one: " +
                    string.Join(", ", matches.Select(Path.GetFileName)))
            };
        }
    }

    public sealed class PlanException : Exception {
        public PlanException(string message) : base(message) { }
    }
}

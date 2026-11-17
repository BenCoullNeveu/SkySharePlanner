using System.Collections.Generic;
using System.Linq;

namespace SkyShare.Planner.Core {

    public enum DarkPhaseKind { Fixed, Repeat }

    /// <summary>One step of a dark session: plan set indices, run in order.</summary>
    public sealed record DarkPhase(DarkPhaseKind Kind, IReadOnlyList<int> SetIndices);

    /// <summary>
    /// Orders a plan's dark sets into phases. No NINA references, so it's unit tested.
    ///   Fixed phase:  each fixed set once, `count` frames each.
    ///   Repeat phase: rounds of `count` frames from each repeating set, until `until`.
    /// fixed_first (default): fixed phase, then repeat phase. repeat_first: the reverse,
    /// so the fixed sets are taken after `until` (fine for darks if the camera is covered).
    /// </summary>
    public static class DarkPlanner {
        public const string FixedFirst = "fixed_first";
        public const string RepeatFirst = "repeat_first";
        public const string AstronomicalDawn = "astronomical_dawn";
        public const string NauticalDawn = "nautical_dawn";
        public const string CivilDawn = "civil_dawn";
        public const string Sunrise = "sunrise";

        public static readonly IReadOnlyList<string> Orders = new[] { FixedFirst, RepeatFirst };
        public static readonly IReadOnlyList<string> UntilEvents = new[] { AstronomicalDawn, NauticalDawn, CivilDawn, Sunrise };

        public static bool IsValidUntil(string? until) =>
            until is not null && (UntilEvents.Contains(until) || Sexagesimal.TryParseHhMm(until, out _));

        public static IReadOnlyList<DarkPhase> Phases(DarkSettings darks) {
            var fixedSets = darks.Sets.Select((s, i) => (s, i)).Where(x => !x.s.Repeat).Select(x => x.i).ToList();
            var repeatSets = darks.Sets.Select((s, i) => (s, i)).Where(x => x.s.Repeat).Select(x => x.i).ToList();

            var phases = new List<DarkPhase>();
            void AddFixed() { if (fixedSets.Count > 0) phases.Add(new DarkPhase(DarkPhaseKind.Fixed, fixedSets)); }
            void AddRepeat() { if (repeatSets.Count > 0) phases.Add(new DarkPhase(DarkPhaseKind.Repeat, repeatSets)); }

            if (darks.Order == RepeatFirst) { AddRepeat(); AddFixed(); }
            else { AddFixed(); AddRepeat(); }
            return phases;
        }

        /// <summary>"astronomical_dawn" -> "astronomical dawn"; "05:30" stays.</summary>
        public static string UntilLabel(string until) => until.Replace('_', ' ');

        /// <summary>Short text for the Load Plan summary and logs.</summary>
        public static string Describe(DarkSettings darks) {
            if (darks.Sets.Count == 0) return "no darks";
            var parts = Phases(darks).Select(p => p.Kind == DarkPhaseKind.Fixed
                ? string.Join(", ", p.SetIndices.Select(i => darks.Sets[i].ToString()))
                : $"rounds of ({string.Join(", ", p.SetIndices.Select(i => darks.Sets[i].ToString()))}) until {UntilLabel(darks.Until)}");
            var cool = darks.Temperature is { } t ? $" at {t:0.#}°C" : "";
            return $"darks{cool}: {string.Join(", then ", parts)}";
        }
    }
}

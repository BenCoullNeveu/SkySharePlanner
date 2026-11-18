using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SkyShare.Planner.Core {

    /// <summary>
    /// Works out which profile filter a plan's filter name refers to.
    ///
    /// Plans store a standard name (H, O, S, L, R, G, B); a NINA profile may spell the same
    /// filter differently ("Ha 7nm", "OIII", "Lum", "Red"). Matching, first hit wins:
    ///   1. exact name
    ///   2. same name ignoring case, spaces, dashes, dots and a bandwidth suffix ("7nm")
    ///   3. same standard filter (both names are aliases of e.g. H: Ha, H-alpha, Halpha, ...)
    ///
    /// Same table and rules as filter_names.py in the Discord bot. Extra spellings can be added
    /// to Plans\filter_aliases.json ({"aliases": {"H": ["My Ha"]}}), which the bot also uses.
    /// </summary>
    public sealed class FilterResolver {
        public const string AliasFileName = "filter_aliases.json";

        // Declared before Default: static fields initialise in order, and Default's constructor uses these.
        private static readonly Regex Bandwidth = new(@"\d+(?:[.,]\d+)?\s*nm$", RegexOptions.Compiled);
        private static readonly Regex NonAlnum = new(@"[^0-9a-z]+", RegexOptions.Compiled);

        public static readonly IReadOnlyDictionary<string, string[]> DefaultAliases = new Dictionary<string, string[]> {
            ["L"] = new[] { "L", "Lum", "Lumi", "Luminance", "Clear", "C", "UVIR", "UV/IR", "UVIR cut", "UV-IR cut", "IR cut", "L3", "Lum L3" },
            ["R"] = new[] { "R", "Red" },
            ["G"] = new[] { "G", "Green" },
            ["B"] = new[] { "B", "Blue" },
            ["H"] = new[] { "H", "Ha", "H-a", "H alpha", "H-alpha", "Halpha", "Hα", "Hydrogen", "Hydrogen alpha", "Hydrogen-alpha" },
            ["O"] = new[] { "O", "O3", "OIII", "O-III", "O 3", "Oxygen", "Oxygen III", "Oxygen-III" },
            ["S"] = new[] { "S", "S2", "SII", "S-II", "S 2", "Sulfur", "Sulphur", "Sulfur II", "Sulphur II" },
        };

        public static FilterResolver Default { get; } = new(DefaultAliases);

        private readonly Dictionary<string, string> lookup = new(); // normalised spelling -> standard name

        public FilterResolver(IReadOnlyDictionary<string, string[]> aliases) {
            foreach (var kv in aliases)
                foreach (var n in kv.Value.Append(kv.Key))
                    lookup.TryAdd(Normalise(n), kv.Key);
        }

        /// <summary>Built-in table plus any extra spellings in <paramref name="plansDir"/>\filter_aliases.json.</summary>
        public static FilterResolver Load(string? plansDir) {
            var aliases = DefaultAliases.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
            try {
                var path = string.IsNullOrEmpty(plansDir) ? null : Path.Combine(plansDir, AliasFileName);
                if (path != null && File.Exists(path)) {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty("aliases", out var a) && a.ValueKind == JsonValueKind.Object)
                        foreach (var std in a.EnumerateObject()) {
                            if (!aliases.TryGetValue(std.Name, out var list)) aliases[std.Name] = list = new List<string>();
                            if (std.Value.ValueKind != JsonValueKind.Array) continue;
                            foreach (var n in std.Value.EnumerateArray())
                                if (n.ValueKind == JsonValueKind.String && n.GetString() is { } s && !list.Contains(s)) list.Add(s);
                        }
                }
            } catch (Exception) { /* unreadable alias file -> built-in table only */ }
            return new FilterResolver(aliases.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
        }

        /// <summary>"H-alpha 7nm" -> "halpha", "O III" -> "oiii", "Hα" -> "halpha".</summary>
        public static string Normalise(string? name) {
            var s = (name ?? "").Trim().ToLowerInvariant().Replace("α", "alpha");
            s = Bandwidth.Replace(s, "").Trim();
            return NonAlnum.Replace(s, "");
        }

        /// <summary>Standard name (H, O, S, L, R, G, B) for any known spelling, else null.</summary>
        public string? StandardName(string? name) => lookup.TryGetValue(Normalise(name), out var std) ? std : null;

        /// <summary>The name in <paramref name="available"/> that <paramref name="requested"/> refers to, or null.</summary>
        public string? Resolve(string requested, IEnumerable<string> available) {
            var names = available.ToList();
            if (names.Contains(requested)) return requested;
            var want = Normalise(requested);
            var hit = names.FirstOrDefault(f => Normalise(f) == want);
            if (hit != null) return hit;
            var std = StandardName(requested);
            return std == null ? null : names.FirstOrDefault(f => StandardName(f) == std);
        }

        /// <summary>
        /// Rewrites every exposure's filter in the plan to the profile's own name. Call after
        /// validation (unresolvable names are left as they are). Returns the distinct renames made.
        /// </summary>
        public IReadOnlyList<(string From, string To)> ResolveAll(NightPlan plan, IEnumerable<string> profileFilters) {
            var available = profileFilters.ToList();
            var renames = new List<(string, string)>();
            foreach (var e in plan.Targets.SelectMany(t => t.Exposures)) {
                var to = Resolve(e.Filter, available);
                if (to == null || to == e.Filter) continue;
                if (!renames.Contains((e.Filter, to))) renames.Add((e.Filter, to));
                e.Filter = to;
            }
            return renames;
        }
    }
}

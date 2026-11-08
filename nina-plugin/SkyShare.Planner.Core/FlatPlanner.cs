using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyShare.Planner.Core {

    /// <summary>Everything about a light frame that its flat must match (except rotation, which groups).</summary>
    public sealed record FlatSpec(string Filter, int Gain, int Offset, int Binning) {
        public override string ToString() => $"{Filter} g{Gain} o{Offset} {Binning}x{Binning}";
    }

    /// <summary>All flat sets taken at one camera orientation.</summary>
    public sealed record FlatGroup(
        double SkyRotation,
        double? MechanicalPosition,   // where to send the rotator; null = don't move
        IReadOnlyList<FlatSpec> Specs,
        IReadOnlyList<string> Targets);

    /// <summary>What actually happened to a target during the night (filled in by the plugin).</summary>
    public sealed record TargetRunRecord(bool Ran, double? MechanicalPosition);

    public sealed record FlatPlan(IReadOnlyList<FlatGroup> Groups, IReadOnlyList<string> Skipped);

    /// <summary>
    /// Turns the night's targets into the minimum set of flats.
    ///
    ///  - Only targets that actually ran get flats (no lights = no flats needed).
    ///  - Specs are de-duplicated per orientation: two targets at rotation 0 using
    ///    H/gain100 share one H flat set.
    ///  - With a rotator, each orientation group moves the rotator to the mechanical
    ///    position recorded while imaging that target.
    ///  - Without a rotator the camera is still at the LAST target's orientation, so
    ///    only that group is taken; the rest are reported as skipped.
    ///  - The last target's group is always first (no rotator move needed).
    /// </summary>
    public static class FlatPlanner {
        public const double RotationTolerance = 0.5; // degrees

        public static FlatPlan Plan(
            NightPlan plan,
            IReadOnlyDictionary<int, TargetRunRecord> runs,
            bool hasRotator) {

            var skipped = new List<string>();
            if (!plan.Flats.Enabled) return new FlatPlan(Array.Empty<FlatGroup>(), new[] { "flats disabled in plan" });

            var ran = Enumerable.Range(0, plan.Targets.Count)
                .Where(i => runs.TryGetValue(i, out var r) && r.Ran)
                .ToList();
            foreach (var i in Enumerable.Range(0, plan.Targets.Count).Except(ran))
                skipped.Add($"{plan.Targets[i].Name}: did not run");
            if (ran.Count == 0) return new FlatPlan(Array.Empty<FlatGroup>(), skipped);

            // Bucket ran targets by sky rotation (within tolerance), keeping first-seen mechanical position.
            var buckets = new List<(double rot, double? mech, List<int> idx)>();
            foreach (var i in ran) {
                var rot = plan.Targets[i].Rotation;
                var b = buckets.FindIndex(x => AngleDiff(x.rot, rot) <= RotationTolerance);
                runs.TryGetValue(i, out var rec);
                if (b < 0) buckets.Add((rot, rec?.MechanicalPosition, new List<int> { i }));
                else {
                    buckets[b].idx.Add(i);
                    // the most recent run wins: it's closest to the current physical state
                    if (rec?.MechanicalPosition is { } m) buckets[b] = (buckets[b].rot, m, buckets[b].idx);
                }
            }

            // Final orientation first.
            var last = ran[^1];
            buckets = buckets.OrderByDescending(b => b.idx.Contains(last)).ToList();

            var groups = new List<FlatGroup>();
            for (int g = 0; g < buckets.Count; g++) {
                var (rot, mech, idx) = buckets[g];
                var names = idx.Select(i => plan.Targets[i].Name).ToList();
                bool isCurrent = g == 0;

                if (!isCurrent && !hasRotator) {
                    skipped.Add($"rotation {rot:0.#}° ({string.Join(", ", names)}): no rotator, camera is at a different angle");
                    continue;
                }
                if (!isCurrent && mech is null) {
                    skipped.Add($"rotation {rot:0.#}° ({string.Join(", ", names)}): no mechanical position recorded - add 'SkyShare: Record Rotation' to the target prototype");
                    continue;
                }

                var specs = idx
                    .SelectMany(i => plan.Targets[i].Exposures)
                    .Select(e => new FlatSpec(e.Filter, e.Gain, e.Offset, e.Binning))
                    .Distinct()
                    .ToList();

                groups.Add(new FlatGroup(rot, isCurrent ? null : mech, specs, names));
            }
            return new FlatPlan(groups, skipped);
        }

        private static double AngleDiff(double a, double b) {
            var d = Math.Abs(a - b) % 360;
            return d > 180 ? 360 - d : d;
        }
    }
}

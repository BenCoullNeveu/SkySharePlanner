using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SkyShare.Planner.Core;

namespace SkyShare.Planner.Tests {

    public class PlanLocatorTests {
        [TestCase("2026-11-07 21:00", "2026-11-07")]
        [TestCase("2026-11-08 02:30", "2026-11-07")] // after midnight = previous night
        [TestCase("2026-11-08 12:00", "2026-11-08")]
        public void NightOf_rolls_over_at_noon(string now, string expected) {
            var night = PlanLocator.NightOf(DateTime.Parse(now));
            Assert.That(PlanLocator.NightKey(night), Is.EqualTo(expected));
        }

        [Test]
        public void Find_requires_exactly_one_and_ignores_temp_files() {
            var dir = Directory.CreateTempSubdirectory().FullName;
            var night = new DateOnly(2026, 11, 7);
            Assert.Throws<PlanException>(() => PlanLocator.Find(dir, night));

            File.WriteAllText(Path.Combine(dir, ".2026-11-07_x.plan.json"), "{}"); // hidden temp
            File.WriteAllText(Path.Combine(dir, "2026-11-07_Jane.plan.json"), "{}");
            Assert.That(Path.GetFileName(PlanLocator.Find(dir, night)), Is.EqualTo("2026-11-07_Jane.plan.json"));

            File.WriteAllText(Path.Combine(dir, "2026-11-07_Bob.plan.json"), "{}");
            Assert.Throws<PlanException>(() => PlanLocator.Find(dir, night));
        }
    }

    public class ParserAndValidatorTests {
        private static NightPlan Example() =>
            PlanParser.ParseFile(Path.Combine(TestContext.CurrentContext.TestDirectory, "examples", "2026-11-07_JaneDoe.plan.json"));

        [Test]
        public void Example_plan_is_valid() {
            var plan = Example();
            var issues = PlanValidator.Validate(plan, new[] { "L", "R", "G", "B", "H", "O", "S" }, hasRotator: true,
                expectedNight: new DateOnly(2026, 11, 7));
            Assert.That(issues.Where(i => i.Severity == Severity.Error), Is.Empty, string.Join("\n", issues));
        }

        [Test]
        public void Camera_temperature_is_optional_and_checked() {
            Assert.That(Example().CameraTemperature, Is.EqualTo(-10));
            var plan = Example();
            plan.CameraTemperature = null;
            Assert.That(PlanValidator.Validate(plan).Where(i => i.Message.Contains("camera_temperature")), Is.Empty);
            plan.CameraTemperature = -80;
            Assert.That(PlanValidator.Validate(plan).Any(i => i.Severity == Severity.Error && i.Message.Contains("camera_temperature")));
            // A different darks temperature is only a warning
            plan.CameraTemperature = -10;
            plan.Darks.Temperature = -5;
            plan.Darks.Sets.Add(new DarkSet { Exposure = 300, Count = 5 });
            Assert.That(PlanValidator.Validate(plan).Any(i => i.Severity == Severity.Warning && i.Message.Contains("darks won't match")));
        }

        private static IEnumerable<string> ExampleFiles() =>
            Directory.GetFiles(Path.Combine(TestContext.CurrentContext.TestDirectory, "examples"), "*.plan.json")
                .Select(Path.GetFileName).OrderBy(n => n)!;

        // Every example is valid, except *_BadPlan, which must have errors.
        [TestCaseSource(nameof(ExampleFiles))]
        public void Examples_validate_as_expected(string file) {
            var plan = PlanParser.ParseFile(Path.Combine(TestContext.CurrentContext.TestDirectory, "examples", file));
            var errors = PlanValidator.Validate(plan).Where(i => i.Severity == Severity.Error).ToList();
            if (file.Contains("BadPlan")) Assert.That(errors, Is.Not.Empty);
            else Assert.That(errors, Is.Empty, string.Join("\n", errors));
        }

        [Test]
        public void Unknown_filter_and_wrong_night_are_errors() {
            var issues = PlanValidator.Validate(Example(), new[] { "L", "R", "G", "B" },
                expectedNight: new DateOnly(2026, 11, 8));
            Assert.That(issues.Any(i => i.Message.Contains("filter not in NINA profile")));
            Assert.That(issues.Any(i => i.Message.Contains("tonight is 2026-11-08")));
        }

        [Test]
        public void Multiple_rotations_without_rotator_warns() {
            var issues = PlanValidator.Validate(Example(), hasRotator: false);
            Assert.That(issues.Any(i => i.Severity == Severity.Warning && i.Message.Contains("rotations")));
        }

        [Test]
        public void Cycle_mode_and_limits_parse() {
            var plan = PlanParser.Parse("""
                { "schema_version": 1, "night": "2026-11-07", "targets": [
                  { "name": "M 31", "ra": "00:42:44", "dec": "+41:16:09", "cycle": true,
                    "max_frames": 120, "max_hours": 2.5, "next_target_altitude": 40,
                    "exposures": [ { "filter": "L", "exposure": 300, "count": 3 } ] },
                  { "name": "NGC 7000", "ra": "20:59:17", "dec": "+44:31:44",
                    "exposures": [ { "filter": "H", "exposure": 600, "count": 10 } ] } ] }
                """);
            var t = plan.Targets[0];
            Assert.That((t.Cycle, t.MaxFrames, t.MaxHours, t.NextTargetAltitude), Is.EqualTo((true, 120, 2.5, 40.0)));
            Assert.That(plan.Targets[1].Cycle, Is.False);
            Assert.That(plan.Targets[1].MaxFrames, Is.Null);
            Assert.That(PlanValidator.Validate(plan).Where(i => i.Severity == Severity.Error), Is.Empty);
        }

        [Test]
        public void Next_target_altitude_on_last_target_is_an_error() {
            var plan = Example();
            plan.Targets[^1].NextTargetAltitude = 30;
            Assert.That(PlanValidator.Validate(plan).Any(i => i.Severity == Severity.Error && i.Message.Contains("last target")));
        }

        [Test]
        public void Cycle_without_any_stop_warns() {
            var plan = Example();
            var t = plan.Targets[0];
            (t.Cycle, t.End, t.MaxFrames, t.MaxHours, t.NextTargetAltitude) = (true, null, null, null, null);
            Assert.That(PlanValidator.Validate(plan).Any(i => i.Severity == Severity.Warning && i.Message.Contains("cycles with no end")));
        }

        [TestCase(0)]
        [TestCase(20000)]
        public void Max_frames_out_of_range_is_an_error(int frames) {
            var plan = Example();
            plan.Targets[0].MaxFrames = frames;
            Assert.That(PlanValidator.Validate(plan).Any(i => i.Message.Contains("max_frames")));
        }

        [TestCase("23:20:48.3", true)]
        [TestCase("24:00:00", false)]
        [TestCase("-01:00:00", false)]
        public void Ra(string s, bool ok) => Assert.That(Sexagesimal.TryParseRaHours(s, out _), Is.EqualTo(ok));

        [TestCase("+61:12:06", 61.2016667)]
        [TestCase("-05:30:00", -5.5)]
        public void Dec(string s, double deg) {
            Assert.That(Sexagesimal.TryParseDecDegrees(s, out var d));
            Assert.That(d, Is.EqualTo(deg).Within(1e-6));
        }

        [Test]
        public void Bad_json_gives_plan_exception() =>
            Assert.Throws<PlanException>(() => PlanParser.Parse("{ not json"));
    }

    public class FlatPlannerTests {
        private static NightPlan TwoTargets(double rot2) => new() {
            SchemaVersion = 1, Night = "2026-11-07",
            Targets = {
                new PlanTarget { Name = "A", Rotation = 0, Exposures = {
                    new PlanExposure { Filter = "H", Gain = 100 }, new PlanExposure { Filter = "O", Gain = 100 } } },
                new PlanTarget { Name = "B", Rotation = rot2, Exposures = {
                    new PlanExposure { Filter = "H", Gain = 100 }, new PlanExposure { Filter = "L", Gain = 0 } } },
            }
        };

        private static Dictionary<int, TargetRunRecord> BothRan(double? mechA = 10, double? mechB = 45) => new() {
            [0] = new(true, mechA), [1] = new(true, mechB)
        };

        [Test]
        public void Same_rotation_dedupes_specs() {
            var fp = FlatPlanner.Plan(TwoTargets(0.2), BothRan(), hasRotator: false);
            Assert.That(fp.Groups, Has.Count.EqualTo(1));
            Assert.That(fp.Groups[0].Specs.Select(s => s.Filter), Is.EquivalentTo(new[] { "H", "O", "L" }));
        }

        [Test]
        public void Different_rotation_with_rotator_makes_two_groups_current_first() {
            var fp = FlatPlanner.Plan(TwoTargets(35), BothRan(), hasRotator: true);
            Assert.That(fp.Groups, Has.Count.EqualTo(2));
            Assert.That(fp.Groups[0].Targets, Is.EqualTo(new[] { "B" }));     // last target = current orientation
            Assert.That(fp.Groups[0].MechanicalPosition, Is.Null);            // no move needed
            Assert.That(fp.Groups[1].MechanicalPosition, Is.EqualTo(10));     // move back to A's recorded position
        }

        [Test]
        public void Different_rotation_without_rotator_skips_old_orientation() {
            var fp = FlatPlanner.Plan(TwoTargets(35), BothRan(), hasRotator: false);
            Assert.That(fp.Groups, Has.Count.EqualTo(1));
            Assert.That(fp.Skipped.Single(), Does.Contain("no rotator"));
        }

        [Test]
        public void Targets_that_did_not_run_get_no_flats() {
            var runs = new Dictionary<int, TargetRunRecord> { [0] = new(true, 10) };
            var fp = FlatPlanner.Plan(TwoTargets(35), runs, hasRotator: true);
            Assert.That(fp.Groups.Single().Targets, Is.EqualTo(new[] { "A" }));
            Assert.That(fp.Skipped.Single(), Does.Contain("B: did not run"));
        }

        [Test]
        public void Preview_assumes_all_run_and_leaves_rotation_pending() {
            var fp = FlatPlanner.Preview(TwoTargets(35), hasRotator: true);
            Assert.That(fp.Groups, Has.Count.EqualTo(2));
            Assert.That(fp.Groups[0].Targets, Is.EqualTo(new[] { "B" }));     // last in plan = current orientation
            Assert.That(fp.Groups[0].RotatorPending, Is.False);
            Assert.That(fp.Groups[1].Targets, Is.EqualTo(new[] { "A" }));
            Assert.That(fp.Groups[1].MechanicalPosition, Is.Null);
            Assert.That(fp.Groups[1].RotatorPending, Is.True);
        }

        [Test]
        public void Preview_without_rotator_keeps_only_final_orientation() {
            var fp = FlatPlanner.Preview(TwoTargets(35), hasRotator: false);
            Assert.That(fp.Groups.Single().Targets, Is.EqualTo(new[] { "B" }));
        }

        [Test]
        public void Real_plan_never_has_pending_rotation() {
            var fp = FlatPlanner.Plan(TwoTargets(35), BothRan(), hasRotator: true);
            Assert.That(fp.Groups.Any(g => g.RotatorPending), Is.False);
        }

        [Test]
        public void Rotation_wraps_at_360() {
            var fp = FlatPlanner.Plan(TwoTargets(359.8), BothRan(), hasRotator: false);
            Assert.That(fp.Groups, Has.Count.EqualTo(1));
        }

        [Test]
        public void Same_rotation_with_rotator_never_moves_rotator() {
            var fp = FlatPlanner.Plan(TwoTargets(0.2), BothRan(), hasRotator: true);
            Assert.That(fp.Groups.Single().MechanicalPosition, Is.Null);
            var preview = FlatPlanner.Preview(TwoTargets(0.2), hasRotator: true);
            Assert.That(preview.Groups.Single().MechanicalPosition, Is.Null);
            Assert.That(preview.Groups.Single().RotatorPending, Is.False);
        }

        [Test]
        public void Single_target_with_rotator_never_moves_rotator() {
            var plan = TwoTargets(35);
            plan.Targets.RemoveAt(1);
            var fp = FlatPlanner.Plan(plan, new Dictionary<int, TargetRunRecord> { [0] = new(true, 10) }, hasRotator: true);
            Assert.That(fp.Groups.Single().MechanicalPosition, Is.Null);
            Assert.That(FlatPlanner.Preview(plan, hasRotator: true).Groups.Single().RotatorPending, Is.False);
        }
    }

    public class SessionManifestTests {
        private static NightPlan Plan(bool processing = true) => new() {
            SchemaVersion = 1, Night = "2026-11-07", Client = "Jane",
            Processing = new ProcessingSettings { Enabled = processing, Triage = "send", Export = true },
            Targets = {
                new PlanTarget { Name = "NGC 7635 Jane", Exposures = {
                    new PlanExposure { Filter = "H", Count = 20, Gain = 100 }, new PlanExposure { Filter = "O", Count = 10, Gain = 100 } } },
                new PlanTarget { Name = "M 31 Jane", Exposures = { new PlanExposure { Filter = "L", Count = 60 } } },
            }
        };

        private static readonly FlatSetResult[] Flats = { new(new FlatSpec("H", 100, -1, 1), 0, 30) };

        [Test]
        public void Partial_night_only_queues_targets_that_ran() {
            var lights = new Dictionary<int, IReadOnlyDictionary<string, int>> {
                [0] = new Dictionary<string, int> { ["H"] = 20, ["O"] = 10 },
                [1] = new Dictionary<string, int> { ["L"] = 0 },
            };
            var m = SessionManifestBuilder.Build(Plan(), "2026-11-07_Jane.plan.json", lights, Flats, new[] { "x" }, DateTime.UtcNow);
            Assert.That(m.Outcome, Is.EqualTo("partial"));
            Assert.That(m.Targets[0].LightsTotal, Is.EqualTo(30));
            Assert.That(m.Targets[1].Ran, Is.False);
            Assert.That(m.Actions.Single().Target, Is.EqualTo("NGC 7635 Jane"));
            Assert.That(m.Actions.Single().Sessions, Is.EqualTo(new[] { "2026-11-07" }));
            Assert.That(m.Actions.Single().Triage, Is.EqualTo("send"));
            Assert.That(m.Flats.FramesTotal, Is.EqualTo(30));
        }

        [Test]
        public void Nothing_captured_and_processing_disabled_give_no_actions() {
            var none = new Dictionary<int, IReadOnlyDictionary<string, int>>();
            var m = SessionManifestBuilder.Build(Plan(), "p.plan.json", none, Array.Empty<FlatSetResult>(), Array.Empty<string>(), DateTime.UtcNow);
            Assert.That(m.Outcome, Is.EqualTo("nothing_captured"));
            Assert.That(m.Actions, Is.Empty);

            var all = new Dictionary<int, IReadOnlyDictionary<string, int>> {
                [0] = new Dictionary<string, int> { ["H"] = 20, ["O"] = 10 },
                [1] = new Dictionary<string, int> { ["L"] = 60 },
            };
            var m2 = SessionManifestBuilder.Build(Plan(processing: false), "p.plan.json", all, Flats, Array.Empty<string>(), DateTime.UtcNow);
            Assert.That(m2.Outcome, Is.EqualTo("complete"));
            Assert.That(m2.Actions, Is.Empty);
        }

        [Test]
        public void Json_round_trips_with_snake_case_names() {
            var lights = new Dictionary<int, IReadOnlyDictionary<string, int>> { [1] = new Dictionary<string, int> { ["L"] = 5 } };
            var json = SessionManifestBuilder.Build(Plan(), "p.plan.json", lights, Flats, Array.Empty<string>(), DateTime.UtcNow).ToJson();
            Assert.That(json, Does.Contain("\"lights_total\": 5"));
            Assert.That(json, Does.Contain("\"type\": \"skystack_run\""));
            Assert.That(SessionManifest.FromJson(json).Actions.Single().Target, Is.EqualTo("M 31 Jane"));
        }

        [TestCase("2026-11-07_Jane.plan.json", "2026-11-07_Jane.session.json")]
        public void Manifest_file_name(string plan, string expected) =>
            Assert.That(SessionManifestBuilder.ManifestFileName(plan), Is.EqualTo(expected));
    }

    public class DarkTests {
        private const string DarkNight = @"{
          ""schema_version"": 1, ""night"": ""2026-11-12"", ""client"": ""Calibration"",
          ""targets"": [],
          ""darks"": { ""temperature"": -10, ""sets"": [
            { ""exposure"": 300, ""count"": 20, ""gain"": 100 },
            { ""exposure"": 600, ""count"": 1, ""gain"": 100, ""repeat"": true },
            { ""exposure"": 60, ""count"": 30, ""gain"": 0 },
            { ""exposure"": 120, ""count"": 2, ""gain"": 0, ""repeat"": true } ] } }";

        [Test]
        public void Darks_only_plan_is_valid_with_defaults() {
            var plan = PlanParser.Parse(DarkNight);
            Assert.That(plan.Darks.Order, Is.EqualTo("fixed_first"));
            Assert.That(plan.Darks.Until, Is.EqualTo("astronomical_dawn"));
            var issues = PlanValidator.Validate(plan, new[] { "L" });
            Assert.That(issues, Is.Empty, string.Join("\n", issues));
        }

        [Test]
        public void Empty_plan_is_an_error() {
            var plan = PlanParser.Parse(@"{ ""schema_version"": 1, ""night"": ""2026-11-12"", ""targets"": [] }");
            Assert.That(PlanValidator.Validate(plan).Any(i => i.Message.Contains("no targets or darks")));
        }

        [Test]
        public void Fixed_sets_come_first_by_default_in_plan_order() {
            var phases = DarkPlanner.Phases(PlanParser.Parse(DarkNight).Darks);
            Assert.That(phases.Select(p => p.Kind), Is.EqualTo(new[] { DarkPhaseKind.Fixed, DarkPhaseKind.Repeat }));
            Assert.That(phases[0].SetIndices, Is.EqualTo(new[] { 0, 2 }));
            Assert.That(phases[1].SetIndices, Is.EqualTo(new[] { 1, 3 }));
        }

        [Test]
        public void Repeat_first_swaps_the_phases() {
            var darks = PlanParser.Parse(DarkNight).Darks;
            darks.Order = "repeat_first";
            Assert.That(DarkPlanner.Phases(darks).Select(p => p.Kind), Is.EqualTo(new[] { DarkPhaseKind.Repeat, DarkPhaseKind.Fixed }));
        }

        [Test]
        public void Only_fixed_or_only_repeat_gives_one_phase() {
            var darks = PlanParser.Parse(DarkNight).Darks;
            darks.Sets.RemoveAll(s => s.Repeat);
            Assert.That(DarkPlanner.Phases(darks).Single().Kind, Is.EqualTo(DarkPhaseKind.Fixed));
        }

        [TestCase("astronomical_dawn", true)]
        [TestCase("nautical_dawn", true)]
        [TestCase("sunrise", true)]
        [TestCase("05:30", true)]
        [TestCase("dawn", false)]
        [TestCase("25:00", false)]
        public void Until_values(string until, bool ok) {
            var plan = PlanParser.Parse(DarkNight);
            plan.Darks.Until = until;
            Assert.That(PlanValidator.Validate(plan).Any(i => i.Message.Contains("darks.until")), Is.EqualTo(!ok));
        }

        [Test]
        public void Bad_dark_settings_are_errors() {
            var plan = PlanParser.Parse(DarkNight);
            plan.Darks.Order = "whatever";
            plan.Darks.Temperature = -80;
            plan.Darks.Sets[0].Count = 0;
            plan.Darks.Sets[1].Exposure = 0;
            var errors = PlanValidator.Validate(plan).Select(i => i.Message).ToList();
            Assert.That(errors, Has.Some.Contains("darks.order"));
            Assert.That(errors, Has.Some.Contains("darks.temperature"));
            Assert.That(errors, Has.Some.Contains("dark set 1: count"));
            Assert.That(errors, Has.Some.Contains("dark set 2: exposure"));
        }

        [Test]
        public void Describe_lists_fixed_then_rounds() {
            var text = DarkPlanner.Describe(PlanParser.Parse(DarkNight).Darks);
            Assert.That(text, Does.StartWith("darks at -10°C: 300s g100 ×20, 60s g0 ×30, then rounds of ("));
            Assert.That(text, Does.EndWith("until astronomical dawn"));
        }

        [Test]
        public void Manifest_counts_darks_and_repeat_sets_never_make_it_partial() {
            var plan = PlanParser.Parse(DarkNight);
            var none = new Dictionary<int, IReadOnlyDictionary<string, int>>();
            DarkSetResult[] Results(int fixedA, int fixedB) => new[] {
                new DarkSetResult(plan.Darks.Sets[0], fixedA), new DarkSetResult(plan.Darks.Sets[1], 7),
                new DarkSetResult(plan.Darks.Sets[2], fixedB), new DarkSetResult(plan.Darks.Sets[3], 14) };

            var done = SessionManifestBuilder.Build(plan, "p.plan.json", none, Array.Empty<FlatSetResult>(),
                Array.Empty<string>(), DateTime.UtcNow, Results(20, 30));
            Assert.That(done.Outcome, Is.EqualTo("complete"));
            Assert.That(done.Darks.FramesTotal, Is.EqualTo(71));
            Assert.That(done.Darks.Sets[1].Planned, Is.Null);
            Assert.That(done.Darks.Sets[0].Planned, Is.EqualTo(20));
            Assert.That(done.Actions, Is.Empty);

            var short1 = SessionManifestBuilder.Build(plan, "p.plan.json", none, Array.Empty<FlatSetResult>(),
                Array.Empty<string>(), DateTime.UtcNow, Results(12, 30));
            Assert.That(short1.Outcome, Is.EqualTo("partial"));

            var nothing = SessionManifestBuilder.Build(plan, "p.plan.json", none, Array.Empty<FlatSetResult>(),
                Array.Empty<string>(), DateTime.UtcNow, Results(0, 0).Select(r => r with { FramesTaken = 0 }).ToArray());
            Assert.That(nothing.Outcome, Is.EqualTo("nothing_captured"));
            Assert.That(done.ToJson(), Does.Contain("\"darks\"").And.Contain("\"frames_total\": 71"));
        }
    }

    public class FilterResolverTests {
        static readonly string[] Ours = { "H", "S", "O", "L", "R", "G", "B" };
        static readonly string[] Other = { "Ha 7nm", "OIII 7nm", "SII 7nm", "Lum", "Red", "Green", "Blue", "Hb" };

        [TestCase("H", "H", "Ha 7nm")]
        [TestCase("Ha", "H", "Ha 7nm")]
        [TestCase("h-alpha", "H", "Ha 7nm")]
        [TestCase("HA 3nm", "H", "Ha 7nm")]
        [TestCase("OIII", "O", "OIII 7nm")]
        [TestCase("Sulphur", "S", "SII 7nm")]
        [TestCase("Luminance", "L", "Lum")]
        [TestCase("Red", "R", "Red")]
        [TestCase("b", "B", "Blue")]
        public void Resolves_by_meaning_not_spelling(string requested, string ours, string other) {
            Assert.That(FilterResolver.Default.Resolve(requested, Ours), Is.EqualTo(ours));
            Assert.That(FilterResolver.Default.Resolve(requested, Other), Is.EqualTo(other));
        }

        [Test]
        public void Unknown_and_different_filters_do_not_match() {
            Assert.That(FilterResolver.Default.Resolve("Hb", Ours), Is.Null);       // H-beta is not H-alpha
            Assert.That(FilterResolver.Default.Resolve("Filter 2", Ours), Is.Null);
            Assert.That(FilterResolver.Default.Resolve("Filter 2", new[] { "Filter 1", "Filter 2" }), Is.EqualTo("Filter 2"));
        }

        [Test]
        public void Alias_file_adds_spellings() {
            var dir = Directory.CreateTempSubdirectory().FullName;
            File.WriteAllText(Path.Combine(dir, FilterResolver.AliasFileName), "{\"aliases\": {\"H\": [\"Antlia Ha\"]}}");
            Assert.That(FilterResolver.Load(dir).Resolve("H", new[] { "Antlia Ha", "R" }), Is.EqualTo("Antlia Ha"));
            Assert.That(FilterResolver.Load(Path.Combine(dir, "missing")).Resolve("Ha", Ours), Is.EqualTo("H"));
        }

        [Test]
        public void Validator_accepts_aliases_and_ResolveAll_renames_to_profile_names() {
            var plan = new NightPlan {
                SchemaVersion = PlanParser.SupportedSchemaVersion, Night = "2026-11-20",
                Targets = { new PlanTarget { Name = "M 31", Ra = "00:42:44", Dec = "+41:16:09",
                    Exposures = { new PlanExposure { Filter = "H", Exposure = 300, Count = 5 },
                                  new PlanExposure { Filter = "L", Exposure = 60, Count = 5 } } } },
            };
            var errors = PlanValidator.Validate(plan, Other).Where(i => i.Severity == Severity.Error).ToList();
            Assert.That(errors, Is.Empty, string.Join("; ", errors.Select(e => e.Message)));

            var renames = FilterResolver.Default.ResolveAll(plan, Other);
            Assert.That(plan.Targets[0].Exposures.Select(e => e.Filter), Is.EqualTo(new[] { "Ha 7nm", "Lum" }));
            Assert.That(renames, Has.Count.EqualTo(2));

            var bad = PlanValidator.Validate(plan, new[] { "R", "G", "B" }).Where(i => i.Severity == Severity.Error);
            Assert.That(bad.Select(e => e.Message), Has.Some.Contains("filter not in NINA profile"));
        }
    }

    public class NightEstimatorTests {
        private static readonly Site Site = new(45.5, -73.6);
        private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
        private static readonly EstimateSettings Fast = new(AutofocusEveryMinutes: 0);

        private static NightPlan Plan(params PlanTarget[] targets) {
            var p = new NightPlan { SchemaVersion = 1, Night = "2026-11-19" };
            p.Targets.AddRange(targets);
            return p;
        }

        // M 31: high in the evening in November
        private static PlanTarget M31(params PlanExposure[] e) {
            var t = new PlanTarget { Name = "M 31", Ra = "00:42:44", Dec = "+41:16:09" };
            t.Exposures.AddRange(e.Length > 0 ? e : new[] { new PlanExposure { Filter = "L", Exposure = 120, Count = 10 } });
            return t;
        }

        private static NightEstimate Run(NightPlan p, EstimateSettings? s = null) =>
            NightEstimator.Estimate(p, Site, s ?? Fast, Zone);

        [Test]
        public void Twilight_times_are_plausible_for_november() {
            var n = Run(Plan());
            Assert.That(n.DarknessLabel, Is.EqualTo("astronomical"));
            Assert.That(n.Sunset.TimeOfDay, Is.InRange(TimeSpan.Parse("16:00"), TimeSpan.Parse("16:45")));
            Assert.That(n.Dusk.TimeOfDay, Is.InRange(TimeSpan.Parse("17:45"), TimeSpan.Parse("18:30")));
            Assert.That(n.Dawn, Is.GreaterThan(n.Dusk));
            Assert.That(n.Dawn.TimeOfDay, Is.InRange(TimeSpan.Parse("05:00"), TimeSpan.Parse("06:00")));
            Assert.That(n.ImagingStart, Is.EqualTo(n.Dusk));
        }

        [Test]
        public void Pole_star_altitude_is_the_latitude() =>
            Assert.That(NightEstimator.Altitude(2.53, 89.26, Site, Zone, new DateTime(2026, 11, 19, 22, 0, 0)),
                        Is.EqualTo(45.5).Within(1.0));

        [Test]
        public void Fixed_counts_finish_with_overheads_counted() {
            var n = Run(Plan(M31()));
            var t = n.Targets.Single();
            Assert.That(t.Skipped, Is.False);
            Assert.That(t.FrameCount, Is.EqualTo(10));
            Assert.That(t.Outcome, Is.EqualTo("all planned frames taken"));
            // 10 × 120 s + overheads, after slew/center + autofocus
            Assert.That(t.ImagingStart, Is.EqualTo(n.Dusk.AddSeconds(180 + 240)));
            var span = (t.End!.Value - t.ImagingStart!.Value).TotalSeconds;
            Assert.That(span, Is.InRange(1200, 1200 + 10 * 5 + 10 + 9 * 30 + 1));
        }

        [Test]
        public void Target_below_the_limit_when_its_turn_comes_is_skipped() {
            // RA 12h is far below the horizon on a November evening
            var low = new PlanTarget { Name = "Low", Ra = "12:00:00", Dec = "+10:00:00" };
            low.Exposures.Add(new PlanExposure { Filter = "L", Exposure = 60, Count = 5 });
            var t = Run(Plan(low)).Targets.Single();
            Assert.That(t.Skipped, Is.True);
            Assert.That(t.Outcome, Does.Contain("below 20°"));
        }

        [Test]
        public void Second_target_starts_after_the_first() {
            var n = Run(Plan(M31(), M31()));
            Assert.That(n.Targets[1].ArriveTime, Is.EqualTo(n.Targets[0].End));
            Assert.That(n.Finish, Is.EqualTo(n.Targets[1].End));
        }

        [Test]
        public void End_time_stops_the_target() {
            var t = M31(new PlanExposure { Filter = "L", Exposure = 300, Count = 100 });
            t.End = "20:00";
            var r = Run(Plan(t)).Targets.Single();
            Assert.That(r.Outcome, Is.EqualTo("end time 20:00"));
            Assert.That(r.End!.Value, Is.GreaterThanOrEqualTo(new DateTime(2026, 11, 19, 20, 0, 0)));
            Assert.That(r.End!.Value, Is.LessThan(new DateTime(2026, 11, 19, 20, 6, 0))); // finishes the frame in progress
        }

        [Test]
        public void Start_time_after_midnight_is_the_next_morning() {
            var t = M31();
            t.Start = "01:30";
            var r = Run(Plan(t)).Targets.Single();
            Assert.That(r.ArriveTime, Is.EqualTo(new DateTime(2026, 11, 20, 1, 30, 0)));
        }

        [Test]
        public void Cycle_mode_runs_until_max_frames() {
            var t = M31(new PlanExposure { Filter = "L", Exposure = 60, Count = 3 },
                        new PlanExposure { Filter = "R", Exposure = 60, Count = 1 });
            t.Cycle = true; t.MaxFrames = 10;
            var r = Run(Plan(t)).Targets.Single();
            Assert.That(r.FrameCount, Is.EqualTo(10));
            Assert.That(r.Frames.Select(f => f.Frames), Is.EqualTo(new[] { 8, 2 })); // L L L R ×2, then L L
            Assert.That(r.Outcome, Does.Contain("max_frames"));
        }

        [Test]
        public void Cycle_without_limits_runs_until_it_sets_or_dawn() {
            var t = M31(new PlanExposure { Filter = "L", Exposure = 300, Count = 1 });
            t.Cycle = true;
            var r = Run(Plan(t)).Targets.Single();
            Assert.That(r.Outcome, Is.EqualTo("dropped below 20°").Or.EqualTo("dawn"));
            Assert.That(r.FrameCount, Is.GreaterThan(50));
        }

        [Test]
        public void Next_target_altitude_hands_over() {
            var first = M31(new PlanExposure { Filter = "L", Exposure = 300, Count = 200 });
            var m42 = new PlanTarget { Name = "M 42", Ra = "05:35:17", Dec = "-05:23:28" };
            m42.Exposures.Add(new PlanExposure { Filter = "L", Exposure = 60, Count = 10 });
            first.NextTargetAltitude = 30;
            var n = Run(Plan(first, m42));
            Assert.That(n.Targets[0].Outcome, Is.EqualTo("M 42 above 30°"));
            Assert.That(NightEstimator.Altitude(5.588, -5.39, Site, Zone, n.Targets[0].End!.Value), Is.GreaterThanOrEqualTo(30));
            Assert.That(n.Targets[1].Skipped, Is.False);
        }

        [Test]
        public void Late_load_starts_from_now() {
            var now = new DateTime(2026, 11, 19, 21, 0, 0);
            var n = NightEstimator.Estimate(Plan(M31()), Site, Fast, Zone, notBefore: now);
            Assert.That(n.ImagingStart, Is.EqualTo(now));
        }

        [Test]
        public void Periodic_autofocus_adds_time() {
            var t = M31(new PlanExposure { Filter = "L", Exposure = 300, Count = 30 });
            var without = Run(Plan(t)).Targets.Single();
            var with = Run(Plan(t), Fast with { AutofocusEveryMinutes = 60 }).Targets.Single();
            Assert.That((with.End!.Value - without.End!.Value).TotalSeconds, Is.EqualTo(2 * 240).Within(240));
        }
    }

    public class SchedulerSummaryTests {
        private static readonly Site Site = new(45.5, -73.6);
        private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
        private static readonly DateTime T0 = new(2026, 11, 19, 19, 0, 0);

        private static IEnumerable<TsFrame> Run(string key, string filter, double exp, DateTime start, int n) =>
            Enumerable.Range(0, n).Select(i => new TsFrame(key, filter, exp, start.AddSeconds(i * exp), start.AddSeconds((i + 1) * exp)));

        private static TsPreview Preview() {
            var frames = Run("1/1", "L", 120, T0, 30)                       // 19:00-20:00 M 31
                .Concat(Run("1/1", "R", 120, T0.AddHours(1), 15))           // 20:00-20:30 still M 31
                .Concat(Run("2/5", "Ha", 300, T0.AddHours(1.5), 12))        // 20:30-21:30 NGC 7000
                .Concat(Run("1/1", "G", 120, T0.AddHours(2.5), 15))         // 21:30-22:00 back to M 31
                .ToList();
            var targets = new[] {
                new TsTarget("1/1", "M 31", "Andromeda", 0.712, 41.27, 25, 40, 70),
                new TsTarget("1/2", "M 110", "Andromeda", 0.673, 41.69, 25, 20, 20),
                new TsTarget("2/5", "NGC 7000", "Cygnus", 20.98, 44.33, 25, 0, 35),
                new TsTarget("3/9", "M 42", "Orion", 5.59, -5.39, 25, 10, 10),
            };
            return new TsPreview(T0, "Main", frames, targets);
        }

        [Test]
        public void Blocks_split_on_target_change_and_merge_filters() {
            var blocks = SchedulerSummary.Blocks(Preview().Frames);
            Assert.That(blocks.Select(b => b.TargetKey), Is.EqualTo(new[] { "1/1", "2/5", "1/1" }));
            Assert.That(blocks[0].Start, Is.EqualTo(T0));
            Assert.That(blocks[0].End, Is.EqualTo(T0.AddHours(1.5)));
            Assert.That(blocks[0].Frames.Select(f => (f.Filter, f.Frames)), Is.EqualTo(new[] { ("L", 30), ("R", 15) }));
            Assert.That(blocks[1].IntegrationSeconds, Is.EqualTo(3600));
        }

        [Test]
        public void Blocks_split_on_a_wait() {
            var frames = Run("1/1", "L", 60, T0, 5).Concat(Run("1/1", "L", 60, T0.AddHours(2), 5));
            Assert.That(SchedulerSummary.Blocks(frames), Has.Count.EqualTo(2));
        }

        [Test]
        public void Scheduled_targets_in_first_visit_order_only() {
            var p = Preview();
            var s = SchedulerSummary.Scheduled(p, SchedulerSummary.Blocks(p.Frames));
            Assert.That(s.Select(t => t.Name), Is.EqualTo(new[] { "M 31", "NGC 7000" }));
        }

        [Test]
        public void Chart_has_one_series_per_scheduled_target_with_all_its_stretches_and_progress() {
            var p = Preview();
            var chart = SchedulerSummary.Chart(p, NightEstimator.Twilight(new DateOnly(2026, 11, 19), Site, Zone));
            Assert.That(chart.Series, Has.Count.EqualTo(2));
            Assert.That(chart.Series[0].Imaging, Has.Count.EqualTo(2));
            Assert.That(chart.Series[0].ProgressBefore, Is.EqualTo(40));
            Assert.That(chart.Series[0].ProgressAfter, Is.EqualTo(70));
            Assert.That(chart.MinAltitude, Is.EqualTo(25));
            Assert.That(chart.Finish, Is.EqualTo(T0.AddHours(3)));
        }

        [Test]
        public void Markdown_lists_targets_projects_and_the_perfect_plan_caveat() {
            var p = Preview();
            var md = SchedulerSummary.Markdown(p, NightEstimator.Twilight(new DateOnly(2026, 11, 19), Site, Zone));
            Assert.That(md, Does.Contain("**M 31** (Andromeda)"));
            Assert.That(md, Does.Contain("19:00–20:30, 21:30–22:00"));
            Assert.That(md, Does.Contain("L 120s×30, R 120s×15, G 120s×15"));
            Assert.That(md, Does.Contain("40% → 70%"));
            Assert.That(md, Does.Contain("**Andromeda**"));
            Assert.That(md, Does.Contain("M 110 20% (not tonight)"));
            Assert.That(md, Does.Contain("Order: M 31 19:00 → NGC 7000 20:30 → M 31 21:30"));
            Assert.That(md, Does.Contain("2 other active target(s)"));   // M 110, M 42
            Assert.That(md, Does.Contain("no time for slews"));
            Assert.That(md.Length, Is.LessThan(1900));
        }

        [Test]
        public void Markdown_when_nothing_is_scheduled() {
            var p = Preview() with { Frames = Array.Empty<TsFrame>() };
            var md = SchedulerSummary.Markdown(p, NightEstimator.Twilight(new DateOnly(2026, 11, 19), Site, Zone));
            Assert.That(md, Does.Contain("would not image anything"));
        }

        [TestCase(40, 70, "`████▓▓▓░░░`")]
        [TestCase(0, 0, "`░░░░░░░░░░`")]
        [TestCase(100, 100, "`██████████`")]
        public void Bar(double before, double after, string expected) =>
            Assert.That(SchedulerSummary.Bar(before, after), Is.EqualTo(expected));

        [Test]
        public void Twilight_matches_the_estimator() {
            var w = NightEstimator.Twilight(new DateOnly(2026, 11, 19), Site, Zone);
            Assert.That(w.Dusk.Hour, Is.InRange(17, 18));
            Assert.That(w.Dawn.Hour, Is.InRange(5, 6));
            Assert.That(NightEstimator.NightOf(new DateTime(2026, 11, 20, 3, 0, 0)), Is.EqualTo(new DateOnly(2026, 11, 19)));
        }

        // November 2026: new moon on the 9th, full moon on the 24th (~09:50 Toronto time).
        [Test]
        public void Moon_illumination_follows_the_phases() {
            Assert.That(NightEstimator.MoonIllumination(Zone, new DateTime(2026, 11, 9, 3, 0, 0)), Is.LessThan(0.03));
            Assert.That(NightEstimator.MoonIllumination(Zone, new DateTime(2026, 11, 24, 10, 0, 0)), Is.GreaterThan(0.98));
            Assert.That(NightEstimator.MoonIllumination(Zone, new DateTime(2026, 11, 17, 0, 0, 0)), Is.InRange(0.3, 0.7)); // first quarter
        }

        [Test]
        public void Separation_basics() {
            Assert.That(NightEstimator.Separation(0, 0, 6, 0), Is.EqualTo(90).Within(1e-9));
            Assert.That(NightEstimator.Separation(5, 89.9, 17, 89.9), Is.EqualTo(0.2).Within(1e-6));
            Assert.That(NightEstimator.Separation(12, -30, 12, 30), Is.EqualTo(60).Within(1e-9));
        }

        [Test]
        public void Moon_during_imaging_finds_the_closest_approach_or_null_when_down() {
            var at = new DateTime(2026, 11, 24, 23, 0, 0);
            var (ra, dec) = NightEstimator.MoonRaDec(Zone, at);
            var near = NightEstimator.MoonDuring(ra, dec + 5, Site, at.AddHours(-1), at.AddHours(1), Zone)!;
            Assert.That(near.SeparationDeg, Is.EqualTo(5).Within(1));
            Assert.That(near.IsClose, Is.True);
            Assert.That(near.ToString(), Does.StartWith("Moon 5° away, 100% lit (close)").Or.StartWith("Moon 5° away, 99% lit (close)"));
            // Nov 9 is new moon; around 22:00 the Moon is below the horizon.
            Assert.That(NightEstimator.MoonDuring(ra, dec, Site, new DateTime(2026, 11, 9, 21, 0, 0), new DateTime(2026, 11, 9, 23, 0, 0), Zone), Is.Null);
        }

        [Test]
        public void Full_moon_is_high_at_midnight_and_down_at_noon() {
            Assert.That(NightEstimator.MoonAltitude(Site, Zone, new DateTime(2026, 11, 24, 23, 59, 0)), Is.GreaterThan(55));
            Assert.That(NightEstimator.MoonAltitude(Site, Zone, new DateTime(2026, 11, 24, 12, 0, 0)), Is.InRange(-22, -17)); // lower culmination: 45.5 + ~25 - 90
        }
    }

    public class HealthReportTests {
        [Test]
        public void Disk_space_levels() {
            Assert.That(HealthReport.Disk("D:\\Images", 100_000_000_000, 30_000_000_000, 20).Level, Is.EqualTo(HealthLevel.Ok));
            Assert.That(HealthReport.Disk("D:\\Images", 40_000_000_000, 30_000_000_000, 20).Level, Is.EqualTo(HealthLevel.Warn));
            Assert.That(HealthReport.Disk("D:\\Images", 20_000_000_000, 30_000_000_000, 20).Level, Is.EqualTo(HealthLevel.Fail));
            Assert.That(HealthReport.Disk("D:\\Images", 40_000_000_000, 30_000_000_000, 20).Detail, Does.Contain("tonight needs ~30 GB"));
        }

        [Test]
        public void Clock_levels() {
            Assert.That(HealthReport.Clock(0.4).Level, Is.EqualTo(HealthLevel.Ok));
            Assert.That(HealthReport.Clock(-5).Level, Is.EqualTo(HealthLevel.Warn));
            Assert.That(HealthReport.Clock(120).Level, Is.EqualTo(HealthLevel.Fail));
            Assert.That(HealthReport.Clock(null).Level, Is.EqualTo(HealthLevel.Warn));
        }

        [Test]
        public void Cooling_levels() {
            Assert.That(HealthReport.Cooling(true, true, -9.8, -10).Level, Is.EqualTo(HealthLevel.Ok));
            Assert.That(HealthReport.Cooling(true, true, 2, -10).Level, Is.EqualTo(HealthLevel.Warn));
            Assert.That(HealthReport.Cooling(true, false, 15, -10).Detail, Does.Contain("cooler off"));
            Assert.That(HealthReport.Cooling(false, false, 20, 0).Level, Is.EqualTo(HealthLevel.Ok));
        }

        [Test]
        public void Markdown_lists_problems_first_in_plain_text() {
            var checks = new[] {
                new HealthCheck("Camera", HealthLevel.Ok, "connected"),
                new HealthCheck("Mount", HealthLevel.Fail, "not connected (ASCOM.Mount)"),
                new HealthCheck("Clock", HealthLevel.Warn, "+5.0 s from internet time"),
            };
            var md = HealthReport.Markdown(checks, new DateTime(2026, 11, 20, 17, 30, 0), "Main");
            Assert.That(md, Does.StartWith("**Health check 2026-11-20 17:30** — Main · 1 FAIL, 1 WARN of 3 checks"));
            var lines = md.Split('\n');
            Assert.That(lines[2], Does.StartWith("FAIL  Mount"));
            Assert.That(lines[3], Does.StartWith("WARN  Clock"));
            Assert.That(lines[4], Does.StartWith("OK    Camera"));
            Assert.That(HealthReport.Summary(new[] { checks[0] }), Is.EqualTo("all 1 checks OK"));
        }
    }

    public class FramingTests {
        // 530 mm focal length, 3.76 µm pixels, 6248 × 4176 sensor (a common APS-C setup)
        private static readonly FieldOfView Fov = Framing.FieldOfView(530, 3.76, 6248, 4176)!;

        [Test]
        public void Field_of_view_from_focal_length_and_sensor() {
            Assert.That(Fov.ArcsecPerPixel, Is.EqualTo(1.463).Within(0.001));
            Assert.That(Fov.WidthDeg, Is.EqualTo(2.54).Within(0.01));
            Assert.That(Fov.HeightDeg, Is.EqualTo(1.70).Within(0.01));
            Assert.That(Framing.FieldOfView(0, 3.76, 6248, 4176), Is.Null);
        }

        [Test]
        public void Survey_url_is_invariant_culture_and_in_degrees() {
            var url = Framing.SurveyUrl(5.5883, -5.391, 4.123456, 400);
            Assert.That(url, Does.StartWith("https://alasky.cds.unistra.fr/hips-image-services/hips2fits?hips=CDS%2FP%2FDSS2%2Fcolor"));
            Assert.That(url, Does.Contain("&ra=83.8245&dec=-5.391"));
            Assert.That(url, Does.Contain("&fov=4.12346"));
            Assert.That(url, Does.Contain("&width=400&height=400"));
        }

        [Test]
        public void Sensor_outline_unrotated_is_centred_and_wide() {
            var cut = Framing.CutoutDeg(Fov);
            var c = Framing.SensorCorners(Fov, 0, cut, 400);
            Assert.That(c[0].X, Is.LessThan(c[1].X));                 // top-left -> top-right along x
            Assert.That(c[0].Y, Is.EqualTo(c[1].Y).Within(1e-9));
            Assert.That((c[0].X + c[2].X) / 2, Is.EqualTo(200).Within(1e-9));
            Assert.That(c[1].X - c[0].X, Is.GreaterThan(c[3].Y - c[0].Y)); // landscape
            Assert.That(c.All(p => p.X > 0 && p.X < 400 && p.Y > 0 && p.Y < 400), Is.True);
        }

        [Test]
        public void Rotation_turns_the_frame_counter_clockwise_on_a_north_up_image() {
            var cut = Framing.CutoutDeg(Fov);
            var top0 = Framing.TopMid(Framing.SensorCorners(Fov, 0, cut, 400));
            var top90 = Framing.TopMid(Framing.SensorCorners(Fov, 90, cut, 400));
            Assert.That(top0.Y, Is.LessThan(200));                      // sensor top points north
            Assert.That(top90.X, Is.LessThan(200));                     // after +90° it points east (left)
            Assert.That(top90.Y, Is.EqualTo(200).Within(1e-9));
            // every rotation stays inside the cutout
            for (int a = 0; a < 360; a += 15)
                Assert.That(Framing.SensorCorners(Fov, a, cut, 400).All(p => p.X >= 0 && p.X <= 400 && p.Y >= 0 && p.Y <= 400), Is.True);
        }
    }
}

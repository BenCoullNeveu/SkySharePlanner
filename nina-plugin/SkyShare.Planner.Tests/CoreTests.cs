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
        public void Rotation_wraps_at_360() {
            var fp = FlatPlanner.Plan(TwoTargets(359.8), BothRan(), hasRotator: false);
            Assert.That(fp.Groups, Has.Count.EqualTo(1));
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
}

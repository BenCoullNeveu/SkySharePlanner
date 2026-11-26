import json
from pathlib import Path

import pytest

import skyshare_plan as sp

ROOT = Path(__file__).resolve().parent.parent
EXAMPLE = ROOT / "examples" / "2026-11-07_JaneDoe.plan.json"
SCHEMA = ROOT / "schema" / "plan.schema.json"

BOT_SEQ = {
    "name": "Some gases in space", "ra": "23:06:55.72", "dec": "41:42:29.03", "rot": 0,
    "exposures": {"L": (300.0, 2), "H": (600.0, 5)},
    "date": "31-12-2026", "user": "Jane Doe",
}


def test_example_is_valid_and_matches_schema():
    jsonschema = pytest.importorskip("jsonschema")
    data = json.loads(EXAMPLE.read_text())
    jsonschema.validate(data, json.loads(SCHEMA.read_text()))
    assert sp.validate(sp.Plan.from_dict(data)) == []


def test_from_bot_wizard_shape():
    plan = sp.plan_from_wizard(BOT_SEQ)
    assert plan.night == "2026-12-31"
    assert plan.filename == "2026-12-31_JaneDoe.plan.json"
    gains = {e.filter: e.gain for e in plan.targets[0].exposures}
    assert gains == {"L": 0, "H": 100}
    assert plan.targets[0].name == "Some gases in space for Jane Doe"   # NINA target and SkyStack folder name
    assert sp.validate(plan) == []


def test_generated_plan_matches_schema():
    jsonschema = pytest.importorskip("jsonschema")
    plan = sp.plan_from_wizard(BOT_SEQ)
    jsonschema.validate(plan.to_dict(), json.loads(SCHEMA.read_text()))


def test_multi_target_shape():
    seq = {"date": "2026-11-07", "user": "Bob", "targets": [
        {**BOT_SEQ, "end": "23:00"},
        {**BOT_SEQ, "name": "M31", "rot": 370, "start": "23:00"},
    ]}
    plan = sp.plan_from_wizard(seq)
    assert len(plan.targets) == 2
    assert plan.targets[1].rotation == 10
    assert sp.rotation_warning(plan, has_rotator=False)
    assert sp.rotation_warning(plan, has_rotator=True) is None


@pytest.mark.parametrize("mutate,needle", [
    (lambda p: setattr(p.targets[0], "ra", "25:00:00"), "ra out of range"),
    (lambda p: setattr(p.targets[0], "dec", "+91:00:00"), "dec out of range"),
    (lambda p: setattr(p.targets[0], "end", "25:00"), "end must be HH:MM"),
    (lambda p: setattr(p.targets[0].exposures[0], "count", 0), "count must be"),
    (lambda p: setattr(p, "night", "2026-02-30"), "not a real date"),
])
def test_validation_catches(mutate, needle):
    plan = sp.plan_from_wizard(BOT_SEQ)
    mutate(plan)
    assert any(needle in x for x in sp.validate(plan))


def test_cycle_mode_and_limits_round_trip_and_match_schema():
    jsonschema = pytest.importorskip("jsonschema")
    seq = {"date": "31-12-2026", "user": "Jane Doe", "targets": [
        {"name": "M 31", "ra": "00:42:44", "dec": "41:16:09", "cycle": True,
         "max_frames": 120, "max_hours": 3, "next_target_altitude": 40,
         "exposures": {"L": (300, 3), "R": (300, 1), "G": (300, 1), "B": (300, 1)}},
        {"name": "NGC 7000", "ra": "20:59:17", "dec": "44:31:44", "exposures": {"H": (600, 10)}},
    ]}
    plan = sp.plan_from_wizard(seq)
    assert sp.validate(plan) == []
    jsonschema.validate(plan.to_dict(), json.loads(SCHEMA.read_text()))
    again = sp.Plan.from_dict(json.loads(plan.to_json()))
    t = again.targets[0]
    assert (t.cycle, t.max_frames, t.max_hours, t.next_target_altitude) == (True, 120, 3, 40)
    assert "cycles of" in sp.summary(plan) and "stop at 120 frames or 3 h" in sp.summary(plan)


def test_next_target_altitude_not_allowed_on_last_target():
    plan = sp.plan_from_wizard({**BOT_SEQ, "next_target_altitude": 30})
    assert any("last target" in p for p in sp.validate(plan))


def test_calibration_plan_round_trips_and_matches_schema():
    jsonschema = pytest.importorskip("jsonschema")
    plan = sp.calibration_plan("12-11-2026", {"temperature": -10, "sets": [
        {"exposure": 300, "count": 20, "gain": 100},
        {"exposure": 600, "count": 1, "gain": 100, "repeat": True},
    ]})
    assert plan.night == "2026-11-12" and plan.is_calibration_only
    assert sp.validate(plan) == []
    jsonschema.validate(plan.to_dict(), json.loads(SCHEMA.read_text()))
    again = sp.Plan.from_dict(json.loads(plan.to_json()))
    assert again.darks == plan.darks
    text = sp.summary(plan)
    assert "300s g100 x20, then rounds of (600s g100 x1 per round) until astronomical dawn" in text
    assert "Flats" not in text


def test_empty_plan_and_bad_darks_are_rejected():
    jsonschema = pytest.importorskip("jsonschema")
    empty = sp.calibration_plan("2026-11-12", {"sets": []})
    assert any("no targets or darks" in p for p in sp.validate(empty))
    with pytest.raises(jsonschema.ValidationError):
        jsonschema.validate(empty.to_dict(), json.loads(SCHEMA.read_text()))
    bad = sp.calibration_plan("2026-11-12", {"order": "x", "until": "dawn", "sets": [{"exposure": 0, "count": 1}]})
    problems = sp.validate(bad)
    assert any("darks.order" in p for p in problems)
    assert any("darks.until" in p for p in problems)
    assert any("dark set 1: exposure" in p for p in problems)


def test_unknown_filter():
    plan = sp.plan_from_wizard(BOT_SEQ)
    problems = sp.validate(plan, known_filters=["L", "R", "G", "B"])
    assert any("unknown filter 'H'" in x for x in problems)


def test_write_is_atomic_and_one_per_night(tmp_path):
    plan = sp.plan_from_wizard(BOT_SEQ)
    path = sp.write_plan(plan, tmp_path)
    assert path.exists() and not list(tmp_path.glob("*.tmp"))
    assert sp.load_plan(path).targets[0].name == "Some gases in space for Jane Doe"

    other = sp.plan_from_wizard({**BOT_SEQ, "user": "Someone Else"})
    with pytest.raises(sp.PlanError, match="already exists"):
        sp.write_plan(other, tmp_path)
    sp.write_plan(other, tmp_path, overwrite=True)
    assert [p.name for p in sp.find_plans_for_night(tmp_path, plan.night)] == [other.filename]


def test_invalid_plan_not_written(tmp_path):
    plan = sp.plan_from_wizard(BOT_SEQ)
    plan.targets[0].ra = "nope"
    with pytest.raises(sp.PlanError):
        sp.write_plan(plan, tmp_path)
    assert not list(tmp_path.iterdir())


def test_bot_integration_publish_and_tail(tmp_path):
    from bot_integration import publish_from_bot, StatusTailer
    path, text = publish_from_bot(BOT_SEQ, tmp_path)
    assert "Some gases in space" in text and path.exists()

    status = tmp_path / (path.name[:-len(".json")] + ".status.jsonl")
    tailer = StatusTailer(tmp_path)
    status.write_text('{"event":"plan_loaded","message":"ok"}\n{"event":"partial"')
    evs = tailer.poll()
    assert [e["event"] for e in evs] == ["plan_loaded"]
    with status.open("a") as f:
        f.write(',"message":"x"}\n')
    assert [e["event"] for e in tailer.poll()] == ["partial"]
    assert tailer.poll() == []


@pytest.mark.parametrize("path", sorted((ROOT / "examples").glob("*.plan.json")), ids=lambda p: p.name)
def test_examples_match_schema_and_validator(path):
    """Every example is valid, except the *_BadPlan one, which must fail both checks."""
    jsonschema = pytest.importorskip("jsonschema")
    d = json.loads(path.read_text(encoding="utf-8"))
    schema_errors = list(jsonschema.Draft202012Validator(json.loads(SCHEMA.read_text())).iter_errors(d))
    problems = sp.validate(sp.Plan.from_dict(d))
    if "BadPlan" in path.name:
        assert problems and schema_errors
    else:
        assert problems == [] and schema_errors == []


def test_target_name_adds_for_client():
    assert sp.target_name("M 31", "Jane Doe") == "M 31 for Jane Doe"
    assert sp.target_name("M 31", None) == "M 31"
    assert sp.target_name("  M 31 ", "  ") == "M 31"
    # idempotent, and the old "<target> <client>" style is converted
    assert sp.target_name("M 31 for Jane Doe", "Jane Doe") == "M 31 for Jane Doe"
    assert sp.target_name("M 31 Jane Doe", "Jane Doe") == "M 31 for Jane Doe"


def test_camera_temperature_is_optional_and_validated():
    plan = sp.plan_from_wizard(BOT_SEQ)
    assert plan.camera_temperature is None
    assert "camera_temperature" in plan.to_dict()          # written as null
    plan = sp.plan_from_wizard({**BOT_SEQ, "camera_temperature": -10})
    assert plan.camera_temperature == -10
    assert sp.Plan.from_dict(plan.to_dict()).camera_temperature == -10
    assert not [e for e in sp.validate(plan) if "camera_temperature" in e]
    plan.camera_temperature = -80
    assert any("camera_temperature" in e for e in sp.validate(plan))

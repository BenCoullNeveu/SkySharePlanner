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
    assert plan.targets[0].name == "Some gases in space Jane Doe"   # SkyStack folder name, as before
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


def test_unknown_filter():
    plan = sp.plan_from_wizard(BOT_SEQ)
    problems = sp.validate(plan, known_filters=["L", "R", "G", "B"])
    assert any("unknown filter 'H'" in x for x in problems)


def test_write_is_atomic_and_one_per_night(tmp_path):
    plan = sp.plan_from_wizard(BOT_SEQ)
    path = sp.write_plan(plan, tmp_path)
    assert path.exists() and not list(tmp_path.glob("*.tmp"))
    assert sp.load_plan(path).targets[0].name == "Some gases in space Jane Doe"

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

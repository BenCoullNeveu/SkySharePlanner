"""Tests for skystack/session_watch.py - queue the pipeline when a SkyShare night finishes."""
from __future__ import annotations

import json
from datetime import datetime, timedelta
from pathlib import Path

import pytest

from skystack import jobs, session_watch as sw

NIGHT = "2026-11-07"
T0 = datetime(2026, 11, 8, 6, 30)


@pytest.fixture
def env(tmp_path):
    cfg = {
        "light_base_dir": str(tmp_path / "local" / "LIGHT"),
        "flat_base_dir": str(tmp_path / "local" / "FLAT"),
        "dropbox_source": str(tmp_path / "dropbox"),
        "session_watch": {"enabled": True, "plans_dir": str(tmp_path / "dropbox" / "Plans"),
                          "settle_minutes": 10, "max_wait_hours": 12},
    }
    (tmp_path / "dropbox" / "Plans").mkdir(parents=True)
    return cfg, tmp_path / "jobs", tmp_path


def manifest(env, targets, outcome="complete", stamp="2026-11-08T11:00:00Z", **extra):
    cfg, _, _ = env
    m = {
        "schema_version": 1, "kind": "skyshare_session", "night": NIGHT, "client": "Jane",
        "plan_file": f"{NIGHT}_Jane.plan.json", "completed_utc": stamp, "outcome": outcome,
        "targets": [{"name": n, "session": NIGHT, "ran": k > 0, "lights_total": k} for n, k in targets],
        "flats": {"frames_total": 30, "sets": [], "skipped": []},
        "actions": [{"type": "skystack_run", "target": n, "sessions": [NIGHT], "triage": "send",
                     "postprocess": None, "export": True} for n, k in targets if k > 0],
        **extra,
    }
    path = Path(cfg["session_watch"]["plans_dir"]) / f"{NIGHT}_Jane.session.json"
    path.write_text(json.dumps(m))
    return path


def lights(env, target, n, where="dropbox"):
    _, _, root = env
    base = root / "dropbox" / "LIGHT" if where == "dropbox" else root / "local" / "LIGHT"
    d = base / target / NIGHT / "LIGHT" / "L"
    d.mkdir(parents=True, exist_ok=True)
    start = len(list(d.glob("*.fits")))
    for i in range(start, start + n):
        (d / f"LIGHT_L_{i:04d}.fits").write_bytes(b"x")


def poll(env, now, **kw):
    cfg, jobs_dir, _ = env
    sent = []
    changed = sw.poll(cfg, jobs_dir, now=now, notify=sent.append, **kw)
    return changed, sent


def finish(env, status="succeeded"):
    """Pretend the queued job ran (the real runner writes this)."""
    jobs_dir = env[1]
    (job,) = jobs.waiting_jobs(jobs_dir)
    job.update(status=status, returncode=0 if status == "succeeded" else 1)
    jobs._write_job(jobs_dir, job)
    return job


def test_waits_for_frames_and_quiet_then_runs_chain_in_order(env):
    manifest(env, [("NGC 7635 Jane", 4), ("M 31 Jane", 2)])
    lights(env, "NGC 7635 Jane", 4)
    lights(env, "M 31 Jane", 1)

    changed, _ = poll(env, T0)
    assert changed == []                                   # M 31 still short

    lights(env, "M 31 Jane", 1, where="local")             # auto_transfer already moved one
    poll(env, T0 + timedelta(minutes=1))                   # complete now, but not quiet yet
    changed, sent = poll(env, T0 + timedelta(minutes=5))
    assert changed == []

    changed, sent = poll(env, T0 + timedelta(minutes=12))
    assert changed[-1]["status"] == "processing"
    assert "processing 2 target(s): NGC 7635 Jane, M 31 Jane" in sent[0]
    (first,) = jobs.waiting_jobs(env[1])                   # only ONE job at a time
    assert first["argv"][:3] == ["run", "-t", "NGC 7635 Jane"]
    assert "--skip-transfer" not in first["argv"]          # first job transfers + builds masters
    assert ["--triage", "send"] == first["argv"][5:7] and "--export" in first["argv"]

    poll(env, T0 + timedelta(minutes=13))                  # still queued: nothing new
    assert len(jobs.waiting_jobs(env[1])) == 1

    finish(env)
    poll(env, T0 + timedelta(minutes=20))
    (second,) = jobs.waiting_jobs(env[1])
    assert second["argv"][:3] == ["run", "-t", "M 31 Jane"]
    assert {"--skip-transfer", "--skip-masters"} <= set(second["argv"])

    finish(env)
    changed, sent = poll(env, T0 + timedelta(minutes=40))
    assert changed[-1]["status"] == "done" and "all 2 pipeline job(s) finished" in sent[-1]
    changed, _ = poll(env, T0 + timedelta(minutes=50))     # never twice
    assert changed == [] and jobs.waiting_jobs(env[1]) == []


def test_failed_job_stops_the_chain(env):
    manifest(env, [("A Jane", 1), ("B Jane", 1)])
    lights(env, "A Jane", 1)
    lights(env, "B Jane", 1)
    poll(env, T0)
    poll(env, T0 + timedelta(minutes=11))
    finish(env, "failed")
    changed, sent = poll(env, T0 + timedelta(minutes=20))
    assert changed[-1]["status"] == "failed"
    assert "1 remaining target(s) not started" in sent[-1]
    assert jobs.waiting_jobs(env[1]) == []


def test_needs_approval(env):
    env[0]["session_watch"]["needs_approval"] = True
    manifest(env, [("A Jane", 1)])
    lights(env, "A Jane", 1)
    poll(env, T0)
    _, sent = poll(env, T0 + timedelta(minutes=11))
    (job,) = jobs.waiting_jobs(env[1])
    assert job["status"] == "awaiting_approval" and "awaiting your approval" in sent[-1]


def test_state_file_is_not_mistaken_for_a_job(env):
    manifest(env, [("A Jane", 1)])
    lights(env, "A Jane", 1)
    poll(env, T0)
    assert (env[1] / "skyshare" / "sessions.json").exists()
    assert all(j.get("argv") for j in jobs.list_jobs(env[1]))


def test_argv_is_accepted_by_run_routine(env):
    pytest.importorskip("xisf")   # run_routine imports the full pipeline
    import run_routine

    path = manifest(env, [("M 31 Jane", 2), ("M 33 Jane", 1)])
    argvs, notes = sw.action_argvs(sw.read_manifest(path))
    assert notes == []
    for a in argvs:
        run_routine.build_parser().parse_args(a)


def test_runs_with_what_arrived_after_max_wait(env):
    manifest(env, [("M 31 Jane", 10)])
    lights(env, "M 31 Jane", 3)
    poll(env, T0)
    changed, _ = poll(env, T0 + timedelta(hours=6))
    assert changed == []
    changed, sent = poll(env, T0 + timedelta(hours=12, minutes=1))
    assert changed[0]["status"] == "processing"
    assert changed[0]["missing"] == ["M 31 Jane: 3/10"]
    assert "running without every frame" in sent[0]


def test_nothing_captured_is_acknowledged_without_a_job(env):
    manifest(env, [("M 31 Jane", 0)], outcome="nothing_captured")
    changed, sent = poll(env, T0)
    assert changed[0]["status"] == "done" and jobs.waiting_jobs(env[1]) == []
    assert "nothing captured" in sent[0]


def test_rewritten_manifest_is_handled_again(env):
    manifest(env, [("M 31 Jane", 1)])
    lights(env, "M 31 Jane", 1)
    poll(env, T0)
    poll(env, T0 + timedelta(minutes=11))
    assert len(jobs.waiting_jobs(env[1])) == 1
    finish(env)

    manifest(env, [("M 31 Jane", 2)], stamp="2026-11-08T12:00:00Z")   # NINA ran again
    lights(env, "M 31 Jane", 1)
    poll(env, T0 + timedelta(minutes=20))
    poll(env, T0 + timedelta(minutes=31))
    assert len(jobs.waiting_jobs(env[1])) == 1             # the new night's job


def test_unknown_action_and_bad_manifest_are_reported_not_fatal(env):
    path = manifest(env, [("M 31 Jane", 1)])
    m = json.loads(path.read_text())
    m["actions"].append({"type": "make_coffee"})
    path.write_text(json.dumps(m))
    argvs, notes = sw.action_argvs(m)
    assert len(argvs) == 1 and "unknown type 'make_coffee'" in notes[0]

    bad = path.with_name("2026-11-06_Bob.session.json")
    bad.write_text("{ nope")
    changed, sent = poll(env, T0)
    assert any(c["status"] == "error" for c in changed)
    changed, sent = poll(env, T0 + timedelta(minutes=1))   # reported once
    assert not any(c.get("status") == "error" for c in changed) and not any("2026-11-06" in s for s in sent)


def test_overdue_plan_without_manifest_is_reported_once(env):
    cfg, _, _ = env
    plans = Path(cfg["session_watch"]["plans_dir"])
    (plans / f"{NIGHT}_Jane.plan.json").write_text("{}")
    _, sent = poll(env, datetime(2026, 11, 8, 13, 0))
    assert sent == []
    _, sent = poll(env, datetime(2026, 11, 8, 14, 5))
    assert "no session manifest" in sent[0]
    _, sent = poll(env, datetime(2026, 11, 8, 15, 0))
    assert sent == []


def test_dry_run_changes_nothing(env):
    manifest(env, [("M 31 Jane", 1)])
    lights(env, "M 31 Jane", 1)
    changed, sent = poll(env, T0, dry_run=True)
    assert sent == [] and not (env[1] / "skyshare" / "sessions.json").exists()
    assert changed[0]["status"] == "would_queue" and changed[0]["argv"][0][:3] == ["run", "-t", "M 31 Jane"]


def test_disabled_does_nothing(env):
    cfg, jobs_dir, _ = env
    cfg["session_watch"]["enabled"] = False
    manifest(env, [("M 31 Jane", 1)])
    assert sw.poll(cfg, jobs_dir, now=T0, notify=lambda m: None) == []

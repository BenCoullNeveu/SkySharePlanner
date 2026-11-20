"""Start the pipeline automatically when a SkyShare night has finished.

The NINA plugin (SkyShare Planner) ends every night by writing a session
manifest next to the night's plan in Dropbox:

    <plans_dir>/2026-11-07_JaneDoe.plan.json        what the client reserved
    <plans_dir>/2026-11-07_JaneDoe.session.json     what was captured + what to run

skystack_server.py calls poll() every tick. For each new manifest it

    1. waits until the frames it lists have arrived (Dropbox source or the local
       LIGHT folder - auto_transfer may already have moved some), and until no
       new LIGHT/FLAT files have appeared for `settle_minutes`, so flats synced
       after the lights are included;
    2. runs the actions as a chain of jobs through skystack.jobs - the same
       queue the dashboard uses, so the one-job-at-a-time rule and owner
       approval (`needs_approval`) apply. Each job is queued only after the
       previous one succeeded (the first runs transfer + masters, later ones
       skip them, so order matters); a failed job stops the chain;
    3. posts what it did to Discord (SKYSTACK_DISCORD_WEBHOOK).

If frames are still missing after `max_wait_hours`, it runs with what arrived
and says so. A manifest with nothing captured is acknowledged without a job.
A plan whose night ended (`overdue_hour` the next day) with no manifest is
reported once - usually the sequence aborted before "Session Complete".

State (which manifests were handled, job ids) lives in
pipeline_jobs/skyshare/sessions.json, so restarts don't queue anything twice. If NINA
rewrites a manifest (new completed_utc), it is handled again.

    python -m skystack.session_watch            # one poll, print the state
    python -m skystack.session_watch --dry-run  # what would be queued, change nothing
"""
from __future__ import annotations

import argparse
import json
import logging
from datetime import datetime, timedelta
from pathlib import Path
from typing import Any, Callable, Dict, Iterable, List, Mapping, Optional

from skystack import jobs

logger = logging.getLogger(__name__)

MANIFEST_SUFFIX = ".session.json"
PLAN_SUFFIX = ".plan.json"
SUPPORTED_SCHEMA = 1
IMAGE_SUFFIXES = {".fits", ".fit", ".fts", ".xisf"}
DEFAULTS = {
    "enabled": False,
    "plans_dir": None,
    "settle_minutes": 10,
    "max_wait_hours": 12,
    "needs_approval": False,
    "overdue_hour": 14,
}

Notifier = Callable[[str], None]


# ---------------------------------------------------------------------------
# Config, state
# ---------------------------------------------------------------------------

def settings(config: Mapping[str, Any]) -> Dict[str, Any]:
    return {**DEFAULTS, **(config.get("session_watch") or {})}


def _state_path(jobs_dir: Path) -> Path:
    # in a subfolder: jobs.list_jobs() treats every *.json directly in jobs_dir as a job
    return Path(jobs_dir) / "skyshare" / "sessions.json"


def load_state(jobs_dir: Path) -> Dict[str, Any]:
    p = _state_path(jobs_dir)
    if p.exists():
        try:
            return json.loads(p.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            logger.error("Corrupt %s; starting fresh (old file kept as .bad)", p)
            p.replace(p.with_suffix(".json.bad"))
    return {"sessions": {}, "overdue_reported": []}


def save_state(jobs_dir: Path, state: Dict[str, Any]) -> None:
    p = _state_path(jobs_dir)
    p.parent.mkdir(parents=True, exist_ok=True)
    tmp = p.with_suffix(".tmp")
    tmp.write_text(json.dumps(state, indent=2), encoding="utf-8")
    tmp.replace(p)


# ---------------------------------------------------------------------------
# Manifests
# ---------------------------------------------------------------------------

class ManifestError(ValueError):
    pass


def read_manifest(path: Path) -> Dict[str, Any]:
    try:
        m = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as e:
        raise ManifestError(f"unreadable: {e}") from e
    if m.get("kind") != "skyshare_session":
        raise ManifestError("not a skyshare_session manifest")
    if m.get("schema_version") != SUPPORTED_SCHEMA:
        raise ManifestError(f"schema_version {m.get('schema_version')} not supported (want {SUPPORTED_SCHEMA})")
    for key in ("night", "targets", "actions"):
        if key not in m:
            raise ManifestError(f"missing '{key}'")
    return m


def _count_images(dirs: Iterable[Path]) -> int:
    n = 0
    for d in dirs:
        if d.exists():
            n += sum(1 for f in d.rglob("*") if f.is_file() and f.suffix.lower() in IMAGE_SUFFIXES)
    return n


def light_dirs(config: Mapping[str, Any], target: str, session: str) -> List[Path]:
    """Where a target's session can be right now: still in Dropbox, or already transferred."""
    dirs = [Path(config["light_base_dir"]) / target / session]
    if config.get("dropbox_source"):
        dirs.append(Path(config["dropbox_source"]) / "LIGHT" / target / session)
    return dirs


def flat_dirs(config: Mapping[str, Any]) -> List[Path]:
    dirs = [Path(config["flat_base_dir"])] if config.get("flat_base_dir") else []
    if config.get("dropbox_source"):
        dirs.append(Path(config["dropbox_source"]) / "FLAT")
    return dirs


def arrival(config: Mapping[str, Any], manifest: Mapping[str, Any]) -> Dict[str, Any]:
    """How many of the manifest's lights are here, plus a fingerprint of LIGHT+FLAT activity."""
    per_target = {}
    for t in manifest["targets"]:
        if not t.get("ran"):
            continue
        have = _count_images(light_dirs(config, t["name"], t.get("session") or manifest["night"]))
        per_target[t["name"]] = {"have": have, "want": int(t.get("lights_total", 0))}
    flats = _count_images(flat_dirs(config))
    complete = all(v["have"] >= v["want"] for v in per_target.values())
    fingerprint = [sum(v["have"] for v in per_target.values()), flats]
    return {"targets": per_target, "flat_files": flats, "complete": complete, "fingerprint": fingerprint}


def action_argvs(manifest: Mapping[str, Any]) -> tuple[List[List[str]], List[str]]:
    """Manifest actions -> run_routine.py argv lists (+ notes for anything skipped)."""
    argvs, notes = [], []
    for i, a in enumerate(manifest.get("actions", [])):
        if a.get("type") != "skystack_run":
            notes.append(f"action {i + 1}: unknown type {a.get('type')!r} skipped")
            continue
        first = not argvs
        try:
            argvs.append(jobs.pipeline_argv(
                "run", a["target"], a.get("sessions") or [manifest["night"]],
                skip_transfer=not first, skip_masters=not first,   # first job transfers + builds masters
                triage_mode=a.get("triage"),
                postprocess=a.get("postprocess"), export=a.get("export"),
            ))
        except (KeyError, ValueError) as e:
            notes.append(f"action {i + 1}: invalid ({e}) skipped")
    return argvs, notes


# ---------------------------------------------------------------------------
# Poll
# ---------------------------------------------------------------------------

def poll(
    config: Mapping[str, Any],
    jobs_dir: Path = jobs.DEFAULT_JOBS_DIR,
    now: Optional[datetime] = None,
    notify: Optional[Notifier] = None,
    dry_run: bool = False,
) -> List[Dict[str, Any]]:
    """Handle new/waiting manifests once. Returns the sessions whose state changed."""
    cfg = settings(config)
    if not cfg["enabled"] or not cfg["plans_dir"]:
        return []
    plans_dir = Path(cfg["plans_dir"])
    if not plans_dir.exists():
        logger.warning("session_watch: plans_dir not found: %s", plans_dir)
        return []

    now = now or datetime.now()
    notify = notify or default_notifier(config)
    state = load_state(jobs_dir)
    sessions = state.setdefault("sessions", {})
    changed: List[Dict[str, Any]] = []

    def update(name: str, entry: Dict[str, Any], message: Optional[str] = None) -> None:
        sessions[name] = entry
        changed.append({"manifest": name, **entry})
        if message:
            logger.info("session_watch: %s", message)
            if not dry_run:
                try:
                    notify(message)
                except Exception as e:  # never let Discord break the server loop
                    logger.warning("session_watch: notify failed: %s", e)

    for path in sorted(plans_dir.glob(f"*{MANIFEST_SUFFIX}")):
        if path.name.startswith("."):
            continue  # atomic-write temp file
        entry = dict(sessions.get(path.name) or {})
        try:
            m = read_manifest(path)
        except ManifestError as e:
            if entry.get("status") != "error" or entry.get("error") != str(e):
                update(path.name, {"status": "error", "error": str(e)}, f"WARNING: {path.name}: {e}")
            continue

        stamp = m.get("completed_utc")
        if entry.get("completed_utc") != stamp:          # new, or NINA rewrote it
            entry = {"status": "waiting", "completed_utc": stamp, "first_seen": now.isoformat(timespec="seconds")}
        who = m.get("client") or "?"
        label = f"{m['night']} {who}"

        if entry["status"] == "processing":
            if not dry_run:
                _advance(path.name, entry, label, jobs_dir, cfg, update)
            continue
        if entry["status"] != "waiting":
            continue

        if m.get("outcome") == "nothing_captured" or not m.get("actions"):
            why = ("nothing captured" if m.get("outcome") == "nothing_captured" else f"darks only ({(m.get('darks') or {}).get('frames_total', 0)} frames)" if not any(t.get("ran") for t in m.get("targets", [])) else "processing disabled in plan")
            update(path.name, {**entry, "status": "done", "jobs": [], "note": why},
                   f"{label}: {why} - no pipeline job.")
            continue

        arr = arrival(config, m)
        if arr["fingerprint"] != entry.get("fingerprint"):
            entry.update(fingerprint=arr["fingerprint"], last_change=now.isoformat(timespec="seconds"))
        quiet = now - datetime.fromisoformat(entry["last_change"]) >= timedelta(minutes=cfg["settle_minutes"])
        waited = now - datetime.fromisoformat(entry["first_seen"]) >= timedelta(hours=cfg["max_wait_hours"])
        entry["arrival"] = arr["targets"]

        # dry runs keep no state, so they can't see "quiet": report as if settled
        if not ((quiet or dry_run) and (arr["complete"] or waited)):
            sessions[path.name] = entry  # still waiting; remember progress
            continue

        argvs, notes = action_argvs(m)
        missing = [f"{n}: {v['have']}/{v['want']}" for n, v in arr["targets"].items() if v["have"] < v["want"]]
        if dry_run:
            changed.append({"manifest": path.name, "status": "would_queue", "argv": argvs, "notes": notes,
                            "missing": missing})
            continue

        msg = f"{label}: data is in - processing {len(argvs)} target(s): " + ", ".join(a[2] for a in argvs)
        if missing:
            msg += f"\nWARNING: running without every frame after {cfg['max_wait_hours']} h: " + "; ".join(missing)
        if notes:
            msg += "\n" + "\n".join(notes)
        entry.update(status="processing", pending=argvs, jobs=[], current=None, missing=missing, notes=notes,
                     ready_at=now.isoformat(timespec="seconds"))
        update(path.name, entry, msg)
        _advance(path.name, entry, label, jobs_dir, cfg, update)

    _report_overdue(plans_dir, state, cfg, now, notify, dry_run, changed)

    if not dry_run:
        save_state(jobs_dir, state)
    return changed


FAILED_STATUSES = ("failed", "died", "cancelled", "rejected")


def _advance(name: str, entry: Dict[str, Any], label: str, jobs_dir: Path, cfg: Mapping[str, Any],
             update: Callable[..., None]) -> None:
    """Move a session's job chain one step: wait, queue the next job, or finish."""
    current = entry.get("current")
    if current:
        try:
            status = jobs.read_job(current, jobs_dir)["status"]
        except (OSError, json.JSONDecodeError, KeyError):
            status = "cancelled"   # job file deleted from the dashboard
        if status not in ("succeeded",) + FAILED_STATUSES:
            return                 # queued / awaiting approval / running
        if status in FAILED_STATUSES:
            left = len(entry.get("pending", []))
            update(name, {**entry, "status": "failed", "failed_job": current, "failed_status": status},
                   f"FAILED: {label}: job {current} {status}"
                   + (f"; {left} remaining target(s) not started" if left else "")
                   + " - see pipeline_jobs/ log, fix, then re-run from the dashboard.")
            return
        entry["current"] = None

    if entry.get("pending"):
        argv = entry["pending"].pop(0)
        job = jobs.submit_request(argv, jobs_dir, needs_approval=bool(cfg["needs_approval"]))
        entry["current"] = job["id"]
        entry.setdefault("jobs", []).append(job["id"])
        # with approval on, every step needs a click - say so each time
        update(name, entry, f"{label}: {argv[2]} is awaiting your approval in the dashboard"
               if cfg["needs_approval"] else None)
        return

    update(name, {**entry, "status": "done"},
           f"{label}: all {len(entry.get('jobs', []))} pipeline job(s) finished.")


def _report_overdue(plans_dir: Path, state: Dict[str, Any], cfg: Mapping[str, Any], now: datetime,
                    notify: Notifier, dry_run: bool, changed: List[Dict[str, Any]]) -> None:
    """A plan whose night is over but that has no manifest: tell the owner once."""
    reported = set(state.setdefault("overdue_reported", []))
    for plan in sorted(plans_dir.glob(f"*{PLAN_SUFFIX}")):
        if plan.name.startswith("."):
            continue
        manifest = plan.with_name(plan.name[: -len(PLAN_SUFFIX)] + MANIFEST_SUFFIX)
        if manifest.exists() or plan.name in reported:
            continue
        try:
            night = datetime.strptime(plan.name[:10], "%Y-%m-%d")
        except ValueError:
            continue
        due = night + timedelta(days=1, hours=int(cfg["overdue_hour"]))
        if now < due or now - due > timedelta(days=7):   # not yet, or too old to matter
            continue
        changed.append({"manifest": manifest.name, "status": "overdue"})
        if not dry_run:
            reported.add(plan.name)
            try:
                notify(f"WARNING: {plan.name}: the night is over but NINA wrote no session manifest "
                       "(sequence aborted before 'SkyShare: Session Complete'?). Nothing was queued.")
            except Exception as e:
                logger.warning("session_watch: notify failed: %s", e)
    state["overdue_reported"] = sorted(reported)


def default_notifier(config: Mapping[str, Any]) -> Notifier:
    from skystack.delivery import post_to_discord, webhook_url

    url = webhook_url(config.get("remote_triage") or {})
    if not url:
        return lambda msg: None
    return lambda msg: post_to_discord(url, msg, timeout_s=15)


def main() -> None:
    from astro_utils.config_loader import load_config

    parser = argparse.ArgumentParser(description="Poll SkyShare session manifests once")
    parser.add_argument("--dry-run", action="store_true", help="show what would be queued, change nothing")
    args = parser.parse_args()
    logging.basicConfig(level="INFO", format="%(asctime)s - %(levelname)s - %(message)s")
    config = load_config(jobs.REPO_ROOT / "config.yaml")
    changed = poll(config, dry_run=args.dry_run, notify=print if args.dry_run else None)
    print(json.dumps(changed or load_state(jobs.DEFAULT_JOBS_DIR), indent=2))


if __name__ == "__main__":
    main()

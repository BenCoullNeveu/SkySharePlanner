"""
skyshare_plan.py - build, validate and publish SkyShare night plans.

A plan is a small JSON file (schema v1, see ../schema/plan.schema.json) that
describes ONE reserved night: which targets, in what order, with which
exposures. The SkyShare Planner NINA plugin reads it at the start of the
night and builds the real sequence from it.

This module has no third-party dependencies so it can be imported by the
Discord bot and the SkyStack dashboard alike.

Typical use (Discord bot, after the user confirms):

    from skyshare_plan import plan_from_wizard, write_plan
    plan = plan_from_wizard(seq)              # seq = the bot's wizard dict
    path = write_plan(plan, PLANS_DIR)        # atomic write into Dropbox

"""
from __future__ import annotations

import json
import os
import re
import tempfile
from dataclasses import dataclass, field, asdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable, Iterable

SCHEMA_VERSION = 1
PLAN_SUFFIX = ".plan.json"

_RA_RE = re.compile(r"^\d{1,2}:\d{1,2}:\d{1,2}(\.\d+)?$")
_DEC_RE = re.compile(r"^[+-]?\d{1,2}:\d{1,2}:\d{1,2}(\.\d+)?$")
_HHMM_RE = re.compile(r"^([01]\d|2[0-3]):[0-5]\d$")
_NIGHT_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")


class PlanError(ValueError):
    """Raised when a plan is invalid. `.problems` lists every issue found."""

    def __init__(self, problems: list[str]):
        super().__init__("; ".join(problems))
        self.problems = problems


# ---------------------------------------------------------------------------
# Data model (mirrors the JSON schema 1:1)
# ---------------------------------------------------------------------------

@dataclass
class Exposure:
    filter: str
    exposure: float
    count: int
    gain: int = -1
    offset: int = -1
    binning: int = 1


@dataclass
class Target:
    name: str
    ra: str
    dec: str
    exposures: list[Exposure]
    rotation: float = 0.0
    start: str | None = None
    end: str | None = None


@dataclass
class Flats:
    enabled: bool = True
    count: int = 30


@dataclass
class Processing:
    """What SkyStack should do after the night (copied into the session manifest)."""
    enabled: bool = True
    triage: str | None = None        # local | web | send | build | None = SkyStack default
    postprocess: bool | None = None
    export: bool | None = None


@dataclass
class Plan:
    night: str
    targets: list[Target]
    client: str | None = None
    flats: Flats = field(default_factory=Flats)
    processing: Processing = field(default_factory=Processing)
    created_by: str | None = None
    created_utc: str | None = None
    schema_version: int = SCHEMA_VERSION

    # -- serialisation -----------------------------------------------------
    def to_dict(self) -> dict:
        d = asdict(self)
        # stable key order: schema_version first, easier to eyeball
        return {"schema_version": d.pop("schema_version"), **d}

    def to_json(self) -> str:
        return json.dumps(self.to_dict(), indent=2, ensure_ascii=False)

    @staticmethod
    def from_dict(d: dict) -> "Plan":
        targets = [
            Target(
                name=t["name"], ra=t["ra"], dec=t["dec"],
                rotation=float(t.get("rotation", 0) or 0),
                start=t.get("start"), end=t.get("end"),
                exposures=[Exposure(**e) for e in t["exposures"]],
            )
            for t in d["targets"]
        ]
        return Plan(
            schema_version=d.get("schema_version", SCHEMA_VERSION),
            night=d["night"], client=d.get("client"),
            created_by=d.get("created_by"), created_utc=d.get("created_utc"),
            targets=targets, flats=Flats(**d.get("flats", {})),
            processing=Processing(**d.get("processing", {})),
        )

    # -- file naming -------------------------------------------------------
    @property
    def filename(self) -> str:
        return f"{self.night}_{slug(self.client) or 'plan'}{PLAN_SUFFIX}"


# ---------------------------------------------------------------------------
# Validation (same rules as the schema + a few semantic checks)
# ---------------------------------------------------------------------------

def validate(plan: Plan, known_filters: Iterable[str] | None = None) -> list[str]:
    """Return a list of problems (empty list = valid).

    `known_filters` - optional list of filter names from the NINA profile.
    The plugin re-checks this on the acquisition PC, but checking early lets
    the bot reject a typo while the user is still in the chat.
    """
    p: list[str] = []
    if plan.schema_version != SCHEMA_VERSION:
        p.append(f"schema_version must be {SCHEMA_VERSION}")
    if not _NIGHT_RE.match(plan.night or ""):
        p.append("night must be YYYY-MM-DD")
    else:
        try:
            datetime.strptime(plan.night, "%Y-%m-%d")
        except ValueError:
            p.append(f"night '{plan.night}' is not a real date")
    if not plan.targets:
        p.append("plan has no targets")
    if not (1 <= plan.flats.count <= 200):
        p.append("flats.count must be 1-200")
    if plan.processing.triage not in (None, "local", "web", "send", "build"):
        p.append("processing.triage must be local, web, send, build or null")
    names = [t.name for t in plan.targets]
    if len(set(names)) != len(names):
        p.append("target names must be unique (they are SkyStack folder names)")

    filters = {f.upper() for f in known_filters} if known_filters else None
    rotations = set()
    for i, t in enumerate(plan.targets, 1):
        tag = f"target {i} ({t.name or '?'})"
        if not t.name or len(t.name) > 80:
            p.append(f"{tag}: name must be 1-80 chars")
        if not _RA_RE.match(t.ra or ""):
            p.append(f"{tag}: ra must be HH:MM:SS")
        else:
            h, m, s = (float(x) for x in t.ra.split(":"))
            if not (0 <= h < 24 and m < 60 and s < 60):
                p.append(f"{tag}: ra out of range")
        if not _DEC_RE.match(t.dec or ""):
            p.append(f"{tag}: dec must be [+-]DD:MM:SS")
        else:
            d, m, s = (float(x) for x in t.dec.lstrip("+-").split(":"))
            if not (d <= 90 and m < 60 and s < 60) or (d == 90 and (m or s)):
                p.append(f"{tag}: dec out of range")
        if not (0 <= t.rotation < 360):
            p.append(f"{tag}: rotation must be 0-359.9")
        rotations.add(round(t.rotation, 1))
        for k in ("start", "end"):
            v = getattr(t, k)
            if v is not None and not _HHMM_RE.match(v):
                p.append(f"{tag}: {k} must be HH:MM (24h) or null")
        if not t.exposures:
            p.append(f"{tag}: no exposures")
        for e in t.exposures:
            if not (0 < e.exposure <= 3600):
                p.append(f"{tag}/{e.filter}: exposure must be 0-3600 s")
            if not (1 <= e.count <= 2000):
                p.append(f"{tag}/{e.filter}: count must be 1-2000")
            if e.gain < -1 or e.offset < -1:
                p.append(f"{tag}/{e.filter}: gain/offset must be >= -1")
            if not (1 <= e.binning <= 4):
                p.append(f"{tag}/{e.filter}: binning must be 1-4")
            if filters is not None and e.filter.upper() not in filters:
                p.append(f"{tag}: unknown filter '{e.filter}' (have: {', '.join(sorted(filters))})")
    return p


def rotation_warning(plan: Plan, has_rotator: bool) -> str | None:
    """Without a rotator the camera can't change angle between targets, so
    flats can only be valid for one rotation. Surface that to the user."""
    rots = sorted({round(t.rotation, 1) for t in plan.targets})
    if not has_rotator and len(rots) > 1:
        return (f"Targets use {len(rots)} different rotations ({', '.join(map(str, rots))}) "
                "but this scope has no rotator - only flats for the final orientation will be taken.")
    return None


# ---------------------------------------------------------------------------
# Building plans from the existing Discord-bot wizard data
# ---------------------------------------------------------------------------

def default_gain(filt: str) -> int:
    """Current SkyShare rule: narrowband at gain 100, broadband at 0."""
    return 100 if filt.upper() in ("H", "O", "S") else 0


def plan_from_wizard(seq: dict, *, gain_rule: Callable[[str], int] = default_gain,
                     flats_count: int = 30, created_by: str = "discord",
                     processing: Processing | None = None) -> Plan:
    """Convert the bot's existing wizard/JSON dict into a Plan.

    Accepts the bot's current shape:
        {"name", "ra", "dec", "rot", "exposures": {"L": (300, 2), ...},
         "date": "DD-MM-YYYY", "user": "Jane"}
    and the new multi-target shape:
        {"date": ..., "user": ..., "targets": [ {name, ra, dec, rot, start, end, exposures}, ... ]}

    Like the old bot, the client name is appended to each target name ("M 31 Jane Doe"):
    that name becomes the NINA target name and therefore SkyStack's LIGHT/<target>/ folder.
    """
    raw_targets = seq.get("targets") or [seq]
    user = seq.get("user")
    targets = []
    for t in raw_targets:
        exps = []
        for filt, v in t["exposures"].items():
            exp, count = (v[0], v[1]) if isinstance(v, (list, tuple)) else (v["exposure"], v["count"])
            exps.append(Exposure(filter=str(filt), exposure=float(exp), count=int(count),
                                 gain=int(gain_rule(str(filt)))))
        name = str(t["name"]).strip()
        if user and not name.endswith(f" {user}"):
            name = f"{name} {user}"
        targets.append(Target(
            name=name, ra=str(t["ra"]), dec=str(t["dec"]),
            rotation=float(t.get("rot", t.get("rotation", 0)) or 0) % 360,
            start=t.get("start"), end=t.get("end"), exposures=exps,
        ))

    return Plan(
        night=_normalise_date(seq.get("date")),
        client=seq.get("user"),
        targets=targets,
        flats=Flats(count=flats_count),
        processing=processing or Processing(),
        created_by=created_by,
        created_utc=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    )


def _normalise_date(date: str | None) -> str:
    """Bot uses DD-MM-YYYY; plans use YYYY-MM-DD. Default = tonight."""
    if not date:
        return datetime.now().strftime("%Y-%m-%d")
    for fmt in ("%Y-%m-%d", "%d-%m-%Y"):
        try:
            return datetime.strptime(date, fmt).strftime("%Y-%m-%d")
        except ValueError:
            pass
    raise PlanError([f"date '{date}' must be DD-MM-YYYY or YYYY-MM-DD"])


# ---------------------------------------------------------------------------
# Publishing (atomic write into the Dropbox plans folder)
# ---------------------------------------------------------------------------

def find_plans_for_night(plans_dir: str | os.PathLike, night: str) -> list[Path]:
    return sorted(Path(plans_dir).glob(f"{night}_*{PLAN_SUFFIX}"))


def write_plan(plan: Plan, plans_dir: str | os.PathLike, *, overwrite: bool = False,
               known_filters: Iterable[str] | None = None) -> Path:
    """Validate and write the plan atomically. Returns the final path.

    Atomic = write to a temp file in the same folder, then os.replace().
    Dropbox (and the NINA plugin) therefore never see a half-written file;
    the plugin also ignores *.tmp files.

    One reservation per night: refuses if another plan exists for the same
    night unless overwrite=True (which replaces ALL plans for that night).
    """
    problems = validate(plan, known_filters)
    if problems:
        raise PlanError(problems)

    plans_dir = Path(plans_dir)
    plans_dir.mkdir(parents=True, exist_ok=True)
    existing = find_plans_for_night(plans_dir, plan.night)
    final = plans_dir / plan.filename
    others = [p for p in existing if p != final]
    if existing and not overwrite:
        raise PlanError([f"a plan already exists for {plan.night}: {', '.join(p.name for p in existing)}"])

    fd, tmp = tempfile.mkstemp(prefix=".", suffix=".tmp", dir=plans_dir)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(plan.to_json())
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp, final)
    except BaseException:
        if os.path.exists(tmp):
            os.unlink(tmp)
        raise
    for p in others:  # overwrite=True: drop the old reservation for that night
        p.unlink(missing_ok=True)
    return final


def load_plan(path: str | os.PathLike) -> Plan:
    with open(path, encoding="utf-8") as f:
        plan = Plan.from_dict(json.load(f))
    problems = validate(plan)
    if problems:
        raise PlanError(problems)
    return plan


def summary(plan: Plan) -> str:
    """Short human-readable summary for Discord confirmations."""
    lines = [f"**Night {plan.night}**" + (f" - {plan.client}" if plan.client else "")]
    for i, t in enumerate(plan.targets, 1):
        window = f"{t.start or 'dusk'} -> {t.end or 'done'}"
        exps = ", ".join(f"{e.filter} {e.exposure:g}s x{e.count}" for e in t.exposures)
        lines.append(f"{i}. {t.name}  rot {t.rotation:g} deg  [{window}]  {exps}")
    lines.append(f"Flats: {'%d per set' % plan.flats.count if plan.flats.enabled else 'off'}")
    return "\n".join(lines)


def slug(s: str | None) -> str:
    return re.sub(r"[^A-Za-z0-9]+", "", s or "")[:32]


if __name__ == "__main__":  # quick CLI: python skyshare_plan.py plan.json
    import sys
    for arg in sys.argv[1:]:
        try:
            print(summary(load_plan(arg)))
        except PlanError as e:
            print(f"{arg}: INVALID\n  - " + "\n  - ".join(e.problems))
            sys.exit(1)

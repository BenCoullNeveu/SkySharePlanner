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
    # False: each filter's full count in turn. True: repeat cycles of `count` frames
    # per filter (e.g. L 3, R 1, G 1, B 1), one dither per cycle, until a limit stops it.
    cycle: bool = False
    # Limits - whichever comes first stops the target (in either mode). `end` is one too.
    max_frames: int | None = None
    max_hours: float | None = None
    next_target_altitude: float | None = None   # stop once the NEXT target is above this


@dataclass
class Flats:
    enabled: bool = True
    count: int = 30


DARK_ORDERS = ("fixed_first", "repeat_first")
DARK_UNTIL_EVENTS = ("astronomical_dawn", "nautical_dawn", "civil_dawn", "sunrise")


@dataclass
class DarkSet:
    exposure: float
    count: int                 # total frames (fixed) or frames per round (repeat)
    gain: int = -1
    offset: int = -1
    binning: int = 1
    repeat: bool = False       # True: keep taking rounds of this set until `until`


@dataclass
class Darks:
    """Dark frames for the SkyShare Darks container. Fixed sets first by default,
    then rounds of the repeating sets until `until` (twilight event or HH:MM)."""
    sets: list[DarkSet] = field(default_factory=list)
    order: str = "fixed_first"
    until: str = "astronomical_dawn"
    temperature: float | None = None   # cool the camera to this first (deg C)


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
    camera_temperature: float | None = None   # deg C for Set Temperature; None = the instruction's value (0)
    flats: Flats = field(default_factory=Flats)
    darks: Darks = field(default_factory=Darks)
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
                cycle=bool(t.get("cycle", False)),
                max_frames=t.get("max_frames"), max_hours=t.get("max_hours"),
                next_target_altitude=t.get("next_target_altitude"),
                exposures=[Exposure(**e) for e in t["exposures"]],
            )
            for t in d["targets"]
        ]
        return Plan(
            schema_version=d.get("schema_version", SCHEMA_VERSION),
            night=d["night"], client=d.get("client"),
            camera_temperature=d.get("camera_temperature"),
            created_by=d.get("created_by"), created_utc=d.get("created_utc"),
            targets=targets, flats=Flats(**d.get("flats", {})),
            darks=_darks_from_dict(d.get("darks") or {}),
            processing=Processing(**d.get("processing", {})),
        )

    @property
    def is_calibration_only(self) -> bool:
        return not self.targets and bool(self.darks.sets)

    # -- file naming -------------------------------------------------------
    @property
    def filename(self) -> str:
        return f"{self.night}_{slug(self.client) or 'plan'}{PLAN_SUFFIX}"


def _darks_from_dict(d: dict) -> Darks:
    return Darks(
        sets=[DarkSet(**s) for s in d.get("sets", [])],
        order=d.get("order", "fixed_first"),
        until=d.get("until", "astronomical_dawn"),
        temperature=d.get("temperature"),
    )


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
    if plan.camera_temperature is not None and not (-60 <= plan.camera_temperature <= 40):
        p.append("camera_temperature must be -60 to 40 C")
    else:
        try:
            datetime.strptime(plan.night, "%Y-%m-%d")
        except ValueError:
            p.append(f"night '{plan.night}' is not a real date")
    if not plan.targets and not plan.darks.sets:
        p.append("plan has no targets or darks")
    p.extend(_validate_darks(plan.darks))
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
        if t.max_frames is not None and not (1 <= t.max_frames <= 10000):
            p.append(f"{tag}: max_frames must be 1-10000")
        if t.max_hours is not None and not (0 < t.max_hours <= 24):
            p.append(f"{tag}: max_hours must be 0-24")
        if t.next_target_altitude is not None:
            if not (0 <= t.next_target_altitude <= 90):
                p.append(f"{tag}: next_target_altitude must be 0-90")
            if i == len(plan.targets):
                p.append(f"{tag}: next_target_altitude is set but this is the last target")
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


def _validate_darks(d: Darks) -> list[str]:
    p: list[str] = []
    if d.order not in DARK_ORDERS:
        p.append(f"darks.order must be {' or '.join(DARK_ORDERS)}")
    if d.until not in DARK_UNTIL_EVENTS and not _HHMM_RE.match(d.until or ""):
        p.append(f"darks.until must be {', '.join(DARK_UNTIL_EVENTS)} or HH:MM")
    if d.temperature is not None and not (-60 <= d.temperature <= 40):
        p.append("darks.temperature must be -60 to 40 C")
    for i, s in enumerate(d.sets, 1):
        tag = f"dark set {i}"
        if not (0 < s.exposure <= 3600):
            p.append(f"{tag}: exposure must be 0-3600 s")
        if not (1 <= s.count <= 2000):
            p.append(f"{tag}: count must be 1-2000")
        if s.gain < -1 or s.offset < -1:
            p.append(f"{tag}: gain/offset must be >= -1")
        if not (1 <= s.binning <= 4):
            p.append(f"{tag}: binning must be 1-4")
    return p


def calibration_plan(night: str, darks: Darks | dict, *, client: str | None = "Calibration",
                     created_by: str = "dashboard") -> Plan:
    """A darks-only night (no targets, no flats, no processing job).

    `darks` may be a Darks or the plan-file dict shape, e.g.
        {"sets": [{"exposure": 300, "count": 20, "gain": 100},
                  {"exposure": 600, "count": 1, "gain": 100, "repeat": True}]}
    """
    return Plan(
        night=_normalise_date(night),
        client=client,
        targets=[],
        flats=Flats(enabled=False),
        darks=darks if isinstance(darks, Darks) else _darks_from_dict(darks),
        processing=Processing(enabled=False),
        created_by=created_by,
        created_utc=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    )


def describe_darks(d: Darks) -> str:
    """Same wording as the plugin's Load Plan summary."""
    if not d.sets:
        return "no darks"
    def one(s: DarkSet) -> str:
        txt = f"{s.exposure:g}s" + (f" g{s.gain}" if s.gain >= 0 else "") + (f" o{s.offset}" if s.offset >= 0 else "")
        txt += (f" bin{s.binning}" if s.binning > 1 else "") + f" x{s.count}" + (" per round" if s.repeat else "")
        return txt
    fixed = [one(s) for s in d.sets if not s.repeat]
    rounds = [one(s) for s in d.sets if s.repeat]
    parts = []
    if fixed:
        parts.append(", ".join(fixed))
    if rounds:
        parts.append(f"rounds of ({', '.join(rounds)}) until {d.until.replace('_', ' ')}")
    if d.order == "repeat_first":
        parts.reverse()
    cool = f" at {d.temperature:g} C" if d.temperature is not None else ""
    return f"darks{cool}: " + ", then ".join(parts)


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

    With a client, each target is named "<target> for <client>" ("M 31 for Jane Doe"); without
    one, just the target ("M 31"). That name becomes the NINA target name and therefore the
    image folders and SkyStack's LIGHT/<target>/ folder, so " for " makes them easy to sort.
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
        name = target_name(str(t["name"]), user)
        targets.append(Target(
            name=name, ra=str(t["ra"]), dec=str(t["dec"]),
            rotation=float(t.get("rot", t.get("rotation", 0)) or 0) % 360,
            start=t.get("start"), end=t.get("end"), exposures=exps,
            cycle=bool(t.get("cycle", False)),
            max_frames=t.get("max_frames"), max_hours=t.get("max_hours"),
            next_target_altitude=t.get("next_target_altitude"),
        ))

    return Plan(
        night=_normalise_date(seq.get("date")),
        client=seq.get("user"),
        camera_temperature=_float_or_none(seq.get("camera_temperature")),
        targets=targets,
        flats=Flats(count=flats_count),
        darks=_darks_from_dict(seq.get("darks") or {}),
        processing=processing or Processing(),
        created_by=created_by,
        created_utc=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    )


def _float_or_none(v) -> float | None:
    return None if v is None or v == "" else float(v)


def target_name(name: str, client: str | None) -> str:
    """'M 31' + 'Jane Doe' -> 'M 31 for Jane Doe'; no client -> 'M 31'.

    Idempotent, and also fixes the old style ('M 31 Jane Doe') so re-saved plans match.
    """
    name = " ".join(name.split())
    client = " ".join((client or "").split())
    if not client:
        return name
    for suffix in (f" for {client}", f" {client}"):
        if name.lower().endswith(suffix.lower()):
            name = name[: -len(suffix)].rstrip()
            break
    return f"{name} for {client}"


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
        if t.cycle:
            exps = f"cycles of ({exps}), dither each cycle"
        stops = [f"{t.max_frames} frames" if t.max_frames else None,
                 f"{t.max_hours:g} h" if t.max_hours else None,
                 f"next target above {t.next_target_altitude:g} deg" if t.next_target_altitude is not None else None]
        stops = [s for s in stops if s]
        limit = f"  stop at {' or '.join(stops)}" if stops else ""
        lines.append(f"{i}. {t.name}  rot {t.rotation:g} deg  [{window}]  {exps}{limit}")
    if plan.targets:
        lines.append(f"Flats: {'%d per set' % plan.flats.count if plan.flats.enabled else 'off'}")
    if plan.darks.sets:
        text = describe_darks(plan.darks)
        lines.append(text[0].upper() + text[1:])
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

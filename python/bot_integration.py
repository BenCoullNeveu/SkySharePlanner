"""
bot_integration.py - glue between skysharebot.py and the SkyShare Planner plugin.

Two pieces:

1. publish_from_bot(seq)   replaces the old `updateSequence(...)` call. Instead of
                           editing TemplateSequence.json it writes a plan file into
                           Dropbox; NINA builds the sequence itself.

2. StatusTailer            reads the "*.plan.status.jsonl" files the plugin appends
                           to during the night, so the bot can post progress
                           (plan loaded, targets built, flats done, errors).

Wire-up in skysharebot.py (both the wizard and the one-shot JSON path):

    from bot_integration import publish_from_bot, StatusTailer
    PLANS_DIR = config["discord"]["plans_directory"]      # e.g. ...\\Dropbox\\SkyShare Data\\Plans

    # was: await asyncio.to_thread(updateSequence, template_path, output_path, ...)
    path, text = await asyncio.to_thread(publish_from_bot, seq, PLANS_DIR)
    await channel.send(f"Plan saved for NINA:\\n{text}", file=discord.File(path))

    # in on_ready():
    tailer = StatusTailer(PLANS_DIR)
    @tasks.loop(seconds=30)
    async def post_plan_status():
        for ev in tailer.poll():
            await discord_send(nightplan_channel, f"`{ev['event']}` {ev['message']}")
    post_plan_status.start()
"""
from __future__ import annotations

import json
from pathlib import Path

from skyshare_plan import plan_from_wizard, write_plan, summary, rotation_warning

# Filters in the NINA profile. Checked again by the plugin on the acquisition PC;
# checking here lets the bot reject typos while the user is still chatting.
KNOWN_FILTERS = ["L", "R", "G", "B", "H", "O", "S"]
HAS_ROTATOR = False


def publish_from_bot(seq: dict, plans_dir: str, *, overwrite: bool = False) -> tuple[Path, str]:
    """Bot wizard dict -> validated plan file in Dropbox. Raises PlanError with
    `.problems` (list of str) that can be sent straight back to Discord."""
    plan = plan_from_wizard(seq)
    path = write_plan(plan, plans_dir, overwrite=overwrite, known_filters=KNOWN_FILTERS)
    text = summary(plan)
    if (w := rotation_warning(plan, HAS_ROTATOR)):
        text += f"\n⚠️ {w}"
    return path, text


class StatusTailer:
    """Returns only NEW status events since the last poll, across all plan files."""

    def __init__(self, plans_dir: str):
        self.dir = Path(plans_dir)
        self._offsets: dict[Path, int] = {}
        # start at the end of existing files so a bot restart doesn't replay the night
        for f in self.dir.glob("*.status.jsonl"):
            self._offsets[f] = f.stat().st_size

    def poll(self) -> list[dict]:
        events = []
        for f in sorted(self.dir.glob("*.status.jsonl")):
            start = self._offsets.get(f, 0)
            size = f.stat().st_size
            if size <= start:
                continue
            with f.open("rb") as fh:
                fh.seek(start)
                chunk = fh.read(size - start)
            # only consume complete lines (Dropbox may sync mid-write)
            complete, _, _ = chunk.rpartition(b"\n")
            if not complete:
                continue
            self._offsets[f] = start + len(complete) + 1
            for line in complete.decode("utf-8", "replace").splitlines():
                try:
                    ev = json.loads(line)
                    ev["plan_file"] = f.name.replace(".status.jsonl", ".json")
                    events.append(ev)
                except json.JSONDecodeError:
                    pass
        return events

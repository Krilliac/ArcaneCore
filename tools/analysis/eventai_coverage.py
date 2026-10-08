"""Count EventAI ids and complete creature scripts in a ClassicDB SQL dump.

The dump has column-less INSERTs, so this script reads the numeric field positions
from its CREATE TABLE declaration. It streams compressed input without expanding it
on disk. No source rows are written to the report.
"""

import argparse
import collections
import gzip
import json
import re
from pathlib import Path


ROW = re.compile(r"\((-?\d+(?:,-?\d+){23}),'(?:\\.|[^'\\])*'\)")
HANDLER = re.compile(r"(?:Event|Action)Type\s*=>\s*(?:\(byte\)EventAi(?:Event|Action)Type\.(\w+)|(\d+))")
ENUM_ITEM = re.compile(r"^\s*(\w+)\s*=\s*(\d+),", re.M)


def handled_ids(root: Path, kind: str) -> set[int]:
    source = (root / "src/ArcaneCore.Game/Creatures/AI/EventAi/EventAiTypes.cs").read_text(encoding="utf-8")
    enum_name = "EventAiEventType" if kind == "Events" else "EventAiActionType"
    body = source.split(f"public enum {enum_name}", 1)[1].split("}", 1)[0]
    enums = dict((name, int(value)) for name, value in ENUM_ITEM.findall(body))
    ids = set()
    for source in (root / "src/ArcaneCore.Game/Creatures/AI/EventAi" / kind).glob("*.cs"):
        for name, literal in HANDLER.findall(source.read_text(encoding="utf-8")):
            ids.add(int(literal) if literal else enums[name])
    return ids


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("dump", type=Path)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    events = handled_ids(args.root, "Events")
    actions = handled_ids(args.root, "Actions") | {0}  # ACTION_T_NONE is an empty slot.
    event_uses = collections.Counter()
    action_uses = collections.Counter()
    creature_rows = collections.defaultdict(list)
    total = 0
    unsupported_parameters = 0
    with gzip.open(args.dump, "rt", encoding="utf-8", errors="replace") as source:
        for line in source:
            if not line.startswith("INSERT INTO `creature_ai_scripts`"):
                continue
            for match in ROW.finditer(line):
                values = [int(value) for value in match.group(1).split(",")]
                creature, event = values[1], values[2]
                slots = [values[index] for index in (12, 16, 20)]
                used_actions = [value for value in slots if value]
                event_uses[event] += 1
                action_uses.update(slots)
                # SpawnedEvent.UnsupportedReason accepts only condition 0, 1 or 2.
                parameters_supported = event != 11 or values[6] in (0, 1, 2)
                unsupported_parameters += not parameters_supported
                creature_rows[creature].append(event in events and all(action in actions for action in used_actions)
                                             and parameters_supported)
                total += 1
    if not total:
        raise SystemExit("No creature_ai_scripts rows parsed")
    complete = sum(all(rows) for rows in creature_rows.values())
    reference_events = set(range(43))  # mangos-classic CreatureEventAI.h EventAI_Type, 0..42.
    reference_actions = set(range(66)) - {6, 7, 8, 49}  # UNUSED / REUSE markers.
    print(json.dumps({
        "rows": total,
        "creatures_with_scripts": len(creature_rows),
        "creatures_fully_supported": complete,
        "creatures_incomplete": len(creature_rows) - complete,
        "rows_with_unsupported_parameters": unsupported_parameters,
        "event_id_uses": {key: event_uses[key] for key in sorted(reference_events | event_uses.keys())},
        "action_id_uses": {key: action_uses[key] for key in sorted(set(range(66)) | action_uses.keys())},
        "used_unsupported_event_ids": {key: count for key, count in sorted(event_uses.items()) if key not in events},
        "used_unsupported_action_ids": {key: count for key, count in sorted(action_uses.items()) if key not in actions},
        "unused_unsupported_event_ids": sorted(reference_events - events - event_uses.keys()),
        "unused_unsupported_action_ids": sorted(reference_actions - actions - action_uses.keys()),
    }, indent=2))


if __name__ == "__main__":
    main()

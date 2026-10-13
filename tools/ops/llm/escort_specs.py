"""Job A - extract structured escort specs from mangos-classic ScriptDev2 npc_escortAI scripts.

  py -3 escort_specs.py prep                      # build units (no API calls)
  py -3 escort_specs.py smoke  --budget-usd 35    # one synchronous request to validate the schema
  py -3 escort_specs.py submit --budget-usd 35    # Message Batches submission (reserves worst case)
  py -3 escort_specs.py collect                   # poll, settle ledger, write out/escorts/*.json + index
"""
from __future__ import annotations

import argparse
import json
import pathlib
import re
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import opsenv  # noqa: E402
import llmlib  # noqa: E402
from sqlparse import iter_insert_rows  # noqa: E402

JOB = "A-escort-specs"
OUT = llmlib.ROOT / "out" / "escorts"
UNITS = OUT / "_units.json"
STATE = OUT / "_batch_state.json"
MODEL = llmlib.SONNET
MAX_TOKENS = 32000


def _sd2_root() -> pathlib.Path:
    explicit = opsenv.optional("ARCANECORE_MANGOS_CLASSIC_REF")
    return pathlib.Path(explicit) if explicit else (opsenv.refs_root() / "mangos-classic")


def _classicdb_dump() -> str:
    explicit = opsenv.optional("ARCANECORE_CLASSICDB_DUMP")
    return explicit if explicit else str(opsenv.refs_root() / "classic-db" / "Full_DB" / "ClassicDB_1_12_1_z2815.sql.gz")

ACTION_TYPES = ["say", "yell", "emote_text", "whisper", "play_emote", "summon", "ambush", "set_faction", "quest_complete",
                "quest_fail", "set_run", "set_walk", "pause_escort", "resume_escort", "cast_spell", "despawn",
                "set_npc_flags", "set_unit_flags", "set_stand_state", "face_target", "move_point", "attack",
                "set_equipment", "open_door_or_use_object", "give_item_or_credit", "timer_start", "other"]

POS = {"type": "object", "additionalProperties": False, "required": ["x", "y", "z", "o"],
       "properties": {k: {"type": "number"} for k in ("x", "y", "z", "o")}}
ACTION = {
    "type": "object", "additionalProperties": False,
    "required": ["type", "text_ids", "args", "positions", "detail", "code_lines"],
    "properties": {
        "type": {"type": "string"},
        "text_ids": {"type": "array", "items": {"type": "integer"}},
        "args": {"type": "array", "items": {"type": "object", "additionalProperties": False, "required": ["name", "value"],
                                            "properties": {"name": {"type": "string"}, "value": {"type": "number"}}}},
        "positions": {"type": "array", "items": {"$ref": "#/$defs/pos"}},
        "detail": {"type": "string"},
        "code_lines": {"type": "string"},
    },
}
ACTIONS = {"type": "array", "items": {"$ref": "#/$defs/action"}}
SCHEMA = {
    "type": "object", "additionalProperties": False,
    "$defs": {"pos": POS, "action": ACTION},
    "required": ["npc_entries", "npc_name", "script_name", "cpp_class", "quest_ids", "start", "waypoint_events",
                 "other_events", "completion", "not_expressible_as_data", "text_ids_used", "confidence", "notes"],
    "properties": {
        "npc_entries": {"type": "array", "items": {"type": "integer"}},
        "npc_name": {"type": "string"}, "script_name": {"type": "string"}, "cpp_class": {"type": "string"},
        "quest_ids": {"type": "array", "items": {"type": "integer"}},
        "start": {"type": "object", "additionalProperties": False,
                  "required": ["trigger", "conditions", "actions", "code_lines"],
                  "properties": {"trigger": {"type": "string"}, "conditions": {"type": "array", "items": {"type": "string"}},
                                 "actions": ACTIONS, "code_lines": {"type": "string"}}},
        "waypoint_events": {"type": "array", "items": {
            "type": "object", "additionalProperties": False, "required": ["point", "path_id", "actions"],
            "properties": {"point": {"type": "integer"}, "path_id": {"type": "integer"}, "actions": ACTIONS}}},
        "other_events": {"type": "array", "items": {
            "type": "object", "additionalProperties": False, "required": ["trigger", "actions"],
            "properties": {"trigger": {"type": "string"}, "actions": ACTIONS}}},
        "completion": {"type": "object", "additionalProperties": False, "required": ["point", "mechanism", "code_lines"],
                       "properties": {"point": {"type": "integer"}, "mechanism": {"type": "string"}, "code_lines": {"type": "string"}}},
        "not_expressible_as_data": {"type": "array", "items": {
            "type": "object", "additionalProperties": False, "required": ["description", "code_lines"],
            "properties": {"description": {"type": "string"}, "code_lines": {"type": "string"}}}},
        "text_ids_used": {"type": "array", "items": {"type": "integer"}},
        "confidence": {"type": "string", "enum": ["high", "medium", "low"]},
        "notes": {"type": "string"},
    },
}

SYSTEM = """You extract machine-readable specifications of ScriptDev2 (mangos-classic, WoW 1.12.1) escort scripts so they can be re-implemented as data in a C# server.

You receive one C++ source file (line-numbered), the name of ONE target class that derives from npc_escortAI, its script registration, the creature entries that use it, its script_waypoint rows from the world DB, and the script_texts rows referenced in the file.

Extract ONLY the target class and the free functions/hooks that belong to its script registration (GossipHello/GossipSelect, QuestAccept, GetAI, spell/effect hooks, dummy-effect handlers). Rules:
- Resolve every enum/constant to its numeric value (text ids are negative, e.g. -1000123; spells, creature entries, quests, factions, emotes are positive). Never invent ids; if a value is computed at runtime, put 0 and explain in detail.
- Waypoint events: one entry per waypoint point index handled in WaypointReached / WaypointStart (or equivalent), in ascending point order. If a case handles several points, emit one entry per point. path_id is the script_waypoint PathId the escort runs on (usually 0; use the value passed to Start()/SetCurrentWaypoints if any).
- Action "type" must be one of: {ACTION_TYPES}. Mapping: DoScriptText/DoBroadcastText with a SAY text -> say (YELL -> yell, EMOTE -> emote_text, WHISPER -> whisper); HandleEmote/SetEmoteState -> play_emote; SummonCreature -> summon (one action per summon call or loop); hostile summons that attack the escort -> ambush; SetFactionTemporary/setFaction -> set_faction; GroupEventHappens/AreaExploredOrEventHappens/RewardPlayerAndGroupAtEvent/KilledMonsterCredit -> quest_complete; FailQuest -> quest_fail; SetRun(true/false) -> set_run/set_walk; SetEscortPaused(true/false) -> pause_escort/resume_escort.
- Action "args": named numeric parameters, using these names where they apply: spell_id, emote_id, creature_entry, count, faction_id, quest_id, delay_ms (a wait/timer value preceding or scheduling the action), despawn_ms, npc_flags, unit_flags, stand_state, gameobject_entry, item_id, radius. "positions": every literal x,y,z,o used (o=0 if not given). "detail": short free text (who speaks/targets, the C++ call). Empty arrays/strings when not applicable.
- code_lines: the line range(s) in the provided file that justify the item, e.g. "412-418" or "412,430-431". Always fill it.
- other_events: everything not tied to a waypoint index: escort death (JustDied), player death / abandon / out-of-range fail (handled by npc_escortAI or overrides), aggro texts, combat abilities with timers, summoned-creature callbacks, timer-driven dialogue sequences (DialogueHelper, UpdateEscortAI timers). Trigger examples: "JustDied", "Aggro", "combat: every 8-12s", "UpdateEscortAI m_uiEventTimer phase 3 (after waypoint 12)".
- not_expressible_as_data: anything a simple per-waypoint event table cannot represent: conditional branches on world state, loops waiting for summoned mobs to die, dynamic target selection, custom movement, per-tick checks, references to other scripts/instance data, GameObject interaction. Be specific; this list decides what must be hand-written in C#.
- completion: the waypoint point where the quest is credited (0 if not on a waypoint) and the mechanism, including what happens to the NPC afterwards (despawn, reset, return).
- confidence: high only if every waypoint case and every id was resolved unambiguously.
- notes: brief caveats (e.g. waypoint rows missing for a point referenced by code, commented-out code, TODOs in source).
Return only the JSON object."""
SYSTEM = SYSTEM.replace("{ACTION_TYPES}", ", ".join(ACTION_TYPES))


def _registrations(text: str) -> list[dict]:
    regs = []
    for blk in re.split(r"pNewScript\s*=\s*new\s+Script\s*;", text)[1:]:
        name = re.search(r'pNewScript->Name\s*=\s*"([^"]+)"', blk)
        getai = re.search(r"pNewScript->GetAI\s*=\s*&\s*(?:GetNewAIInstance<\s*(\w+)\s*>|(\w+))", blk)
        hooks = re.findall(r"pNewScript->(\w+)\s*=\s*&\s*(\w+)", blk)
        if name:
            regs.append({"name": name.group(1), "getai_class": getai.group(1) if getai and getai.group(1) else None,
                         "getai_fn": getai.group(2) if getai and getai.group(2) else None, "hooks": hooks})
    return regs


def _getai_fn_class(text: str, fn: str) -> str | None:
    m = re.search(rf"\b{re.escape(fn)}\s*\([^)]*\)\s*\{{(.*?)\}}", text, re.S)
    if m:
        c = re.search(r"new\s+(\w+)\s*\(", m.group(1))
        if c:
            return c.group(1)
    return None


def _scriptname_entries(sd2: pathlib.Path) -> dict[str, list[int]]:
    m: dict[str, list[int]] = {}
    sd2_sql = sd2 / "sql/scriptdev2/scriptdev2.sql"
    for line in sd2_sql.read_text(encoding="utf-8", errors="replace").splitlines():
        mm = re.match(r"UPDATE creature_template SET ScriptName\s*=\s*'([^']+)' WHERE entry\s*(?:=\s*(\d+)|IN\s*\(([^)]*)\))", line, re.I)
        if mm:
            ids = [int(mm.group(2))] if mm.group(2) else [int(x) for x in re.findall(r"\d+", mm.group(3))]
            m.setdefault(mm.group(1), []).extend(ids)
    return m


def prep(_args):
    sd2 = _sd2_root()
    scripts = sd2 / "src/game/AI/ScriptDevAI/scripts"
    classicdb = _classicdb_dump()
    sn = _scriptname_entries(sd2)
    wps: dict[int, list] = {}
    for r in iter_insert_rows(classicdb, "script_waypoint"):
        wps.setdefault(int(r[0]), []).append(r)
    texts = {int(r[0]): r for r in iter_insert_rows(classicdb, "script_texts")}
    units = []
    for cpp in sorted(scripts.rglob("*.cpp")):
        if "examples" in cpp.parts:
            continue
        text = cpp.read_text(encoding="utf-8", errors="replace")
        classes = re.findall(r"struct\s+(\w+)\s*:\s*public\s+npc_escortAI\b", text)
        if not classes:
            continue
        regs = _registrations(text)
        for r in regs:
            if r["getai_fn"]:
                r["getai_class"] = _getai_fn_class(text, r["getai_fn"])
        for cls in classes:
            myregs = [r for r in regs if r["getai_class"] == cls]
            names = [r["name"] for r in myregs] or [re.sub(r"AI$", "", cls)]
            entries = sorted({e for n in names for e in sn.get(n, [])})
            rows = [r for e in entries for r in sorted(wps.get(e, []), key=lambda x: (x[1], x[2]))]
            tids = sorted({int(x) for x in re.findall(r"-1\d{6}\b", text)})
            trows = [texts[t] for t in tids if t in texts]
            units.append({"cpp": str(cpp).replace("\\", "/"), "cpp_rel": str(cpp.relative_to(sd2)).replace("\\", "/"),
                          "cls": cls, "script_names": names, "registrations": myregs, "entries": entries,
                          "waypoints": rows, "texts": [[t[0], t[11], t[12], t[13], t[1], t[15] if len(t) > 15 else ""]
                                                       for t in trows]})
    OUT.mkdir(parents=True, exist_ok=True)
    UNITS.write_text(json.dumps(units, indent=1), encoding="utf-8")
    print(f"{len(units)} escort classes; {sum(1 for u in units if u['waypoints'])} with script_waypoint rows; "
          f"{sum(1 for u in units if not u['entries'])} without a creature entry mapping")
    for u in units:
        print(f"  {u['cls']:42s} {','.join(u['script_names']):40s} entries={u['entries']} wps={len(u['waypoints'])}")


def _params(u: dict) -> dict:
    src = pathlib.Path(u["cpp"]).read_text(encoding="utf-8", errors="replace")
    file_block = f"FILE: {u['cpp_rel']}\n{llmlib.numbered(src)}"
    wp = "\n".join(f"({r[0]}, path {r[1]}, point {r[2]}, {r[3]:.2f}, {r[4]:.2f}, {r[5]:.2f}, o={r[6]}, wait={r[7]}, "
                   f"script={r[8]}, '{r[9] or ''}')" for r in u["waypoints"]) or "(no script_waypoint rows found)"
    tx = "\n".join(f"{t[0]} type={t[1]} lang={t[2]} emote={t[3]} '{t[4]}' -- {t[5]}" for t in u["texts"]) or "(none)"
    target = (f"TARGET CLASS: {u['cls']}\nScript registration(s): {json.dumps(u['registrations'])}\n"
              f"Creature entries with this ScriptName (ClassicDB/SD2 SQL): {u['entries'] or 'unknown - infer from source'}\n\n"
              f"script_waypoint rows (Entry, PathId, Point, X, Y, Z, Orientation, WaitTime ms, ScriptId, Comment):\n{wp}\n\n"
              f"script_texts referenced in this file (entry, type 0=say 1=yell 2=emote 3=boss_emote 4=whisper, language, emote, text, comment):\n{tx}\n\n"
              "Extract the spec for the TARGET CLASS.")
    return {
        "model": MODEL, "max_tokens": MAX_TOKENS,
        "system": [{"type": "text", "text": SYSTEM}],
        "messages": [{"role": "user", "content": [
            {"type": "text", "text": file_block, "cache_control": {"type": "ephemeral"}},
            {"type": "text", "text": target}]}],
        "output_config": {"effort": "high", "format": {"type": "json_schema", "schema": SCHEMA}},
    }


def _cid(u):
    return re.sub(r"[^A-Za-z0-9_-]", "_", u["cls"])[:64]


def smoke(args):
    units = json.loads(UNITS.read_text(encoding="utf-8"))
    u = next(x for x in units if x["cls"] == args.cls)
    msg = llmlib.call_sync(JOB, args.budget_usd, _params(u), f"smoke/{u['cls']}")
    print("stop_reason:", msg.stop_reason, "usage:", llmlib.usage_dict(msg.usage))
    spec = json.loads(llmlib.first_text(msg))
    _write(u, spec)
    print(json.dumps(spec, indent=1)[:3000])


def submit(args):
    if STATE.exists() and not json.loads(STATE.read_text())["collected"] and not args.force:
        sys.exit(f"batch already pending in {STATE}; run collect (or --force)")
    units = json.loads(UNITS.read_text(encoding="utf-8"))
    if args.only:
        units = [u for u in units if u["cls"] in args.only.split(",")]
    reqs = [(_cid(u), _params(u)) for u in units]
    llmlib.submit_batch(JOB, args.budget_usd, reqs, STATE, "batch")


def _write(u, spec, extra=None):
    key = (spec.get("npc_entries") or u["entries"] or [0])[0]
    if not key:
        key = u["cls"]
    spec["_source"] = {"file": u["cpp_rel"], "class": u["cls"], "script_names": u["script_names"],
                       "db_entries": u["entries"], "script_waypoint_rows": len(u["waypoints"]), "model": MODEL, **(extra or {})}
    p = OUT / f"{key}.json"
    p.write_text(json.dumps(spec, indent=1), encoding="utf-8")
    return p


def collect(_args):
    units = {_cid(u): u for u in json.loads(UNITS.read_text(encoding="utf-8"))}
    res = llmlib.collect_batch(STATE)
    index, failures = [], []
    for cid, r in sorted(res.items()):
        u = units[cid]
        if r["type"] != "succeeded" or r["stop_reason"] not in ("end_turn",):
            failures.append({"class": u["cls"], "result": r.get("type"), "stop_reason": r.get("stop_reason"), "error": r.get("error")})
            continue
        try:
            spec = json.loads(r["text"])
        except json.JSONDecodeError as e:
            failures.append({"class": u["cls"], "error": f"json: {e}"})
            continue
        p = _write(u, spec, {"usage": r["usage"]})
        index.append({"file": p.name, "entries": spec["npc_entries"], "npc_name": spec["npc_name"],
                      "script_name": spec["script_name"], "class": u["cls"], "quests": spec["quest_ids"],
                      "waypoint_events": len(spec["waypoint_events"]),
                      "non_data_items": len(spec["not_expressible_as_data"]), "confidence": spec["confidence"],
                      "source": u["cpp_rel"], "db_waypoint_rows": len(u["waypoints"]),
                      "already_ported": 12818 in spec["npc_entries"]})
    summary = {"job": JOB, "model": MODEL, "specs": len(index), "failures": failures,
               "confidence": {k: sum(1 for i in index if i["confidence"] == k) for k in ("high", "medium", "low")},
               "escorts": sorted(index, key=lambda i: i["source"])}
    (OUT / "_index.json").write_text(json.dumps(summary, indent=1), encoding="utf-8")
    print(f"wrote {len(index)} specs, {len(failures)} failures -> {OUT}")


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("prep")
    s = sub.add_parser("smoke"); s.add_argument("--budget-usd", type=float, required=True); s.add_argument("--cls", default="npc_ruul_snowhoofAI")
    s = sub.add_parser("submit"); s.add_argument("--budget-usd", type=float, required=True); s.add_argument("--only"); s.add_argument("--force", action="store_true")
    sub.add_parser("collect")
    a = ap.parse_args()
    {"prep": prep, "smoke": smoke, "submit": submit, "collect": collect}[a.cmd](a)


if __name__ == "__main__":
    main()

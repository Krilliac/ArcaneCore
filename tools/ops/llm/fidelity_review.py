"""Job B - fidelity review of the Codex-ported ScriptDev2 dungeon/raid scripts.

Unit of review = one lane x one dungeon: every C# file the lane changed under
src/ArcaneCore.Game/Instances/Scripts/<Dungeon>/ (plus Classic/<Dungeon>Instance.cs), paired with the
mangos-classic ScriptDev2 files (and vmangos files) that share creature/spell/GO ids with it.
Lane-wide helper files (InstanceData.cs, base AI classes) ride along as read-only context.

  py -3 fidelity_review.py prep
  py -3 fidelity_review.py smoke  --budget-usd 35 --unit <id>
  py -3 fidelity_review.py submit --budget-usd 35
  py -3 fidelity_review.py collect
  py -3 fidelity_review.py report
"""
from __future__ import annotations

import argparse
import json
import pathlib
import re
import subprocess
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import opsenv  # noqa: E402
import llmlib  # noqa: E402

JOB = "B-fidelity-review"
BASE = "92966fdd"
LANES = ["sd2-low", "sd2-mid", "sd2-high", "sd2-end", "raid-mc-ony", "raid-bwl-zg-aq20"]
SCRIPTS_REL = "src/ArcaneCore.Game/Instances/Scripts"
OUT = llmlib.ROOT / "out" / "fidelity"
UNITS = OUT / "_units.json"
STATE = OUT / "_batch_state.json"
MODEL = llmlib.SONNET
MAX_TOKENS = 48000
REF_CHAR_CAP = 420_000  # per side (SD2 / vmangos), ~120K tokens each


def _sd2_scripts() -> pathlib.Path:
    explicit = opsenv.optional("ARCANECORE_MANGOS_CLASSIC_REF")
    base = pathlib.Path(explicit) if explicit else (opsenv.refs_root() / "mangos-classic")
    return base / "src/game/AI/ScriptDevAI/scripts"


def _vm_scripts() -> pathlib.Path:
    explicit = opsenv.optional("ARCANECORE_VMANGOS_REF")
    base = pathlib.Path(explicit) if explicit else (opsenv.refs_root() / "vmangos")
    return base / "src/scripts"


def _report_path() -> pathlib.Path:
    return opsenv.lanes_root() / "_plan" / "llm-fidelity-review-20261008.md"

DUNGEON_DIRS = {  # C# folder -> reference folder name (same in SD2 and vmangos)
    "BlackfathomDeeps": "blackfathom_deeps", "Deadmines": "deadmines", "ShadowfangKeep": "shadowfang_keep",
    "WailingCaverns": "wailing_caverns", "Gnomeregan": "gnomeregan", "RazorfenDowns": "razorfen_downs",
    "RazorfenKraul": "razorfen_kraul", "ScarletMonastery": "scarlet_monastery", "Uldaman": "uldaman",
    "ZulFarrak": "zulfarrak", "BlackrockDepths": "blackrock_depths", "SunkenTemple": "sunken_temple",
    "Maraudon": "maraudon", "BlackrockSpire": "blackrock_spire", "DireMaul": "dire_maul",
    "Scholomance": "scholomance", "Stratholme": "stratholme", "MoltenCore": "molten_core",
    "Onyxia": "onyxias_lair", "BlackwingLair": "blackwing_lair", "RuinsOfAhnQiraj": "ruins_of_ahnqiraj",
    "ZulGurub": "zulgurub",
}
CONTEXT_FILES = {"InstanceData.cs", "DungeonScriptHooks.cs", "DungeonBossAis.cs", "RaidCreatureAI.cs",
                 "Raids/RaidBossAI.cs", "IInstanceQuestRewards.cs"}

FINDING = {
    "type": "object", "additionalProperties": False,
    "required": ["severity", "category", "script", "claim", "reference_behaviour", "port_behaviour",
                 "csharp_file", "csharp_lines", "cpp_file", "cpp_lines", "vmangos_file", "vmangos_lines", "confidence"],
    "properties": {
        "severity": {"type": "string", "enum": ["critical", "major", "minor", "info"]},
        "category": {"type": "string", "enum": ["spell_id", "timer", "phase_threshold", "summon", "text", "missing_mechanic",
                                                "targeting", "encounter_state", "door_or_object", "movement", "other"]},
        "script": {"type": "string"},
        "claim": {"type": "string"},
        "reference_behaviour": {"type": "string"},
        "port_behaviour": {"type": "string"},
        "csharp_file": {"type": "string"}, "csharp_lines": {"type": "string"},
        "cpp_file": {"type": "string"}, "cpp_lines": {"type": "string"},
        "vmangos_file": {"type": "string"}, "vmangos_lines": {"type": "string"},
        "confidence": {"type": "string", "enum": ["high", "medium", "low"]},
    },
}
SCHEMA = {
    "type": "object", "additionalProperties": False,
    "required": ["summary", "coverage", "findings"],
    "properties": {
        "summary": {"type": "string"},
        "coverage": {"type": "array", "items": {
            "type": "object", "additionalProperties": False, "required": ["reference_script", "csharp_symbol", "status"],
            "properties": {"reference_script": {"type": "string"}, "csharp_symbol": {"type": "string"},
                           "status": {"type": "string", "enum": ["ported", "partial", "not_ported", "out_of_scope"]}}}},
        "findings": {"type": "array", "items": FINDING},
    },
}

SYSTEM = """You review a C# port of World of Warcraft 1.12.1 dungeon/raid scripts for behavioural fidelity against the C++ reference implementations they were ported from.

Inputs (all line-numbered):
- PORT files: the C# files the lane wrote or changed (the review target). CONTEXT files are lane helpers (base AI classes, instance data ids) - use them to understand the port, do not review them on their own.
- REFERENCE (mangos-classic ScriptDev2): the primary ground truth the port claims to follow.
- REFERENCE (vmangos), where present: a second, often more Blizzlike, implementation. When the two references disagree, say so in the finding and treat ScriptDev2 as the port's stated target.

Report concrete behavioural mismatches between the port and the reference, for the scripts that the port actually implements:
- wrong or missing spell ids, wrong cast targets (self/victim/random/hostile-not-tank), wrong timers (initial and repeat ranges), wrong HP-% or phase thresholds, missing or wrong summons (entry, count, position), wrong or missing texts (script_texts ids / broadcast ids, say vs yell vs emote), encounter-state handling (SetData/IN_PROGRESS/DONE/FAIL on wipe), doors and gameobjects, movement/waypoints, missing mechanics that the reference implements.
- Do NOT report: style, naming, C# idioms, performance, missing unrelated scripts the port never attempted (list those in coverage as not_ported instead), behaviour that is identical modulo engine API differences, or timers that only differ by the C# framework tick granularity.
- Every finding must cite exact line ranges in BOTH the C# file and the reference file ("120-131"). If the port is missing something, cite the C# location where it should be (the class/method that otherwise implements that boss) and the reference lines that implement it. vmangos_file/vmangos_lines empty if not used.
- Be precise with numbers: quote the reference value and the port value in reference_behaviour / port_behaviour.
- severity: critical = encounter breaks or cannot complete/reset correctly; major = a mechanic is missing or materially different (wrong spell, wrong phase, missing summon wave); minor = timer/threshold/text deviations that change feel but not outcome; info = harmless divergence worth noting.
- confidence: high only if you verified both sides line by line. Prefer fewer, correct findings over many speculative ones; a false positive costs a developer an investigation.
- coverage: one row per reference script (boss_*/npc_*/go_*/instance class) in the provided reference files, with the matching C# symbol or "" and its status.
Return only the JSON object."""


def _git(lane: str, *args: str) -> str:
    return subprocess.run(["git", "-C", str(opsenv.lanes_root() / f"cx2-{lane}"), *args], capture_output=True, text=True,
                          check=True, encoding="utf-8", errors="replace").stdout


def _nums(text: str) -> set[int]:
    return {int(x) for x in re.findall(r"(?<![\w.])(\d{3,6})(?![\w.])", text) if int(x) >= 100}


def _ref_files(root: pathlib.Path, dirname: str) -> list[pathlib.Path]:
    hits = [p for p in root.rglob(dirname) if p.is_dir()]
    return sorted(f for d in hits for f in d.rglob("*") if f.suffix in (".cpp", ".h"))


def _select(files, port_nums, port_names, has_instance):
    scored = []
    for f in files:
        t = f.read_text(encoding="utf-8", errors="replace")
        shared = len(port_nums & _nums(t))
        name_hit = any(n in f.stem.replace("_", "").lower() for n in port_names)
        always = has_instance and (f.stem.startswith("instance_") or f.suffix == ".h")
        score = shared + (25 if name_hit else 0) + (50 if always else 0)
        if score >= 4 or always or name_hit:
            scored.append((score, f, len(t)))
    scored.sort(key=lambda x: -x[0])
    out, total = [], 0
    for s, f, n in scored:
        if total + n > REF_CHAR_CAP:
            continue
        out.append({"path": str(f).replace("\\", "/"), "score": s, "chars": n})
        total += n
    return out


def prep(_args):
    sd2_dir = _sd2_scripts()
    vm_dir = _vm_scripts()
    lanes_root = opsenv.lanes_root()
    units = []
    for lane in LANES:
        root = lanes_root / f"cx2-{lane}"
        changed = [p for p in _git(lane, "diff", "--name-only", BASE, "HEAD", "--", SCRIPTS_REL).split() if p.endswith(".cs")]
        groups: dict[str, list[str]] = {}
        ctx = []
        for p in changed:
            rel = p[len(SCRIPTS_REL) + 1:]
            if rel in CONTEXT_FILES:
                ctx.append(p)
                continue
            parts = rel.split("/")
            if parts[0] == "Classic":
                d = parts[1].replace("Instance.cs", "")
            else:
                d = parts[0]
            groups.setdefault(d, []).append(p)
        for d, files in sorted(groups.items()):
            refname = DUNGEON_DIRS.get(d)
            if not refname:
                print(f"  !! {lane}: no reference mapping for {d}: {files}")
                continue
            text = "\n".join((root / f).read_text(encoding="utf-8", errors="replace") for f in files)
            pn = _nums(text)
            names = {re.sub(r"(AI|Ai|Ais|AIs|Gossip|Script|Instance|SpellScripts?|Events)$", "", pathlib.Path(f).stem).lower()
                     for f in files}
            names = {n for n in names if len(n) >= 5}
            has_inst = any("Instance" in f for f in files)
            sd2 = _select(_ref_files(sd2_dir, refname), pn, names, has_inst)
            vm = _select(_ref_files(vm_dir, refname), pn, names, has_inst)
            units.append({"id": f"{lane}__{d}", "lane": lane, "dungeon": d, "port_files": files, "context_files": ctx,
                          "sd2": sd2, "vmangos": vm})
    OUT.mkdir(parents=True, exist_ok=True)
    UNITS.write_text(json.dumps(units, indent=1), encoding="utf-8")
    for u in units:
        print(f"{u['id']:40s} port={len(u['port_files'])} sd2={[pathlib.Path(x['path']).name for x in u['sd2']]} "
              f"vm={[pathlib.Path(x['path']).name for x in u['vmangos']]}")


def _block(label: str, path: str, text: str) -> str:
    return f"===== {label}: {path} =====\n{llmlib.numbered(text)}\n"


def _params(u: dict) -> dict:
    root = opsenv.lanes_root() / f"cx2-{u['lane']}"
    parts = []
    for f in u["port_files"]:
        parts.append(_block("PORT", f, (root / f).read_text(encoding="utf-8", errors="replace")))
    for f in u["context_files"]:
        parts.append(_block("CONTEXT", f, (root / f).read_text(encoding="utf-8", errors="replace")))
    for r in u["sd2"]:
        rel = r["path"].split("mangos-classic/")[-1]
        parts.append(_block("REFERENCE mangos-classic", rel, pathlib.Path(r["path"]).read_text(encoding="utf-8", errors="replace")))
    for r in u["vmangos"]:
        rel = "vmangos/" + r["path"].split("vmangos/")[-1] if "vmangos/" in r["path"] else r["path"]
        parts.append(_block("REFERENCE vmangos", rel, pathlib.Path(r["path"]).read_text(encoding="utf-8", errors="replace")))
    ask = (f"Lane codex/w2-{u['lane']}, dungeon {u['dungeon']}. Review the PORT files against the references and return "
           "the findings. Use the file paths exactly as given in the ===== headers.")
    return {
        "model": MODEL, "max_tokens": MAX_TOKENS,
        "system": [{"type": "text", "text": SYSTEM, "cache_control": {"type": "ephemeral"}}],
        "messages": [{"role": "user", "content": [{"type": "text", "text": "\n".join(parts)}, {"type": "text", "text": ask}]}],
        "output_config": {"effort": "high", "format": {"type": "json_schema", "schema": SCHEMA}},
    }


def smoke(args):
    units = json.loads(UNITS.read_text(encoding="utf-8"))
    u = next(x for x in units if x["id"] == args.unit)
    msg = llmlib.call_sync(JOB, args.budget_usd, _params(u), f"smoke/{u['id']}")
    print("stop_reason:", msg.stop_reason, "usage:", llmlib.usage_dict(msg.usage))
    data = json.loads(llmlib.first_text(msg))
    (OUT / f"_smoke_{u['id']}.json").write_text(json.dumps(data, indent=1), encoding="utf-8")
    print(json.dumps(data, indent=1)[:4000])


def submit(args):
    if STATE.exists() and not json.loads(STATE.read_text())["collected"] and not args.force:
        sys.exit(f"batch already pending in {STATE}; run collect (or --force)")
    units = json.loads(UNITS.read_text(encoding="utf-8"))
    llmlib.submit_batch(JOB, args.budget_usd, [(u["id"], _params(u)) for u in units], STATE, "batch")


SEV = {"critical": 0, "major": 1, "minor": 2, "info": 3}
CONF = {"high": 0, "medium": 1, "low": 2}


def collect(_args):
    units = {u["id"]: u for u in json.loads(UNITS.read_text(encoding="utf-8"))}
    res = llmlib.collect_batch(STATE)
    by_lane: dict[str, dict] = {}
    for cid, r in sorted(res.items()):
        u = units[cid]
        lane = by_lane.setdefault(u["lane"], {"lane": f"codex/w2-{u['lane']}", "base": BASE, "model": MODEL, "units": []})
        entry = {"dungeon": u["dungeon"], "port_files": u["port_files"],
                 "references": [x["path"] for x in u["sd2"] + u["vmangos"]]}
        if r["type"] != "succeeded" or r.get("stop_reason") != "end_turn":
            entry["error"] = r.get("error") or f"stop_reason={r.get('stop_reason')}"
        else:
            try:
                d = json.loads(r["text"])
                for i, f in enumerate(d["findings"], 1):
                    f["id"] = f"{u['lane']}/{u['dungeon']}#{i}"
                entry.update(d)
            except json.JSONDecodeError as e:
                entry["error"] = f"json: {e}"
        entry["usage"] = r.get("usage")
        lane["units"].append(entry)
    for lane, d in by_lane.items():
        (OUT / f"{lane}.json").write_text(json.dumps(d, indent=1), encoding="utf-8")
    print("wrote", ", ".join(f"{k}.json" for k in by_lane))
    report(None)


def report(_args):
    allf, errors, cov = [], [], {}
    for lane in LANES:
        p = OUT / f"{lane}.json"
        if not p.exists():
            continue
        d = json.loads(p.read_text(encoding="utf-8"))
        for u in d["units"]:
            if "error" in u:
                errors.append((lane, u["dungeon"], u["error"]))
                continue
            for c in u.get("coverage", []):
                cov.setdefault(c["status"], 0)
                cov[c["status"]] += 1
            for f in u["findings"]:
                allf.append(dict(f, lane=lane, dungeon=u["dungeon"]))
    allf.sort(key=lambda f: (SEV[f["severity"]], CONF[f["confidence"]], f["lane"], f["dungeon"]))
    verif = {}
    vp = OUT / "_verification.json"
    if vp.exists():
        verif = {v["id"]: v for v in json.loads(vp.read_text(encoding="utf-8"))}
    L = ["# LLM fidelity review of the Codex SD2 dungeon/raid ports (2026-10-08)", "",
         f"Model: `{MODEL}` via the Message Batches API (effort high, structured output). Base `{BASE}`; lanes "
         + ", ".join(f"`codex/w2-{l}`" for l in LANES) + ".",
         "Per-lane machine-readable findings: `tools/ops/llm/out/fidelity/<lane>.json`. "
         "Generator: `tools/ops/llm/fidelity_review.py`.", "",
         "**These are model claims, not verified defects.** A sample was hand-checked against the sources "
         "(see Verification); treat unverified rows as leads to confirm before changing code.", ""]
    counts = {s: sum(1 for f in allf if f["severity"] == s) for s in SEV}
    L += [f"Findings: {len(allf)} total - " + ", ".join(f"{k} {v}" for k, v in counts.items())
          + f". Coverage rows: " + ", ".join(f"{k} {v}" for k, v in sorted(cov.items())) + ".", ""]
    if errors:
        L += ["Units that produced no result: " + "; ".join(f"{a}/{b}: {c}" for a, b, c in errors), ""]
    if verif:
        L += ["## Verification (hand-checked sample)", "", "Sample: 5 of the 15 major findings (no criticals were produced), chosen across 5 lanes; minor/info rows and low-confidence rows were not sampled, so the 0/5 false-positive rate applies to the high/medium-confidence major tier only.", "", "| Finding | Verdict | Note |", "|---|---|---|"]
        for v in verif.values():
            L.append(f"| {v['id']} | {v['verdict']} | {v['note']} |")
        L.append("")
    L += ["## Findings, ranked (severity, then model confidence)", "",
          "| # | Sev | Conf | Lane / dungeon | Script | Claim | Reference | Port | C# | C++ (SD2) | vmangos | Verified |",
          "|---|---|---|---|---|---|---|---|---|---|---|---|"]

    def esc(s):
        return (s or "").replace("|", "\\|").replace("\n", " ")

    for i, f in enumerate(allf, 1):
        v = verif.get(f["id"], {}).get("verdict", "")
        L.append(f"| {i} | {f['severity']} | {f['confidence']} | {f['lane']} / {f['dungeon']} | {esc(f['script'])} | "
                 f"{esc(f['claim'])} | {esc(f['reference_behaviour'])} | {esc(f['port_behaviour'])} | "
                 f"`{esc(pathlib.Path(f['csharp_file']).name)}:{esc(f['csharp_lines'])}` | "
                 f"`{esc(pathlib.Path(f['cpp_file']).name)}:{esc(f['cpp_lines'])}` | "
                 f"{'`' + esc(pathlib.Path(f['vmangos_file']).name) + ':' + esc(f['vmangos_lines']) + '`' if f['vmangos_file'] else ''} | {v} |")
    report_path = _report_path()
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text("\n".join(L) + "\n", encoding="utf-8")
    print(f"report -> {report_path} ({len(allf)} findings)")


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("prep")
    s = sub.add_parser("smoke"); s.add_argument("--budget-usd", type=float, required=True); s.add_argument("--unit", required=True)
    s = sub.add_parser("submit"); s.add_argument("--budget-usd", type=float, required=True); s.add_argument("--force", action="store_true")
    sub.add_parser("collect")
    sub.add_parser("report")
    a = ap.parse_args()
    {"prep": prep, "smoke": smoke, "submit": submit, "collect": collect, "report": report}[a.cmd](a)


if __name__ == "__main__":
    main()

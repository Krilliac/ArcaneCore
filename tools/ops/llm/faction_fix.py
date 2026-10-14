"""Deterministic post-pass for Job A specs: re-resolve faction ids from the cited C++ lines.

The model only sees the escort's own .cpp, so FACTION_ESCORT_* constants (ScriptDevAIMgr.h) were often
guessed. For every set_faction action, read the cited lines, find the FACTION_* token, look it up in
ScriptDevAIMgr.h or the file's own enum, and correct the value. Reports raw accuracy before fixing.

Usage:
  py -3 tools/ops/llm/faction_fix.py [--sd2 DIR] [--specs-glob GLOB]
"""
from __future__ import annotations

import argparse
import glob
import json
import pathlib
import re
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import opsenv  # noqa: E402


def lines(src: list[str], spec: str) -> str:
    out = []
    for part in re.findall(r"\d+(?:-\d+)?", spec or ""):
        a, _, b = part.partition("-")
        ai = int(a)
        bi = int(b or a)
        out += src[ai - 1 : bi]
    return "\n".join(out)


def main() -> int:
    ap = argparse.ArgumentParser(description="Re-resolve faction IDs in Job A escort specs from cited C++ lines.")
    ap.add_argument("--sd2", default=opsenv.optional("ARCANECORE_MANGOS_CLASSIC_REF"), help="mangos-classic checkout (default: <$ARCANECORE_REFS_ROOT>/mangos-classic)")
    ap.add_argument("--specs-glob", default=str(pathlib.Path(__file__).resolve().parent / "out" / "escorts" / "[0-9]*.json"), help="glob of escort spec JSON files")
    args = ap.parse_args()

    sd2 = pathlib.Path(args.sd2) if args.sd2 else (opsenv.refs_root() / "mangos-classic")
    consts = {
        m[0]: int(m[1])
        for m in re.findall(
            r"(FACTION_\w+)\s*=\s*(\d+)",
            (sd2 / "src/game/AI/ScriptDevAI/ScriptDevAIMgr.h").read_text(encoding="utf-8"),
        )
    }
    stats = {"checked": 0, "correct": 0, "wrong": 0, "unresolved_zero": 0, "no_constant_in_lines": 0}
    changes = []
    for f in sorted(glob.glob(args.specs_glob)):
        with open(f, encoding="utf-8") as fh:
            s = json.load(fh)
        srcp = sd2 / s["_source"]["file"]
        text = srcp.read_text(encoding="utf-8", errors="replace")
        local = {m[0]: int(m[1]) for m in re.findall(r"(FACTION_\w+)\s*=\s*(\d+)", text)}
        src = text.splitlines()
        acts = (
            list(s["start"]["actions"])
            + [a for w in s["waypoint_events"] for a in w["actions"]]
            + [a for o in s["other_events"] for a in o["actions"]]
        )
        for a in acts:
            for arg in a["args"]:
                if arg["name"] != "faction_id":
                    continue
                seg = lines(src, a["code_lines"])
                toks = [t for t in re.findall(r"\bFACTION_\w+", seg) if t in local or t in consts]
                lit = re.findall(r"(?:setFaction|SetFactionTemporary)\s*\(\s*(\d+)", seg)
                truth = (local.get(toks[0]) or consts.get(toks[0])) if toks else (int(lit[0]) if lit else None)
                stats["checked"] += 1
                if truth is None:
                    stats["no_constant_in_lines"] += 1
                    continue
                if int(arg["value"]) == truth:
                    stats["correct"] += 1
                    continue
                stats["unresolved_zero" if arg["value"] == 0 else "wrong"] += 1
                changes.append((pathlib.Path(f).name, s["cpp_class"], arg["value"], truth))
                arg["value"] = truth
                a["detail"] = (a["detail"] + " [faction re-resolved from source by faction_fix.py]").strip()
        s.setdefault("_source", {})["faction_fix"] = True
        with open(f, "w", encoding="utf-8") as fh:
            json.dump(s, fh, indent=1)
    print(json.dumps(stats))
    for c in changes:
        print("  fixed", *c)
    return 0


if __name__ == "__main__":
    sys.exit(main())

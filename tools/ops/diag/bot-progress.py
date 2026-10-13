"""Snapshot how far ArcaneCore's managed playerbots have progressed (read-only).

Every run appends one JSON line to <out>/snapshots.jsonl and rewrites <out>/summary.md:
- population-owned bots: count, running (managed_playerbot.State), level histogram, max level, levels gained since each bot's
  first snapshot, top zones;
- all managed bots: count running;
- the World process's working set and the system's free memory.
The databases are opened read-only (URI mode=ro) and always closed. Nothing is written to the server profile.

Usage: py -3 tools/ops/diag/bot-progress.py [--profile <dir>] [--out <dir>] [--every SECONDS] [--count N]
"""
from __future__ import annotations

import argparse
import collections
from contextlib import closing
import datetime
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import opsenv  # noqa: E402


def main() -> int:
    ap = argparse.ArgumentParser(description="Read-only snapshot of managed playerbot progression.")
    ap.add_argument("--profile", default=opsenv.optional(opsenv.LIVE_PROFILE), help=f"live profile directory (default: ${opsenv.LIVE_PROFILE})")
    ap.add_argument("--out", default=None, help=f"output directory (default: <${opsenv.LANES_ROOT}>/_diag/bot-progress)")
    ap.add_argument("--every", type=int, default=600, help="seconds between snapshots when --count > 1")
    ap.add_argument("--count", type=int, default=1, help="number of snapshots to take")
    args = ap.parse_args()

    profile = Path(args.profile) if args.profile else opsenv.live_profile()
    out = Path(args.out) if args.out else (opsenv.lanes_root() / "_diag" / "bot-progress")
    out.mkdir(parents=True, exist_ok=True)
    snapshots = out / "snapshots.jsonl"
    first_levels_path = out / "first-levels.json"
    first_levels = json.loads(first_levels_path.read_text(encoding="utf-8")) if first_levels_path.exists() else {}

    def ro(name: str):
        return closing(sqlite3.connect(f"file:{os.path.join(profile, name)}?mode=ro", uri=True, timeout=10))

    def memory() -> dict:
        ps = ("$o=Get-CimInstance Win32_OperatingSystem; $w=(Get-Process -Name ArcaneCore.World,dotnet -ErrorAction SilentlyContinue | "
              "Sort-Object WS -Descending | Select-Object -First 1).WS; "
              "'{0}|{1}|{2}' -f $o.FreePhysicalMemory,$o.FreeVirtualMemory,$w")
        try:
            r = subprocess.run(["powershell.exe", "-NoProfile", "-Command", ps], capture_output=True, text=True, timeout=60)
            free_kb, commit_kb, ws = r.stdout.strip().split("|")
            return {"free_phys_gb": round(int(free_kb) / 1048576, 1), "free_commit_gb": round(int(commit_kb) / 1048576, 1),
                    "world_ws_gb": round(int(ws or 0) / 1073741824, 2)}
        except Exception as e:
            return {"error": str(e)[:120]}

    def snapshot() -> dict:
        now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%SZ")
        with ro("characters.db") as db:
            pop = db.execute(
                "select c.Id, c.Name, c.Level, c.ZoneId, m.State, p.Band from playerbot_population_member p "
                "join characters c on c.Id = p.CharacterId left join managed_playerbot m on m.BotId = p.BotId").fetchall()
            managed_running = db.execute("select count(*) from managed_playerbot where State = 'Running' or State = 2").fetchone()[0]
            managed_total = db.execute("select count(*) from managed_playerbot").fetchone()[0]
        levels: collections.Counter[int] = collections.Counter()
        zones: collections.Counter[int] = collections.Counter()
        gained = []
        running = 0
        for cid, _name, level, zone, state, _band in pop:
            levels[(level - 1) // 5 * 5 + 1] += 1
            zones[zone] += 1
            if str(state) in ("Running", "2"):
                running += 1
            key = str(cid)
            if key not in first_levels:
                first_levels[key] = level
            gained.append(level - first_levels[key])
        first_levels_path.write_text(json.dumps(first_levels), encoding="utf-8")
        snap = {
            "time": now, "population_bots": len(pop), "population_running": running,
            "managed_total": managed_total, "managed_running": managed_running,
            "max_level": max((r[2] for r in pop), default=0),
            "levels_by_5": {f"{k}-{k + 4}": v for k, v in sorted(levels.items())},
            "levels_gained": {"total": sum(gained), "bots_gained": sum(1 for g in gained if g > 0),
                              "max": max(gained, default=0)},
            "top_zones": zones.most_common(15), "memory": memory(),
        }
        with snapshots.open("a", encoding="utf-8") as f:
            f.write(json.dumps(snap) + "\n")
        lines = [json.loads(l) for l in snapshots.read_text(encoding="utf-8").splitlines() if l.strip()]
        md = ["# Playerbot population progress", "",
              "| Time (UTC) | Pop bots | Pop running | All running | Max level | Levels gained (sum / bots / max) | World WS GB | Free phys GB | Free commit GB |",
              "|---|---|---|---|---|---|---|---|---|"]
        for s in lines:
            m, g = s.get("memory", {}), s["levels_gained"]
            md.append(f"| {s['time']} | {s['population_bots']} | {s['population_running']} | {s['managed_running']} | {s['max_level']} | "
                      f"{g['total']} / {g['bots_gained']} / {g['max']} | {m.get('world_ws_gb', '?')} | {m.get('free_phys_gb', '?')} | {m.get('free_commit_gb', '?')} |")
        last = lines[-1]
        md += ["", f"## Latest level spread ({last['time']})", "", "| Levels | Bots |", "|---|---|"]
        md += [f"| {k} | {v} |" for k, v in last["levels_by_5"].items()]
        md += ["", "## Top zones (zone id: bots)", "", ", ".join(f"{z}: {n}" for z, n in last["top_zones"])]
        (out / "summary.md").write_text("\n".join(md) + "\n", encoding="utf-8")
        return snap

    for i in range(args.count):
        s = snapshot()
        print(json.dumps({k: s[k] for k in ("time", "population_bots", "population_running", "managed_running", "max_level", "levels_gained", "memory")}), flush=True)
        if i + 1 < args.count:
            time.sleep(args.every)
    return 0


if __name__ == "__main__":
    sys.exit(main())

"""Summarise open [bot-qa] GitHub issues by kind, goal and creation date (read-only).

Usage:
  py -3 tools/ops/rsi/botqa-stats.py [--input FILE] [--repo DIR]
"""
from __future__ import annotations

import argparse
import collections
import json
import pathlib
import re
import subprocess
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import opsenv  # noqa: E402


def main() -> int:
    ap = argparse.ArgumentParser(description="Summarise open [bot-qa] GitHub issues by kind, goal and creation date.")
    ap.add_argument("--input", default=None, help="JSON file from `gh issue list --json number,title,createdAt` (default: query GitHub via gh)")
    ap.add_argument("--repo", default=str(opsenv.repo_root()), help="checkout used by gh to resolve the repository")
    args = ap.parse_args()

    if args.input:
        issues = json.loads(pathlib.Path(args.input).read_text(encoding="utf-8"))
    else:
        raw = subprocess.run(
            ["gh", "issue", "list", "--label", "bot-qa", "--state", "open", "--limit", "2000", "--json", "number,title,createdAt"],
            cwd=args.repo,
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=True,
        ).stdout
        issues = json.loads(raw)

    print("open", len(issues))
    kind: collections.Counter[str] = collections.Counter()
    goal: collections.Counter[str] = collections.Counter()
    day: collections.Counter[str] = collections.Counter()
    for i in issues:
        t = i["title"]
        day[i["createdAt"][:10]] += 1
        m = re.match(r"\[bot-qa\] (\w[\w -]*?):", t)
        kind[m.group(1) if m else t[:40]] += 1
        g = re.search(r"goal (\w+)", t)
        goal[g.group(1) if g else "-"] += 1
    print("kinds", kind.most_common(15))
    print("goals", goal.most_common(15))
    print("days", sorted(day.items())[-10:])
    return 0


if __name__ == "__main__":
    sys.exit(main())

"""RSI digest: one read-only snapshot of how the live server's bot-QA findings move across builds.

Reads the live profile's qa/report.json (and, read-only, the running bot count from characters.db when present) plus the
bot-qa GitHub issues, and writes <out>/digest-<utc>.{json,md} plus <out>/history.jsonl (one line per run) so successive
digests can be compared.

Verdicts per published issue:
  quiet      - its finding has not been seen on the current build (not SeenOnCurrentBuild; fallback: Stale is true)
  recurring  - its finding was seen on the current build (SeenOnCurrentBuild; fallback: Stale is false)
  orphan     - the issue is open but no live finding carries its number (finding pruned, or filed by an older store)
A quiet issue becomes a close candidate only after --quiet-hours of uptime on the current build. Findings whose issue is
KnownClosed (or closed on GitHub) that recur on the current build are listed under Regressions.

Usage: py -3 tools/ops/rsi/rsi_digest.py [--profile DIR] [--out DIR] [--quiet-hours 24] [--no-github]
"""
from __future__ import annotations

import argparse
import collections
from contextlib import closing
import datetime as dt
import json
import pathlib
import re
import sqlite3
import subprocess
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import opsenv  # noqa: E402

PUB = re.compile(r"^(Published|KnownClosed)#(\d+)$")


def parse_ts(s: str) -> dt.datetime:
    # .NET writes 7 fractional digits; Python accepts at most 6.
    m = re.match(r"(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(\.\d+)?", s)
    if not m:
        raise ValueError(f"cannot parse timestamp: {s!r}")
    frac = (m.group(2) or ".0")[:7]
    return dt.datetime.fromisoformat(m.group(1) + frac).replace(tzinfo=dt.timezone.utc)


def seen_on_current_build(f: dict) -> bool:
    if "SeenOnCurrentBuild" in f:
        return bool(f["SeenOnCurrentBuild"])
    return not bool(f.get("Stale"))


def occurrences_on_current_build(f: dict) -> int:
    if "OccurrencesOnCurrentBuild" in f:
        return int(f["OccurrencesOnCurrentBuild"] or 0)
    return int(f.get("Count", 0) or 0) if not f.get("Stale") else 0


def read_bot_count(profile: pathlib.Path) -> int | None:
    """Read the running managed-bot count from the profile's characters.db (mode=ro), or None if unavailable."""
    cfg_path = profile / "appsettings.json"
    db_path = profile / "characters.db"
    if cfg_path.is_file():
        try:
            cfg = json.loads(cfg_path.read_text(encoding="utf-8-sig"))
            cs = cfg.get("Database", {}).get("Characters", "")
            if isinstance(cs, dict):
                cs = cs.get("ConnectionString", "")
            m = re.search(r"(?i)Data Source=([^;]+)", str(cs))
            if m:
                candidate = pathlib.Path(m.group(1).strip('"'))
                db_path = candidate if candidate.is_absolute() else (profile / candidate)
        except Exception:
            pass
    if not db_path.is_file():
        return None
    try:
        with closing(sqlite3.connect(f"file:{db_path.as_posix()}?mode=ro", uri=True, timeout=5)) as db:
            row = db.execute("SELECT COUNT(*) FROM managed_playerbot WHERE State = 'Running' OR State = 2").fetchone()
            return int(row[0]) if row else None
    except Exception:
        return None


def gh_issues(repo_dir: pathlib.Path, state: str) -> list[dict]:
    raw = subprocess.run(
        ["gh", "issue", "list", "--label", "bot-qa", "--state", state, "--limit", "2000", "--json", "number,title,createdAt,state"],
        cwd=repo_dir,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=True,
    ).stdout
    return json.loads(raw)


def main() -> int:
    ap = argparse.ArgumentParser(description="Read-only RSI digest of live bot-QA findings across builds.")
    ap.add_argument("--profile", default=opsenv.optional(opsenv.LIVE_PROFILE), help=f"live profile directory (default: ${opsenv.LIVE_PROFILE})")
    ap.add_argument("--out", default=None, help=f"output directory (default: <${opsenv.LANES_ROOT}>/_wf/rsi)")
    ap.add_argument("--deploy-dir", default=None, help=f"deploy directory for fallback build-start lookup (default: ${opsenv.DEPLOY_ROOT})")
    ap.add_argument("--repo", default=str(opsenv.repo_root()), help="checkout used by gh to resolve the repository")
    ap.add_argument("--quiet-hours", type=float, default=24.0, help="hours of uptime on the current build before a quiet issue is a close candidate")
    ap.add_argument("--build-start", default=None, help="UTC ISO time the current build started (default: report BuildSinceUtc, else newest live-*/report.json)")
    ap.add_argument("--bots", type=int, default=None, help="running bot count to record (default: read from characters.db when present)")
    ap.add_argument("--no-github", action="store_true", help="do not query GitHub issues via gh")
    args = ap.parse_args()

    profile = pathlib.Path(args.profile) if args.profile else opsenv.live_profile()
    out_dir = pathlib.Path(args.out) if args.out else (opsenv.lanes_root() / "_wf" / "rsi")
    repo_dir = pathlib.Path(args.repo)

    report = json.loads((profile / "qa" / "report.json").read_text(encoding="utf-8"))
    findings = report["Findings"]
    now = parse_ts(report["GeneratedUtc"])
    build = report.get("Build", "?")

    start = parse_ts(args.build_start) if args.build_start else None
    if start is None and report.get("BuildSinceUtc"):
        start = parse_ts(report["BuildSinceUtc"])
    if start is None:
        deploy_dir = pathlib.Path(args.deploy_dir) if args.deploy_dir else (
            pathlib.Path(opsenv.optional(opsenv.DEPLOY_ROOT)) if opsenv.optional(opsenv.DEPLOY_ROOT)
            else ( pathlib.Path(opsenv.optional(opsenv.LANES_ROOT)) / "_deploy" if opsenv.optional(opsenv.LANES_ROOT) else None )
        )
        if deploy_dir and deploy_dir.is_dir():
            reports = sorted(deploy_dir.glob("live-*/report.json"), key=lambda p: p.stat().st_mtime)
            if reports:
                finished = json.loads(reports[-1].read_text(encoding="utf-8")).get("finished")
                if finished:
                    start = parse_ts(finished)
    uptime_h = (now - start).total_seconds() / 3600 if start else None
    bot_count = args.bots if args.bots is not None else read_bot_count(profile)

    by_kind: dict[str, collections.Counter] = collections.defaultdict(collections.Counter)
    by_issue: dict[int, dict] = {}
    regressions: list[dict] = []

    for f in findings:
        k = by_kind[f["Kind"]]
        k["findings"] += 1
        seen = seen_on_current_build(f)
        k["active" if seen else "quiet"] += 1
        k["occurrences_on_this_build"] += occurrences_on_current_build(f)
        pub = f.get("Publication", "")
        m = PUB.match(pub)
        if m:
            state, num = m.group(1), int(m.group(2))
            by_issue[num] = f
            if state == "KnownClosed" and seen:
                regressions.append({
                    "issue": num,
                    "finding": f["Id"],
                    "kind": f["Kind"],
                    "subject": f.get("Subject", ""),
                    "occurrences_on_this_build": occurrences_on_current_build(f),
                    "count": f.get("Count", 0),
                    "bots": f.get("DistinctBots", 0),
                    "closed_build": f.get("ClosedBuild"),
                    "last_build": f.get("LastBuild"),
                    "last_seen": f.get("LastSeenUtc"),
                })
        elif pub.startswith("Failed"):
            k["publish_failed"] += 1

    issues = [] if args.no_github else gh_issues(repo_dir, "open")
    closed_by_num: dict[int, dict] = {}
    if not args.no_github:
        for ci in gh_issues(repo_dir, "closed"):
            closed_by_num[ci["number"]] = ci

    seen_regression_issues = {r["issue"] for r in regressions}
    for num, ci in closed_by_num.items():
        if num in seen_regression_issues:
            continue
        f = by_issue.get(num)
        if f is not None and seen_on_current_build(f):
            regressions.append({
                "issue": num,
                "finding": f["Id"],
                "kind": f["Kind"],
                "subject": f.get("Subject", ci.get("title", "")),
                "occurrences_on_this_build": occurrences_on_current_build(f),
                "count": f.get("Count", 0),
                "bots": f.get("DistinctBots", 0),
                "closed_build": f.get("ClosedBuild"),
                "last_build": f.get("LastBuild"),
                "last_seen": f.get("LastSeenUtc"),
            })

    rows = []
    for i in issues:
        f = by_issue.get(i["number"])
        if f is None:
            verdict = "orphan"
        elif not seen_on_current_build(f):
            verdict = "quiet"
        else:
            verdict = "recurring"
        close_candidate = verdict == "quiet" and uptime_h is not None and uptime_h >= args.quiet_hours
        rows.append({
            "issue": i["number"],
            "title": i["title"],
            "verdict": verdict,
            "close_candidate": close_candidate,
            "finding": f and f["Id"],
            "occurrences_on_this_build": f and occurrences_on_current_build(f),
            "count": f and f.get("Count"),
            "bots": f and f.get("DistinctBots"),
            "first_build": f and f.get("FirstBuild"),
            "last_build": f and f.get("LastBuild"),
            "reported_build": f and f.get("ReportedBuild"),
            "last_seen": f and f.get("LastSeenUtc"),
        })

    verdicts = collections.Counter(r["verdict"] for r in rows)
    summary = {
        "generated": now.isoformat(),
        "build": build,
        "build_start": start and start.isoformat(),
        "uptime_hours": uptime_h and round(uptime_h, 2),
        "bots_running": bot_count,
        "findings": len(findings),
        "kinds": {k: dict(v) for k, v in sorted(by_kind.items())},
        "open_issues": len(issues),
        "verdicts": dict(verdicts),
        "close_candidates": sum(r["close_candidate"] for r in rows),
        "regressions": len(regressions),
        # Not a fix signal on its own: a finding can only recur where bots still go.
        "active_findings": sum(v["active"] for v in by_kind.values()),
    }

    out_dir.mkdir(parents=True, exist_ok=True)
    stamp = now.strftime("%Y%m%dT%H%MZ")
    (out_dir / f"digest-{stamp}.json").write_text(
        json.dumps({"summary": summary, "issues": rows, "regressions": regressions}, indent=1),
        encoding="utf-8",
    )
    with (out_dir / "history.jsonl").open("a", encoding="utf-8") as h:
        h.write(json.dumps(summary) + "\n")

    bots_note = f", bots running: {bot_count}" if bot_count is not None else ", bots running: unknown"
    md = [
        f"# RSI digest {stamp}",
        "",
        f"Build `{build}`, up {summary['uptime_hours']} h (since {summary['build_start']}){bots_note}.",
        "",
        "| kind | findings | active on this build | quiet | occurrences on this build | publish failed |",
        "|---|---|---|---|---|---|",
    ]
    for k, v in sorted(by_kind.items(), key=lambda kv: -kv[1]["active"]):
        md.append(
            f"| {k} | {v['findings']} | {v['active']} | {v['quiet']} | {v['occurrences_on_this_build']} | {v['publish_failed']} |"
        )
    md += [
        "",
        f"Open bot-qa issues: {len(issues)} - " + ", ".join(f"{k} {n}" for k, n in verdicts.most_common()),
        f"Close candidates (quiet for >= {args.quiet_hours} h on this build): {summary['close_candidates']}",
        f"Regressions (KnownClosed or closed issues seen on this build): {len(regressions)}",
        "",
        "## Regressions",
        "",
    ]
    if regressions:
        for reg in sorted(regressions, key=lambda r: -(r["occurrences_on_this_build"] or 0)):
            closed_b = reg.get("closed_build") or "unknown"
            md.append(
                f"- #{reg['issue']} ({reg['kind']}: {reg['subject']}) - {reg['occurrences_on_this_build']} on this build "
                f"({reg['count']} lifetime), {reg['bots']} bots, closed on `{closed_b}`, last {reg['last_seen']}"
            )
    else:
        md.append("None.")
    md += ["", "## Recurring on this build", ""]
    for r in sorted((r for r in rows if r["verdict"] == "recurring"), key=lambda r: -(r["occurrences_on_this_build"] or r["count"] or 0)):
        md.append(
            f"- #{r['issue']} {r['title']} - {r['occurrences_on_this_build']} on this build ({r['count']} lifetime), "
            f"{r['bots']} bots, last {r['last_seen']}"
        )
    (out_dir / f"digest-{stamp}.md").write_text("\n".join(md) + "\n", encoding="utf-8")
    print("\n".join(md[: 14 + len(by_kind)]))
    print(f"wrote {out_dir / ('digest-' + stamp + '.md')}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

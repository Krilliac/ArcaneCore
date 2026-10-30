"""Finish a deploy-live.py run whose servers started but whose wait gave up before the post-start checks ran.

Reads the run's report.json and the live databases (mode=ro only), runs the same checks deploy-live.py runs after the world starts,
and, only if they all pass, marks the report healthy, records the new PIDs in the profile's standard-processes.json and opens the
status console (supervising, as deploy-live.py does, and only when none runs for the port yet). It never starts, stops or signals a process.

Usage: py -3 tools/ops/deploy/deploy-post-checks.py --label live-w37-r1 [--deploy-dir DIR] [--status-console PS1] [--no-transports] [--no-optional-data] [--no-elevators]
"""
from __future__ import annotations

import argparse
from contextlib import closing
import datetime
import json
from pathlib import Path
import os
import sqlite3
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import deploy_log_lines  # noqa: E402
import opsenv  # noqa: E402


def require(condition: object, message: object) -> None:
    """A safety gate: stop before anything is marked healthy when the condition is false. Not `assert`, which `python -O` compiles out."""
    if not condition:
        sys.exit(f"deploy-post-checks: {message}")


def status_console_launch(console: str, profile: Path, world_port: int, auth_port: int) -> tuple[list[str], dict[str, str]]:
    """The command line and environment for the status console, the same as deploy-live.py opens: -Supervise and ARCANECORE_LIVE_SERVER=1.

    Without -Supervise the console is read-only, and server-status.ps1 closes every older console for the same World port, so a read-only
    one would silently replace a supervising console and the live servers would lose crash and hang recovery.
    """
    env = dict(os.environ)
    env["ARCANECORE_LIVE_SERVER"] = "1"
    argv = ["pwsh", "-NoProfile", "-NoExit", "-File", console, "-Profile", str(profile),
            "-WorldPort", str(world_port), "-AuthPort", str(auth_port), "-Supervise"]
    return argv, env


def console_running_script(world_port: int) -> str:
    """PowerShell that prints how many pwsh/powershell processes run server-status.ps1 for the World port (the port defaults to 8085 there)."""
    port = int(world_port)
    return ("(Get-CimInstance Win32_Process -Filter \"Name='pwsh.exe' OR Name='powershell.exe'\" | Where-Object { "
            + r"$_.CommandLine -match 'server-status\.ps1' -and "
            + r"$(if ($_.CommandLine -match '-WorldPort\s+(\d+)') { [int]$Matches[1] } else { 8085 }) -eq "
            + str(port) + " } | Measure-Object).Count")


def port_of(config: dict, section: str) -> int:
    """The section's Port from appsettings.json as an integer; it is formatted into PowerShell code, so nothing else may get through."""
    raw = config.get(section, {}).get("Port")
    try:
        port = int(raw)
    except (TypeError, ValueError):
        sys.exit(f"deploy-post-checks: {section}:Port in appsettings.json is not a port number: {raw!r}")
    require(0 < port < 65536, f"{section}:Port in appsettings.json is out of range: {port}")
    return port


def main() -> int:
    ap = argparse.ArgumentParser(description="Run post-start deploy checks read-only against a started deploy run.")
    ap.add_argument("--label", required=True, help="deploy run label under the deploy directory")
    ap.add_argument("--deploy-dir", default=None, help=f"deploy root directory (default: ${opsenv.DEPLOY_ROOT})")
    ap.add_argument("--status-console", default=None, help="status script to open in its own window after checks pass")
    ap.add_argument("--no-transports", action="store_true")
    ap.add_argument("--no-optional-data", action="store_true")
    ap.add_argument("--no-elevators", action="store_true")
    args = ap.parse_args()

    deploy_dir = Path(args.deploy_dir) if args.deploy_dir else opsenv.deploy_root()
    out = deploy_dir / args.label
    report_path = out / "report.json"
    report = json.loads(report_path.read_text(encoding="utf-8"))
    require(not report.get("healthy"), "this run is already marked healthy")
    profile = Path(report["profile"])
    config = json.loads((profile / "appsettings.json").read_text(encoding="utf-8-sig"))
    world_port, auth_port = port_of(config, "World"), port_of(config, "Auth")
    paths = {}
    for role in ("Characters",):
        cs = config["Database"][role]["ConnectionString"]
        parts = dict((p.split("=", 1)[0].strip().lower(), p.split("=", 1)[1].strip()) for p in cs.split(";") if "=" in p)
        paths[role] = Path(parts["data source"])

    def ps(code: str):
        return subprocess.run(["powershell", "-NoProfile", "-Command", code], capture_output=True, text=True, timeout=60, check=False)

    def listener_pid(port: int) -> int | None:
        r = ps(f"(Get-NetTCPConnection -LocalPort {int(port)} -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess")
        return int(r.stdout.strip()) if r.stdout.strip() else None

    def ro(role: str):
        return closing(sqlite3.connect("file:" + paths[role].as_posix() + "?mode=ro", uri=True))

    def query(sql: str) -> list:
        with ro("Characters") as db:
            return [list(r) for r in db.execute(sql)]

    world_pid, realm_pid = report["new_world"], report["new_realm"]
    require(listener_pid(world_port) == world_pid, f"the new world (PID {world_pid}) is not the world listener on port {world_port}")
    require(listener_pid(auth_port) == realm_pid, f"the new realm (PID {realm_pid}) is not the auth listener on port {auth_port}")

    log = (out / "logs" / "world.log") if (out / "logs" / "world.log").is_file() else (out / "world.log")
    text = log.read_text(encoding="utf-8", errors="replace")
    enabled = sum(1 for b in report["bots_stopped"] if b[4])
    max_bots = int(config.get("World", {}).get("Playerbots", {}).get("MaxBots", 8))
    started = text.count("Started managed playerbot")
    require(started >= min(enabled, max_bots), f"{started} bots started; {min(enabled, max_bots)} expected (enabled {enabled}, MaxBots {max_bots})")

    now_chars = {tuple(c) for c in query("SELECT Id,AccountId,Name,Race,Class,Gender FROM characters ORDER BY Id")}
    lost_chars = [c for c in report["characters_stopped"] if tuple(c) not in now_chars]
    require(not lost_chars, f"characters lost or changed: {lost_chars[:5]}")
    has_pop = query("SELECT 1 FROM sqlite_master WHERE type='table' AND name='playerbot_population_member'")
    pop = set(report["population_stopped"]) | ({r[0] for r in query("SELECT BotId FROM playerbot_population_member")} if has_pop else set())
    now_bots = {tuple(b[:4]): b[4] for b in query(
        "SELECT b.BotId,b.AccountId,b.CharacterId,c.Name,b.DesiredEnabled FROM managed_playerbot b JOIN characters c ON c.Id=b.CharacterId")}
    lost_bots = [b for b in report["bots_stopped"] if tuple(b[:4]) not in now_bots]
    require(not lost_bots, f"managed bots lost or changed: {lost_bots[:5]}")
    flipped = [b for b in report["bots_stopped"] if b[0] not in pop and now_bots[tuple(b[:4])] != b[4]]
    require(not flipped, f"DesiredEnabled changed for non-population bots: {flipped[:5]}")

    checks = deploy_log_lines.check_log(text, args.no_transports, args.no_optional_data, args.no_elevators)
    missing = [k for k, ok in checks.items() if not ok]
    require(not missing, f"the world log lacks expected lines {missing}; the run is not marked healthy and standard-processes.json is untouched")
    report["world_log_checks"] = checks
    report["world_log_counts"] = {k: text.count(k) for k in ("unknown target map", "not existing zone id", " WARN ", " ERROR ", " FATAL ")}
    report.update(enabled_bots=enabled, max_bots=max_bots, started_bots=started, post_checks="deploy-post-checks.py",
                  healthy=True, finished=datetime.datetime.now(datetime.timezone.utc).isoformat())
    report_path.write_text(json.dumps(report, indent=2, default=str), encoding="utf-8")

    meta_path = profile / "standard-processes.json"
    if meta_path.is_file():
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        (out / "standard-processes.before.json").write_text(json.dumps(meta, indent=2), encoding="utf-8")
        binr = Path(report["artifacts"]) / "bin"
        meta["services"]["World"] = dict(pid=world_pid, dll=str(binr / "ArcaneCore.World/release/ArcaneCore.World.dll"))
        meta["services"]["Realm"] = dict(pid=realm_pid, dll=str(binr / "ArcaneCore.Realm/release/ArcaneCore.Realm.dll"))
        meta["qualification"] = args.label
        meta_path.write_text(json.dumps(meta, indent=2), encoding="utf-8")
    if args.status_console:
        # Never replace a console that is already running for this World port: it may be the supervisor (server-status.ps1 closes older ones).
        running = ps(console_running_script(world_port)).stdout.strip()
        if running and running != "0":
            print(f"deploy-post-checks: a status console is already running for World port {world_port}; left in place", file=sys.stderr)
        else:
            argv, status_env = status_console_launch(args.status_console, profile, world_port, auth_port)
            subprocess.Popen(argv, close_fds=True, env=status_env, creationflags=subprocess.CREATE_NEW_CONSOLE)
    print(json.dumps(dict(healthy=True, world=world_pid, realm=realm_pid, started_bots=started, enabled_bots=enabled, max_bots=max_bots,
                          missing_log_lines=missing, log_counts=report["world_log_counts"])))
    return 0


if __name__ == "__main__":
    sys.exit(main())

"""Deploy an integrated build to an ArcaneCore profile: graceful World shutdown, Realm stop, arcane-db upgrade and check,
the content refresh of the world database (every map and area, the ships' templates, the flight tables) together with the
optional client data keys (item sets, random suffixes, enchantments, pages, the character appearance check), the ship switch,
then Realm and World from the new artifacts, and a check that the characters and the managed bots came back and the new content loaded.

Running this script against the live profile restarts the live servers: only the operator decides that.

Usage (Python 3.12, `py -3`):
  py -3 tools/ops/deploy/deploy-live.py --profile <dir with appsettings.json> --label <new output dir under deploy root> --artifacts <dir> [options]

Options:
  --profile <dir>          the server profile directory (default: $ARCANECORE_LIVE_PROFILE)
  --label <name>           the deploy run label (output directory name under the deploy root)
  --out <dir>              where the logs, snapshots and backups go (default: <$ARCANECORE_DEPLOY_ROOT>/<label>)
  --artifacts <dir>        the deploy candidate directory produced by build-integrated.sh (required)
  --world-port N           the World's listen port in this profile (default 8085)
  --auth-port N            the Realm's listen port in this profile (default 3724)
  --terrain <dir>          World:Maps:DataDirectory to set (default <$ARCANECORE_DATA_ROOT>/terrain-5875; "" leaves it)
  --dbc-dir <dir>          the build-5875 DBCs with a SHA256SUMS listing them (default <$ARCANECORE_DATA_ROOT>/client-dbc-5875,
                           the full 154-file set): the refresh needs AreaTrigger, WorldSafeLocs, Map, AreaTable, TaxiNodes,
                           TaxiPath and TaxiPathNode; the elevators TransportAnimation; the optional data ItemSet,
                           ItemRandomProperties, SpellItemEnchantment, CharSections and CharacterFacialHairStyles.
                           The world reads the last six at every start: give a permanent directory.
  --dump <file>            the classic-db dump (default <$ARCANECORE_REFS_ROOT>/classic-db/Full_DB/ClassicDB_1_12_1_z2815.sql.gz)
  --no-optional-data       do not set the seven optional client data keys (refresh-world-content.ps1 without -AppSettings)
  --no-transports          leave World:Transports:Enabled as it is (by default it is set true: 9 ships)
  --no-elevators           do not set GameObjects:TransportAnimationDbcPath
  --mangos-defense-wear    set Items:DurabilityLossChanceParry/Block/Absorb to 0.05/0.05/0.5 (the pre-wave-3 wear)
  --old-artifacts <dir>    the build the running servers use (default: read from the running World's command line); its
                           arcane-account promotes the operator for step 1. The candidate's must not: opening the auth database
                           upgrades its schema under the old, still running Realm and before step 3's backup.
  --operator NAME          the account used for '.server shutdown' (default CLAUDEOP1, character Claudeop); it is promoted to
                           administrator with a fresh random password for the shutdown and demoted to player afterwards

Everything that can be checked before the World is stopped is checked first (artifacts, every DBC against SHA256SUMS, the dump, the
terrain, the optional-data keys through set-optional-data.ps1 -DryRun); a failure there leaves the running servers alone.
"""
from __future__ import annotations

from contextlib import closing
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import sqlite3
import subprocess
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import deploy_log_lines  # noqa: E402
import opsenv  # noqa: E402


def require(condition: object, message: object) -> None:
    """A safety gate: stop the deploy with the message when the condition is false. Not `assert`, which `python -O` compiles out."""
    if not condition:
        sys.exit(f"deploy-live: {message}")


def ps(code: str, env: dict | None = None):
    """Runs Windows PowerShell code. Values that are not this script's own integers reach it through env, never through the code text."""
    return subprocess.run(["powershell", "-NoProfile", "-Command", code], capture_output=True, text=True, timeout=60, env=env, check=False)


def world_db_free(path) -> bool:
    """True when the file opens with no sharing, i.e. nobody else holds it. The path is passed as an environment variable: interpolated
    into the code, an apostrophe in it ended the quoted string (a free file read as held) or ran as PowerShell."""
    env = dict(os.environ, ARCANE_DEPLOY_EXCLUSIVE_PATH=str(path))
    code = ("try { $f=[System.IO.File]::Open($env:ARCANE_DEPLOY_EXCLUSIVE_PATH,'Open','ReadWrite','None'); $f.Dispose(); exit 0 } "
            "catch { exit 1 }")
    return ps(code, env=env).returncode == 0


def main() -> int:
    ap = argparse.ArgumentParser(description="Graceful deploy of an integrated ArcaneCore build to a server profile.")
    ap.add_argument("--profile", default=opsenv.optional(opsenv.LIVE_PROFILE), help=f"server profile directory (default: ${opsenv.LIVE_PROFILE})")
    ap.add_argument("--label", required=True, help="deploy run label (subdirectory under the deploy root)")
    ap.add_argument("--out", default=None, help=f"output directory (default: <${opsenv.DEPLOY_ROOT}>/<label>)")
    ap.add_argument("--artifacts", required=True, help="deploy candidate directory produced by build-integrated.sh")
    ap.add_argument("--world-port", type=int, default=8085)
    ap.add_argument("--auth-port", type=int, default=3724)
    ap.add_argument("--terrain", default=None, help=f"World:Maps:DataDirectory to set (default: <${opsenv.DATA_ROOT}>/terrain-5875; '' leaves it)")
    ap.add_argument("--dbc-dir", default=None, help=f"build-5875 DBC directory with SHA256SUMS (default: <${opsenv.DATA_ROOT}>/client-dbc-5875)")
    ap.add_argument("--dump", default=None, help=f"classic-db SQL dump (default: <${opsenv.REFS_ROOT}>/classic-db/Full_DB/ClassicDB_1_12_1_z2815.sql.gz)")
    ap.add_argument("--no-optional-data", action="store_true")
    ap.add_argument("--no-transports", action="store_true")
    ap.add_argument("--no-elevators", action="store_true")
    ap.add_argument("--mangos-defense-wear", action="store_true")
    ap.add_argument("--old-artifacts", default=None)
    ap.add_argument("--set", action="append", metavar="SECTION:KEY=VALUE",
                    help="extra appsettings value written in step 5 (repeatable), e.g. World:Playerbots:MaxBots=410")
    ap.add_argument("--operator", default="CLAUDEOP1")
    ap.add_argument("--shutdown-timeout-s", type=int, default=300,
                    help="how long to wait for the World to save and exit after the shutdown command")
    ap.add_argument("--servers-stopped", action="store_true",
                    help="the live Realm/World are already stopped: check both ports are free and skip the graceful shutdown (steps 1-2)")
    ap.add_argument("--visible-consoles", action="store_true",
                    help="start Realm and World each in its own visible console window with stdin attached")
    ap.add_argument("--status-console", default=None, metavar="PS1",
                    help="after a healthy start, open this status script (e.g. tools/ops/diag/server-status.ps1) in its own window")
    ap.add_argument("--operator-character", default="Claudeop")
    args = ap.parse_args()

    profile = Path(args.profile) if args.profile else opsenv.live_profile()
    out = Path(args.out) if args.out else (opsenv.deploy_root() / args.label)
    art = Path(args.artifacts)
    terrain_arg = str(opsenv.data_root() / "terrain-5875") if args.terrain is None else args.terrain
    dbc_dir = Path(args.dbc_dir) if args.dbc_dir else (opsenv.data_root() / "client-dbc-5875")
    dump_path = Path(args.dump) if args.dump else (opsenv.refs_root() / "classic-db" / "Full_DB" / "ClassicDB_1_12_1_z2815.sql.gz")

    for item in args.set or []:
        require(item.partition("=")[0] and "=" in item, f"--set expects Section:Key=value, got {item!r}")

    binr = art / "bin"
    world_dll = binr / "ArcaneCore.World/release/ArcaneCore.World.dll"
    realm_dll = binr / "ArcaneCore.Realm/release/ArcaneCore.Realm.dll"
    dbtool = binr / "ArcaneCore.DbUpgrade/release/arcane-db.exe"
    account_tool = binr / "ArcaneCore.AccountTool/release/arcane-account.exe"
    client_tool = binr / "ArcaneCore.MockClient/release/arcane-mock.exe"
    importer = binr / "ArcaneCore.ContentImporter/release/arcane-content-importer.dll"
    refresh_script = art / "tools/content/refresh-world-content.ps1"
    optional_script = art / "tools/content/set-optional-data.ps1"

    # --- checks that need no server stopped ---------------------------------------------------------------------------------------
    for p in (world_dll, realm_dll, dbtool, account_tool, client_tool, importer, refresh_script, optional_script):
        require(p.is_file(), f"{p} is missing; nothing was stopped")
    if terrain_arg:
        require(all((Path(terrain_arg) / d).is_dir() for d in ("maps", "vmaps", "mmaps")),
                f"{terrain_arg} lacks maps, vmaps or mmaps; nothing was stopped")
    require(dump_path.is_file(), f"{dump_path} is missing; nothing was stopped")

    REFRESH_DBCS = ["AreaTrigger.dbc", "WorldSafeLocs.dbc", "Map.dbc", "AreaTable.dbc", "WMOAreaTable.dbc", "TaxiNodes.dbc", "TaxiPath.dbc", "TaxiPathNode.dbc"]
    OPTIONAL_DBCS = ["ItemSet.dbc", "ItemRandomProperties.dbc", "SpellItemEnchantment.dbc", "CharSections.dbc", "CharacterFacialHairStyles.dbc"]
    needed = REFRESH_DBCS + ([] if args.no_elevators else ["TransportAnimation.dbc"]) + ([] if args.no_optional_data else OPTIONAL_DBCS)
    sums_path = dbc_dir / "SHA256SUMS"
    require(sums_path.is_file(), f"{sums_path} is missing: the DBC directory needs a SHA256SUMS (sha256sum format) listing every DBC; nothing was stopped")
    sums = {}
    for line in sums_path.read_text(encoding="utf-8").splitlines():
        m = re.match(r"^([0-9A-Fa-f]{64}) [ *]?(.+)$", line)
        if m:
            sums[m.group(2).strip()] = m.group(1).lower()
    missing = [n for n in needed if not (dbc_dir / n).is_file() or n not in sums]
    if missing:
        sys.exit(f"{dbc_dir} lacks (or its SHA256SUMS does not list): {', '.join(missing)}; nothing was stopped.")
    for n in needed:
        h = hashlib.sha256((dbc_dir / n).read_bytes()).hexdigest()
        require(h == sums[n], f"{n} does not match {sums_path} ({h}); nothing was stopped")

    out.mkdir(exist_ok=False)

    def listener_pid(port: int) -> int | None:
        r = ps(f"(Get-NetTCPConnection -LocalPort {int(port)} -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess")
        return int(r.stdout.strip()) if r.stdout.strip() else None

    def gone(pid: int) -> bool:
        return ps(f"if(Get-Process -Id {int(pid)} -ErrorAction SilentlyContinue){{exit 1}}").returncode == 0

    settings_path = profile / "appsettings.json"

    def load_config() -> dict:
        return json.loads(settings_path.read_text(encoding="utf-8-sig"))

    config = load_config()
    require(config["World"]["Port"] == args.world_port and config["Auth"]["Port"] == args.auth_port, "ports do not match the profile")
    paths = {}
    for role in ("Auth", "Characters", "World"):
        cs = config["Database"][role]["ConnectionString"]
        parts = dict((p.split("=", 1)[0].strip().lower(), p.split("=", 1)[1].strip()) for p in cs.split(";") if "=" in p)
        paths[role] = Path(parts["data source"])
        require(paths[role].is_file(), f"the {role} database {paths[role]} is missing")

    def ro(role: str):
        # closing(): sqlite3's own context manager only commits, it never closes; a leaked mode=ro handle on world.db made the content
        # refresh's exclusive-open check fail (live-w19-r1/r2).
        return closing(sqlite3.connect("file:" + paths[role].as_posix() + "?mode=ro", uri=True))

    def bots() -> list:
        with ro("Characters") as db:
            return [list(r) for r in db.execute(
                "SELECT b.BotId,b.AccountId,b.CharacterId,c.Name,b.DesiredEnabled FROM managed_playerbot b "
                "JOIN characters c ON c.Id=b.CharacterId ORDER BY b.CharacterId")]

    def population_members() -> list:
        """The bot ids the population manager owns (it may create, start and park these); empty before the population schema."""
        with ro("Characters") as db:
            if not db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='playerbot_population_member'").fetchone():
                return []
            return [r[0] for r in db.execute("SELECT BotId FROM playerbot_population_member")]

    def characters() -> list:
        """Every character's identity (a running bot may gain a level or move between two reads; those columns are not compared)."""
        with ro("Characters") as db:
            return [list(r) for r in db.execute("SELECT Id,AccountId,Name,Race,Class,Gender FROM characters ORDER BY Id")]

    def snapshot(suffix: str, roles=("Auth", "Characters", "World")) -> None:
        """A database a server holds open (it has a -wal or -shm beside it) is read through the backup API from a mode=ro connection; one
        nobody holds (no -wal, no -shm) is copied as a file, so no -wal/-shm is created next to it."""
        for role in roles:
            target = out / f"{suffix}-{role.lower()}.db"
            if Path(str(paths[role]) + "-wal").exists() or Path(str(paths[role]) + "-shm").exists():
                with ro(role) as s, closing(sqlite3.connect(target)) as b:
                    s.backup(b)
            else:
                shutil.copyfile(paths[role], target)
            with closing(sqlite3.connect(target)) as b:
                result = b.execute("PRAGMA integrity_check").fetchone()[0]
            require(result == "ok", f"the {suffix} snapshot of the {role} database fails PRAGMA integrity_check ({result}): {target}")

    def run(argv, name: str, env=None, check: bool = True, timeout: int = 1800) -> int:
        with (out / f"{name}.log").open("x", encoding="utf-8") as log:
            r = subprocess.run([str(a) for a in argv], cwd=profile, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
        if check and r.returncode != 0:
            raise RuntimeError(f"{name} exited {r.returncode}; see {name}.log")
        return r.returncode

    report: dict = dict(started=datetime.datetime.now(datetime.timezone.utc).isoformat(), artifacts=str(art), profile=str(profile),
                        dbc_dir=str(dbc_dir), dbcs_checked=needed)

    def save() -> None:
        (out / "report.json").write_text(json.dumps(report, indent=2, default=str), encoding="utf-8")

    # The optional data keys, checked (files, field counts, SHA256SUMS, the dump's two tables) without writing anything.
    if not args.no_optional_data:
        run(["powershell", "-NoProfile", "-File", optional_script, "-AppSettings", settings_path, "-DbcDirectory", dbc_dir, "-Dump", dump_path,
             "-DryRun"], "00-optional-data-dryrun")

    old_world, old_realm = listener_pid(args.world_port), listener_pid(args.auth_port)
    if args.servers_stopped:
        require(not old_world and not old_realm, f"--servers-stopped, but something listens on {args.world_port}/{args.auth_port} ({old_world}/{old_realm})")
    else:
        require(old_world and old_realm, "World and Realm must both be listening before the deploy (or pass --servers-stopped)")
    report.update(old_world=old_world, old_realm=old_realm, bots_before=bots(), characters_before=characters())
    # The operator for step 1 is managed with the RUNNING build's arcane-account (wave 8): the candidate's would upgrade the auth schema
    # under the old Realm, outside step 3's backed-up upgrade.
    if args.servers_stopped:
        old_art = None
    elif args.old_artifacts:
        old_art = Path(args.old_artifacts)
    else:
        cmd = ps(f'(Get-CimInstance Win32_Process -Filter "ProcessId={int(old_world)}").CommandLine').stdout.strip()
        m = re.search(r'([A-Za-z]:\\[^"]*?)\\bin\\ArcaneCore\.World\\release\\ArcaneCore\.World\.dll', cmd, re.IGNORECASE)
        require(m, f"cannot tell the running build from the World command line ({cmd!r}); pass --old-artifacts. Nothing was stopped.")
        old_art = Path(m.group(1))
    if old_art is not None:
        old_account_tool = old_art / "bin/ArcaneCore.AccountTool/release/arcane-account.exe"
        require(old_account_tool.is_file(), f"{old_account_tool} is missing; pass --old-artifacts. Nothing was stopped.")
        report["old_artifacts"] = str(old_art)
    snapshot("before", roles=("Auth", "Characters"))
    save()

    deploy_lock = profile / "deploying.lock"
    deploy_lock.write_text(f"{datetime.datetime.now(datetime.timezone.utc).isoformat()} {args.label}\n", encoding="utf-8")
    try:
        if not args.servers_stopped:
            # 1. Graceful World shutdown through a temporary operator (fresh random password, demoted afterwards).
            env = dict(os.environ, ARCANE_ACCOUNT_PASSWORD=secrets.token_hex(8))
            try:
                run([old_account_tool, "set-password", args.operator], "operator-password", env=env)
                run([old_account_tool, "set-gmlevel", args.operator, "administrator"], "operator-promote", env=env)
                script = out / "shutdown.chat"
                script.write_text(".server shutdown 10\n", encoding="utf-8")
                with (out / "shutdown-client.log").open("x", encoding="utf-8") as log:
                    c = subprocess.Popen([str(client_tool), "live", "--account", args.operator, "--password-env", "ARCANE_ACCOUNT_PASSWORD",
                                          "--realm", f"127.0.0.1:{args.auth_port}", "--character", args.operator_character, "--script-file", str(script),
                                          "--duration-s", "25"], cwd=profile, env=env, stdout=log, stderr=subprocess.STDOUT,
                                         stdin=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
                    c.wait(timeout=75)
                deadline = time.monotonic() + args.shutdown_timeout_s
                while time.monotonic() < deadline and not gone(old_world):
                    time.sleep(1)
                require(gone(old_world), "World did not exit gracefully; not force-killing.")
            finally:
                run([old_account_tool, "set-gmlevel", args.operator, "player"], "operator-demote", check=False)
            report["world_stopped"] = True
            save()

            # 2. Stop the Realm (stateless logon server).
            ps(f"Stop-Process -Id {int(old_realm)}")
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline and not gone(old_realm):
                time.sleep(1)
            require(gone(old_realm), f"the Realm (PID {old_realm}) did not exit within 30 s of Stop-Process")
            report["realm_stopped"] = True
            snapshot("after-stop")
        else:
            report.update(world_stopped="already", realm_stopped="already")
            snapshot("after-stop")
        # The baseline for the checks below is what the stopped servers left, not what was read before the stop: with the population
        # manager on, the running World creates and parks bots between the first read and the shutdown.
        report.update(characters_stopped=characters(), bots_stopped=bots(), population_stopped=population_members())
        save()

        # 3. Schema: status, upgrade (a no-op when nothing is pending), check.
        (out / "backups").mkdir()
        # arcane-db reads the appsettings.json beside its own binary (a MySQL default), not the working directory's: name the profile's.
        cfg_arg = ["--config", str(settings_path)]
        run([dbtool, "status", "--no-fail-on-pending"] + cfg_arg, "01-status")
        run([dbtool, "upgrade", "--confirm-backup", "--backup-dir", out / "backups"] + cfg_arg, "02-upgrade")
        report["check_rc"] = run([dbtool, "check"] + cfg_arg, "03-check", check=False)
        require(report["check_rc"] == 0, "arcane-db check reported drift; see 03-check.log")
        require(characters() == report["characters_stopped"] and bots() == report["bots_stopped"], "the schema upgrade changed characters or bots")
        save()

        # 4. Content refresh of the world database (the script backs it up, checks the DBC hashes, refuses a database in use) and, with
        #    -AppSettings, the optional client data keys (set-optional-data.ps1 keeps its own appsettings.json.<stamp>.bak beside the file).
        shutil.copyfile(settings_path, out / "appsettings.before.json")
        # The refresh script refuses a world.db anyone else holds (exclusive open). arcane-db's check exits a moment earlier and Windows may not
        # have released its handle yet: wait up to 30 s for exclusive access before handing over.
        for _ in range(30):
            if world_db_free(paths["World"]):
                break
            time.sleep(1)
        else:
            raise RuntimeError(f"{paths['World']} is still held open after 30 s (another process, or a leaked handle in this script); nothing was refreshed")
        refresh = ["powershell", "-NoProfile", "-File", refresh_script, "-WorldDatabase", paths["World"], "-DbcDirectory", dbc_dir,
                   "-Dump", dump_path, "-Importer", importer, "-BackupDirectory", out / "content-refresh-backups", "-Report", out / "04-refresh-report.json"]
        if not args.no_optional_data:
            refresh += ["-AppSettings", settings_path]
        run(refresh, "04-refresh")
        report["refreshed"] = True
        save()

        # 5. Configuration, on top of what the refresh wrote (the file before step 4 is kept as appsettings.before.json).
        config = load_config()
        changed = {}
        config.setdefault("ClientData", {})["DbcDirectory"] = str(dbc_dir)
        changed["ClientData:DbcDirectory"] = str(dbc_dir)
        if terrain_arg:
            config.setdefault("World", {}).setdefault("Maps", {})["DataDirectory"] = terrain_arg
            changed["World:Maps:DataDirectory"] = terrain_arg
        if not args.no_elevators:
            path = str(dbc_dir / "TransportAnimation.dbc")
            config.setdefault("GameObjects", {})["TransportAnimationDbcPath"] = path
            changed["GameObjects:TransportAnimationDbcPath"] = path
        if args.mangos_defense_wear:
            items = config.setdefault("Items", {})
            items.update(DurabilityLossChanceParry=0.05, DurabilityLossChanceBlock=0.05, DurabilityLossChanceAbsorb=0.5)
            changed["Items:DurabilityLossChanceParry/Block/Absorb"] = "0.05/0.05/0.5"
        if not args.no_transports:
            config["World"].setdefault("Transports", {})["Enabled"] = True
            changed["World:Transports:Enabled"] = True
        metrics_cfg = config.setdefault("Ops", {}).setdefault("Metrics", {})
        if metrics_cfg.get("Enabled"):
            metrics_cfg.setdefault("PrometheusPrefix", "http://localhost:9464/metrics/")
            metrics_cfg.setdefault("RealmPrometheusPrefix", "http://localhost:9465/metrics/")
        watchdog_cfg = config.setdefault("Ops", {}).setdefault("Watchdog", {})
        hb_cfg = watchdog_cfg.setdefault("Heartbeat", {})
        hb_cfg["Mode"] = "File"
        hb_cfg["FilePath"] = "logs/world.heartbeat"
        hb_cfg["IntervalSeconds"] = 5
        changed["Ops:Watchdog:Heartbeat:Mode/FilePath/IntervalSeconds"] = "File/logs/world.heartbeat/5"
        tm_cfg = watchdog_cfg.setdefault("TickMonitor", {})
        tm_cfg.setdefault("ActionHangMs", 60000)
        tm_cfg.setdefault("Action", "FailFast")
        changed["Ops:Watchdog:TickMonitor:ActionHangMs/Action"] = f"{tm_cfg['ActionHangMs']}/{tm_cfg['Action']}"
        for item in args.set or []:
            key, _, raw = item.partition("=")
            try:
                value = json.loads(raw)
            except ValueError:
                value = raw
            node = config
            parts = key.split(":")
            for part in parts[:-1]:
                node = node.setdefault(part, {})
            node[parts[-1]] = value
            changed[key] = value
        settings_path.write_text(json.dumps(config, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        report["config_changed"] = changed
        before_cfg = json.loads((out / "appsettings.before.json").read_text(encoding="utf-8-sig"))

        def flatten(o, prefix: str = "") -> dict:
            if isinstance(o, dict):
                r = {}
                for k, v in o.items():
                    r.update(flatten(v, f"{prefix}:{k}" if prefix else k))
                return r
            return {prefix: o}

        fb, fa = flatten(before_cfg), flatten(config)
        report["config_diff"] = {k: [fb.get(k, "(not set)"), fa.get(k, "(not set)")] for k in sorted(set(fb) | set(fa)) if fb.get(k) != fa.get(k)}
        snapshot("before-start")
        save()

        # 6. Start Realm, then World.
        def log_path(name: str) -> Path:
            return out / "logs" / f"{name}.log" if args.visible_consoles else out / f"{name}.log"

        # Live servers share the box with agent builds/test runs; AboveNormal keeps their ticks ahead of that work.
        LIVE_PRIORITY = subprocess.ABOVE_NORMAL_PRIORITY_CLASS

        def start(dll: Path, name: str):
            env = dict(os.environ)
            # The World refuses the packet tools whenever this is 1, whatever the profile's configuration says
            # (docs/areas/map-feed.md, "Never on the live server"); launch-daemon.ps1 and server-status.ps1 -Supervise set it too.
            env["ARCANECORE_LIVE_SERVER"] = "1"
            if args.visible_consoles:
                # log_text() and the deploy log checks read this file as text lines (" WARN ", "Started managed playerbot"), so this
                # run pins the one log file to the Text format whatever the profile's Logging:ArcaneCore:Format says. With Format
                # set the path is File:Path alone; the deprecated Json:Path alias is not set, so the daemon has no deprecated key to warn about.
                env["Logging__ArcaneCore__Format"] = "Text"
                env["Logging__ArcaneCore__File__Enabled"] = "true"
                env["Logging__ArcaneCore__File__Path"] = str(log_path(name))
                env.pop("Logging__ArcaneCore__Json__Path", None)  # an inherited legacy alias would only draw a deprecation warning
                log_path(name).parent.mkdir(exist_ok=True)
                return subprocess.Popen(["dotnet", str(dll)], cwd=profile, env=env, close_fds=True,
                                        creationflags=subprocess.CREATE_NEW_CONSOLE | LIVE_PRIORITY)
            log = (out / f"{name}.log").open("x", encoding="utf-8")
            return subprocess.Popen(["dotnet", str(dll)], cwd=profile, env=env, stdout=log, stderr=subprocess.STDOUT,
                                    stdin=subprocess.DEVNULL,
                                    creationflags=subprocess.CREATE_NO_WINDOW | subprocess.DETACHED_PROCESS | LIVE_PRIORITY)

        def log_text(name: str) -> str:
            p = log_path(name)
            return p.read_text(encoding="utf-8", errors="replace") if p.is_file() else ""

        realm = start(realm_dll, "realm")
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline and listener_pid(args.auth_port) != realm.pid:
            require(realm.poll() is None, "Realm exited; see realm.log")
            time.sleep(1)
        require(listener_pid(args.auth_port) == realm.pid, "Realm not listening")
        world = start(world_dll, "world")
        report.update(new_realm=realm.pid, new_world=world.pid)
        save()
        enabled = sum(1 for b in report["bots_stopped"] if b[4])
        # Only World:Playerbots:MaxBots run at once (StartCoreAsync takes the first MaxBots enabled bots); the population manager can leave
        # more enabled than that (live-w37-r1: 1005 enabled, 1000 started), so wait for the bots the server will actually start.
        max_bots = int(load_config().get("World", {}).get("Playerbots", {}).get("MaxBots", 8))
        expected_started = min(enabled, max_bots)
        report.update(enabled_bots=enabled, max_bots=max_bots, expected_started=expected_started)
        deadline = time.monotonic() + 300
        while time.monotonic() < deadline:
            require(world.poll() is None, f"World exited {world.returncode}; see world.log")
            text = log_text("world")
            if listener_pid(args.world_port) == world.pid and text.count("Started managed playerbot") >= expected_started:
                break
            time.sleep(2)
        else:
            raise RuntimeError("World listener or bot restoration unconfirmed; see world.log")
        # Nothing the stopped servers left may be lost or altered; the population manager may add bots and park its own members
        # (DesiredEnabled of a population member may change), so additions are allowed and population members' DesiredEnabled is ignored.
        now_chars = {tuple(c) for c in characters()}
        lost_chars = [c for c in report["characters_stopped"] if tuple(c) not in now_chars]
        require(not lost_chars, f"characters lost or changed: {lost_chars[:5]}")
        pop = set(report["population_stopped"]) | set(population_members())
        now_bots = {tuple(b[:4]): b[4] for b in bots()}
        lost_bots = [b for b in report["bots_stopped"] if tuple(b[:4]) not in now_bots]
        require(not lost_bots, f"managed bots lost or changed: {lost_bots[:5]}")
        flipped = [b for b in report["bots_stopped"] if b[0] not in pop and now_bots[tuple(b[:4])] != b[4]]
        require(not flipped, f"DesiredEnabled changed for non-population bots: {flipped[:5]}")
        text = log_text("world")
        report["world_log_checks"] = deploy_log_lines.check_log(text, args.no_transports, args.no_optional_data, args.no_elevators)
        report["world_log_counts"] = {k: text.count(k) for k in ("unknown target map", "not existing zone id", " WARN ", " ERROR ", " FATAL ")}
        missing_lines = [k for k, ok in report["world_log_checks"].items() if not ok]
        if missing_lines:
            save()
            require(False, f"the World started but its log lacks expected lines {missing_lines}; not marked healthy, standard-processes.json not updated; see world.log")
        report.update(healthy=True, finished=datetime.datetime.now(datetime.timezone.utc).isoformat())
        save()
        meta_path = profile / "standard-processes.json"
        if meta_path.is_file():
            meta = json.loads(meta_path.read_text(encoding="utf-8"))
            (out / "standard-processes.before.json").write_text(json.dumps(meta, indent=2), encoding="utf-8")
            meta["services"]["World"] = dict(pid=world.pid, dll=str(world_dll))
            meta["services"]["Realm"] = dict(pid=realm.pid, dll=str(realm_dll))
            meta["qualification"] = args.label
            meta_path.write_text(json.dumps(meta, indent=2), encoding="utf-8")
        deploy_lock.unlink(missing_ok=True)
        if args.status_console:
            # The supervisor restarts the live daemons; it sets ARCANECORE_LIVE_SERVER itself, and inherits it here as well.
            status_env = dict(os.environ)
            status_env["ARCANECORE_LIVE_SERVER"] = "1"
            subprocess.Popen(["pwsh", "-NoProfile", "-NoExit", "-File", args.status_console, "-Profile", str(profile),
                              "-WorldPort", str(args.world_port), "-AuthPort", str(args.auth_port), "-Supervise"],
                             close_fds=True, env=status_env, creationflags=subprocess.CREATE_NEW_CONSOLE)
            report["status_console"] = args.status_console
            save()
    finally:
        deploy_lock.unlink(missing_ok=True)
    print(json.dumps(dict(healthy=True, world=world.pid, realm=realm.pid, characters=len(characters()),
                          bots=len(bots()), enabled_bots=enabled, missing_log_lines=missing_lines,
                          log_counts=report["world_log_counts"], config_changed=changed)))
    return 0


if __name__ == "__main__":
    sys.exit(main())

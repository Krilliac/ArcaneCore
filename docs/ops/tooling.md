# Operator tooling (`tools/ops/`)

The repository ships its operator scripts under `tools/ops/`. Every host-specific location is read from an environment variable or a command-line option (`tools/ops/opsenv.py` for Python, `tools/ops/OpsEnv.ps1` for PowerShell, and `${ARCANECORE_*}` variables for Bash), so nothing in the tree names a machine or a user. When a required variable is unset and no command-line override is given, the script stops with a message naming the variable and this page.

## Environment variables

| Variable | Purpose |
|---|---|
| `ARCANECORE_LIVE_PROFILE` | The live server profile directory (the one that holds `appsettings.json`, `auth.db`, `characters.db`, `world.db`, and `qa/report.json`). |
| `ARCANECORE_LIVE_ROOT` | Fallback alias for `ARCANECORE_LIVE_PROFILE` when `ARCANECORE_LIVE_PROFILE` is unset. |
| `ARCANECORE_LIVE_HOME` | The local live install root used by `tools/ops/live/` and `tools/ops/diag/attach-debugger.ps1`: `bin/` (the daemons run from here), `logs/` (`world.debug.log`/`.jsonl` and `realm.debug.log`/`.jsonl`, which `launch-daemon.ps1` gives each daemon; `win32-debugger.log`), `snapshots/` (a `bin-<stamp>` copy per redeploy) and `dumps/`. |
| `ARCANECORE_LANES_ROOT` | The root directory that holds lane worktrees and operator output directories (`_deploy`, `_diag`, `_wf`, `_plan`), and the build-slot files `slot-locks/` and logs `slot-logs/` of `tools/ops/ci/run-in-slot.ps1`. |
| `ARCANECORE_DEPLOY_ROOT` | Directory for deploy artifacts and per-run deploy logs (defaults to `<ARCANECORE_LANES_ROOT>/_deploy`). |
| `ARCANECORE_DATA_ROOT` | Directory holding extracted build-5875 client data (`terrain-5875` and `client-dbc-5875`). |
| `ARCANECORE_TERRAIN_DIR` | Explicit override for the extracted `.map`/`vmaps`/`mmaps` directory (defaults to `<ARCANECORE_DATA_ROOT>/terrain-5875`). |
| `ARCANECORE_DBC_DIR` | Explicit override for the build-5875 DBC directory (defaults to `<ARCANECORE_DATA_ROOT>/client-dbc-5875`). |
| `ARCANECORE_REFS_ROOT` | Root directory holding reference checkouts and SQL dumps (`mangos-classic`, `vmangos`, `classic-db`). |
| `ARCANECORE_MANGOS_CLASSIC_REF` | Explicit override for the `mangos-classic` checkout (defaults to `<ARCANECORE_REFS_ROOT>/mangos-classic`). |
| `ARCANECORE_VMANGOS_REF` | Explicit override for the `vmangos` reference checkout (defaults to `<ARCANECORE_REFS_ROOT>/vmangos`). |
| `ARCANECORE_WOTLK_REF` | Optional 3.3.5a (`wotlk`) reference checkout path for cross-version comparison tools. |
| `ARCANECORE_CLASSICDB_DUMP` | Explicit override for the ClassicDB SQL dump (defaults to `<ARCANECORE_REFS_ROOT>/classic-db/Full_DB/ClassicDB_1_12_1_z2815.sql.gz`). |
| `ARCANECORE_GH_REPO` | GitHub `owner/repo` slug queried by CI and RSI scripts (defaults to `Krilliac/ArcaneCore`). |
| `ARCANECORE_SONDER_MCP_URL` | Optional endpoint URL for the local Sonder MCP offload service. |
| `ANTHROPIC_API_KEY` | Anthropic API key used by `tools/ops/llm/*` (read from the environment or `HKCU\Environment`; never logged). |

---

## Top-level scripts (`tools/ops/`)

| Script | Live server impact | Purpose and usage |
|---|---|---|
| `tools/ops/opsenv.py` | Read-only | Shared Python environment lookup module (`lanes_root()`, `data_root()`, `refs_root()`, `live_profile()`, `deploy_root()`, `repo_root()`). Imported by the Python scripts in `rsi/`, `deploy/`, `diag/`, and `llm/`. Smoke check: `py -3 tools/ops/opsenv.py --help`. |
| `tools/ops/OpsEnv.ps1` | Read-only | Shared PowerShell environment lookup module (`Get-OpsEnv`, `Get-OpsRequiredEnv`). Dot-sourced by PowerShell scripts under `tools/ops/`. |
| `tools/ops/OpsLib.ps1` | Read-only | Pure PowerShell 7 helper functions (`New-OpsPassword`, `Test-OpsCommandLine`, `ConvertFrom-OpsTeleSpec`, `Get-OpsWeightedAssignment`, `Get-OpsBotNames`, `ConvertFrom-OpsClassSpec`, `New-OpsProvisionScript`, `Test-OpsProvisionLog`) tested by `tests/ArcaneCore.World.Tests/Ops/OpsScriptTests.cs`. |
| `tools/ops/run-gm-commands.ps1` | **Touches live server** (promotes operator account, logs in via `arcane-mock live`, sends GM commands, restores account level; `-DryRun` is read-only) | Runs paced `.` GM commands on a live world through an operator account and restores the account's prior security level in `finally`. Usage: `pwsh -NoProfile -File tools/ops/run-gm-commands.ps1 -ProfileDirectory <dir> -BinDirectory <dir> [-Command <line>...] [-CommandFile <path>] [-DryRun]`. |
| `tools/ops/provision-bots.ps1` | **Touches live server** (creates, starts, boosts, teleports, and optionally anchors playerbots via `run-gm-commands.ps1`; `-DryRun` is read-only) | Provisions `N` geared playerbots at level `L` spread across weighted `game_tele` destinations, retries transient boost refusals, and verifies every bot is `Running`. Usage: `pwsh -NoProfile -File tools/ops/provision-bots.ps1 -ProfileDirectory <dir> -BinDirectory <dir> [-Count 200] [-Level 14] [-TeleSpec Westfall] [-Home] [-DryRun]`. |

---

## RSI & Bot-QA (`tools/ops/rsi/`)

| Script | Live server impact | Purpose and usage |
|---|---|---|
| `tools/ops/rsi/botqa-stats.py` | Read-only | Summarises open `[bot-qa]` GitHub issues by kind, goal, and creation date. Reads `ARCANECORE_GH_REPO` / repo checkout or a saved JSON file. Usage: `py -3 tools/ops/rsi/botqa-stats.py [--input FILE] [--repo DIR]`. |
| `tools/ops/rsi/rsi_digest.py` | Read-only (opens `characters.db` with `mode=ro` and reads `qa/report.json`) |Snapshots how live bot-QA findings move across builds (`quiet`, `recurring`, `orphan`, close candidates, and regressions) into `<out>/digest-<utc>.{json,md}` and `history.jsonl`. Uses `ARCANECORE_LIVE_PROFILE`, `ARCANECORE_LANES_ROOT`, and `ARCANECORE_DEPLOY_ROOT`. Usage: `py -3 tools/ops/rsi/rsi_digest.py [--profile DIR] [--out DIR] [--quiet-hours 24] [--no-github]`. |

---

## Deploy (`tools/ops/deploy/`)

| Script | Live server impact | Purpose and usage |
|---|---|---|
| `tools/ops/deploy/build-integrated.sh` | Read-only (builds a clean worktree into `<deploy-root>/integrated-<tag>`) | Builds a Release deploy candidate from a clean worktree (`dotnet build -m:1 -nodeReuse:false -p:UseSharedCompilation=false`), copies `tools/`, verifies required scripts and binaries, and writes `SOURCE.txt`. Uses `ARCANECORE_DEPLOY_ROOT` or `ARCANECORE_LANES_ROOT`. Usage: `bash tools/ops/deploy/build-integrated.sh <worktree> <tag> "<source note>" [deploy-root]`. |
| `tools/ops/deploy/deploy-live.py` | **Touches live server** (gracefully shuts down World, stops Realm, upgrades schemas, refreshes world content, updates `appsettings.json`, restarts Realm and World) | Performs a full qualification deploy of an `integrated-<tag>` artifact directory to a server profile after preflight-checking every binary, DBC SHA-256 hash, SQL dump, and terrain directory. Uses `ARCANECORE_LIVE_PROFILE`, `ARCANECORE_DEPLOY_ROOT`, `ARCANECORE_DATA_ROOT`, and `ARCANECORE_REFS_ROOT`. Usage: `py -3 tools/ops/deploy/deploy-live.py --label <name> --artifacts <dir> [--profile <dir>] [--visible-consoles] [--status-console tools/ops/diag/server-status.ps1]`. |
| `tools/ops/deploy/deploy-post-checks.py` | Read-only on databases and processes (updates `report.json`, `standard-processes.json`, and optionally opens the status console window) | Completes post-start verification for a `deploy-live.py` run whose servers finished starting after the initial wait window timed out. Never starts, stops, or signals a daemon. Usage: `py -3 tools/ops/deploy/deploy-post-checks.py --label <name> [--deploy-dir DIR] [--status-console PS1]`. |

---

## Local live server (`tools/ops/live/`)

The local live server runs Debug builds of Realm and World from `<ARCANECORE_LIVE_HOME>/bin`, each in its own console, started in `ARCANECORE_LIVE_PROFILE`. Typical cycle: build a worktree (Debug), `redeploy.ps1` from it, then `livecheck.ps1`, `tools/ops/diag/server-status.ps1` and `tools/ops/diag/attach-debugger.ps1` alongside.

| Script | Live server impact | Purpose and usage |
|---|---|---|
| `tools/ops/live/LiveEnv.ps1` | Read-only | Dot-sourced helpers: `Resolve-LiveHome`, `Resolve-LiveProfile` (parameter, else `ARCANECORE_LIVE_HOME` / `ARCANECORE_LIVE_PROFILE`, else a stop naming the variable), `Get-LiveDaemon` (the one running Realm or World process; refuses to guess between several), `Get-LiveDaemonLogPath` (`<LiveHome>/logs/<role>.debug.log` or `.jsonl`), `Set-LiveDaemonLogEnvironment` (points `Logging__ArcaneCore__File__Path`/`Json__Path` at the role's files) and `Get-LiveLogFallback` (fallback files written since a time; see below). |
| `tools/ops/live/launch-daemon.ps1` | **Starts a live daemon** in the current console | Starts `ArcaneCore.<Role>.exe` from `<LiveHome>/bin` in the profile directory with QuickEdit off, `DOTNET_ENVIRONMENT=Development`, `NO_COLOR` removed, the role's own log paths (`<LiveHome>/logs/<role>.debug.log` and `.jsonl`, set through the environment so they override the profile's `appsettings.json`; whether each sink is on stays the profile's `Enabled` choice) and (World) `DOTNET_GCHighMemPercent` (default 97). Usage: `pwsh -NoExit -File tools/ops/live/launch-daemon.ps1 -Role <Realm\|World> [-LiveHome <dir>] [-Profile <dir>] [-GCHighMemPercent 97]`. |
| `tools/ops/live/redeploy.ps1` | **Touches live server** (plans first; then nothing, a hot apply through the World's control pipe, or the hard restart: Ctrl-C stops World then Realm, copies binaries, optionally swaps `world.db`, restarts both at AboveNormal); `-Plan`/`-WhatIf` is read-only | Plans with `RedeployPlan.ps1` ([redeploy.md](redeploy.md)): compares the worktree with the deploy record `<LiveHome>/bin/arcanecore-deploy.json` (deployed SHA, uncommitted paths, configuration fingerprint, module hashes), the profile's configuration and the module directory, and picks `None`, `Hot` (live config keys, `-ContentTables` that `.reload` supports, allowlisted hot modules: `.reload`/`.hotmodule` over the control pipe), `Restart` or `Blocked` (exit 2). A hot path that is unavailable falls back to the restart; a change the World rejects stops with exit 3. The restart snapshots `<LiveHome>/bin` to `snapshots/bin-<stamp>`, copies both Debug builds from `-Src` (default: the worktree the script is in; it must be built) and changed approved modules, and with `-WorldDbSwap <file>` puts a refreshed world database in place while the World is stopped (the target is the Sqlite `Data Source` of `Database:World:ConnectionString` in the profile's `appsettings.json`, or `-WorldDb`; an empty target is refused) (old file and its `-wal`/`-shm` kept as `world.db.bak-<stamp>`, copy hash-checked). After the start it fails if either daemon's log name was taken by another writer (a fallback file written since the start), then writes the deploy record. Usage: `pwsh -NoProfile -File tools/ops/live/redeploy.ps1 [-Src <worktree>] [-Plan\|-WhatIf] [-Json] [-ForceRestart] [-ContentTables <names>] [-WorldDbSwap <world.db> [-WorldDb <target>]] [-LiveHome <dir>] [-Profile <dir>]`. |
| `tools/ops/live/RedeployPlan.ps1` | Read-only (the hot-apply functions send commands only when `redeploy.ps1` calls them) | Dot-sourced: the planner (`Get-RedeployPlan`, pure; `Get-LiveRedeployPlan` gathers its inputs), the deploy record (`Read-`/`Write-DeployRecord`), the configuration fingerprint, and the control-pipe client (`Connect-WorldControl` checks the serving PID, `Invoke-WorldControlCommand`, `Invoke-HotReloadStep`, `Invoke-HotModuleStep`, `Invoke-HotRedeploy`). |
| `tools/ops/live/livecheck.ps1` | Read-only | One-shot health read of `logs/world.debug.log` (warning when a fallback file was written in the window) since `-Since` (UTC, default 15 min ago): startup markers, level counts, distinct ERROR/FATAL/exception lines, first-chance crash-report count, top WARN lines (numbers folded), tick health and daemon process state. Usage: `pwsh -NoProfile -File tools/ops/live/livecheck.ps1 [-Since 'yyyy-MM-dd HH:mm'] [-LiveHome <dir>]`. |


A log file has one writer (`RollingFileWriter` locks `<file>.lock`). A daemon that finds its configured log already owned writes `<name>.<process>.<ext>` instead (for example `world.debug.ArcaneCore.Realm.log`; then `<name>.<process>.<pid>.<ext>`) and says so once on stderr. Such a file in `logs/` means two processes were configured with one path: `livecheck.ps1` warns about it and `redeploy.ps1` fails on it.

---

## Diagnostics (`tools/ops/diag/`)

| Script | Live server impact | Purpose and usage |
|---|---|---|
| `tools/ops/diag/attach-debugger.ps1` | **Attaches a Win32 debugger** to Realm and World (never breaks in; every event is continued; KillOnExit off, so closing the console detaches) | Logs native exceptions, breakpoints, process exits with exit codes, and a count of CLR first-chance exceptions (the first 10, then every 500th) to `<LiveHome>/logs/win32-debugger.log`. It sees only exception code `0xE0434352`: name the CLR types with `exception-probe`. Usage: `pwsh -NoExit -File tools/ops/diag/attach-debugger.ps1 [-RealmPid <pid>] [-WorldPid <pid>] [-LogPath <file>] [-LiveHome <dir>]` (PIDs default to the running daemons). While attached, every debug event (thread start or exit, DLL load, first-chance exception) stops the whole daemon until the script continues it, so a debugger console that stalls (a selection in it, the machine paging) stalls the World with it: hang lines then read `verdict process-stalled`. |
| `tools/ops/diag/exception-probe/` | Read-only (an EventPipe session; the target keeps running) | Prints, for a running daemon, how often each exception type + message was thrown over `seconds` and which method caught it, including types the crash handler keeps out of the log (cancellations, socket errors). Not in `ArcaneCore.slnx`. Usage: `dotnet run --project tools/ops/diag/exception-probe -- [pid\|World\|Realm] [seconds=60]`. |
| `tools/ops/diag/alloc-probe/` | Read-only (an EventPipe session with a method rundown at the end; the target keeps running) | Names what a running daemon allocates on the large object heap over `seconds`: sampled megabytes by type and by allocating call stack (`frames` deep), plus the heaviest small-object types. A `GCAllocationTick` fires about once per 100 KB, so the LOH figures are near exact. Found the World's LOH churn (navmesh tile reads, the bot QA store) for issues #744/#755. Not in `ArcaneCore.slnx`. Usage: `dotnet run --project tools/ops/diag/alloc-probe -- [pid\|World\|Realm] [seconds=30] [frames=8]`. |
| `tools/ops/diag/bot-progress.py` | Read-only (opens `characters.db` with `mode=ro`) | Appends a JSONL snapshot of population-owned and managed playerbots (counts, levels, level gains, top zones, World working set, and host free memory) to `<out>/snapshots.jsonl` and rewrites `<out>/summary.md`. Uses `ARCANECORE_LIVE_PROFILE` and `ARCANECORE_LANES_ROOT`. Usage: `py -3 tools/ops/diag/bot-progress.py [--profile DIR] [--out DIR] [--every 600] [--count 1]`. |
| `tools/ops/diag/diag-dump.ps1` | **Suspends live process** during capture (`dotnet-dump collect --type Full`; `-SummaryOnly` is read-only) | Captures a full process dump of a running daemon and writes a GC + LOH type summary beside it. Uses `ARCANECORE_LANES_ROOT`. Usage: `pwsh -NoProfile -File tools/ops/diag/diag-dump.ps1 -ProcessId <pid> [-OutDir <dir>]` or `-SummaryOnly -Dump <path>`. |
| `tools/ops/diag/diskmon.ps1` | Read-only | Samples Windows `PhysicalDisk` latency, queue length, write throughput, page-ins, and available memory once per second into a CSV file. Usage: `pwsh -NoProfile -File tools/ops/diag/diskmon.ps1 -Out <csv-path> [-Seconds 150] [-Drive "0 C:"]`. |
| `tools/ops/diag/hang-stacks.sh` | **Briefly attaches to live process** (`dotnet-stack report -p <pid>`) | Watches `world.log` for in-progress `world tick has been running for` lines and captures up to `max` managed thread stack dumps into `<ARCANECORE_LANES_ROOT>/_diag/hangstacks`. Usage: `bash tools/ops/diag/hang-stacks.sh <world-pid> <world.log> [max=3] [out-dir]`. |
| `tools/ops/diag/server-status.ps1` | Read-only by default (closes older instances of `server-status.ps1` watching the same World port); **restarts crashed/hung daemons when `-Supervise` is passed** | Live refreshing status console showing `realmlist` rows (`auth.db` opened `mode=ro`), Realm/World listener PIDs and memory/CPU, watchdog heartbeat state (`logs/world.heartbeat`), and scraped Prometheus metrics (`Ops:Metrics:PrometheusPrefix`). With `-Supervise`, automatically restarts a crashed or `.server restart` (exit code 2) Realm/World daemon from `standard-processes.json`, kills and restarts a World process whose heartbeat file stays stale past `-HangKillSeconds` (default 90 s), pauses while `deploying.lock` exists, and enforces a 5-restarts-per-180s circuit breaker. Uses `ARCANECORE_LIVE_PROFILE`. Usage: `pwsh -NoExit -File tools/ops/diag/server-status.ps1 [-Profile <dir>] [-WorldPort 8085] [-AuthPort 3724] [-Supervise] [-HangKillSeconds 90]`. |

---

## CI & Worktree Hygiene (`tools/ops/ci/`)

| Script | Live server impact | Purpose and usage |
|---|---|---|
| `tools/ops/ci/bisect-one.sh` | Read-only on live server (modifies the target bisect worktree) | Checks out `<sha>` in a bisect worktree, builds `tests/ArcaneCore.World.Tests` in Release, and runs a filtered test pass with `ARCANECORE_DATA_ROOT`. Usage: `bash tools/ops/ci/bisect-one.sh <sha> [filter]`. |
| `tools/ops/ci/ci-gate.sh` | Read-only on live server (pushes and later deletes an `integrate/<name>-<sha8>` branch) | Runs the full test gate on GitHub Actions: pushes the worktree's `HEAD` to a per-commit `integrate/` branch, waits for the `ci.yml` run for exactly that SHA, prints each suite's result and the failing tests. Exit 0 = every suite passed, 2 = no run appeared, 3 = failed/cancelled, 4 = timeout. CI has no terrain/DBC data, so real-data tests still need a local run. Uses `ARCANECORE_GH_REPO` (else the worktree's `gh repo view`). Usage: `bash tools/ops/ci/ci-gate.sh <worktree> <name>`. |
| `tools/ops/ci/run-in-slot.ps1` | Read-only on live server (caps concurrent builds/tests so the live server keeps its CPU and memory) | Runs one build/test command once one of `-Slots` (default 2) machine-wide slot files is free and at least `-MinAvailableMB` (default 2000) memory is available, in a BelowNormal child pwsh, logging to `<LanesRoot>/slot-logs/<lane>-<n>.log`. Frees slots whose holder died. Passes `ARCANECORE_TEST_TERRAIN_DIR`/`_DBC_DIR` from `ARCANECORE_DATA_ROOT` so real-data tests run. Exit code = the command's (124 on timeout). Usage: `pwsh -NoProfile -File tools/ops/ci/run-in-slot.ps1 -Lane <name> -WorkDir <worktree> -Command '<pwsh command>' [-TimeoutMin 45] [-LanesRoot <dir>]`. |
| `tools/ops/ci/full-suite-gate.ps1` | Read-only on live server | Local full gate run from a worktree root (normally through `run-in-slot.ps1`): builds `ArcaneCore.slnx`, runs each test suite and the MockClient self-test, printing `BUILD_EXIT`, `SUITE_EXIT <suite>=<rc>` and `SELFTEST_EXIT`. Exit 0 = all passed, 1 = a failure, 2 = the build failed (nothing ran). Usage: `pwsh -NoProfile -File tools/ops/ci/full-suite-gate.ps1 [-Suites World,Game,...]`. |
| `tools/ops/ci/ci-peek.sh` | Read-only | Prints the latest Node1 CI suite summary lines from `${ARCANECORE_NODE_CI_DIR:-/root/ci}`. Usage: `bash tools/ops/ci/ci-peek.sh [sha-prefix]`. |
| `tools/ops/ci/node-ci.ps1` | Read-only on live server (runs CI on Node1 WSL) | Ships `git archive <commit>` to Node1 (`Node@10.77.0.2`) and runs restore, Release build, each `tests/ci/<Suite>.slnf` suite, and the `Rest` MockClient self-test against Node1's MariaDB and PostgreSQL services. Respects `NODE_CI_MAX_CONCURRENT` and `ARCANECORE_NODE_TEST_DB_PASS`. Usage: `pwsh -NoProfile -File tools/ops/ci/node-ci.ps1 -Repo <dir> -Commit <ref> [-Suites Game,World,Data,Rest]`. |
| `tools/ops/ci/node-ci-queue.ps1` | Read-only on live server (runs `node-ci.ps1` sequentially on Node1) | Serializes `node-ci.ps1` runs across a list of git refs, waiting for any active `node-ci.ps1` process to finish before starting the next. Usage: `pwsh -NoProfile -File tools/ops/ci/node-ci-queue.ps1 -Repo <dir> -Refs <ref1,ref2,...>`. |
| `tools/ops/ci/wait-checks.sh` | Read-only | Polls `gh pr checks` or `gh run view` using `gh --jq` until all checks on a PR or workflow run complete. Uses `ARCANECORE_GH_REPO`. Usage: `bash tools/ops/ci/wait-checks.sh <pr\|run> <id> [max-minutes=120]`. |
| `tools/ops/ci/wt-hygiene.sh` | Read-only on live server (removes clean, merged git worktrees) | Removes clean lane worktrees whose `HEAD` is already an ancestor of `origin/main` and deletes their local branch, sleeping between removals to avoid disk thrash. Usage: `bash tools/ops/ci/wt-hygiene.sh [repo-dir] [worktree-grep-pattern]`. |

---

## LLM Batch & Review Tools (`tools/ops/llm/`)

| Script | Live server impact | Purpose and usage |
|---|---|---|
| `tools/ops/llm/llmlib.py` | Read-only | Shared Anthropic client and hard spend ledger (`ledger.json`). Prices every request at worst case before submission and settles actual token cost on completion. Usage: `py -3 tools/ops/llm/llmlib.py [--help]`. |
| `tools/ops/llm/sqlparse.py` | Read-only | Streaming parser for MySQL `INSERT INTO` rows in `.sql` and `.sql.gz` dumps (used by `escort_specs.py`). Usage: `py -3 tools/ops/llm/sqlparse.py --help`. |
| `tools/ops/llm/Set-AnthropicKey.ps1` | Read-only (updates user environment variable) | Reads an Anthropic API key with hidden console input, validates it against `https://api.anthropic.com/v1/models`, and stores it in the Windows user environment (`ANTHROPIC_API_KEY`). Usage: `pwsh -NoProfile -File tools/ops/llm/Set-AnthropicKey.ps1 [-Name ANTHROPIC_API_KEY] [-SkipTest]`. |
| `tools/ops/llm/escort_specs.py` | Read-only | Job A: extracts structured JSON specifications from `mangos-classic` ScriptDev2 `npc_escortAI` C++ scripts and ClassicDB `script_waypoint`/`script_texts` rows. Uses `ARCANECORE_REFS_ROOT` (`ARCANECORE_MANGOS_CLASSIC_REF`, `ARCANECORE_CLASSICDB_DUMP`). Usage: `py -3 tools/ops/llm/escort_specs.py <prep\|smoke\|submit\|collect> [...]`. |
| `tools/ops/llm/faction_fix.py` | Read-only | Deterministic post-pass for Job A escort specs: re-resolves `FACTION_*` constants from cited C++ lines and `ScriptDevAIMgr.h`. Uses `ARCANECORE_REFS_ROOT` / `ARCANECORE_MANGOS_CLASSIC_REF`. Usage: `py -3 tools/ops/llm/faction_fix.py [--sd2 DIR] [--specs-glob GLOB]`. |
| `tools/ops/llm/fidelity_review.py` | Read-only | Job B: reviews C# dungeon and raid script ports against `mangos-classic` ScriptDev2 and `vmangos` C++ reference files using the Message Batches API. Uses `ARCANECORE_LANES_ROOT`, `ARCANECORE_REFS_ROOT`, `ARCANECORE_MANGOS_CLASSIC_REF`, and `ARCANECORE_VMANGOS_REF`. Usage: `py -3 tools/ops/llm/fidelity_review.py <prep\|smoke\|submit\|collect\|report> [...]`. |
| `tools/ops/llm/review_diff.py` | Read-only | Job C: chunks a git branch diff (`base..head`) with annotated new-file line numbers, reviews each chunk against the repository charter and rules, and writes ranked findings to JSON and Markdown. Usage: `py -3 tools/ops/llm/review_diff.py --repo <dir> --base <rev> --head <rev> --budget-usd <num> [--dry-run] [--batch]`. |

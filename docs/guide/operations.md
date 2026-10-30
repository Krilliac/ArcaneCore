# Operating a realm

How to run, stop, back up, upgrade and watch an ArcaneCore realm, and the release caveats you should decide on before exposing it. For the first
installation see [installation and first run](installation.md). Every key named here is in the generated [configuration reference](../reference/configuration.md).

## Start, stop and restart

Start the realm daemon, then the world daemon (`dotnet run --project src/ArcaneCore.Realm -c Release` and `src/ArcaneCore.World`), after a
solution build in the same configuration (`dotnet build ArcaneCore.slnx -c Release`): the world loads the Playerbots pack from its own directory and
exits 78 without it, and building `src/ArcaneCore.World` alone does not refresh it. Run the world daemon's
`check-config` verb before a deployment; it exits with 78 when the configuration is invalid and the daemon runs the same check at every start.

Stop or restart the world from an in-game administrator account with the retail countdown commands: `.server shutdown`, `.server restart`
(each takes `cancel` or a delay and an optional exit code) and the `.server idleshutdown` / `.server idlerestart` forms. They need the Administrator
account level (see the [GM command reference](../reference/gm-commands.md)). A restart ends the process with exit code 2, and a supervisor that
restarts on 2 reproduces the vmangos run-mangosd loop; a configuration error exits with 78 and must not be restarted. All exit codes are in
[exit codes](../reference/exit-codes.md). Characters are saved every `World:AutosaveIntervalMs` (default 900000 ms; 0 disables),
and `.saveall` saves everyone at once.

Only one world process may run on a characters database, and a deployment must stop the old world before it starts the new one. A world on a file-backed SQLite
characters database takes an exclusive lock on the sidecar file `<characters database>.world.lock` first thing at start, before the schema initializers open, upgrade or
checkpoint the database, whether or not `Cluster:Fencing:Enabled` is on. The lock belongs to the host container: it is released after every hosted service has stopped (the WAL
checkpointer's final checkpoint included) or when the process ends, and the operating system also releases it on a crash. A start that fails also ends in the host disposing the container, which releases it. A second world on the
same file exits with the schema-refusal exit code and `database refused: the characters database is already owned by another world process (could not take the exclusive writer lock '<path>')`,
having read and written nothing; a lock file that cannot be created at all (permissions, a full disk) is refused the same way with its own message, not as another world. Limits:
the lock is keyed on the file path (a symlinked database file resolves to its target; a symlinked directory, a bind mount or a second drive letter for the same folder is a different
path), `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` or a network filesystem that ignores locks disables it, and on Unix deleting the `.world.lock` file while a world runs defeats it (Windows
holds the file open with `FileShare.None`, so it cannot be deleted). On Windows an antivirus or backup tool that briefly opens the sidecar can make a start look contended; the world then
refuses to start, and starting it again fixes that.
Before it restores any playerbot the world also checks that `World:Port` can be bound; that is check-then-bind, which narrows the window and does not close it, and a port that is still
taken fails the start before a single bot is logged in.
MariaDB, MySQL and PostgreSQL databases have no file, so the world takes the same process-lifetime exclusive lock as a session-level advisory lock held on one dedicated, unpooled
connection (MariaDB/MySQL `GET_LOCK(name, 0)`, PostgreSQL `pg_try_advisory_lock(key)`), in the same place and order as the SQLite lock: before the schema initializers, whether or not
`Cluster:Fencing:Enabled` is on, released last with the container. The lock is keyed on the name of the characters database from `Database:Characters`: the MariaDB/MySQL lock name is
`arcanecore.world.writer:<lower-cased database name>` (lower-cased because the server folds database-name case on case-insensitive file systems, `lower_case_table_names`; replaced by the prefix and 32 hex
characters of the name's SHA-256 when longer than MariaDB's 64-character limit) and the PostgreSQL key is the first 8 bytes of the SHA-256 of `arcanecore.world.writer:<database name>`, so different realms'
databases do not collide and two worlds configured with the same database name on the same server do. PostgreSQL scopes advisory locks to the database a session is connected to, so every world takes
the lock in one fixed maintenance database, `postgres` unless `Database:WriterLockDatabase` (or `Database:Characters:WriterLockDatabase`) names another; it must exist and the login needs CONNECT on it.
Only the characters database takes this lock, so the key means nothing under `Database:Auth` or `Database:World`. `pg_locks` is cluster-wide, so after taking the lock the world also checks that no other
session holds the same key in any database: two worlds configured with different lock databases still exclude each other (started at the same instant, both may refuse). MariaDB
and MySQL need no database for `GET_LOCK`, so the lock works before the characters database has been created. A second world gets the exit code `Refused` at once, with the same message shape as for SQLite
naming the advisory lock instead of a file, having read and written nothing; a server that cannot be reached is retried and reported like the schema step (`Resilience:Database:Bootstrap`, exit code for an
unreachable database). The server releases the lock when the connection ends, so a process crash leaves nothing to clean up. Limits: the lock lives in a server session. A periodic check (every 30 s, on the lock
connection) logs at Critical when the lock is lost (server restart, dropped connection, killed session); until it is back this world does not exclude a second one, and the fencing claim below is the remaining guard.
On every later check the world tries to take the lock again on a fresh connection and logs a warning (`RE-TAKEN`) when that works. If another session holds it for longer than a dead session of this process
could (four check intervals plus one, 150 s), another world is writing the database: the world logs Critical, sets exit code 1 and stops in an orderly way (new logins are not gated separately; the shutdown
saves still run). A server that cannot be reached leaves the lock lost and is retried on every check.
The opposite failure also exists: a host that died or a network partition can leave the server holding the dead session, so a new world is refused until the server ends it. The lock session carries an idle
limit of four check intervals (120 s): `wait_timeout` on MariaDB/MySQL, `idle_session_timeout` on PostgreSQL 14 and later (the 30 s check keeps a live session far below it), and PostgreSQL's server-side
`tcp_keepalives_idle` / `tcp_keepalives_interval` are set too, so a vanished host is dropped within about two minutes. To clear it sooner use
`SELECT pg_terminate_backend(pid)` (PostgreSQL, the pid is in `pg_locks` for `locktype = 'advisory'`) or `KILL <id>` (MariaDB/MySQL, the id is `SELECT IS_USED_LOCK('<lock name>')`). A standby worker takes the lock (SQLite or server) when it is promoted,
see [the gateway](../areas/gateway.md#standby-worker-tier-3b-groundwork).
The `Cluster:Fencing` claim, which an unfenced world checks at start and then every
`Cluster:Fencing:ClaimWatchIntervalSeconds`, and that fence (`CharacterWriteFenceInterceptor`) remain the guard against a lost lock and the thing that bounds character autosaves and session-close saves of a
second writer. If two worlds still end up writing the same playerbot rows, the world that already owned them (the older one, whose checkpoints are refused) logs `Split brain` at Critical,
stops every bot and stops writing playerbot rows (checkpoints, creates, starts, stops, deletes, homes and the fleet reset), and reports it in `.playerbot status`, `.playerbot splitbrain` and the gauge
`arcanecore.playerbots.split_brain` ([playerbots](../areas/playbots.md#another-world-on-the-same-database)). Character rows are still saved by the character autosave and when a session closes; split brain does not stop those.
The older world stands down and the intruder keeps running on purpose: neither world can stop the other, and the one that already lost its rows backing off is the only move that guarantees a single writer
(the operator then stops the intruder and clears the latch).

A clustered realm must keep its characters on a server database. A SQLite characters database allows one writer process, and the world processes of a cluster write it concurrently, so the config check
(`--check-config`; start-up as well, whatever `Startup:Strict` says) is an error, exit code 78, when the characters provider is SQLite and any of these is set: `Cluster:Worker:Enabled` (a gateway worker, a standby
included), `Cluster:Node:Role` = MapWorker, or `Cluster:Supervisor:Enabled` with a gateway-worker child (role `worker`) or more than one world-type child (role `combined` or `worker`) under `Cluster:Supervisor:Processes`. `tools/ops/live/start-cluster.ps1 -Gateway` makes the world a gateway worker, so it needs a server database for `Database:Characters`. The message names the setting and
the provider and says to use PostgreSQL or MariaDB for `Database:Characters` (moving a SQLite realm: [database upgrade](../ops/database-upgrade.md)). The supervisor refuses before it launches any child. A supervisor with exactly one combined world child is
still allowed on SQLite (one writer), with a warning that the realm cannot grow beyond one world on SQLite. The provider checked is the one the process's own configuration resolves for `Database:Characters`
(for a supervisor, its own; its children are expected to share it).

Both daemons accept typed commands at their console window (`World:Console:Enabled`, `Realm:Console:Enabled`, both on by default). The world console runs the GM commands with Administrator authority and no character; the realm console has `help`, `info`, `realmlist`, `online`, `account create|set-password|set-gmlevel` and `quit`. Nothing is read when standard input is redirected (a service, a pipe, a deploy script). Commands that need a character or a selection answer that they need a player. See [the server console](../areas/server-console.md). The world daemon also takes the same commands from local tools: over the control pipe, a Windows named pipe (`World:Control:Pipe:Enabled`, off by default; `tools/ops/live/redeploy.ps1` uses it), and over [remote administration](../ops/remote-admin.md) (RA, `World:RemoteAdmin`), a TCP listener that is on by default on 127.0.0.1:3443 and runs each command with the security level of the account that logged in. RA is clear text, passwords included: keep `World:RemoteAdmin:BindAddress` on a loopback address, or set `World:RemoteAdmin:Enabled` to `false`. For account, password and ban work without a running daemon use the `arcane-account` tool.

## Backups and upgrades

Back up the databases with the tools of your engine; `arcane-db backup-info` prints how for each configured database. Upgrading the schema is a deliberate
step, not something a daemon should do by surprise: set `Database:Upgrade:Policy` to `CreateOnly` or `Never` once the databases exist, then:

1. Stop every daemon (rolling upgrades are unsupported).
2. Back up every database (SQLite: `--backup-dir <directory>` writes a verified copy).
3. `arcane-db status`, then `arcane-db plan` (read-only; `--script` prints the SQL).
4. `arcane-db upgrade --confirm-backup`, which applies the pending steps in the order auth, characters, world and then checks.
5. `arcane-db check` to compare the databases with the model.

The runbook, with the exit codes, the per-engine behaviour while a step runs and recovery, is [database upgrade tooling](../ops/database-upgrade.md).
The schema versions are listed in the [schema reference](../reference/schema.md). Characters are per realm: run the tool once per realm configuration.

## Single-host supervisor (optional)

The cluster host supervisor can run the realm and a combined world on one machine and relaunch either after a crash or a hang. It is off by default; the live
setup keeps using `tools/ops/diag/server-status.ps1 -Supervise`. To try it, run `ArcaneCore.World` with `Cluster:Supervisor:Enabled` true and two processes:

```
Cluster:Supervisor:Enabled = true
Cluster:Supervisor:MinClusterProcesses = 2
Cluster:Supervisor:Processes:realm:Name = realm
Cluster:Supervisor:Processes:realm:Role = realm
Cluster:Supervisor:Processes:realm:ExecutablePath = ArcaneCore.Realm
Cluster:Supervisor:Processes:realm:StartOrder = 0
Cluster:Supervisor:Processes:world:Name = world
Cluster:Supervisor:Processes:world:Role = combined
Cluster:Supervisor:Processes:world:ExecutablePath = ArcaneCore.World
Cluster:Supervisor:Processes:world:StartOrder = 1
Cluster:Supervisor:Processes:world:RealmId = 1
```

A world that sends no heartbeat tick for 120 seconds is drained, killed and relaunched; exit code 2 (`.server restart`) relaunches at once, 0 stays down, 78 and 64
fault. With `RealmId` set, `.server restart 60 2 <realmid>` on one world restarts another world on the same host (not across hosts). Details are in the
[clustering design](../CLUSTERING_DESIGN.md).

## Starting the local live server

Two launchers in `tools/ops/live/` start the local live server for a developer machine. Both read `ARCANECORE_LIVE_HOME` and `ARCANECORE_LIVE_PROFILE` (or `-LiveHome` / `-Profile`), start nothing under `-Plan` (or `-WhatIf`; it prints every window title, command line, environment variable with secrets masked, and what it waits for), refuse when a Realm or World is already running or a port is taken, and put the build found in `<LiveHome>/bin` in every window title: `ArcaneCore.World (Debug, live)`. The build (Debug, Release, Mixed or unknown) is read from the assemblies' `DebuggableAttribute` and `AssemblyConfigurationAttribute` without loading them. Each opens a status window and a Win32 debugger window; `-NoStatus` and `-NoDebugger` skip them. Details and the full parameter list are in [operator tooling](../ops/tooling.md#local-live-server-toolsopslive).

```
pwsh -NoProfile -File tools/ops/live/start-single.ps1 [-NoStatus] [-NoDebugger] [-Plan]
pwsh -NoProfile -File tools/ops/live/start-cluster.ps1 [-Gateway [-MapWorker]] [-HangCapture None|Stacks|Dump] [-NoStatus] [-NoDebugger] [-Plan]
```

- **`start-single.ps1`** starts the Realm, waits until it is alive and listening, then the World, each in its own console through `launch-daemon.ps1` (the same diagnostics, log paths and environment as `redeploy.ps1`). The status window is `tools/ops/diag/server-status.ps1`, always read-only here: nothing restarts a daemon that exits or hangs, and `-Supervise` is refused with a message pointing at `start-cluster.ps1` (the status window can restart only from the profile's `standard-processes.json`, which `deploy-live.py` writes for an older artifact directory, so it would kill the World this script started and fail to relaunch it or relaunch an old build). Use `start-cluster.ps1` when you want restarts.
- **`start-cluster.ps1`** runs `ArcaneCore.World` as the host supervisor above in its own console and gives it the realm (start order 0) and one combined world (start order 1, realm id 1, `MinClusterProcesses` 2) as `Cluster__Supervisor__...` environment variables built from its parameters; no configuration file is edited. The children's output appears in the supervisor window as `[realm]` and `[world]` lines. The status window runs without `-Supervise` (the cluster supervisor owns restarts) and watches the world's heartbeat file; the debugger attaches to each child but never to the supervisor itself, and follows the children, attaching to a relaunched child's new PID. A child that exits while it starts is relaunched by the supervisor, and the launcher waits for the next process instead of failing, so the status and debugger windows still open. `-Gateway` adds `ArcaneCore.Gateway` and turns the world into a gateway worker (`-MapWorker` also sets `Cluster:Node:Role` to `MapWorker`); it needs a link secret of at least 16 characters in `ARCANECORE_CLUSTER_LINK_SECRET` (or `-LinkSecret`), which goes only into the environment of the supervisor console and is never printed. By default the supervisor takes a full dump of the world child before it kills it as hung (`-HangCapture Dump`, into `<LiveHome>/dumps`, under the existing dump caps); a dump is gigabytes and holds secrets (`-HangCaptureTimeoutSeconds` is 1 to 3600, default 300; `-Plan` warns when Dump is chosen with less than 120).

The supervisor console's environment, the link secret included, also reaches the hidden diagnostics sidecar and `dotnet-counters`, and appears in full crash and hang dumps. In cluster mode the sidecar collects counters from the supervisor, not from the world child. `MinClusterProcesses` equals the child count with `StopWhenBelowMinimum` on, so if the supervisor gives up on a crash-looping gateway the whole cluster stops.

A Win32 debugger stops the whole target on every debug event until it continues it. The debugger window continues each event at once and logs through bounded queues drained by separate writer threads, so a stalled debugger console (text selected in it, the machine paging) does not hold a target stopped: lines are dropped and counted ("N lines dropped") instead. In cluster mode it is never attached to the supervisor, because a stopped supervisor stops reading its merged children's output. What remains: an exception-heavy target is slowed by one whole-process stop per exception (measured: about 60000 exceptions a second alone, about 4500 attached, roughly 0.2 ms each), and under the cluster supervisor with `-HangCapture Dump` a dump request for a child the debugger has stopped can wait up to `-HangCaptureTimeoutSeconds` before the supervisor kills it. Raise `-HangTimeoutSeconds` or pass `-NoDebugger` when a debugging session must not cost a restart.

## Bans

Ban from in-game with `.ban account`, `.ban character` and `.ban ip`, and lift with `.unban`; `.baninfo` and `.banlist` read the history. Without a client use
`arcane-account ban <username> <duration|0> <reason>`, `unban`, `baninfo` and `banlist`. A duration is `1d2h3m4s`; `0`, and (unless
`Bans:RejectUnparseableDuration` is on) any string that does not parse, is a permanent ban, exactly as in retail. A ban made by the account tool is a database
row: a connected player is disconnected by the periodic re-check (`Bans:RecheckIntervalSeconds`, 60 seconds by default); with it set to 0 the ban applies at
that account's next login. The ban tables,
IP bans and the live enforcement options are described in [live bans](../security/live-bans.md).

## Monitoring

The world daemon logs slow updates through the `PerformanceLog` section (`SlowWorldUpdate`, `SlowMapUpdate`, `SlowPackets`, in milliseconds; 0 disables a
threshold), tagged with the `Perf` event id so a log sink can filter them. By default they are summarised once a minute (`PerformanceLog:SummaryIntervalSeconds`, 0 = one line per slow update), with a detail line only for the outliers. `.server info` shows the version, the players online and the uptime.
See [operations and performance](../areas/ops-perf.md). Both daemons also run the `Ops:Watchdog` monitors on a dedicated thread: rate-limited
warnings on world-tick overruns with percentiles, a critical line on a hung tick, heap high-water marks and memory-pressure warnings, a
thread-pool starvation probe, a periodic counter dump and a supervisor heartbeat (systemd `Type=notify` with `WatchdogSec`, a liveness file, or
stdout) that is withheld while the world thread hangs. See [runtime health watchdogs](../ops/watchdog.md).

Latency and rates: game masters get full percentile lines (p50 to p99.9, mean, stddev) for the tick body and frame over the last 1 and 15 minutes in `.server info`; the world also logs a latency summary at Information level every 5 minutes (character saves, SQLite checkpoints, path queries, login to in-world, slowest packet handlers). The fault and anomaly ledgers report rates per hour (last 15 minutes and last hour), first/last seen and the top subjects; see [operations and performance](../areas/ops-perf.md).

Both daemons log through the `Logging:ArcaneCore` provider: a colour console by default (plain automatically when stdout is a pipe or `NO_COLOR` is set;
`Logging:ArcaneCore:Console:Mode=Plain` for journald), an optional rolling log file (`Logging:ArcaneCore:File:Enabled`), one file per daemon, as JSON lines or plain text
(`Logging:ArcaneCore:Format`, default `Json`, which suits a log shipper). The file keys need a restart; the console mode and the `Logging:LogLevel` rules reload when the file changes.
See [logging](../ops/logging.md).

## Release caveats

Decide on each of these before exposing a realm to the internet. **Default is** says whether the shipped default is the retail (vmangos) behaviour; where it is
not, the "what to do" column says how to get retail. The defaults in this table are checked against the code by a test, so a changed default fails until this page is reviewed.

<!-- register:begin (generated by DeviationRegister; edit the register, not this table) -->
| Key | Default | Default is | Retail (vmangos) | What to do |
|---|---|---|---|---|
| `Auth:MaxConnections` | `0` | retail | No vmangos equivalent; unlimited. | Set a cap on an internet-facing realm. |
| `Auth:MaxConnectionsPerIp` | `0` | retail | No vmangos equivalent; unlimited. | Set a per-address cap on an internet-facing realm. |
| `World:MaxConnections` | `0` | retail | No vmangos equivalent; unlimited. | Set a cap on an internet-facing realm (restart to change). |
| `World:MaxConnectionsPerIp` | `0` | retail | No vmangos equivalent; unlimited. | Set a per-address cap on an internet-facing realm (restart to change). |
| `Auth:ReadTimeoutSeconds` | `0` | retail | vmangos has none (slow-read clients are not cut off). | A positive value cuts a client that trickles a logon packet. |
| `Auth:StrictUsernameCharset` | `false` | retail | Retail accepts any bytes in a name. | True rejects names outside printable ASCII. |
| `Auth:AutocreateAccounts` | `false` | retail | vmangos has no auto-create; accounts are created with the account command. | Leave off. When on, an unknown account is created on first login with the password equal to the name. |
| `World:WriterDrainGrace` | `00:00:00` | **not retail** | vmangos does not wait for a closing connection's writes: `CloseSocket` shuts the socket down and closes it at once (AsyncSocket_windows.cpp:318-329), dropping what is still queued. | `00:00:00` is the built-in 5 s bound: queued frames (a refusal reply) get up to 5 s to reach the client, then the stream is aborted, so a client that stopped reading cannot hold a connection. No value waits forever; a shorter positive value comes closer to retail. |
| `World:PreAuthTimeout` | `00:00:10` | retail | Retail rule: vmangos Network.TimeoutSecsIfNoAuth = 10 (WorldSocket.cpp:621-628). | Leave at the retail value; 00:00:00 disables it. |
| `World:MaxQueuedWorldPackets` | `8192` | **not retail** | vmangos queues without a bound (WorldSession.cpp:307). | On by default as hardening; 0 restores the retail unbounded queue. |
| `World:MaxQueuedWorldBytes` | `8388608` | **not retail** | vmangos queues without a bound. | On by default as hardening (8 MiB); 0 restores the retail unbounded queue. |
| `World:Social:MaxJoinedChannels` | `0` | retail | vmangos has no channel cap. | A positive value caps channels per player. |
| `Auth:IpBanCacheSeconds` | `60` | **not retail** | vmangos realmd reads `ip_banned` on every logon challenge (AuthSocket.cpp:338-352); mangosd keeps the same list in memory, reloaded every 60 s. | On by default so a reconnect flood does not cost one query per connection; a ban written elsewhere reaches the logon screen within the period (world login refuses it at once), and an unban written elsewhere also takes up to one period to let the address back in. If the list cannot be loaded, challenges fall back to the per-challenge row read for one period. Set 0 for the per-challenge read. |
| `Bans:RecheckIntervalSeconds` | `60` | **not retail** | Retail: a ban row written by another process does not kick a connected account (mangosd only reloads its IP-ban cache every BanListReloadTimer = 60 s). | On by default so `arcane-account` and SQL bans reach connected players within a minute; set 0 for exact retail. |
| `Bans:RevokeSessionKeyOnBan` | `false` | retail | Retail keeps the session key and relies on the ban check at world login. | True also revokes the key, so a banned client's reconnect is refused earlier. |
| `Bans:RejectUnparseableDuration` | `false` | retail | Retail turns a malformed duration into a PERMANENT ban. | True refuses a malformed duration instead; set it unless you want exact retail behaviour. |
| `Bans:ProtectHigherSecurity` | `true` | **not retail** | Retail has no hierarchy guard on `.ban account` / `.ban character`. | On by default; set false for exact retail. |
| `World:GmCommands:LowerSecurity` | `true` | **not retail** | vmangos GM.LowerSecurity defaults to 0 (mangosd.conf.dist.in:2536), which lets staff act on a higher account. | On by default (stricter); set false for exact retail. |
| `HotReload:Commands` | `false` | development only | Retail always registers its reload commands. | Leave off in production; it exposes `.reload` to administrators. |
| `World:HotCode:Enabled` | `false` | development only | Not a retail feature. | Accepted only in Development or Staging; the daemon refuses to start otherwise (exit code 78). |
| `World:HotCode:Modules:Enabled` | `false` | development only | Not a retail feature. | Loads code from disk into the daemon; leave off in production. |
| `World:ListenRangeSay` | `25` | retail | vmangos code default 25 (World.cpp:556) but its shipped mangosd.conf.dist.in sets 40 (:1559); a stock vmangos realm therefore says 40. | Set 40 to match a stock vmangos realm. |
| `World:ListenRangeTextEmote` | `25` | retail | vmangos code default 25 (World.cpp:558) but its shipped mangosd.conf.dist.in sets 40 (:1560). | Set 40 to match a stock vmangos realm. |
| `World:Maps:GridUnload` | `true` | retail | vmangos code default true (World.cpp:582) but its shipped mangosd.conf.dist.in sets 0 (:399). | Set false to keep grids loaded as the shipped vmangos configuration does. |
| `Database:Upgrade:Policy` | `Always` | **not retail** | vmangos never creates or upgrades a database on start. | Use `CreateOnly` or `Never` once the databases exist and upgrade with `arcane-db`. |
| `Net:Protection:MaxConnectionsPerIp` | `16` | **not retail** | No vmangos equivalent; unlimited. The daemon caps `Auth:`/`World:MaxConnectionsPerIp` stay 0, this shared cap is what applies. | On by default as hardening: 16 simultaneous connections per client address on each listener, so a LAN party or campus NAT with more players than that behind one address is cut off at the 17th. A listener bound to loopback does not cap loopback clients (`ExemptLoopbackOnLoopbackBind`, default true; the owner's client and local crowd tests); `ExemptAddresses` lists further owner addresses or networks. Set 0 to restore the retail unlimited behaviour (restart to change). |
| `Net:Protection:WorldOpcodeBurst` | `4000` | **not retail** | vmangos handles every packet a world client sends; it has no per-opcode budget. | On by default as hardening, far above a retail client (an empty client cache sends a few hundred queries of one opcode at once; a 3000-packet burst after a stall stays untouched): a packet over its opcode's bucket (refilled at `WorldOpcodeRefillPerSecond`, 1000) is dropped and counted, the connection stays. Set 0 for retail. |
| `Net:Protection:WorldFloodPacketsPerSecond` | `8000` | **not retail** | vmangos never disconnects for the packet rate (only its unbounded receive queue grows). | On by default: more than 8000 packets in one second close the connection (`WorldPacketsPerSecond`, 4000, drops the excess first). Set both 0 for retail. |
| `AntiCheat:Enabled` | `true` | **not retail** | vmangos ships its movement anticheat off (`Anticheat.Enable = 0`, mangosd.conf.dist.in:2306); a retail server checks nothing. | On by default in log-only mode (`AntiCheat:Action` = `Log`): findings are scored, logged and written to `character_anticheat_log`, nothing is ever done to a player. Set false for retail; raise `AntiCheat:Action` (GmAlert, Rubberband, Kick) and turn on `AntiCheat:Autoban:Enabled` to act (docs/areas/anticheat.md). |
| `Creatures:Movement:MissingWaypointPathFallback` | `Random` | **not retail** | A waypoint creature without a path stands still (vmangos WaypointMovementGenerator::LoadPath logs and keeps an empty path, Movement/WaypointMovementGenerator.cpp:47-52). | On by default so a content gap does not freeze a patrol: the creature wanders within its spawn distance. Set `Idle` for exact retail; the server logs the affected spawns once at load either way. |
| `Creatures:NoMeleeFleeOnAggro` | `false` | retail | vmangos only takes the melee away from a creature with static flag NO_MELEE (0x00100000, original comment "Flee", AI/CreatureAI.cpp:40); critters still run from a hit through CritterAI. cmangos also sends any such creature running for 30 s when a player engages it (Unit.cpp:7993-7998). | Leave off for vmangos. True turns on the cmangos panic flight (30 s, `Creatures:NoMeleeFleeMs`, then an evade). |
| `Locomotion:PlayerSpeedRate` | `1` | retail | vmangos has no player speed rate: speeds come only from auras and the ghost rates (Unit::UpdateSpeed, Unit.cpp:6959-7061). The MaNGOS Zero fork's Movement.PlayerSpeedRate (percent) is the model. | Leave at 1 for retail. Any other value (0.1 to 10) speeds or slows every player's movement and is a visible non-retail change; the per-type `Locomotion:Player{Run,RunBack,Swim,SwimBack,Walk}SpeedRate` multiply on top. Live: a configuration reload or `.movement set` re-sends every online player's speeds (docs/areas/rates.md). |
| `Locomotion:PlayerTurnRate` | `1` | retail | Retail turns at 3.141594 rad/s (vmangos baseMoveSpeed[MOVE_TURN_RATE], Unit.cpp:67-74); no 1.12 aura or vmangos option changes it. | Leave at 1 for retail; other values (0.1 to 10) change how fast players turn with the keyboard. |
| `World:Honor:Rate` | `1` | retail | vmangos has no honor rate; the MaNGOS Zero fork and mangos-classic declare Rate.Honor but never read it. | Leave at 1 for retail. Other values scale the honor earned from kills, battleground bonuses and quests (not dishonor or `.honor add`), which moves the weekly standings. |
| `World:GmCommands:LiveFx` | `true` | **not retail** | vmangos has no `.fx` root: its nearest commands are debug play music, sound and cinematic and debug worldstate, each sent to the invoker only (Chat.cpp:288-323). | On by default: non-retail GM tooling that pushes music, sounds, spell visuals, cinematics, zone-under-attack alerts, world states, client clock speed, screen text, multi-zone weather and event presets to the invoker, the selection, the zone, the map or the server. GameMaster accounts; the map and server scopes and `.fx weather` need an Administrator; every use is in the GM audit log. Set false to leave the root out (restart to change). |
| `World:RemoteAdmin:Enabled` | `true` | **not retail** | vmangos ships its remote administration console off (`Ra.Enable = 0`, mangosd.conf.dist.in:3087) and, once enabled, listening on every address (`Ra.IP = 0.0.0.0`, :3088). | On by default for local tools and agents, bound to 127.0.0.1 (`World:RemoteAdmin:BindAddress`). RA is clear text, passwords included: set false where no local tool needs it, and never bind it to a public address (docs/ops/remote-admin.md). |
<!-- register:end -->

One thing that vmangos has and ArcaneCore does not, so it is among the limits and not options: the **wrong-password throttle** of the logon server (vmangos
`WrongPass.MaxAttempts = 10` per 60 seconds, realmd.conf.dist.in:208-209), which means a password can be guessed without a delay; put the realm port behind a
firewall or rate limiter if it is exposed. The other open security items are in the
[hardening notes](../security/hardening.md).

MySQL, PostgreSQL and SQLite are accepted `Provider` values, but the provider test matrix that proves MariaDB and PostgreSQL behaviour runs only on the hosted CI, not on a
developer machine; treat any engine you have not run the test suite against yourself as unproven.

## Configuration files versus retail

A vmangos `mangosd.conf` or `realmd.conf` is replaced by `appsettings.json` and environment variables; the mapping of retail keys to ArcaneCore keys is in the
`Meaning` column of the configuration reference wherever an option has a retail counterpart (for example `World:AutosaveIntervalMs` is `PlayerSave.Interval`). ArcaneCore does not read
`mangosd.conf` or `realmd.conf`.

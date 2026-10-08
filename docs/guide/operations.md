# Operating a realm

How to run, stop, back up, upgrade and watch an ArcaneCore realm, and the release caveats you should decide on before exposing it. For the first
installation see [installation and first run](installation.md). Every key named here is in the generated [configuration reference](../reference/configuration.md).

## Start, stop and restart

Start the realm daemon, then the world daemon (`dotnet run --project src/ArcaneCore.Realm` and `src/ArcaneCore.World`). Run the world daemon's
`check-config` verb before a deployment; it exits with 78 when the configuration is invalid and the daemon runs the same check at every start.

Stop or restart the world from an in-game administrator account with the retail countdown commands: `.server shutdown`, `.server restart`
(each takes `cancel` or a delay and an optional exit code) and the `.server idleshutdown` / `.server idlerestart` forms. They need the Administrator
account level (see the [GM command reference](../reference/gm-commands.md)). A restart ends the process with exit code 2, and a supervisor that
restarts on 2 reproduces the vmangos run-mangosd loop; a configuration error exits with 78 and must not be restarted. All exit codes are in
[exit codes](../reference/exit-codes.md). Characters are saved every `World:AutosaveIntervalMs` (default 900000 ms; 0 disables),
and `.saveall` saves everyone at once.

There is no server-console command sender: commands that vmangos can run from its console only run from an in-game account here. For account, password and
ban work without a game client use the `arcane-account` tool.

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

## Bans

Ban from in-game with `.ban account`, `.ban character` and `.ban ip`, and lift with `.unban`; `.baninfo` and `.banlist` read the history. Without a client use
`arcane-account ban <username> <duration|0> <reason>`, `unban`, `baninfo` and `banlist`. A duration is `1d2h3m4s`; `0`, and (unless
`Bans:RejectUnparseableDuration` is on) any string that does not parse, is a permanent ban, exactly as in retail. A ban made by the account tool is a database
row: a connected player is disconnected by the periodic re-check (`Bans:RecheckIntervalSeconds`, 60 seconds by default); with it set to 0 the ban applies at
that account's next login. The ban tables,
IP bans and the live enforcement options are described in [live bans](../security/live-bans.md).

## Monitoring

The world daemon logs slow updates through the `PerformanceLog` section (`SlowWorldUpdate`, `SlowMapUpdate`, `SlowPackets`, in milliseconds; 0 disables a
threshold), tagged with the `Perf` event id so a log sink can route them to their own file. `.server info` shows the version, the players online and the uptime.
See [operations and performance](../areas/ops-perf.md). Both daemons also run the `Ops:Watchdog` monitors on a dedicated thread: rate-limited
warnings on world-tick overruns with percentiles, a critical line on a hung tick, heap high-water marks and memory-pressure warnings, a
thread-pool starvation probe, a periodic counter dump and a supervisor heartbeat (systemd `Type=notify` with `WatchdogSec`, a liveness file, or
stdout) that is withheld while the world thread hangs. See [runtime health watchdogs](../ops/watchdog.md).

Both daemons log through the `Logging:ArcaneCore` provider: a colour console by default (plain automatically when stdout is a pipe or `NO_COLOR` is set;
`Logging:ArcaneCore:Console:Mode=Plain` for journald), an optional rolling text file (`Logging:ArcaneCore:File:Enabled`) and an optional JSON-lines file
(`Logging:ArcaneCore:Json:Enabled`) for a log shipper. File paths need a restart; the console mode and the `Logging:LogLevel` rules reload when the file changes.
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
| `Net:Protection:MaxConnectionsPerIp` | `16` | **not retail** | No vmangos equivalent; unlimited. The daemon caps `Auth:`/`World:MaxConnectionsPerIp` stay 0, this shared cap is what applies. | On by default as hardening: 16 simultaneous connections per client address on each listener, so a LAN party or campus NAT with more players than that behind one address is cut off at the 17th. Set 0 to restore the retail unlimited behaviour (restart to change). |
| `Creatures:Movement:MissingWaypointPathFallback` | `Random` | **not retail** | A waypoint creature without a path stands still (vmangos WaypointMovementGenerator::LoadPath logs and keeps an empty path, Movement/WaypointMovementGenerator.cpp:47-52). | On by default so a content gap does not freeze a patrol: the creature wanders within its spawn distance. Set `Idle` for exact retail; the server logs the affected spawns once at load either way. |
| `Creatures:NoMeleeFleeOnAggro` | `false` | retail | vmangos only takes the melee away from a creature with static flag NO_MELEE (0x00100000, original comment "Flee", AI/CreatureAI.cpp:40); critters still run from a hit through CritterAI. cmangos also sends any such creature running for 30 s when a player engages it (Unit.cpp:7993-7998). | Leave off for vmangos. True turns on the cmangos panic flight (30 s, `Creatures:NoMeleeFleeMs`, then an evade). |
<!-- register:end -->

Two things that vmangos has and ArcaneCore does not, so they are limits and not options: the **wrong-password throttle** of the logon server (vmangos
`WrongPass.MaxAttempts = 10` per 60 seconds, realmd.conf.dist.in:208-209), which means a password can be guessed without a delay; put the realm port behind a
firewall or rate limiter if it is exposed. And the **console command sender** (vmangos marks commands `AllowConsole`, Chat.h:66-70). The other open security items are in the
[hardening notes](../security/hardening.md).

MySQL, PostgreSQL and SQLite are accepted `Provider` values, but the provider test matrix that proves MariaDB and PostgreSQL behaviour runs only on the hosted CI, not on a
developer machine; treat any engine you have not run the test suite against yourself as unproven.

## Configuration files versus retail

A vmangos `mangosd.conf` or `realmd.conf` is replaced by `appsettings.json` and environment variables; the mapping of retail keys to ArcaneCore keys is in the
`Meaning` column of the configuration reference wherever an option has a retail counterpart (for example `World:AutosaveIntervalMs` is `PlayerSave.Interval`). ArcaneCore does not read
`mangosd.conf` or `realmd.conf`.

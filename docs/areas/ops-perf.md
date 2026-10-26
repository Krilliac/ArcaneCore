# Operations and performance (ops-perf lane)

Delivered scope, limits and provenance. Reference sources are read-only GPL projects used to
verify behaviour; nothing is copied. `<refs-root>\vmangos` is the primary reference.

## Load benchmark (2026-10-08)

Bot-think allocations (2026-10-19): the quest-candidate scan's spawn gate, `CanTakeQuest` and `IsRewardable` no longer allocate
(closures, delegates and LINQ enumerators replaced by loops). The 200-bot benchmark's bot feature dropped from about 1,260 to 947 KiB
per tick. `tools/perf` adds `QuestCandidateBenchmarks` and `CreatureTickBenchmarks`. See the [lane report](../integration/perf-tick-20261019.md).

Bot-think scans (2026-10-20): the death-loop check of target choice, the defensive-target pick and the quest-travel ranking
(`PlayerbotWorldDestinations.HasQuestCandidate` / `Update`) no longer allocate per think. At 400 bots the bot feature dropped from
about 2,080 to 1,320 KiB per tick, with a third fewer gen0 collections. `tools/perf` adds `PlayerbotHazardBenchmarks`. See the
[lane report](../integration/perf-tick-20261020.md).

`ActiveCellReactivationTests` adds a manual-clock reactivation workload (2026-10-11): players leave loaded patrol cells for ten
minutes and return together. It separates creature-update and path-search time from grid loading. Dormant splines now exclude
inactive time, preventing simultaneous false waypoint arrivals and next-leg path searches. This reproduces a reactivation burst,
but does not establish the cause of the reported live 6.1 s TickHang. See the [reactivation report](../integration/active-cell-reactivation-20261011.md).

`WorldTickLoadTests` measures 66k synthetic units, six moving simulated players, two ships and optional real
terrain/vmap/mmap queries with manual world time. It records per-phase elapsed time, per-tick allocations and
GC deltas to CSV. Player visibility candidate buffers and heartbeat membership snapshots now reuse storage;
visible sets, ordering and heartbeat timers are preserved. See the [lane report](../integration/perf-tick-20261008.md)
for before/after runs, regression evidence and limits. This does not establish that the live 177 ms p99 is fixed;
cold collision loading and full managed-bot/database workloads remain outside the optimization.

## Limiters, tick timing and GC (2026-10-08)

After the live stress test: the world loop waits on a high-resolution timer (`World:TickTimer`, late only beyond
`World:TickLateToleranceMs`), `.server info` reports the tick and frame medians, frame p90, allocation per phase and

Latency detail (2026-10-15): game masters see six more `.server info` lines (tick body and frame latency over the last 1 and 15 minutes with p50..p99.9, mean and stddev, the per-phase p50/p99, and the slowest other histograms: character save, SQLite checkpoints, navigation path queries, login to in-world, the three slowest packet handlers); players see only the original ten. `LatencySummaryFeature` logs the same histograms at Information level every 5 minutes (`Latency <metric>, last 15 min: ...` and `Packet handler latency, top N opcodes by p99`). The fault and anomaly ledgers carry first/last seen, the rate per hour over the last 15 minutes and 1 hour, inter-arrival p50/p99, the top subjects or messages and the three most recent distinct details; they appear in the log lines, `.server fault` / `.server anomalies`, the crash report and the QA report (`report.md`, `report.json` and the chat top list also give rate per hour, first/last seen and bots affected).
feature and the garbage collector, the World runs Server GC with background collections, and the managed-bot and
per-address connection caps were reworked. Measurements and limits: [lane report](../integration/perf-limits-20261008.md).

## Delivered

### 1. Tick statistics and the slow-update log

- `TickStats` (`Game/Maps/TickStats.cs`): ring of the last 4096 world ticks (duration in
  microseconds, bytes allocated on the world thread), overrun counter (tick strictly longer than
  `TickIntervalMs`), heartbeat timestamp, nearest-rank percentiles (p50/p90/p99/p99.9/max/mean).
  `Record` does not allocate; `Snapshot` copies the ring under a short lock so any thread may
  read it. An empty snapshot has `Samples == 0` (its percentiles are 0 and must not be read as
  measurements); `NearestRank` throws on empty input.
- `WorldRuntime.Stats` is recorded by the world loop (`Run`) only. `RunTick` called directly
  (tests) records nothing.
- `PerformanceLog` section (`SlowWorldUpdate`, `SlowMapUpdate`, `SlowPackets`, milliseconds, 0
  disables): defaults 100/100/20 from vmangos `mangosd.conf.dist.in:898-906`. Entries carry
  `EventId` name `Perf` (`PerformanceLogOptions.PerfEventId`) so a file sink can route them to a
  Perf log. SlowWorldUpdate defaults to retail: the interval between two frames,
  sleep included, checked at the start of the next frame, text "Slow world update: Nms"
  (`WorldRunnable.cpp:60-74`). `PerformanceLog:SlowWorldUpdateMeasure=TickDuration` is the
  opt-in deviation that logs only the tick body's duration (and the tick interval).
  Slow packets are timed around the in-world handler call (`WorldSession.cs`,
  vmangos `WorldSession.cpp:620`).
- By default slow world frames and slow map updates are not one line each: `PerformanceLog:SummaryIntervalSeconds` (60) writes ONE
  `Slow update summary` per interval, and only when something was slow (world frames over the threshold as N of M with p50/p95/max; per map
  instance the count, p50/p95/max of its own work and the top three contributors by summed ms; the host-starved / quiet / gc verdict counts;
  the number of suppressed detail lines). Verdicts are host-starved, quiet and gc (gc-pauses and gc-paging are both counted as gc), plus process-busy, process-stalled, memory-starved and process-blocked when they occur; `unknown`
  (the old "other") counts samples with no host sampler or no verdict. The label is the real elapsed time (a stop or a hitch gives a different
  length), the contributor table holds 64 names per map and prints `(+N ms unattributed)` past that, and the detail cap restarts at each interval. An update over `SlowWorldUpdateDetail` (250) / `SlowMapUpdateDetail` (200) ms still gets its own
  line, at most `DetailLinesPerMapPerInterval` (1) per map instance (and for the world) per interval. `SummaryIntervalSeconds = 0` restores
  one vmangos-style line per slow update. The aggregation is `PerfLogAggregator` (world thread, no allocation per sample once a map is known).
- A slow map update lists its phases (simulation, visibility, values, flush, cleanup) and
  counters: players, moved objects, changed objects, new objects and `visibility candidates`
  (how many viewer/target candidates the visibility phase evaluated). A visibility phase with a
  large candidate count per moved object is the shape of the creature-mover hot path fixed in
  docs/areas/grid-terrain.md ("Visibility on cells").

### 2. Retail shutdown and restart

- `ShutdownCountdown` (`World/Ops/Lifecycle`): pure state machine of vmangos
  `World::ShutdownServ` / `_UpdateGameTime` / `ShutdownMsg` / `ShutdownCancel`
  (`World.cpp:2665-2767`). Announcement cadence: the request itself, every second below 10 s,
  every 5 s below 30 s, every minute below 5 min, every 5 min below 30 min, every hour below
  12 h, every 12 h above 12 h (exactly 12 h is announced by neither rule, as in vmangos).
  Idle mode never announces and pins the timer at 1 while sessions remain.
- `ServerTimeText` is vmangos `secsToTimeString` (`Util.cpp:197-243`) including its quirks
  ("1 Minute " with a trailing space, "0 Second.").
- `SMSG_SERVER_MESSAGE` (0x291): u32 type (1 shutdown, 2 restart, 3 custom, 4 shutdown
  cancelled, 5 restart cancelled) plus CString (wow_messages `smsg_server_message.wowm`), sent to
  players in the world only (vmangos `SendGlobalMessage`, `World.cpp:2152-2166`).
- Commands, all Administrator (vmangos `Chat.cpp:946-997`): `.server shutdown|restart|idleshutdown|idlerestart [cancel | delay [exitcode]]`
  (`ServerCommands.cpp:409-502`; exit code at most 125). Defaults: shutdown 0, restart 2.
- On expiry `ExitCodes.Current` is set and `IHostApplicationLifetime.StopApplication()` is
  called; the existing `WorldHost.StopAsync` order (world stop and final save, features, save
  queue) runs, so no second shutdown path exists. `Program.cs` returns `ExitCodes.Current`.
- `HostOptions` is bound from configuration. The shipped `HostOptions:ShutdownTimeout` (one minute) is a signal, not a cut-off: `Host.StopAsync` cancels the token it hands to each hosted service after that time and then keeps awaiting the service, and `WorldHost` does not forward the token to the write queues, so a large save drain is never cut short by the host. What bounds the stop is the write queues' own stall budget (`ShutdownDrain`, completed attempts that landed nothing: 10 s on the feature lane, 15 s on the save lane); a healthy drain runs to its end, a stalled one ends within the budget plus one in-flight attempt per lane (at most 30 s and 35 s, 65 s together, plus one in-flight attempt per queue that was mid-write when its stop began). See docs/areas/persistence.md, "Other queues".
- Timer lifetime: the one second timer runs only while a countdown is pending. Start and end
  are decided on the world thread alone (`AfterChange`; `Cancel` also ends the timer), each run
  has an id and a tick from an ended or replaced run is ignored, so cancel followed at once by a
  new request cannot leave a countdown without a timer. Tested on a real world thread with the
  real timer (`RealTimer_*`, `CancelThenRestart_*` in `ServerLifecycleTests`). The process exit
  code of the built executable after a restart countdown is still not run end to end.

### 3. Configuration validation

- `check-config` verb (`ArcaneCore.World.exe check-config`) and the same validation at every
  start: all problems are listed with key, reason, fix and the environment-variable spelling
  (`World__Port`). Exit 78 when invalid (errors, or warnings when `Startup:Strict=true`).
  Checks: bind address, ports, tick length, characters per realm, autosave and compression
  values, performance-log thresholds, data directories that must exist when set, per-database
  provider/connection-string shape, default `arcane/arcane` credentials against a non-loopback
  database host or world listener. Messages never contain connection strings.
- Exit-code contract (`Kernel/Ops/ExitCodes.cs`): 0 stop, 1 failure, 2 restart (vmangos
  `World.h:76-81`), 78 invalid configuration (ArcaneCore, sysexits EX_CONFIG: supervisors must
  not restart on it), 64 usage error. Verified by running the built executable: `check-config`
  valid 0, `--World:Port=0` 78, unknown verb 64.

## Shared-file edits (small and additive; for the integrator)

- `WorldRuntime.cs`: `Stats`, timing in `Run`, per-map timing in `RunTick`; `WorldRuntime.Perf.cs`: `LogIfSlow`.
- `WorldRuntimeOptions.cs`: `Perf`. `WorldServiceCollectionExtensions.cs`: one `PostConfigure` line.
- `WorldSession.cs`: `ProcessWorldPackets` times the handler; `LogIfSlowPacket`.
- `BuiltinCommands.cs`: `.. ServerLifecycleCommands.Children` in the `server` group.
- `Program.cs` (World): verbs, startup validation, `HostOptions` binding, `return ExitCodes.Current`.
- `appsettings.json` (World): `HostOptions`, `PerformanceLog`.

## Limits and not done

- Slow-packet logging has no dedicated test (the shared code path with the slow-map log is tested).
- Restart/shutdown exit codes through a real host stop (`StopApplication` to process exit) were not
  run end to end; the countdown and `ExitCodes.Current` are tested, `Program.cs` returning it was
  verified only for the verb paths.
- `.server exit`, `.server plimit`, `.server set motd`, the retail `.server info` text, and the
  retail `.announce` and `.notify` strings (vmangos `ServerCommands.cpp:46-66, 302-316`) were not
  changed in this lane: the first three needed a console sender or the login queue, the rest sit in
  `BuiltinCommands.cs`, which the gm-commands lane owns. Since then the
  [server console](server-console.md) runs commands without a character, `.server set motd` (GM
  lane, `Gm/Server/ServerCommands.cs`) among them; `.server exit` and `.server plimit` still do not
  exist (the console's `quit` and `exit` run `.server shutdown 0`).
- Remote administration console, metrics and health endpoint, file logging, packaging, backup and
  restore, staggered autosave, the login queue and the optimisation slices were not started in this
  lane; no performance numbers are claimed by this lane. Delivered since by other lanes:
  [remote administration](../ops/remote-admin.md), [metrics](../ops/metrics.md) (`Ops:Metrics`, off
  by default), the rolling log file ([logging](../ops/logging.md)), Docker packaging
  ([Running in Docker](../guide/docker.md)), backup support
  ([database upgrade tooling](../ops/database-upgrade.md#backups): `arcane-db backup-info` prints
  the dump command for each database, and `arcane-db upgrade --backup-dir` writes a verified copy
  of a SQLite database; there is no restore command) and staggered autosave
  ([persistence](persistence.md#autosave-one-timer-per-character)). There is no health endpoint;
  the liveness signal for a supervisor is the
  [watchdog heartbeat](../ops/watchdog.md#4-heartbeat-opswatchdogheartbeat).
- Database reachability is not probed by `check-config`.
- Real 1.12.1 client display of the countdown was not checked.

## Wave-2 integration note
The GM-commands lane had a second shutdown implementation (`ShutdownScheduler`, `ShutdownFeature`, `.server shutdown|restart|idle*`).
This lane's `ServerLifecycleFeature`/`ShutdownCountdown`/`ExitCodes` is the single implementation (it owns the process exit code).
The GM lane keeps `.server set motd` and `ServerStats`. `Program.cs` runs the ops verbs and config validation first, then the
code hot-reload launch gate. The retail `.server`/`.announce` texts live in `BuiltinCommands` (GM lane) as before.

## Creature path-search budget

`CreatureMapSystem.PathBudget` (`CreaturePathBudget`, default 64 searches / 16 384 A* nodes per map update) bounds creature path searches on the world thread. Chase/follow re-paths over budget are deferred to the next update (the creature keeps its spline); other requests (waypoint, flee, home, formation, confused) take the straight line. Before: one search (up to 2 048 nodes) per chaser per update, unbounded; after: at most 64 searches and 16 383 + 2 048 nodes. Tests: `CreaturePathBudgetTests`.

Deferral is fair: the creature walk of the next update starts at the creature deferred first, so deferred chasers are served before ones already served and no chaser starves behind the same early creatures (with a moving target all 40 chasers of the test get a path within 15 updates (the asserted bound) at budget 8; a fixed order served only 16). The budget is per map update (`CreatureMapSystem`), so the world-thread bound is maps x budget when maps update one after another; the 6.1 s TickHang bound is not global. Straight-line fallback for waypoint, flee, home, point, formation and confused moves is a deliberate degradation: it can cut through walls or terrain, and vmangos goes straight only on NOPATH, not on budget exhaustion. Searches that run no A* (no navigation data) do not count against the budget.

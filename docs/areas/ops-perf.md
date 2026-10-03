# Operations and performance (ops-perf lane)

Delivered scope, limits and provenance. Reference sources are read-only GPL projects used to
verify behaviour; nothing is copied. `D:\refs\vmangos` is the primary reference.

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
- `HostOptions` is now bound from configuration; the shipped `HostOptions:ShutdownTimeout` is one
  minute so a large save drain is not cut short by the host default.
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

- `WorldRuntime.cs`: `Stats`, timing in `Run`, per-map timing in `RunTick`, `LogIfSlow`.
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
  retail `.announce` and `.notify` strings (vmangos `ServerCommands.cpp:46-66, 302-316`) are not
  changed: the first three need a console sender or the login queue, the rest sit in
  `BuiltinCommands.cs`, which the gm-commands lane owns. Remote administration console, metrics and
  health endpoint, file logging, the bot-swarm perf harness and baselines, packaging, backup and
  restore, staggered autosave, the login queue and the optimisation slices were not started; no
  performance numbers are claimed by this lane.
- Database reachability is not probed by `check-config`.
- Real 1.12.1 client display of the countdown was not checked.

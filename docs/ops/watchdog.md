# Runtime health watchdogs (`Ops:Watchdog`)

Both daemons run one dedicated monitoring thread that watches the process from outside its hot paths: the world tick (world daemon),
the managed heap, the thread pool, a supervisor heartbeat and a counters registry other lanes plug into. Everything here is passive by
default (it logs); the only active behaviour, the memory-pressure action, is opt-in. There is no vmangos equivalent beyond the per-frame
"Slow world update" line (`WorldRunnable.cpp:60-74`), so nothing in this page claims retail fidelity; it is operations tooling.

Code: `src/ArcaneCore.Kernel/Ops/Watchdog` (everything but the world feed), `src/ArcaneCore.Kernel/Configuration/WatchdogOptions.cs`,
`src/ArcaneCore.World/Ops/Watchdog` (the tick feed and the world wiring), `src/ArcaneCore.Realm/Ops` (the realm wiring).
Tests: `tests/ArcaneCore.Realm.Tests/Watchdog` (the Kernel classes, with a fake clock; a Kernel test project did not exist when this
lane was written, and the realm test project is the lightest one that references Kernel) and `tests/ArcaneCore.World.Tests/Ops/TickWatchdogFeatureTests.cs`
(the feed on a real world thread). Every key below is in the generated [configuration reference](../reference/configuration.md).

## Wiring

- World: `builder.Services.AddWorldWatchdog(builder.Configuration);` (one line in `Program.cs`; the extension lives in the
  `Microsoft.Extensions.DependencyInjection` namespace, which `Program.cs` already imports). It binds the section and registers the
  shared monitors plus the tick monitor. `TickWatchdogFeature` is an ordinary `IWorldFeature`, discovered like every other feature; it
  attaches nothing when the tick monitor is not registered (test hosts) or `Ops:Watchdog:TickMonitor:Enabled` is false.
- Realm: `builder.Services.AddRealmWatchdog(builder.Configuration);`. Memory, thread pool, heartbeat and counters; there is no tick.
- `check-config` and the world daemon's start-up report include the watchdog rules (`WatchdogOptionsValidation`), so a bad value is one
  of the listed problems and exit code 78, not a binder exception. Both daemons also validate the bound options at host start
  (`ValidateOnStart`), which stops the realm daemon, whose start-up has no report, before it binds a port.
- `Ops:Watchdog:Enabled=false` starts no thread and runs nothing in this section, including the heartbeat.

## Thread ownership and cost (the contract)

- **The watchdog thread** (`WatchdogHost`, name `watchdog`, above-normal priority, a `ManualResetEventSlim.Wait` loop, never a timer) runs
  every monitor's `Check` once per `CheckIntervalMs`. It is a plain thread so that it keeps running when the thread pool is starved
  (the thing the probe measures) and when the world thread hangs (the thing the tick monitor measures). A monitor that throws is logged
  (rate-limited per monitor, counted in `watchdog.monitor_faults`) and the round goes on.
- **The world thread** touches the watchdog in exactly one place: the `WorldRuntime.TickStarting` handler of `TickWatchdogFeature`, which calls
  `TickMonitor.OnTick`. That is one clock read and two to five plain stores (a ring slot, the ring count, the last-start stamp, the overrun
  and hang counters): no allocation, no lock (a frame over `HangMs` also takes the feature's `_hungGate` lock and allocates one small record, see below), no interlocked instruction, no log. `TickMonitorTests.OnTick_DoesNotAllocate` asserts a zero
  `GC.GetAllocatedBytesForCurrentThread` delta over 100 000 ticks. The one-writer rule is what makes the plain stores correct: only the world
  thread writes those fields, the watchdog thread reads them with volatile loads.
- **The ring** (`TickRing`) is lock-free for its one writer and any number of readers. A writer publishes a slot with a release store of the
  count; a reader copies and then re-reads the count to drop every slot the writer may have overwritten meanwhile, so a snapshot is never
  torn or mixed between generations (`TickRingTests.Snapshot_ConcurrentWithTheWriter_...`). Snapshots go into a pooled buffer on the
  watchdog thread (`ArrayPool<long>`), sorted in place for nearest-rank percentiles.
- **Counters** (`Counter`) are one interlocked instruction per increment, on any thread, with the value padded to its own cache line so two hot
  counters never false-share. `CounterRegistry.Counters` is one volatile load of an immutable array: enumerating it allocates nothing and
  never blocks a registration (copy on write under a lock, the cold path).
- **Logging** happens only on the watchdog thread and only through rate limiters (`LogRateLimiter`: one line per interval, the rest counted
  and reported as "N suppressed"), so a storm of overruns is one warning, not a flood. Formatting a log line allocates; that is the
  watchdog thread's cost, never the world thread's.
- The tick-body percentiles in the frame lines come from `WorldRuntime.Stats` (the ops-perf lane's recorder). Its `Snapshot` takes that
  recorder's short lock to copy its ring; the world thread's `Record` can wait for that copy (a few microseconds) once per warning or
  summary, which is the only way the watchdog thread can touch the world thread's time. It never calls into the world.

## The monitors

### 1. World tick (`Ops:Watchdog:TickMonitor`, world daemon only)

The measure is the **frame time**: the interval between two consecutive tick starts, exactly vmangos' "Slow world update" measure. A tick that
fits its budget shows as the tick interval (sleep included), one that does not shows as its real length, so overruns and hangs are measured
exactly. The sub-budget distribution does not floor at the interval: the drift-compensated loop (`WorldTickScheduler`) follows an
oversleeping wait with a shorter one, and when the wait overslept a whole interval or more it starts the tick due now at once (as vmangos'
WorldRunnable skips the sleep after an overrun). A tick interval below the OS wait granularity (the 5 ms test hosts on the ~15.6 ms Windows
timer) therefore gives an even split of ~15.6 ms and ~0 ms frames, whose p50 can land on either side; the mean frame is the tick rate. `WorldRuntime.TickStarting` is raised as the first step of each
tick, so a frame is the previous tick's body plus the wait after it; the body percentiles of the runtime's own
recorder are appended to each line so both views are on one line.

- **Overrun**: a frame longer than `BudgetMs` (0 = twice the tick interval, 100 ms at the retail 50 ms tick, which tolerates sleep jitter).
  One `Warning` per `WarnIntervalSeconds` with the number of new overruns, the frame p50/p99, the slowest recent frame over the ring and the
  tick-body p50/p99/max (event `TickOverrun`, 7101). Overruns in between are counted and appear in the next line.
- **Hang**: a frame longer than `HangMs`. Two `Critical` lines (event `TickHang`, 7102): one while the tick is still running past the threshold
  (repeated every `WarnIntervalSeconds` while it stays hung), one when it completes, with the slowest recent frame. Never rate-limited away.
- **Hang detail** (`TickMonitor.HangDetail`, set by `TickWatchdogFeature`): the `TickHang` and `TickOverrun` lines end with a `; ` suffix that
  says *where* the time went, so a stall is attributable from the log alone. It is built on the watchdog thread, only when a line is about
  to be logged, and a failing callback shows as `; hang detail unavailable (<ExceptionType>)` instead of losing the line. Two modes:
  - **In progress** (the first hang line, while the tick is still running): `phase=<Phase>, running=<work> for <N> ms`. The phase is one of
    `Commands`, `WorldTick`, `Maps`, `Features`, `Autosave` (read from `WorldRuntime.CurrentPosition`, which the world thread publishes with
    volatile stores at each phase change and before each command, world-tick handler, map and feature update); `running` names the posted
    command's declaring class, the handler's class (a lambda is named after the class that declares it), or `map <id> instance <id>`.
    Between ticks it says `world thread is between ticks (not inside a tick body)`, which means the stall is outside the tick body
    (the sleep, the GC, a suspended process). The three values are read separately, so a read taken as the thread moves on may pair a
    phase with the next item.
  - **After the fact** (the overrun line; the completion hang line below describes its own frame): `slowest tick body <N> ms: commands <a> ms (<n> run), world-tick
    handlers <b> ms, features <c> ms, maps <d> ms, autosave <e> ms (<k> snapshots); top features: <Name> <ms> ms, ...`, the phase split of the
    slowest tick body recorded in the **60 s slow-tick window** (`TickStats.SlowTickRetention`) and its five slowest features, named
    `WorldTick:<Type>` for world-tick handlers. `TickStats` keeps the eight slowest tick bodies that ran over the tick interval, each for
    60 s, recorded by the world thread without allocating once its ring is full. When none was recorded the suffix says `no tick body over the
    interval was recorded in the last 60 s (the stall was outside the tick body)`: a frame can be long while the body is short, for example
    when the OS did not schedule the world thread or a collection paused it.
  - **The completion hang line** describes the hung frame itself, not the window. The frame boundary is the start of a tick (`WorldRuntime.TickStarting`,
    raised before the commands run), so when `TickMonitor.OnTick` closes a frame over `HangMs` the world thread still holds that tick's finished phases, length and
    slowest features and snapshots them (`TickMonitor.HangClosed`, one small record per hang). The line reads `hung frame <F> ms: tick body <B> ms: <phase split>;
    top features: ...; <X> ms outside tick code`, where `X = F - B` is the wait after the tick or a pause outside tick code (a GC pause, paging, a suspended process).
    Several hangs between two checks are reported once, the worst frame shown, with `; <N> hung frames since the last report, worst shown`. When the time outside
    the tick body is more than the body, bot QA's hang stage is `OutsideTick`, else the body's dominant phase and work. The overrun line keeps the window-wide
    `slowest tick body` text above. No timestamps are matched across threads. The record and the number of hangs it stands for are taken together under `_hungGate`,
    and the report counts the hangs it consumed, so a hang that closes while the check runs is reported once with its own record; with no record left the line says
    `hung frame described in the previous report` and nothing goes to bot QA. World-thread work after `RunTick` returns (the tick observer, the tick-completed event, the
    performance log) is not in the tick body, so it counts as outside tick code.
  Both modes end with `; save queue depth <N>, oldest pending <M> ms`, the character save queue's backlog
  (see [persistence](../areas/persistence.md)), when the queue is registered. The same phase split is added to the runtime's own
  `Slow world update` performance-log line. Cost on the world thread: a few stores per phase change and per handler, a top-five insertion
  per feature, no lock and no allocation (the slow-tick record takes the stats lock only for ticks over the interval).
- **Summary**: every `SummaryIntervalSeconds` an `Information` line (event `TickSummary`, 7103) with p50/p90/p99/max over the ring, overruns, hangs
  and frames since start. 0 disables it.
- **Heartbeat gate**: with `GatesHeartbeat` (default true) the tick monitor is a liveness source: while a tick has run longer than `HangMs`
  the heartbeat is withheld, so a systemd watchdog or external supervisor (`server-status.ps1 -Supervise`) restarts a world whose thread hung although the process still answers. Before the first
  tick (database load, feature attach) and after the world stopped on purpose (`StopAsync`, so a long final save drain is not killed) the
  source reports alive.
- **Terminal hang action** (opt-in): when a single tick remains wedged for `ActionHangMs` (0 = never, whatever `Action` says; must be at least
  `HangMs` when non-zero) and `HostLoadSampler` does not attribute the stall to OS CPU starvation (`HostStarved`), GC pauses (`GcPauses`, `GcPaging`) or a stopped process (`ProcessStalled`),
  the configured `Action` fires at most once for that hung tick, always with a `Critical` line (event `TickAction`, 7104) carrying the
  in-progress hang detail (`phase=<Phase>, running=<work>`): `Log` only logs; `Stop` sets `ExitCodes.Current = 1` and signals
  `IHostApplicationLifetime.StopApplication()`; `FailFast` writes a synchronous crash report to `Console.Error` and calls
  `Environment.Exit(70)` (`ExitCodes.UnhandledException`) so a supervisor immediately restarts a process whose world thread is permanently
  deadlocked without waiting on a stuck graceful-shutdown drain. `Action` set with `ActionHangMs` 0 is a configuration warning (the action
  never fires). A `MemoryStarved` verdict (an idle process on a memory-loaded host) does not stop the action but defers it: it fires only
  once the hang has lasted more than 3 x `ActionHangMs` (exactly 3 x still defers), because an idle process is also what a deadlock looks like. The
  deferral is announced once per hung tick with a `Warning` line (event `TickActionDeferred`, 7105) that names the time the action will fire.
- **Hang stack capture**: once per hung tick, when the hang is first reported, the stack tool in `StackCaptureTool` (dotnet-stack; the World
  appsettings point it at `%USERPROFILE%/.dotnet/tools/dotnet-stack.exe`) runs as `<tool> report -p <pid>` on the thread pool and its
  output, every managed thread's stack with the world thread inside `WorldRuntime.RunTick`, goes to
  `StackCaptureDirectory/hang-stacks-<utc>-<pid>.txt` (a Warning line names the file). At most one capture runs at a time and one per
  `StackCaptureIntervalSeconds`; nothing runs while no tick is hung, and a missing tool is said once (Information) and skipped.
  The file starts with `#` header lines. `# EMPTY CAPTURE` means the tool printed no thread stacks (the output was blank, or the tool exited 0
  with no `Thread (` marker), and the log line is then a Warning saying the capture is empty instead of naming stacks. `# GC-DOMINATED` means the GC pause covers
  half or more of the running tick (the GC-dominated share, 0.5), so the stack shows where the threads were parked, not the cause. `# GC pause unknown` means reading the
  host-load sampler failed (see the fault ledger), so the stack may be GC-dominated.
- Counters: `watchdog.tick.frames`, `watchdog.tick.overruns`, `watchdog.tick.hangs`, `watchdog.tick.actions`, gauges `watchdog.tick.frame_p99_us`, `watchdog.tick.frame_max_us` (see the latency gauges below).
- Latency gauges (microseconds, last minute, refreshed every check): `watchdog.tick.frame_{p50,p75,p90,p95,p99,p999,max,mean,stddev}_us` (p99 and max come from the same 1 minute snapshot as the others, so the set is consistent and ordered), the same set for the tick body as `watchdog.tick.body_*_us`, and `watchdog.tick.phase_<name>_p50_us` / `_p99_us` for the phases `commands`, `world_tick`, `features`, `maps` and `autosave`. The overrun and summary log lines append `; frame latency last 1 min: p50 .. p75 .. p90 .. p95 .. p99 .. p99.9 .. max .. (n=.., min .., mean .. +/-..)` (the summary adds the 15 minute window), then the tick body and per-phase p50/p99. The percentile primitive is `LatencyHistogram` (log buckets, at most 1/32 above the exact nearest-rank value, rolling 1 and 15 minute windows, lock-free).

### 2. Memory pressure (`Ops:Watchdog:Memory`)

Every `SampleIntervalSeconds` the watchdog thread reads `GC.GetGCMemoryInfo()` (heap, gen2, LOH, POH, fragmentation, memory load, the GC's own
high-load threshold, pause percentage), `GC.CollectionCount(2)` and `Environment.WorkingSet`.

- High-water marks of the heap, gen2 and the LOH never fall. When the gen2 or LOH mark rises by `GrowthLogBytes` since the last line, one
  `Information` line with all marks (event `MemoryGrowth`, 7110). 0 disables growth lines.
- `WarnHeapBytes` (0 = off) and `WarnLoadPercent` (0 = off; the runtime's own high-load threshold is 90%) each give a rate-limited `Warning`
  (event `MemoryPressure`, 7111, one per `WarnIntervalSeconds` per kind, with the suppressed count).
- **Action** (opt-in): when the heap reaches `ActionHeapBytes` (0 = never, whatever `Action` says) the configured `Action` fires at most once
  per `ActionCooldownSeconds`, always with a `Critical` line (event `MemoryAction`, 7112): `Log` only logs; `Collect` runs a blocking,
  compacting gen2 collection with the LOH compacted once (this pauses every managed thread, the world thread included; use it knowingly);
  `Stop` sets `ExitCodes.Current = 1` and stops the host once (a supervisor that restarts on 1 brings the realm back with a fresh heap).
  `Action` set with `ActionHeapBytes` 0 is a configuration warning (the action never fires).
- `FullGcNotifications` (default false) registers `GC.RegisterForFullGCNotification` and reports approach and completion from a second
  dedicated thread (`watchdog-gc`). The runtime refuses the registration under concurrent (background) GC, which is the default
  configuration; the refusal is logged once at `Information` and the monitor polls only. To use it set `<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>`
  (or `DOTNET_gcConcurrent=0`) and accept the longer gen2 pauses that come with non-concurrent GC.
- Counters: gauges `watchdog.memory.heap_bytes`, `gen2_bytes`, `loh_bytes`, `loh_fragmented_bytes`, `gen2_collections`, `watchdog.gc.last_full_pause_ms` (the pause of the last full blocking collection); counters `watchdog.memory.actions`, `samples`. The growth line also gives the LOH fragmentation (MiB and %) and that pause. For a GC-pause investigation see [memory and GC diagnostics](diagnostics.md): slow ticks now carry `gc N ms (gen0 +a, gen1 +b, gen2 +c)`, and `.debug gc`, `.debug alloc` and `.debug dump` exist.

### 3. Thread-pool starvation (`Ops:Watchdog:ThreadPool`)

Every `ProbeIntervalSeconds` the watchdog thread queues the probe object itself as a work item (`IThreadPoolWorkItem`, no closure, no
allocation) and the pool thread that runs it stores the queue delay. The next check judges it: above `WarnDelayMs` a rate-limited `Warning`,
above `CriticalDelayMs` a `Critical` line, both with `ThreadPool.ThreadCount`, `PendingWorkItemCount` and `CompletedWorkItemCount` (event
`ThreadPoolStarvation`, 7120). A probe that has not run at all for `CriticalDelayMs` is reported as well, which a pool-based timer could never
do because it would be starved too; one probe is outstanding at a time. Counters: `watchdog.threadpool.probes`, `starvations`, gauges
`delay_us`, `threads`, `pending`.

### 4. Heartbeat (`Ops:Watchdog:Heartbeat`)

| Mode | What happens |
|---|---|
| `Auto` (default) | systemd when `NOTIFY_SOCKET` is set in the environment, otherwise no heartbeat. |
| `None` | Nothing, whatever the environment says. |
| `Systemd` | `sd_notify(3)` in managed code: datagrams on the unix socket named by `NOTIFY_SOCKET` (a leading `@` is the abstract namespace). `READY=1` + `STATUS=` at start, `WATCHDOG=1` every interval, `STOPPING=1` at stop. The interval defaults to half of `WATCHDOG_USEC` (at least one second) when systemd set it for this process (`WATCHDOG_PID` absent or equal to our pid); without `WATCHDOG_USEC` only READY and STOPPING go out and a warning says so. The socket is non-blocking: a reader that stopped draining fails the beat (reported, rate-limited) instead of parking the watchdog thread in `sendmsg`. Without the socket, or on a platform without unix datagram sockets (Windows), one warning and no heartbeat; nothing throws. |
| `File` | Every beat rewrites `FilePath` (relative paths are under the content root) with `<UTC ISO-8601> pid=<pid> <ready\|alive\|stopping>`, written to a sibling `.tmp` and renamed over the target, so a supervisor checking the modification time or the content never reads a half-written file. The file is left in place at stop with `stopping` as its last word. `File` without a `FilePath` is a configuration error. |
| `Stdout` | One `heartbeat <UTC> pid=<pid> <state>` line per beat on standard output. |

`IntervalSeconds` (default 0 = the systemd rule above, otherwise 10) is the beat period. **Fail closed**: before each beat every
`ILivenessSource` is asked; if any reports not alive the beat is withheld, counted in `watchdog.heartbeat.withheld` and one rate-limited
`Warning` names the source and the reason (event `HeartbeatWithheld`, 7130). A withheld beat is re-attempted after a quarter of the
interval (at most 5 s), not a full interval later: with the systemd default of half `WATCHDOG_USEC`, a retry one interval later would land
on the supervisor's deadline and a world that had already recovered from a short stall would be restarted anyway. While the source stays
unhealthy the beats keep being withheld at that cadence and the supervisor acts. The tick monitor is the one source today; a later lane can
register more (database reachability, listener accepting). A sink that cannot deliver (socket gone, file unwritable) is a rate-limited
warning (event `HeartbeatSink`, 7131) and the writer keeps trying. Counters: `watchdog.heartbeat.sent`, `withheld`, `failed`.

A systemd unit for the world daemon:

```
[Service]
Type=notify
NotifyAccess=main
WatchdogSec=30
Restart=on-failure
RestartPreventExitStatus=78
ExecStart=/opt/arcanecore/ArcaneCore.World
```

With `WatchdogSec=30` the daemon beats every 15 s; a world thread hung for `HangMs` stops the beats and systemd kills and restarts the
process after 30 s. `RestartPreventExitStatus=78` keeps the existing rule that an invalid configuration is never restarted
([exit codes](../reference/exit-codes.md)).

### 5. Counters (`Ops:Watchdog:Counters`)

`CounterRegistry.Default` is the process-wide registry, registered in DI as `CounterRegistry` as well. A feature obtains its counter once, at
construction (`registry.GetOrAdd("net.world.connections_refused")`, names `[a-z0-9_.]`, at most 64 characters; `CounterKind.Gauge` for a level
that goes up and down), holds the `Counter` and calls `Increment` / `Add` / `Set` on any thread. Asking for a name that exists with another
kind throws: two owners disagree on what the counter means. `Snapshot()` reads every counter once, in registration order; a value read later
in the snapshot is never older than one read earlier, and a counter cannot go backwards between two snapshots
(`CounterRegistryTests.ConcurrentIncrements_...` runs eight writers, a snapshotting reader and registrations at once).

Every `DumpIntervalSeconds` (0 = off; the registry still counts) one `Information` line `counters: a=1 b=2(+2) ...` (event `CounterDump`,
7140); the parenthesised number is the change since the previous dump, `ChangedOnly` leaves idle counters out. The watchdog's own counters are
listed above, plus `watchdog.checks` and `watchdog.monitor_faults`. No existing code path was changed to count anything in this lane (the
network and session files belong to other lanes); the registry is the plug point, and the counter dump is where their numbers will appear.

## Options (defaults and fail-closed behaviour)

All keys are read once at start (restart-only; `Ops:Watchdog` is not part of `.reload config`, which binds the `World` section only; adding it
would need a second configuration view in `WorldConfigKeys`, a later change). An invalid value is a `check-config` error (exit 78 at start)
and a host-start failure in the realm daemon. Byte values are plain numbers of bytes.

| Key | Default | Meaning | When invalid or unavailable |
|---|---|---|---|
| `Ops:Watchdog:Enabled` | `true` | Master switch. | `false`: no thread, no monitor, no heartbeat. |
| `Ops:Watchdog:CheckIntervalMs` | `1000` | Watchdog thread period, 100-60000. | Out of range: error. |
| `Ops:Watchdog:TickMonitor:Enabled` | `true` | Tick monitor on/off. | Off: no ring, no lines, heartbeat not gated by the tick. |
| `Ops:Watchdog:TickMonitor:RingCapacity` | `4096` | Ring slots (power of two, 16-1048576); the percentiles cover the last capacity-1 frames. | Out of range: error. |
| `Ops:Watchdog:TickMonitor:BudgetMs` | `0` | Overrun threshold; 0 = twice the tick interval. | Negative: error. |
| `Ops:Watchdog:TickMonitor:HangMs` | `2000` | Hang threshold; 0 disables hang detection and the heartbeat gate. | Not above `BudgetMs`: error. |
| `Ops:Watchdog:TickMonitor:ActionHangMs` | `0` | Terminal hang threshold at which `Action` fires; 0 = never. | Negative, or positive when `HangMs` is 0, or below `HangMs`: error. |
| `Ops:Watchdog:TickMonitor:Action` | `None` | `None`, `Log`, `Stop`, `FailFast`. | Unknown name: error. Set with `ActionHangMs` 0: warning, never fires. |
| `Ops:Watchdog:TickMonitor:WarnIntervalSeconds` | `30` | Rate limit of the overrun and hang-in-progress lines. | Out of 1-86400: error. |
| `Ops:Watchdog:TickMonitor:SummaryIntervalSeconds` | `300` | Frame summary period; 0 = none. | Negative: error. |
| `Ops:Watchdog:TickMonitor:StackCaptureTool` | empty | Stack tool run once per hung tick (`%VAR%` expanded); empty or missing = no capture. | - |
| `Ops:Watchdog:TickMonitor:StackCaptureDirectory` | `logs/hangstacks` | Where the captures are written. | - |
| `Ops:Watchdog:TickMonitor:StackCaptureIntervalSeconds` | `300` | Least seconds between two captures. | Out of 10-86400: error. |
| `Ops:Watchdog:TickMonitor:GatesHeartbeat` | `true` | A hung tick withholds the heartbeat. | `false`: the heartbeat ignores the tick. |
| `Ops:Watchdog:Memory:Enabled` | `true` | Memory monitor on/off. | Off: no sample, no action. |
| `Ops:Watchdog:Memory:SampleIntervalSeconds` | `10` | Sample period, 1-3600. | Out of range: error. |
| `Ops:Watchdog:Memory:GrowthLogBytes` | `16777216` | High-water growth that earns a line; 0 = none. | Negative: error. |
| `Ops:Watchdog:Memory:WarnHeapBytes` | `0` | Heap warning threshold; 0 = off. | Negative: error. |
| `Ops:Watchdog:Memory:WarnLoadPercent` | `85` | Memory-load warning threshold; 0 = off. | Out of 0-100: error. |
| `Ops:Watchdog:Memory:Action` | `None` | `None`, `Log`, `Collect`, `Stop`. | Unknown name: error. Set with `ActionHeapBytes` 0: warning, never fires. |
| `Ops:Watchdog:Memory:ActionHeapBytes` | `0` | Heap size at which the action fires; 0 = never. | Negative: error. |
| `Ops:Watchdog:Memory:ActionCooldownSeconds` | `300` | Least time between actions. | Out of 1-86400: error. |
| `Ops:Watchdog:Memory:FullGcNotifications` | `false` | Register for full-GC notifications. | Runtime refuses (concurrent GC): logged once, polling only. |
| `Ops:Watchdog:Memory:WarnIntervalSeconds` | `60` | Rate limit of the memory warnings. | Out of 1-86400: error. |
| `Ops:Watchdog:ThreadPool:Enabled` | `true` | Probe on/off. | Off: nothing queued. |
| `Ops:Watchdog:ThreadPool:ProbeIntervalSeconds` | `5` | Probe period, 1-3600. | Out of range: error. |
| `Ops:Watchdog:ThreadPool:WarnDelayMs` | `200` | Warning delay; 0 = off. | Negative: error. |
| `Ops:Watchdog:ThreadPool:CriticalDelayMs` | `2000` | Critical delay (and "has not run" deadline); 0 = off. | Not above `WarnDelayMs`: error. |
| `Ops:Watchdog:ThreadPool:WarnIntervalSeconds` | `30` | Rate limit of the starvation warnings. | Out of 1-86400: error. |
| `Ops:Watchdog:Heartbeat:Mode` | `Auto` | See the table above. | Unknown name: error. Sink unavailable: one warning, no heartbeat. |
| `Ops:Watchdog:Heartbeat:IntervalSeconds` | `0` | Beat period; 0 = systemd rule, else 10. | Negative: error. |
| `Ops:Watchdog:Heartbeat:FilePath` | `""` | Liveness file for `File` mode. | Empty in `File` mode: error. |
| `Ops:Watchdog:Heartbeat:WarnIntervalSeconds` | `30` | Rate limit of the withheld/failed warnings. | Out of 1-86400: error. |
| `Ops:Watchdog:Counters:DumpIntervalSeconds` | `300` | Counter dump period; 0 = off. | Negative: error. |
| `Ops:Watchdog:Counters:ChangedOnly` | `false` | Dump only counters that changed. | - |

The shipped `appsettings.json` of both daemons carries an `Ops:Watchdog` block with the defaults that an operator is most likely to touch
(`Ops:Watchdog:TickMonitor:HangMs`, `Ops:Watchdog:Memory:Action`, `Ops:Watchdog:Heartbeat:Mode`, `Ops:Watchdog:Counters:DumpIntervalSeconds`); every other key keeps its default when absent.

## Reading the log

Every line carries an event id in 7100-7199 (`WatchdogEvents`) so a sink can route them to their own file, as the `Perf` id does for the
performance log. Levels: `Critical` for a hung tick, a probe that waited past the critical delay and a memory action; `Warning` for an
overrun, a memory threshold, a starved probe, a withheld or undeliverable heartbeat; `Information` for summaries, growth lines, counter
dumps and the start line (`watchdog started: tick, memory, threadpool, heartbeat, counters every 1000 ms`).

## Recommended command surface (for the GM-commands lane; nothing here registers a command)

Administrator level, under the existing `.server` root, all read-only except the last:

- `.server watchdog` (or `.server info` extended): the tick frame p50/p90/p99/max over the ring, overruns and hangs since start, the memory
  high-water marks and the last sample, the last thread-pool probe delay, the heartbeat plan and counts (sent/withheld) and the uptime. All
  of it is readable from any thread: `TickMonitor.Stats`, `MemoryMonitor.LastSample` / `*HighWaterBytes`, `ThreadPoolProbe.LastDelayMicros`,
  `HeartbeatWriter.Plan` / `Sent` / `Withheld`.
- `.server counters [prefix]`: `CounterRegistry.Snapshot()` filtered by name prefix, one counter per line.
- `.server watchdog reset`: zero the tick ring and the high-water marks (needs small `Reset` methods on the monitors; not added here because
  nothing calls them yet).
- `.server gc` (Administrator, dangerous): `IMemoryPressureActuator.Collect()` on demand, logged as a memory action.

## Not delivered and known limits

- Only the frame time is measured by the watchdog's own ring; the tick body distribution comes from `WorldRuntime.Stats`, whose snapshot takes
  its lock. A body-time seam in Kernel would need `WorldRuntime` to report the end of each tick, which is `Game/` code this lane does not edit.
- No counters were added to the network or session code (other lanes' files); the registry is the plug point.
- The systemd sink was exercised against a real unix datagram socket in the tests on Linux; a real systemd unit with `Type=notify` was not run.
- Memory `Collect` and `Stop` were tested through the actuator seam, not by exhausting a real heap.
- `Ops:Watchdog` is restart-only; it is not in the `.reload config` set.
- No new NuGet packages: the heartbeat, the probes and the rate limiters are plain BCL code, so there is no dependency to audit or to keep in
  step with the .NET 11 runtime; the trade-off is that the watchdog's own counters (`CounterRegistry`) are not exported as instruments: the counter dump in the log carries them. A metrics exporter does exist (`Ops:Metrics`, Prometheus and OTLP, docs/ops/metrics.md) and exports the world tick duration histograms and process gauges; a tick hang also reaches bot QA as a `tick hang` finding (docs/areas/playbots-qa.md), and `arcanecore_botqa_findings{kind="tick-hang"}` counts those findings (distinct hang causes, not hangs; `arcanecore_botqa_occurrences_total{kind="tick-hang"}` counts the reported hangs).

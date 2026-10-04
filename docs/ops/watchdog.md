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
- **The world thread** touches the watchdog in exactly one place: the `WorldRuntime.WorldTick` handler of `TickWatchdogFeature`, which calls
  `TickMonitor.OnTick`. That is one clock read and two to five plain stores (a ring slot, the ring count, the last-start stamp, the overrun
  and hang counters): no allocation, no lock, no interlocked instruction, no log. `TickMonitorTests.OnTick_DoesNotAllocate` asserts a zero
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
exactly while the sub-budget distribution floors at the interval. The existing `WorldRuntime.WorldTick` event is raised at the start of each
tick, which is why the frame, not the body, is what the feed can see without editing `Game/`; the body percentiles of the runtime's own
recorder are appended to each line so both views are on one line.

- **Overrun**: a frame longer than `BudgetMs` (0 = twice the tick interval, 100 ms at the retail 50 ms tick, which tolerates sleep jitter).
  One `Warning` per `WarnIntervalSeconds` with the number of new overruns, the frame p50/p99, the slowest recent frame over the ring and the
  tick-body p50/p99/max (event `TickOverrun`, 7101). Overruns in between are counted and appear in the next line.
- **Hang**: a frame longer than `HangMs`. Two `Critical` lines (event `TickHang`, 7102): one while the tick is still running past the threshold
  (repeated every `WarnIntervalSeconds` while it stays hung), one when it completes, with the slowest recent frame. Never rate-limited away.
- **Summary**: every `SummaryIntervalSeconds` an `Information` line (event `TickSummary`, 7103) with p50/p90/p99/max over the ring, overruns, hangs
  and frames since start. 0 disables it.
- **Heartbeat gate**: with `GatesHeartbeat` (default true) the tick monitor is a liveness source: while a tick has run longer than `HangMs`
  the heartbeat is withheld, so a systemd watchdog restarts a world whose thread hung although the process still answers. Before the first
  tick (database load, feature attach) and after the world stopped on purpose (`StopAsync`, so a long final save drain is not killed) the
  source reports alive.
- Counters: `watchdog.tick.frames`, `watchdog.tick.overruns`, `watchdog.tick.hangs`, gauges `watchdog.tick.frame_p99_us`, `watchdog.tick.frame_max_us`.

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
- Counters: gauges `watchdog.memory.heap_bytes`, `gen2_bytes`, `loh_bytes`, `gen2_collections`; counters `watchdog.memory.actions`, `samples`.

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
`Warning` names the source and the reason (event `HeartbeatWithheld`, 7130). The tick monitor is the one source today; a later lane can
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
| `Ops:Watchdog:TickMonitor:WarnIntervalSeconds` | `30` | Rate limit of the overrun and hang-in-progress lines. | Out of 1-86400: error. |
| `Ops:Watchdog:TickMonitor:SummaryIntervalSeconds` | `300` | Frame summary period; 0 = none. | Negative: error. |
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
  step with the .NET 10 runtime; the trade-off is that there is no exporter for an external metrics system (Prometheus, OpenTelemetry). The
  counter dump in the log is the export today; a later lane can read `CounterRegistry.Default` into whichever exporter is chosen.

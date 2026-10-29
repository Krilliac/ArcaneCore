# Memory and GC diagnostics on a live server

Added after live w28 logged `TickHang` lines of 2.3 s and 2.9 s with nothing in the log saying why. The cause was a full blocking gen2
collection on a 5.5 GB heap (LOH 3 GB, 1.75 GB of it free and fragmented). `dotnet-gcdump` fails on heaps this large and
`dotnet-dump --type Heap` fails with `E_INVALIDARG`; only `--type Full` (7.4 GB) worked. These tools run inside the server, so an operator can
find the cause without any external tool, and the hang lines now name it.

All commands extend the `.debug` root, work from the server console (Administrator authority, no character needed) and are listed by
`.help debug`. They have no vmangos equivalent. Source: `src/ArcaneCore.World/Ops/Diagnostics/`.

| Command | Permission | Cost |
|---|---|---|
| `.debug gc` | Game master (level 3) | Microseconds: runtime counter reads, on the calling thread. |
| `.debug gccollect` | Administrator (level 6) | Suspends the process for two full blocking, compacting collections (LOH included). |
| `.debug selfcheck` | Game master (level 3) | Microseconds: runtime, thread-pool, GC, fault-ledger, invariant, tick, and circuit-breaker health check. |
| `.debug alloc start [seconds=30]` / `report [top=15]` / `stop` | Administrator (level 6) | Measured as noise-level on the world process, see below; only while a sample runs. |
| `.debug dump [full\|heap\|mini]` | Administrator (level 6), and only when `Ops:Diagnostics:Dumps:Enabled` is set | Suspends the process while it is written. |

## GC attribution on slow ticks

`WorldRuntime` reads `GC.GetTotalPauseDuration()` and `GC.CollectionCount(0/1/2)` before and after every tick body and stores the difference in
`TickPhases` (`GcSnapshot`, a struct of four integers; nothing is boxed and no string is built on the per-tick path, which a test pins at 0
bytes allocated over 10,000 reads). The text `gc 2345.5 ms (gen0 +3, gen1 +2, gen2 +1)` is appended to `TickPhases.Format()`, so it appears in:

- the `TickDuration`-measure `Slow world update: N ms (interval M ms): commands ..., maps ..., autosave ..., gc ...` line
  (`PerformanceLog:SlowWorldUpdateMeasure=TickDuration`);
- the watchdog's after-the-fact detail on the `TickHang` (completed hang) and `TickOverrun` lines: `slowest tick body N ms: commands ..., autosave ..., gc ...`;
- the slow-tick record (`TickStats.SlowTicks`).

Reading it: the pause is **process-wide** (a blocking collection suspends the world thread as well), and the collection counts are the runtime's
cumulative ones, so one blocking gen2 collection shows as `+1` in all three. `gc 0.0 ms (gen0 +0, ...)` on a slow tick means the GC was not
the cause. The in-progress line (`world tick has been running for N ms`) cannot show the pause, because the runtime only adds it to the total when
the collection ends; the completion line that follows does.

## `.debug gc`

Prints, from `GC.GetGCMemoryInfo(GCKind.Ephemeral / FullBlocking / Background)`, one block per kind: collection index, generation, pause
durations, percentage of time in GC, promoted bytes, compacted, concurrent, heap size, fragmented bytes, and then for gen0, gen1, gen2, LOH and POH
the size and the fragmentation before and after that collection. Above them: the GC mode (server or workstation, concurrent, DATAS, latency mode, LOH
compaction mode), the collection counts and total pause time, and the memory load, high-memory-load threshold, total available and committed bytes.

DATAS is read from the `DOTNET_GCDynamicAdaptationMode` environment variable, then the `System.GC.DynamicAdaptationMode` runtimeconfig knob; when
neither is set, server GC reports `on (default for server GC)` (the .NET 9+ default) and workstation GC `n/a`. A kind with no collection yet prints
`none has happened yet`.

## `.debug gccollect`

Forces a full blocking, compacting collection with the LOH compacted once (`GCLargeObjectHeapCompactionMode.CompactOnce`), waits for the pending
finalizers and collects again, then prints the pause, the managed heap and working set before and after, the collection counts and the fragmented
and committed bytes of that collection. It is listed after `gc`: a command word resolves to the first `.debug` entry it is a prefix of, so a
`gccollect` listed first would take every `.debug gc` (an administrator forcing a collection instead, a game master being refused).

## `.debug alloc start|report|stop`

A sampling allocation profiler. `start` subscribes an in-process `EventListener` to `Microsoft-Windows-DotNETRuntime` with the GC keyword (0x1) at
Verbose and aggregates the `GCAllocationTick` events (`AllocationAmount64`, `AllocationKind` 0 = SOH, 1 = LOH, 2 = POH, `TypeName`; the payload is
read by field name and an event missing a field is counted as unreadable, never mis-attributed). The runtime raises about **one event per 100 KB
allocated** and charges it with that amount, so every number is a sampled estimate and small types are under-represented.

- `start [seconds]`: 1-600 seconds (default 30), accepted as `60` or `seconds=60`. It stops by itself. One sample at a time.
- `report [top]`: for the running or the last finished sample, separately for SOH, LOH and POH, the `top` biggest types with sampled MiB, MiB per
  second and the share of that heap, plus the number of events and rows. Types beyond the 2,000-row cap are summed in `(other types, beyond the row cap)`.
- `stop`: ends the sample now; `report` still shows it.

**Extra events, and why the sample should be short.** `GCAllocationTick` (event 10) is a Verbose event of the runtime's GC keyword (0x1), and the
keyword cannot be narrowed to it: enabling it also delivers every other event of that keyword at Verbose, for example `FinalizeObject` (29), `PinObjectAtGCTime`
(33, raised once per pinned handle **inside the GC pause**), `GCJoin_V2` (203), `GCFitBucketInfo` (209) and the memory-pressure events. They are counted
and thrown away (`N other GC events (discarded)` in the report), but the runtime still produces them, so a sample lengthens GC pauses somewhat on a
heap with many pinned objects or many GCs. The cost on the live server was not measured: use **10-30 s** samples there (`start 30`), not the 600 s maximum.

Aggregation takes one short lock per event (events arrive on a runtime thread, a few thousand a second at most). Disposing the listener disables the
event source first, so nothing is delivered after `stop`; the detach runs outside the profiler lock and, for the command, off the world thread (`DisableEvents` may wait for the runtime to drain its event queue), and `report` shows the totals meanwhile; a test allocates after disposal and checks the count does not move.

**Overhead.** Measured by `AllocationProfilerTests.ListenerOverhead_IsMeasured` on the development box (Ryzen 9 9900X3D, workstation test host): a
loop of 40 M small objects plus 4,000 LOH arrays took 508 ms without the listener and 468 ms with it (72,193 events), i.e. no measurable
difference above run-to-run noise (about +-10%). The cost on the live server, with its much larger heap and the server GC, was **not measured**;
the sample is bounded in time for that reason, and the first use on the live box should be a short one (`start 30`).

## `.debug dump [full|heap|mini]`

Writes a dump of the server's own process with `Microsoft.Diagnostics.NETCore.Client` 0.2.750501
(`new DiagnosticsClient(Environment.ProcessId).WriteDump(type, path, logDumpGeneration: false)`; a Microsoft package, owner `dotnetframework`
on nuget.org, MIT, a `net8.0` asset that runs on net11.0). `full` (default) is `DumpType.Full`, `heap` is `WithHeap`, `mini` is `Normal`.
**Default is full because heap-type dumps fail on the production box with `E_INVALIDARG`**; the failure is reported, with a hint to use `full`.

A dump contains everything in memory: session keys, password material in flight, character data. It is therefore **off by default** and refused unless
`Ops:Diagnostics:Dumps:Enabled` is true. The command also refuses when:

- another dump is being written;
- the last dump started less than `MinIntervalSeconds` ago (a failed attempt does not count, so `heap` failing can be followed by `full`);
- the dump directory cannot be created, or its volume has less than **2x the process working set** free (or the free space cannot be determined).

The write runs on the thread pool, never on the world thread. The command replies `Dump started: <kind> -> <path>` with the warning that **a full
dump suspends the whole process for roughly 1 minute at about 7 GB**; the path, size and duration (or the error) are logged when it ends (a failed dump's partial file is deleted, since it holds the same secrets), and are
replied to the invoker when he is still online. Expect a `TickHang` line afterwards, and note that with `Ops:Watchdog:TickMonitor:GatesHeartbeat`
(default true) the heartbeat is withheld while the world is suspended, so a supervisor whose watchdog timeout is shorter than the dump will restart the
server: lengthen it (or dump from a drained server) first. Files are named `arcanecore-world-<kind>-<utc timestamp>-pid<pid>.dmp`; keep the directory
private and delete the files when the analysis is done.

Unit tests cover the gate with a fake writer; no test writes a real dump. A real `DiagnosticsClient.WriteDump` against the live 5.5 GB heap is
**unverified** here.

### Configuration

Section `Ops:Diagnostics:Dumps` (World daemon, restart-only; listed in [the configuration reference](../reference/configuration.md)):

| Key | Default | Meaning |
|---|---|---|
| `Ops:Diagnostics:Dumps:Enabled` | `false` | Allow `.debug dump`. |
| `Ops:Diagnostics:Dumps:Directory` | empty = `dumps` beside `Logging:ArcaneCore:File:Path` (so `logs/dumps`, whichever format the file uses) | Where dump files go; created on first use. |
| `Ops:Diagnostics:Dumps:MinIntervalSeconds` | `600` | Least seconds between two dumps; 0 disables the limit. |

## Watchdog memory line

The `Ops:Watchdog:Memory` growth line (`heap high-water marks: ...`) now also shows `LOH fragmented N MiB = P%` and `last full blocking GC pause N ms`,
and two gauges are added to the counters registry: `watchdog.memory.loh_fragmented_bytes` and `watchdog.gc.last_full_pause_ms` (see [watchdog](watchdog.md)).

## Recipe: a GC pause (TickHang)

1. A `TickHang` or `TickOverrun` line ends `... autosave 0 ms (0 snapshots), gc 2345.5 ms (gen0 +1, gen1 +1, gen2 +1)`: a blocking gen2 collection is the cause.
   The memory line shows how fragmented the LOH is and how long the last full collection paused.
2. `.debug gc`: look at the FullBlocking block: pause, compacted, and the LOH size and fragmentation before and after.
3. `.debug alloc start 60`, wait, `.debug alloc report`: the LOH section names the types that allocate large objects (typically `System.Byte[]`, `List<T>` /
   array growth); the SOH section names the churn.
4. `.debug dump full` (with dumps enabled), then offline:
   `dotnet-dump analyze <file>.dmp -c "dumpheap -stat -min 85000"` lists what lives on the LOH (objects of 85,000 bytes and more).

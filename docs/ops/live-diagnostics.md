# Live launch diagnostics (`launch-daemon.ps1 -Diagnostics`)

The live World had 8-17 s tick hangs at 1% process CPU (a wait, not work) with the watchdog's hang stack capture off, so the log said a tick
hung and nothing said where. `tools/ops/live/launch-daemon.ps1` now starts both daemons with `-Diagnostics Max` by default (and
`redeploy.ps1 -Diagnostics Standard|Max` passes the level through): the most information that is still safe for a live server with about
1000 bots, meaning bounded disk, bounded pause time and no cost on the world thread beyond a counter increment. `-Diagnostics Standard`
adds nothing (the profile and the repo defaults decide).

The settings go in through the launching process's **environment**, which overrides the profile's `appsettings.json` (and is what the .NET
runtime reads for the `DOTNET_` keys). A variable that is **already set** when the launcher runs is never replaced and is listed as
`kept from the environment` in the launch output, so setting one variable before `launch-daemon.ps1`/`redeploy.ps1` overrides that one value.
Because the environment wins over the profile, a stack tool path or dump setting written in the profile is superseded while `Max` is on:
use `-Diagnostics Standard` to hand control back to the profile.

## What is active

At launch the script prints one `[diagnostics]` line per active capture (and a warning for each one it had to skip):

```
[diagnostics] diagnostics Max (World)
[diagnostics] crash dump: Full -> <LiveHome>\dumps
[diagnostics] hang stacks: <dotnet-stack> every >= 60 s -> <LiveHome>\logs\hangstacks
[diagnostics] counters: System.Runtime (+ the ArcaneCore meters when Ops:Metrics:Enabled is true in the profile) -> <LiveHome>\logs\counters
[diagnostics] repo options: fault log limit 50, slow transaction/command 500 ms, write-gate stats 60 s, heap sample 5 s, counter dump 60 s
[diagnostics] sidecar pid 1234 (ends with the daemon)
```

## Settings applied by `Max`

| Variable | Value | Daemons | What it does, and why that value |
|---|---|---|---|
| `DOTNET_DbgEnableMiniDump` | `1` | both | The runtime's `createdump` writes a dump when the process crashes. `Diagnostics:OnUnhandled=FailFast` (the default, `DiagnosticsOptions.cs:45`) ends in a runtime abort, so an unhandled exception leaves a dump. (`invariants.md` already names this variable.) |
| `DOTNET_DbgMiniDumpType` | `4` (Full), or `2` (Heap) | both | Full holds every mapped page: the most information, about 7 GB for the 5.5 GB live heap (`diagnostics.md`). Heap (`-DumpType Heap`) is smaller. Full falls back to Heap, with a warning, when the volume has less than 16 GB free. |
| `DOTNET_DbgMiniDumpName` | `<LiveHome>\dumps\<role>-%p-%t.dmp` | both | One file per crash (process id, time), under the live home's `dumps` directory. |
| `DOTNET_CreateDumpDiagnostics`, `DOTNET_CreateDumpVerboseDiagnostics` | `1` | both | `createdump` says what it did and why a dump failed. |
| `DOTNET_CreateDumpLogToFile` | `<LiveHome>\dumps\<role>-createdump.log` | both | That diagnostic text goes to a file beside the dump, not the dying process's console. |
| `Ops__Watchdog__TickMonitor__StackCaptureTool` | the resolved `dotnet-stack` | World | `dotnet-stack report -p <pid>` once per hung tick, out of process, off the world thread (`HangStackCapture.cs`, `WatchdogOptions.cs:71`). Looked for in `<DOTNET_ROOT>\tools`, then `%USERPROFILE%\.dotnet\tools`; absent: a warning and the capture stays as the profile configures it. |
| `Ops__Watchdog__TickMonitor__StackCaptureDirectory` | `<LiveHome>\logs\hangstacks` | World | `WatchdogOptions.cs:74`. |
| `Ops__Watchdog__TickMonitor__StackCaptureIntervalSeconds` | `60` (`-HangStackIntervalSeconds`, 10-86400) | World | At most one capture a minute (default 300, `WatchdogOptions.cs:77`); each one briefly samples every thread, so the rate is the bound on the pause. A 60 s timeout stops a stuck tool (`HangStackCapture.ToolTimeout`). |
| `Ops__Diagnostics__Dumps__Enabled`, `...__Directory` | `true`, `<LiveHome>\dumps` | World | Lets an administrator write a dump with `.debug dump` (`OpsDiagnosticsOptions.cs:26-35`), into the same directory so one cap covers both. `MinIntervalSeconds` stays 600: a dump suspends the process. |
| `Diagnostics__FaultLogLimit` | `50` | both | Full log lines per fault signature before it is rate-limited to one a minute (default 10, `DiagnosticsOptions.cs:70`). The ledger counts every occurrence either way. |
| `Ops__Watchdog__Counters__DumpIntervalSeconds` | `60` | both | The counters registry line every minute (default 300, `WatchdogOptions.cs:214`). One line, on the watchdog thread. |
| `Ops__Watchdog__Memory__SampleIntervalSeconds` | `5` | both | Heap sample every 5 s on the watchdog thread (default 10, `WatchdogOptions.cs:113`). |
| `Ops__Watchdog__HostLoad__HistorySeconds` | `300` | both | Five minutes of host-load samples to difference a slow window against (default 120, `WatchdogOptions.cs:103`); 300 small structs. |
| `Database__SlowTransactionWarnMs` | `500` | both | WARN, with the DbContext and the closing code, for a transaction open over 500 ms (default 2000, `DatabaseOptions.cs:108`). Logged by the interceptor on the thread that ran the transaction, when it closes; it adds no polling, and it is off the tick only for work that does not run on the world thread. |
| `Database__SlowCommandWarnMs` | `500` | both | The same for a single command (default 2000, `DatabaseOptions.cs:116`; identical warnings are limited to one per 10 s). |
| `Database__Sqlite__SingleWriter__StatsIntervalMs` | `60000` | both | The SQLite write gate's statistics line (writes, contended writes, waits, longest hold) every minute (default 300000, `SqliteSingleWriterOptions.cs:44`). Its WARN for a writer that waited behind the gate (1000 ms) and ERROR for a leaked hold (60 s) are not settings (`SqliteSingleWriterOptions.cs:38-41`). |

## Counters sidecar and disk caps

With `Max` the launcher starts a hidden `diagnostics-sidecar.ps1` for each daemon. It finds the daemon (the child of the launcher console),
runs until it exits, and never outlives it. It stops only its own collector, by process id, never by image name.

- **Counters.** With `dotnet-counters` it runs `collect` on the daemon, 10 s refresh, CSV, in segments of 60 minutes
  (`<LiveHome>\logs\counters\<role>-<pid>-<utc stamp>.csv`; the tool has no rotation, so the sidecar starts the next segment). Providers requested:
  `System.Runtime` (always present), `ArcaneCore.Runtime`, `ArcaneCore.World`, `ArcaneCore.Faults`, `ArcaneCore.Anomalies`,
  `ArcaneCore.Playerbots.Qa`, `ArcaneCore.Playerbots.Qa.Findings`. A requested meter that does not exist yet is simply absent from the CSV:
  `ArcaneCore.Runtime` is created only by `MetricsHost.StartAsync` and `ArcaneCore.World` only by `WorldMetricsFeature`, both after the
  `Ops:Metrics:Enabled` check (`MetricsHost.cs:32-39`, `WorldMetricsFeature.cs:45-46`; default false, `MetricsOptions.cs:23`), so **they
  appear only when the profile enables `Ops:Metrics`** (the launcher does not, because that opens an exporter endpoint). `ArcaneCore.Faults`
  and `ArcaneCore.Anomalies` are created lazily on the first fault or anomaly (`FaultLedger.cs:1040`, `AnomalyLedger.cs:586`); the Playerbots
  meters when the Playerbots QA code runs (`PlayerbotQaMeters.cs`). With `Ops:Metrics` off the CSV is mostly `System.Runtime`. The collection has at most 2000 time series and 50 histograms so a per-map or per-opcode dimension cannot
  grow the target's memory without bound. A collector that ends at once is retried after 60 s (`-RestartBackoffSeconds` on the sidecar). The sidecar and its collector are tied to the daemon's process object, not to its id, so a reused process id is never followed; paths with spaces are quoted when the sidecar and the collector are started. Its own log is `<role>-sidecar.log` there.
- **Retention.** At launch and at every segment the oldest files beyond a cap are deleted (the newest file is always kept):

| Directory | Files | Default cap | Parameters |
|---|---|---|---|
| `dumps` (`*.dmp`) | crash dumps and `.debug dump` files | 3 files, 30 GB | `-MaxDumpFiles`, `-MaxDumpGB` |
| `logs\hangstacks` (`hang-stacks-*.txt`) | hang stacks | 500 files, 200 MB | `-MaxHangStackFiles`, `-MaxHangStackMB` |
| `logs\counters` (`*.csv`) | counters segments | 48 files, 2048 MB | `-MaxCountersFiles`, `-MaxCountersMB` |

Environment inheritance: the launcher starts the sidecar with the `DOTNET_DbgEnableMiniDump`, `DOTNET_DbgMiniDump*` and `DOTNET_CreateDump*` variables removed (`Invoke-WithoutDumpEnvironment`), so the sidecar and its `dotnet-counters` collectors never dump into `dumps`. The World removes the same variables (and their `COMPlus_` forms) from the environment of the `dotnet-stack` it runs for a hang capture (`ProcessHangStackRunner.CreateStartInfo`), so a crash of that tool cannot write a `world-<pid>-*.dmp` that counts against the dump cap and pushes the World's own crash dump out. The launcher console keeps the `Ops__`/`Database__`/`DOTNET_` variables after the daemon ends (`-NoExit`), so a .NET tool run there afterwards inherits them.

A dump is written by the crashing process, so a cap trims at the next launch (or the next segment), not at the moment of the crash: keep
`-MaxDumpFiles` times the dump size free on the volume. Dumps contain session keys and character data (`OpsDiagnosticsOptions.cs:21-26`):
keep `dumps` private. `Max` also sets `Ops:Diagnostics:Dumps:Enabled=true` on the World, which turns on the `.debug dump` command: administrator-only and rate-limited (`MinIntervalSeconds` 600), but a security-relevant default; use `-Diagnostics Standard` or set `Ops__Diagnostics__Dumps__Enabled=false` in the environment to keep it off.

## Log levels a profile must not raise

The launcher does not touch `Logging` levels, so the profile decides them. Two of the World's performance lines come from the
`ArcaneCore.Game.Maps.WorldRuntime` category, and a profile that sets `Logging:LogLevel:ArcaneCore.Game.Maps.WorldRuntime` to `Error` hides them:

- `Slow map update` and `Slow world update`, the per-map and per-tick lines over the `SlowMapUpdate` and `SlowWorldUpdate` thresholds. They are written
  at **Warning**. "Slow map update" is written by `LogIfSlow` and `LogIfSlowMicros` (`WorldRuntime.Perf.cs:131` and `:140`), called from
  `WorldRuntime.cs:547` (`LogIfSlow`) and `Parallel/WorldRuntime.Parallel.cs:288` (`LogIfSlowMicros`). "Slow world update" is written at
  `WorldRuntime.cs:784` and `:832`.
- The slow-update summary, "Slow update summary: ...". When `PerformanceLog:SummaryIntervalSeconds` is above 0, the per-map and per-tick lines
  are mostly suppressed and counted in `PerfLogAggregator` instead (`WorldRuntime.Perf.cs:118-125`; `RecordWorldFrame` at `WorldRuntime.cs:782` and `:830`).
  The aggregator writes its summary at **Warning** (`PerfLogAggregator.cs:364`) through the same `WorldRuntime` logger (`WorldRuntime.Perf.cs:162`),
  so the `Error` level hides the summary too.
- The parallel maps phase summary, "Parallel maps phase (N s): ... serial gate N entries, N contended". It is written at **Information**
  (`Parallel/WorldRuntime.Parallel.cs:332`). So is the one-time "Parallel maps phase: N map threads" line at start (`:152`).

Use `Information` for that category to keep all of them. Use `Warning` if only the slow lines and their summary are wanted. The lines are bounded: one per
slow map or tick over the threshold (fewer with a summary interval), and one summary per interval. They are not free when unwritten:
`LogIfSlowMicros` builds the host-load and parallel text (`WorldRuntime.Perf.cs:128`) without checking the log level, and only the parallel
summary checks `IsEnabled` first (`Parallel/WorldRuntime.Parallel.cs:322`). This is a profile change; the launcher and the repo defaults do not set it.

## When the tick watchdog starts

The tick monitor counts frames from the first tick, but its checks start later. That is what reports a hang, writes `TickHang` and runs the hang
stack capture.

- The world feature attaches the monitor, and `TickWatchdogFeature.cs:54` calls `StartTicking`, inside `WorldHost.StartAsync` before
  `world.Start()` (`WorldHost.cs:168`). So every frame of the tick loop is recorded.
- The checks run on the watchdog thread. `WatchdogHost.StartAsync` (`WatchdogHost.cs:48-62`) starts that thread. `WatchdogHost` is a hosted
  service added by `AddWorldWatchdog` (`WorldProductionServices.cs:51`), which comes after `AddWorldDaemon` (`:45`, `WorldHost` and `WorldServer`)
  and after the console, control pipe, map feed and remote admin services.
- The generic host starts hosted services one at a time, in registration order. So the watchdog thread starts only once all of those have
  finished starting. That was about 2 minutes after the first tick, observed on one live launch (2026-10-27).
- A hang in that window is reported by the first check, after the fact. It shows as a completed hang with the slowest recent frame. No hang
  stack is captured, because the capture runs only while a tick is still hung.

The fix is to register the watchdog before the world daemon, which is a change in `WorldProductionServices.cs`, or to start the watchdog
thread from `TickWatchdogFeature.Attach`. Either one changes start-up and stop ordering: hosted services stop in reverse registration order
(`WorldProductionServices.cs:38-39`). It is not made here. `TickMonitorTests.AHangInTheFirstTicks_IsReportedByTheFirstCheck` pins that the
monitor itself reports a hang in the first ticks, still in progress, as soon as its first check runs. It pins only `TickMonitor` behavior, not the host start order.

## Survey: what was left out, and why

| Knob | Source | Decision |
|---|---|---|
| `Diagnostics:FirstChanceExceptions` | `DiagnosticsOptions.cs:61` | **Not enabled.** The hook runs on every throw, caught or not, on whatever thread throws, including the world thread; a bot swarm throws constantly. The fault ledger and `Diagnostics:FaultLogLimit` already record every handled fault. Turn it on in the profile for a short hunt. |
| `Ops:Observability:DuplicatePackets:CaptureStacks` | `ObservabilityOptions.cs:59` | Already on by default; the stack is taken only when a duplicate is going to be logged (token-bucket bounded). `IncludeBots` (`:44`) stays off: it would hash every bot packet on the send path. |
| `Ops:Watchdog:TickMonitor:HangMs` | `WatchdogOptions.cs:53` | Left at 2000. Lowering it fails config validation when a profile's `BudgetMs` is at or above the new value (`WatchdogOptionsValidation.cs:37`), so the launcher does not change it; set both in the profile to capture shorter hangs. |
| `Ops:Watchdog:TickMonitor:Action`/`ActionHangMs` | `WatchdogOptions.cs:65-68` | Left off: they stop or kill the world, which is not a diagnostic. |
| `PerformanceLog:*` | `PerformanceLogOptions.cs` | Left to the profile (`WorldConfigChecks.cs:65` validates the detail keys). The defaults already log a summary a minute plus one detail line per map per interval. |
| `Ops:Metrics` | `MetricsOptions.cs:23` | Left to the profile (it opens an exporter endpoint). The counters sidecar needs it: without `Ops:Metrics:Enabled` the `ArcaneCore.Runtime` and `ArcaneCore.World` meters are never created, so the CSV holds `System.Runtime` plus whatever fault, anomaly and QA meters have been created. The sidecar attaches through the diagnostics port, not the exporter's listener. |
| `Logging` levels | profile | Left to the profile: raising them to Trace costs tick-thread time on every log call. |
| `DOTNET_EnableEventPipe` (+ `DOTNET_EventPipeOutputPath`, `DOTNET_EventPipeConfig`, `DOTNET_EventPipeCircularMB`) | runtime | **Not enabled.** A startup EventPipe session writes a `.nettrace` for the life of the process: it grows without bound, and the sample-profiler and GC-verbose providers cost CPU on a loaded process. `dotnet-stack` and `dotnet-counters` use short, bounded EventPipe sessions through the diagnostics port instead; a long trace is an operator action (`dotnet-trace collect -p <pid> --duration ...`). |
| `DOTNET_EnableCrashReport` (JSON crash report) | runtime | Not supported on Windows (Microsoft's "Collect dumps on crash" article), so there is nothing to enable; the crash dump above is the capture. |
| `.debug alloc` (EventListener, GC keyword) | `diagnostics.md` | On demand only (administrator command); its sampling is measured as noise-level but it is not left running. |
| `DOTNET_gcServer`, `DOTNET_GCHighMemPercent` | `ArcaneCore.World.csproj:12`, `launch-daemon.ps1` | Not diagnostics. `launch-daemon.ps1` already sets `DOTNET_GCHighMemPercent` for both daemons. |

## Verification

`tests/ArcaneCore.World.Tests/Ops/LiveDiagnosticsTests.cs` runs the plan, retention, counters arguments and the sidecar in a fresh `pwsh`
against a temporary live home and a fake tools directory (no daemon is started, the live home is never touched). The `DOTNET_CreateDump*`
log keys follow the runtime's createdump documentation; they were not exercised against a real crash here.

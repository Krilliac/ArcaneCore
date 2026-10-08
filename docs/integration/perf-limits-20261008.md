# Limiters and tick timing after the live stress test (2026-10-08)

Base: `45ee84af` (origin/main), branch `claude/perf-limits`. Input: the live stress test of 2026-10-08 16:04-16:37
(`D:/ArcaneCore-lanes/_plan/live-stress-20261008.md`), which stopped at 21 players because two configured caps refused
the ramp long before the tick budget was under pressure, and which recorded a jittery tick cadence and 400-850 ms stalls.
Nothing here was measured on the live server: every number below comes from a test harness in this repository, on the
shared development machine (other agents were building and testing; machine CPU and memory load were not controlled).

## 1. Managed bot caps (`World:Playerbots`)

| Before | After |
|---|---|
| `MaxBots` validated 0..64 | 0..`PlayerbotOptions.MaxBotsCeiling` = **1000** (measurement below) |
| `.playerbot create` refused when *registered* bots (running or stopped) reached `MaxBots` | `MaxBots` counts **running** bots only (it always did at `.playerbot start`); creation is bounded by the new `MaxRegisteredBots` (default 1000, 0..10000) with the code `playerbot-registry-full` |
| read once at start | `MaxBots` and `MaxRegisteredBots` are **live** through `.reload config` (`WorldConfigKeys`) |

Why running bots only: the option's own documentation always said "the most managed bots online at once", and
`StartCoreAsync` already compared `_active.Count`; only `CreateAsync` compared the registered count, so the live world
(10 slots: 5 running, 5 stopped) could neither create nor (without stopping something) usefully start a bot. vmangos bounds bots
that are online, not stored characters: `RandomBot.MaxBots` (`PlayerBotMgr.cpp:55`) is the upper end of "Between %u and %u
bots online" (`:143`), kept as `confMaxOnline` beside a separate `totalBots` count of registered bots (`:134-136`). That is
the convention followed here (read in `D:/refs/vmangos`, nothing copied). Lowering `MaxBots` below the running count **stops nobody**:
new starts, startup restores and quarantine retries are refused (`playerbot-capacity`; a retry waits for a slot) until
enough bots are stopped. Stopping the newest bots automatically was rejected: it would turn a configuration reload into
character logouts nobody asked for.

### Measurement behind the ceiling

`tests/ArcaneCore.World.Tests/Playerbots/ManagedPlayerbotScaleMeasurementTests.cs` (opt-in: `ARCANECORE_BOT_SCALE=25,50,...`):
one world test host, in-memory stores, **no terrain, no creatures, no real clients**, 50 ms real-clock tick, autonomous bots
created and started through the ordinary `CreateAsync`/`StartAsync`, 15 s hold per step. Tick work = world-level tick event
to the end of the world features (maps included), nearest-rank. Times in ms.

| Running bots | mean | p50 | p95 | p99 | max | bot feature (EMA) | allocated / tick |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 0 | 0.03 | 0.01 | 0.03 | 0.05 | 5.8 | 0.00 | 0.5 KiB |
| 100 | 0.42 | 0.36 | 0.55 | 0.96 | 17.5 | 0.25 | 117.8 KiB |
| 200 | 0.83 | 0.65 | 1.87 | 2.36 | 8.5 | 0.64 | 229.2 KiB |
| 400 | 1.89-2.84 | 1.10-1.89 | 4.9-8.7 | 7.0-11.0 | 9.8-14.9 | 1.5-2.1 | 452-453 KiB |
| 700 | 5.11 | 2.93 | 12.6 | 15.2 | 22.1 | 6.1 | 779.8 KiB |
| 1000 | 12.2 | 11.9 | 17.2 | 21.3 | 24.9 | 9.6 | 1083.7 KiB |

At 1000 running bots the synthetic tick stays inside the 50 ms budget (p99 21 ms, max 25 ms) and allocates about 1 MiB per
tick, which is where 1000 was put. **This is a ceiling for the validator, not a recommended setting**: the live world adds
content (creatures, spells, terrain, real sessions) that this fixture does not have, so the operator still chooses
`MaxBots` by watching `.server info` (or the managed-bot stage of the stress script, below).

## 2. Per-address connection cap exemption (`Net:Protection`)

`Net:Protection:MaxConnectionsPerIp` (16) refused the 15th local stress client and would have refused the owner's own
client: every local client connects from 127.0.0.1. Two new keys:

- `ExemptLoopbackOnLoopbackBind` (default **true**): when the listener is bound to a loopback address (the live
  `World:BindAddress` and `Auth:BindAddress` are `127.0.0.1`), loopback clients are not subject to the shared cap. Only
  local processes can reach such a listener at all. A listener bound to `0.0.0.0`, `::` or a LAN/public address keeps
  capping loopback clients like any other address, so a reverse proxy on the same host is not exempted by default.
- `ExemptAddresses` (default empty): addresses or CIDR networks exempt from the shared cap on any bind.

Only the shared cap is lifted. The daemon's own `Auth:`/`World:MaxConnectionsPerIp` (an explicit operator choice, default
0), the global cap, the connection rate (`ConnectionBurstPerIp` 100, `ConnectionsPerMinutePerIp` 300) and the
authentication failure budget still apply to exempt addresses. A crowd test that opens more than ~100 connections at once
should therefore pace its spawns (the stress script spawns one client per 0.4 s) or raise the burst. Malformed entries
exempt nothing; the listener logs one warning and `check-config` reports an error. Tests:
`tests/ArcaneCore.Realm.Tests/Security/NetGuardExemptionTests.cs` (unit cases and a real logon listener on loopback that
holds 5 connections from 127.0.0.1 with the shared cap at 2).

## 3. Tick timing (`World:TickTimer`, `World:TickLateToleranceMs`)

The world loop waited with `ManualResetEventSlim.Wait(ms)`, which on Windows wakes on the ~15.6 ms system timer. The
drift-compensated schedule kept the mean at 50 ms, but individual frames alternated around 46/61 ms, and because a tick
counted as late at any delay at all, ~94 % of live ticks were "late".

Now (`src/ArcaneCore.Game/Maps/WorldTickWaiter.cs`): `World:TickTimer` = `Precise` (default) sleeps on a Windows
high-resolution waitable timer (`CreateWaitableTimerExW` with `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`, Windows 10 1803+,
no process-wide `timeBeginPeriod`), waited together with the stop event, until the due time on the loop's `Stopwatch`
(microsecond schedule instead of whole milliseconds). A Windows without that timer falls back to `Legacy` (the old wait);
other platforms sleep the whole milliseconds on the event and yield/spin the sub-millisecond rest. The scheduler keeps
its drift-free fixed cadence and skip-on-stall rule. `World:TickLateToleranceMs` (default 2, 0..1000) is the slack before
a start counts as late and before a frame counts as a frame overrun. Both are restart-only. The frame interval recorded
in `TickStats` is now measured on the `Stopwatch` (microseconds) instead of the millisecond diff.

Measured with `tests/ArcaneCore.Game.Tests/Ops/TickCadenceMeasurementTests.cs` (opt-in: `ARCANECORE_TICK_CADENCE_SECONDS`): a
real `WorldRuntime` thread at 50 ms with a 3 ms busy tick (about the live mean), 60 s, frame = time between tick starts.

| Wait | frames | mean | p1 | p10 | p50 | p90 | p99 | max | frames > 52 ms | scheduler late | frame overruns | process CPU ms/s |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| before (old code) | 1195 | 49.99 | 41.58 | 45.60 | 46.53 | 61.62 | 64.69 | 76.78 | 300 | 1132 | 303 | 59.1 |
| `Legacy` (new code, 2 ms tolerance) | 1194 | 49.99 | 44.95 | 45.71 | 46.55 | 61.76 | 63.04 | 77.15 | 286 | 970 | 288 | 68.5 |
| **`Precise`** (default, no spin) | 1195 | 50.00 | 49.13 | 49.65 | 50.00 | 50.33 | 50.93 | 52.19 | 2 | 0 | 2 | 64.3 |
| `Precise` + 0.5 ms spin | 1195 | 50.00 | 49.36 | 49.98 | 50.00 | 50.02 | 50.61 | 51.63 | 0 | 0 | 0 | 65.1 |
| `Precise` + 1 ms spin | 1195 | 50.00 | 48.91 | 50.00 | 50.00 | 50.00 | 51.09 | 52.18 | 1 | 0 | 1 | 64.6 |

The high-resolution timer alone brings p90 from 61.6 to 50.3 ms and the late count from 1132 to 0; a spin margin tightens p90
further but buys nothing that matters at a 2 ms tolerance, so the default spins nothing (`WorldTickWaiter.DefaultSpinMarginMicros`
= 0). The CPU column is dominated by the 3 ms busy tick (60 ms/s) and varied with machine load; no measurable cost of the
precise wait is claimed or excluded beyond that. `late` and `frameOverruns` are now meaningful: they count ticks that
really started late, not timer granularity.

## 4. `.server info`

New on existing lines: `Tick frames: ... p50=... p90=...` (frame interval percentiles), `Tick work: mean=... p50=...`
(the watchdog's TickSummary had the only median), `Tick schedule: late=N (>2 ms) ...`. Two new lines: `Tick allocation
mean: commands=... maps=... features=...; top: <three largest allocating features>` (per-phase and per-feature bytes the
world thread allocated, from `GC.GetAllocatedBytesForCurrentThread` at the phase boundaries and around each feature) and
`GC: server|workstation concurrent|non-concurrent gen0/1/2=... pauseTotal=... ms lastPause=... ms (gen)`. The stress
script parses all of them.

## 5. Stalls, GC settings and allocation

### What the World ran with

No GC settings anywhere (`Directory.Build.props`, the World `.csproj`, no `runtimeconfig.template.json`): **Workstation GC,
concurrent (background) collections on** (the default), TieredPGO on (the .NET 8+ default), no `GCHeapAffinitizeMask`,
no `GCHeapCount`. On Workstation GC a gen0/gen1 collection runs on the thread that triggered it, which is usually the
world thread, single-threaded, with every managed thread suspended.

### Harness

`tests/ArcaneCore.Game.Tests/Ops/GcModeMeasurementTests.cs` (opt-in: `ARCANECORE_GC_MODE_SECONDS`), run with the GC chosen
through `DOTNET_gcServer` / `DOTNET_gcConcurrent` / `DOTNET_GCHeapCount` / `DOTNET_GCDynamicAdaptationMode`. **Synthetic**:
a real `WorldRuntime` thread at 50 ms over a live-sized retained heap (1.13 GB after a compacting collection: 65 % small
objects, 35 % 100 KB arrays on the LOH, as live: 1.2-1.4 GB, LOH 355-420 MB) while each tick allocates 960 KiB of short-lived
objects (live: ~0.95 MiB/tick), replaces 4000 retained small objects (garbage that reaches gen2), replaces a large array
every 20 ticks and spins 3 ms. 90 s per run, two rounds; ms unless noted; "pause" is process-wide GC pause time.

| GC | round | work p99 | work p99.9 | work max | ticks > 50 ms | frame max | gen0/1/2 | pause total | longest pause in a tick | peak WS MiB |
|---|---|---:|---:|---:|---:|---:|---|---:|---:|---:|
| workstation, concurrent (**live**) | 1 | 49.1 | 177.4 | 194.8 | 16 | 976.6 | 101/69/5 | 4122 | 108.6 | 1635 |
| | 2 | 67.5 | 109.3 | 125.2 | 38 | 125.2 | 55/55/1 | 3223 | 124.3 | 1697 |
| workstation, non-concurrent | 1 | 62.4 | 1437.1 | 1619.6 | 33 | 1619.7 | 74/32/4 | 7729 | 1618.7 | 1401 |
| | 2 | 40.9 | 95.0 | 308.0 | 8 | 308.0 | 54/13/1 | 2249 | 307.2 | 1737 |
| **server, concurrent, DATAS** (new default) | 1 | 11.8 | 72.8 | 134.6 | 2 | 189.4 | 7/1/0 | 308 | 133.4 | 1827 |
| | 2 | 12.3 | 52.9 | 186.5 | 2 | 186.5 | 6/1/0 | 375 | 184.0 | 1921 |
| server, concurrent, 4 heaps, DATAS off | 1 | 10.9 | 114.6 | 235.7 | 4 | 235.7 | 5/2/1 | 548 | 214.9 | 2057 |
| | 2 | 7.3 | 149.5 | 264.4 | 3 | 682.2 | 5/2/1 | 565 | 262.0 | 1957 |

The live configuration reproduces stalls of the reported size in this synthetic fixture (a 977 ms frame, 3.2-4.1 s of GC pause
per 90 s, 16-38 ticks over budget). Server GC with DATAS (Dynamic Adaptation To Application Sizes, on by default for Server GC
since .NET 9) cut total pause time by ~10x, ticks over budget to 2 and the tick p99 to 12 ms, for ~190-220 MB more peak working
set. Non-concurrent workstation GC is worse (one 1.6 s stall); a fixed 4-heap Server GC without DATAS was worse than DATAS
(one 682 ms frame) and used more memory.

**Change:** `src/ArcaneCore.World/ArcaneCore.World.csproj` sets `ServerGarbageCollection` and `ConcurrentGarbageCollection`
(the World's `runtimeconfig.json` now carries `System.GC.Server: true`, `System.GC.Concurrent: true`). `DOTNET_gcServer=0`
in the World's environment returns to the workstation collector without a rebuild. **No live improvement is claimed**: the
live stalls also coincided with 73-91 % machine CPU from concurrent builds, which no GC setting removes, and the next live
sample should be read from the new `GC:` and `Tick allocation mean:` lines of `.server info` before and after a stall.

### Per-tick allocation

The world thread's allocation is now visible per phase and per feature (section 4). The bot-scale harness with the runtime's
own allocation sampler (an in-process `EventListener` on `GCAllocationTick`, `ARCANECORE_BOT_SCALE_ALLOC_TYPES=1`) showed the
largest single source at 200 bots was `PlayerbotStatus`: `ManagedPlayerbotFeature` rebuilt its whole status snapshot (and
re-sorted its running bots through `ConcurrentDictionary.Values` + `OrderBy`) every tick. Changes:

- `ManagedPlayerbotFeature`: the running bots are re-sorted only when the set changes; a bot's status line is reused when none
  of its fields changed, and the snapshot array is rebuilt only when a status changed, the running count changed or
  `PublishStopped`/startup touched it. Snapshot semantics are unchanged (same entries, refreshed every tick when they change).
- `Map.FlushPlayer`: the per-player update flush created a closure and a delegate per player per tick; the delegate is now made
  once per player (`Player.PendingUpdatesSend`).

| Running bots | allocated / tick before | after |
|---:|---:|---:|
| 25 | 31.0 KiB | 24.6 KiB |
| 50 | 59.9 KiB | 48.3 KiB |
| 100 | 117.8 KiB | 94.1 KiB |
| 200 | 229.2 KiB | 181.2 KiB |
| 400 | 452.4 KiB | 359.9 KiB |

(-20 % per bot; tick times in those runs varied with machine load and are not compared.) The remaining per-bot allocation is
inside the brain, risk and navigation code (sampled types: `Func<Creature,bool>` and `Where` iterators, `Vector3[]` routes,
`Concat` over attackers/threatened-by in `PlayerbotBrain.RememberAttackers` and `PlayerbotRisk.ObserveFight`, `OrderBy` over
destinations and items). Those are the next targets; they were not changed here. The live 0.95 MiB/tick with 5 bots is **not**
explained by bots (5 bots are ~6 KiB/tick here); the new `Tick allocation mean:` line on the live server will show which phase
it is.

## 6. Stress script

`D:/ArcaneCore-lanes/_deploy/stress-20261008-1604/stress.py bots` (outside the repository): `.playerbot create Stressbot<xx>
<race> <class>` (ten valid race/class pairs, both factions) and `.playerbot start` up to each of `STRESS_BOT_STEPS` (default
10,25,50,100,200,300,400) with the same `.server info` sampling and stop rules as the client ramp (commit and World private-bytes
headroom projected from the per-bot growth of the previous step, p95 over budget for a whole step, >10 % overruns, >50
ERROR/FATAL lines, Nathan's running bots not running, >10 % of the stress bots not running, `playerbot-capacity` /
`playerbot-registry-full`), then stops (never deletes) the stress bots and samples recovery. `STRESS_BIN`,
`STRESS_WORLD_PID`, `STRESS_WORLD_LOG` point it at a redeployed World. It needs a World with this branch and `MaxBots` above the
last step plus the bots already running (raise it live with `.reload config`). It has not been run.

## Reproduction

```powershell
dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false
$env:ARCANECORE_TICK_CADENCE_SECONDS = 60   # optional: ARCANECORE_TICK_CADENCE_TIMER=Legacy|Precise, _SPIN_US, _WORK_MS, _CSV
dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build --filter FullyQualifiedName~TickCadenceMeasurementTests --logger 'console;verbosity=detailed'
$env:ARCANECORE_GC_MODE_SECONDS = 90; $env:DOTNET_gcServer = 1   # or 0; DOTNET_gcConcurrent, DOTNET_GCHeapCount, ...
dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build --filter FullyQualifiedName~GcModeMeasurementTests --logger 'console;verbosity=detailed'
$env:ARCANECORE_BOT_SCALE = '25,50,100,200,400'   # optional: ARCANECORE_BOT_SCALE_ALLOC_TYPES=1, _HOLD_SECONDS, _CSV
dotnet test tests/ArcaneCore.World.Tests -c Release --no-build --filter FullyQualifiedName~ManagedPlayerbotScaleMeasurementTests --logger 'console;verbosity=detailed'
```

# Multithreading and parallel performance: research and plan (2026-10-17)

Status: research and design only. Nothing here is implemented, and no code or configuration changed with this page.
Requested by Nate on 2026-10-17 ("start deep researching into multi threading and other related techniques and prepare
to work on that throughout the project"). It fills in the ROADMAP item "Multithreading and parallel performance"
([ROADMAP](../ROADMAP.md)). That item asks for "parallel map/instance updates, keeping one writer per map, and moving bot think
and pathfinding work off the world thread", checked against the references before any code is written.

Source pin: `grok/live-sweep` at `4e9e85b4` (2026-10-17 7:22 PM CT). Live evidence comes from `D:\ArcaneCore-live\logs` on Nate's
PC (Ryzen 9 9900X3D, 12 cores / 24 threads, 31 GB). Two windows were read: the 24-hour log `world.debug-20261017-001.log`
(Oct 16 7:00 PM CT to Oct 17 6:59 PM CT) and the current log (7:00 to 7:24 PM CT). Log timestamps are UTC; times on this
page are CT unless marked otherwise.

Proposed configuration keys appear in **bold** below, not as code. None of them exist yet, and the docs key audit
(`DocKeyAuditTests`) only accepts code-formatted keys that an options class reads.

---

## 0. Executive summary

1. **The world uses one core out of 24.** At about 980 managed bots (468 on map 0, 515 on map 1), slow-map lines show the
   world process at **1-5 % process CPU on a 24-CPU machine**: one busy thread. Tick body p50 is 53 ms and p99 is about
   115 ms against a 50 ms tick. Every simulation system runs on one `world` thread (`WorldRuntime.Run`): commands, world-tick
   handlers, every map one after another, then features (bot think), then autosave. Only side work runs on the thread pool:
   the save queues, the async bot navigation executor, tile prefetch and the region-graph build.
2. **Not all of the lag is code.** Of 11,400 verdicts on slow-map lines in 24 h, **3,652 say "host-starved"**: other
   processes held 79-95 % of the machine (concurrent `dotnet build` workers on the same PC). **274 say "gc-pauses"**, with
   blocking pauses of **1.2-4.3 s**, gen1 as well as gen2. A gen2 pause of 3.3 s (2:25 PM CT) turned a normal map-1 update
   into 3,326 ms. With Server GC, a collection waits for all of its GC threads. When the CPU is saturated, the slowest GC
   thread sets the pause.
3. **The live build is a Debug build.** `D:\ArcaneCore-sweep\README.md` deploys with `-c Debug`, and
   `Directory.Build.props` does not override Optimize, so the JIT runs with optimizations disabled. This is the cheapest
   large CPU win there is (typically 1.5-3x on hot loops; not measured here). The ROADMAP leaves the decision to Nate
   ("live stays on Debug"), so this plan only recommends measuring it (§6 W0).
4. **The references agree on one core design:** *one owner thread per map (or per map partition), and maps run in
   parallel*. TrinityCore and AzerothCore schedule every Map::Update on a MapUpdater pool, controlled by `MapUpdate.Threads`.
   vmangos goes further: one thread per continent, an instance pool, optional continent partitioning along zone lines
   (`Continents.Instanciate`), parallel cell stripes inside a continent (`MapUpdate.Continents.MTCells.*`), parallel motion,
   visibility and update-packet building, and far teleports held until every map has finished. Albion Online runs one
   thread per zone and hands DB, pathfinding and logging to pools that post results back. The same design matches
   ArcaneCore's own clustering decision, "one owner per map/instance" ([CLUSTERING_DESIGN](../CLUSTERING_DESIGN.md)).
5. **Persistence is the second structural problem.** About 11 independent write queues (`CharacterSaveQueue`,
   `KeyedStoreWriteQueue`, Rest, Reputation, Social, Honor, Taxi, ExploredZones, Instance, StateSave, GmAudit, plus the
   spellbook writer) each open their own connection to one SQLite file. SQLite admits **one writer at a time**, even in WAL
   mode, and Microsoft.Data.Sqlite runs its "async" calls synchronously. The result: **959 `database is locked` errors in
   24 h** and save backlogs with the oldest snapshot waiting 5.8 s. The fix is a single characters-database writer that
   batches many logical writes into one transaction.
6. **Plan, in order:** W0 measure and quick operational wins → W1 single SQLite writer → W2 thread-ownership foundation
   (an audit and guards, no behaviour change) → W3 parallel map update behind **World:Parallel:MapUpdateThreads** (default 1,
   so behaviour is unchanged) → W4 bot think partitioned per map → W5 creature pathfinding job system → W6 AI level of
   detail and adaptive activation → W7 intra-map parallelism (vmangos-style striping) → W8 network buffers → W9
   allocation and GC. §9 lists the first three PR-sized steps.

Expected effect at today's load, as estimates to verify with the benchmarks in §8: W3 brings the maps phase from
sum(map0, map1) to max(map0, map1), about 29 → 15-18 ms p50. W4 roughly halves the 17-100 ms features phase (bots split
across two continents) and takes the 1.1 s "policy snapshot" outliers off the tick. W1 removes the lock storms. An
optimized build (W0) multiplies all of these gains.

---

## 1. Live evidence (what the logs actually say)

| Signal | Value | Source |
|---|---|---|
| Population | 468 players on map 0, 515 on map 1 (almost all managed bots); 10.7k / 9.9k creatures, ~4.1-5.0k of them active per continent | Slow map update lines, 7:21-7:23 PM CT |
| World process CPU | 1-5 % of 24 CPUs (= about one core) | `host: process cpu 5% / machine cpu 84%` in the same lines |
| Tick body (last 1 min) | p50 53 ms, p90 72-74 ms, p99 96-115 ms, max 148 ms | TickOverrun 7:23 / 7:24 PM CT |
| Phase split p50/p99 | commands 4.6/10-12, world_tick 1.3/2-3, **features 17-18/57-59**, **maps 29/44-64**, autosave 0.2/0.4 ms | same |
| Top feature | `ManagedPlayerbotFeature` 62-92 ms in the slowest ticks; **3,626 ms** in one TickHang (12:01 AM CT) | TickOverrun / TickHang |
| Slow map verdicts, 24 h | host-quiet 7,247; **host-starved 3,652**; **gc-pauses 274**; process-stalled 12; process-busy 4; unknown 210 | `verdict ...` field |
| GC | Server GC, concurrent, latency Interactive, DATAS on by default; heap 2.6 GB (gen2 1.7 GB, LOH 918 MB); blocking pauses up to 4,340 ms | MemoryGrowth line; `gc +1/+1/+1 ... pause 4063 ms` |
| Creature outliers | one creature (entry 1941, WaypointMovementGenerator) took 162 ms of AI in one update; CreatureMapSystem 366 ms on map 0 | 1:39 AM CT line |
| Bot think outliers | a single think of 1,256 ms, of which "policy snapshot" was 1,116 ms | `slow think` line 12:00 AM CT |
| Path budget | 0.3-1.9 million "deferred" path outcomes since start; 20-30 searches per update are allowed | CreatureMapSystem counters |
| SQLite | **959** `database is locked` in 24 h, across several writers (respawn, rest, quest, battleground, bot recovery); backlog warnings up to 72 characters with the oldest waiting 5.8 s | log scan |

What follows from this:
- Parallelism is the right lever. A single-thread-bound process on a 24-thread machine leaves most of the CPU unused.
- Parallelism alone is not enough. A 162 ms creature or a 1.1 s bot think on one thread still stalls whichever map runs
  it. Algorithmic outliers need budgets and time-slicing (W4-W6) in either model.
- The host matters. Build workers and the live server share the PC. Until W0's isolation is in place, a third of the
  slow frames are not ArcaneCore's fault, and every benchmark must record the host load (the slow lines already do).

---

## 2. Current threading model (as of `4e9e85b4`)

### 2.1 Tick loop

`WorldRuntime` (`src/ArcaneCore.Game/Maps/WorldRuntime.cs`) owns a dedicated `Thread("world")`. `WorldTickScheduler` keeps a
drift-compensated fixed cadence (50 ms, `WorldTickTimer.Precise`). After a stall it skips missed ticks instead of bursting.
`RunTick` runs these phases strictly in order:

1. **Commands**: drains a `ConcurrentQueue<Action>` posted from other threads (`Post`, `InvokeAsync`). Bounded by
   `MaxCommandsPerTick` (1024) and `CommandTimeBudgetMs` (5 ms).
2. **WorldTick handlers** plus `Scripts.World.OnUpdate`: world-level services (game events, rest, war effort, the
   watchdog's handler).
3. **Maps**: a snapshot of `_maps` (a plain `Dictionary<(MapId, InstanceId), Map>`) is copied to a rented array, then
   **each `Map.Update` runs one after another**.
4. **Unload requested maps**, then **Features** (`Updated` handlers; this is where `ManagedPlayerbotFeature` thinks for
   every bot on every map), then **Autosave** (per-character staggered timers that snapshot onto the save queue).

`IsWorldThread` (34 call sites) assumes that exactly one thread owns all simulation state.

### 2.2 Map update (`src/ArcaneCore.Game/Maps/Map.cs`, `Update`)

Per map, in order: (1) in-world packets for each player in the map, handled on the world thread; (1a) transit players;
(1b) logout timers; heartbeat for every unit (from a snapshot that is rebuilt only when membership changes); active-cell
marking (vmangos `UpdateActiveCellsSynch`); (1c) the `IMapUpdater` list (creatures, game objects, combat, totems, traps,
transports, trade, flight, quest NPC, loot rolls, stealth, enchant, item maintenance, instance data and others: 23
`AddUpdater` sites); (2) visibility (moved players, then moved objects in join order); (3) values; (4) flush per player;
(5) grid/terrain lifecycle; (6) work scheduled for after the update (the per-map version of vmangos ScheduleFarTeleport).

Grids and cells already exist (vmangos layout), and `World:Maps:UpdateActiveCellsOnly` limits creature updates to
active cells. That cell structure is what intra-map partitioning would use later (W7).

### 2.3 Network IO and packets

- `WorldSession` (`src/ArcaneCore.World/Net/WorldSession.cs`): one async read loop per connection on the thread pool
  (`Stream.ReadAsync`, one header byte and then `ReadExactlyAsync` of the rest, with a per-frame deadline). Each payload is
  a fresh `byte[]`. Character-screen packets are handled on the session task. **In-world packets are queued**
  (`ConcurrentQueue<QueuedPacket>`) and handled on the world thread during their map's update.
- Sends: each frame is a new `byte[]`; the header is encrypted under `_sendLock` (so cipher order matches queue order) and
  written to an unbounded `Channel<byte[]>` (SingleReader) drained by one writer task, which coalesces frames into a
  buffer. **Sending is already thread-safe from any thread.** That matters for W3: a whisper from a map-0 thread to a
  map-1 player is safe today.
- Managed (bot) sessions capture outbound packets instead of serializing them (`CaptureManagedPacket`).

### 2.4 Creature AI and pathfinding

- `CreatureMapSystem` is one per map. It runs AI and motion generators on the world thread under a **per-map, per-update
  path-search budget** (`CreaturePathBudget`: searches, nodes, ms). Requests over the budget are deferred with fairness
  rotation, never sent in a straight line. The budget is per map, so while maps run one after another the world-thread
  cost is maps x budget.
- Navmesh A* (`NavMeshQuery`, `NavMeshPathfinder`) already uses **`[ThreadStatic]` scratch state** and per-thread counters
  (`PathSearchCounters`). Since 2026-10-11 it allocates 12.7 KiB per query instead of 3.8 MiB. Tile loads can be
  prefetched off-thread (`TilePrefetchCache`), and the region graph builds on `Task.Run`. The pathfinding core is therefore
  close to safe to run in parallel. The parts that are not: shared tile caches and map-owned collision views (audit in W2/W5).
- Bots: `PlayerbotNavigationExecutor` is a bounded off-thread executor (`World:Playerbots:AsyncNavigationEnabled`) that
  takes **detached value-only requests**, keeps private query state, and lets the owner validate results with
  supersession/staleness rules ([ASYNC_NAVIGATION_ACCEPTANCE](../ASYNC_NAVIGATION_ACCEPTANCE.md)). Today it covers
  long-distance quest travel only. **This is the pattern every offload in this plan reuses.**

### 2.5 Playerbots

`ManagedPlayerbotFeature` thinks for all bots in the Features phase, on the world thread, under an adaptive per-tick think
budget (`PlayerbotThinkBudget`: min/max ms, tick share, a priority combat lane and a normal lane rotated by cursor). A single
think is bounded by `MaxThinkSearchNodes`, not by time. That is how a 1.1 s policy snapshot (`PlayerbotBrain.PolicySnapshot`:
quest, town, trainer, riding and destination candidate scans plus target collection) gets through. The policy engine and
telemetry already use worker threads for scoring. Bot chat inference (`InProcessChatEngine`, `InferenceTeam`) owns
dedicated BelowNormal threads.

### 2.6 Persistence

`docs/areas/persistence.md` describes the details. In short: `CharacterSaveQueue` is a single consumer with per-character
coalescing, a priority lane for settlements, and busy back-off. About ten more write queues each have their own consumer
and connection to `characters.db`. WAL is on, `busy_timeout` and the write timeout are 5 s, checkpoints run off-thread
(`SqliteCheckpointService`), and EF transactions are `BEGIN IMMEDIATE`. The page itself lists the open limits: "Other
background writers ... keep their own queues and connections" and "One consumer: ... a single slow store call still delays
every other character".

### 2.7 Clustering (M15)

The design fixes "one owner per map/instance" with gateways and durable ownership fencing. Map-level parallelism inside one
process uses the same unit of ownership, so W3 is a step toward clustering, not a rival to it: a map that can run on any
thread under an owner contract can also run in another process.

### 2.8 Shared mutable state and hazards for parallel maps (inventory to finish in W2)

| Hazard | Where | Treatment |
|---|---|---|
| `_maps` dictionary mutated during the maps phase (an instance created from inside a map update: "a map system may create another map") | `WorldRuntime.GetMap` | Create maps only in serial phases; a request made during the parallel phase is queued (vmangos runs instance creation on its own thread and switches players only after all maps finish) |
| Feature singletons shared by every map's updater (`TradeMapUpdater(this)`, `FlightUpdater(this)`, `TotemMapUpdater(this)`, `SkillPersistenceUpdater(this)`, `TransportSystem`, `InstanceManager`, `SpellSystem`, loot) | 23 `AddUpdater` sites | Classify each piece of state: per-map (move it into the map), shared read-only after load (fine), or shared mutable (lock, partition by map, or route through the serial phase) |
| Static mutable fields (e.g. `PassiveReapply._depth`, `BotLeanStats` counters) | Game | `[ThreadStatic]` / Interlocked / per-map; enforce with an analyzer |
| 34 `IsWorldThread` assertions | World, Playerbots, Game | Generalize to "owner of this map or serial phase"; keep the strict meaning for world-global services |
| `Random.Shared` (83 uses) | everywhere | Thread-safe but not reproducible; deterministic tests need per-map seeded RNGs (W2) |
| Cross-map actions (groups/raids, loot rolls, chat channels, mail, AH, guilds, battleground queues, far teleports, summons) | features | Do them in the serial phase or through mailboxes (§5.2) |
| Event observers (`MapUpdated`, `Stats.RecordFeature`, fault/anomaly ledgers) | Kernel | Make thread-safe (the ledgers mostly are) or per-map with a merge at the barrier |
| Per-map diagnostics (`ReusableDiagnostics`) | Map | Already per map: fine |

### 2.9 Allocation hotspots seen in code

- Per-packet `byte[]` on receive and on send (`new byte[headerLength + payload.Length]`), plus `payload.ToArray()` for the
  observer.
- The update-object builders (`UpdateData`) and snapshot lists. Most hot ones were already pooled in the
  2026-10-04/08/10 allocation passes ([tick-perf](../integration/tick-perf-20261011.md)).
- LOH at 918 MB: worth one `dotnet-gcdump` to find which large arrays or dictionaries live there (W9).

---

## 3. Reference survey

### 3.1 TrinityCore / AzerothCore: a per-map task pool

`MapUpdater::activate(num_threads)` starts N workers. `MapManager::Update` calls `schedule_update(map, diff)` for each map
and then `wait()` until `pending_requests == 0`, and only then runs delayed (cross-map) work. The default is
`MapUpdate.Threads = 1`. A continent is still one Map, so a crowded continent stays on one thread. AzerothCore turned down
continent partitioning in 2025 ("map partitioning is nothing we will pursue on AC") and is pursuing clustering
(ToCloud9, PR 16832) instead. Maintainers warn that more threads than cores add contention.
Sources: [MapUpdater.cpp](https://github.com/TrinityCore/TrinityCore/blob/master/src/server/game/Maps/MapUpdater.cpp),
[AC issue 22571](https://github.com/azerothcore/azerothcore-wotlk/issues/22571),
[AC PR 16832](https://github.com/azerothcore/azerothcore-wotlk/pull/16832),
[AC issue 22467](https://github.com/azerothcore/azerothcore-wotlk/issues/22467).

**What we take:** the barrier model (schedule all maps → wait → serial cross-map work). Thread count should be small and
configurable.

### 3.2 vmangos: continents in parallel plus parallelism inside a map

From `mangosd.conf.dist.in` and `MapManager.cpp` / `Map.cpp` on the development branch:
- `MapManager::Update`: runs `UpdateSync` serially for every map, then **one thread per continent** (`m_continentThreads`)
  and a pool for instances (`MapUpdate.Instanced.UpdateThreads`, default 2). Instances may update repeatedly while the
  continents are still running. `CreateNewInstancesForPlayers` runs on its own thread. `SwitchPlayersInstances` and far
  teleports run only after every map has finished (`ScheduleFarTeleport` queues while `asyncMapUpdating`).
- `Continents.Instanciate`: "Run continents as instanced maps split along zone boundaries to reduce update times".
- `MapUpdate.Continents.MTCells.Threads` / `SafeDistance` (default 1066 yd, about two grids): parallel cell updates on
  one continent. Rows are striped by `safeDistCells`, and each stripe set runs in **two steps**
  (`threadId = 2*threadId + step`). Stripes processed at the same time are therefore always at least one safe distance
  apart: a red-black decomposition.
- `Continents.MotionUpdate.Threads`: motion and spline updates (`UpdateMotionAsync`) in parallel.
- `MapUpdate.ObjectsUpdate.MaxThreads` / `VisibilityUpdate.MaxThreads` (default 4): building update packets and visibility
  in parallel inside a map, with work handed out through an atomic counter and a time bound.
- Load shedding: `MapUpdate.ReduceGridActivationDist.Tick`, `ReduceVisDist.Tick` (with minimums),
  `Continents.InactivePlayers.SkipUpdates`, `MapUpdate.Empty.UpdateTime`.
- Warning in the config: preload terrain or risk crashes with more than one map thread ("one map loads a tile, while the
  other uses pathfinding").
Sources: [vmangos wiki: Server Architecture](https://vmangos.github.io/wiki/Server-Architecture/),
[Map.cpp](https://github.com/vmangos/core/blob/development/src/game/Maps/Map.cpp),
[MapManager.cpp](https://github.com/vmangos/core/blob/development/src/game/Maps/MapManager.cpp).

**What we take:** almost all of it, in stages. ArcaneCore already follows vmangos for grids, active cells and
ScheduleFarTeleport, so the same partition and safe-distance rules carry over. The terrain warning means W3 needs
thread-safe tile caches or preloaded tiles.

### 3.3 cMaNGOS / MaNGOS forks

cMaNGOS-classic keeps a single map-update thread in mainline (its config covers grid loading, `Autoload.Active` and so
on). The mangosR2 "MTMaps" fork added a map thread pool much like TrinityCore's ("For stable works 1-3 threads").
Sources: [cmangos mangosd.conf](https://github.com/cmangos/mangos-classic/blob/master/src/mangosd/mangosd.conf.dist.in),
[mangosR2 MT01](https://github.com/mangosR2/mangos/commit/46e5ddc5cc899e2b065a55fc230d4b8debbb156d).

### 3.4 Albion Online: one thread per zone, everything else offloaded

"The game logic of a cluster is handled by a single thread. Making this multi-threaded would be very error-prone ... all
incoming player commands are put into a synchronized event queue ... Any work that can be offloaded, like database
operations, pathfinding or logging, are handed to separate thread pools ... and put the result back into the event
queue." Monsters create AI timers when a player comes near and delete them when the player leaves.
Source: [David Salz, Secrets of Albion Online part 1](https://davidsalz.de/secrets-of-albion-online-part-1/).

**What we take:** keep simulation single-writer per partition, and offload only pure work (paths, DB, planning) with
results posted back. Event- or timer-driven AI for inactive areas is a form of AI level of detail (W6).

### 3.5 EVE Online: Time Dilation

When a node cannot keep up, TiDi slows the game clock (down to 10 %) so every request is served fairly, instead of letting
work queue up without bound. Its author notes that going multithreaded "increases how much load can be processed per
second, but never as much as most people think".
Sources: [Introducing TiDi](https://www.eveonline.com/news/view/introducing-time-dilation-tidi),
[TiDi: how's that going](https://www.eveonline.com/news/view/time-dilation-hows-that-going).

**What we take:** the principle (degrade by design, do not let queues grow). The mechanism does not fit: the 1.12.1
client runs its own timers (casts, swings, buffs), so we cannot slow the clock. The WoW-compatible equivalents are
already partly built: budgets, deferral, active cells, vmangos-style activation and visibility reduction. W6 makes them
load-adaptive and scales cadence only for systems players cannot see (inactive creatures, bot think frequency).

### 3.6 SpatialOS (discontinued): spatial authority and load balancing

Entities had one authoritative worker per component, workers covered spatial regions, and a load balancer moved authority
as entities moved. The difficulty was always the boundary: interest overlap and authority handoff. That is the problem W7
(intra-map stripes) avoids with a safe distance, and that M15 handles with fencing.
Sources: [Worker authority demo](https://github.com/FriendsOfSpatial/Worker-Authority-demo),
[Improbable: dynamic interest management](https://medium.com/improbable-engineering/thinking-spatially-using-dynamic-interest-management-to-create-revolutionary-game-designs-80ed0fe7693).

### 3.7 ECS / data-oriented job systems (Unity DOTS, Bevy)

Systems declare their data access. A scheduler runs systems with no conflicts in parallel and serializes the rest (Bevy:
`Res` vs `ResMut`, `Query` filters, ambiguity detection). Unity's job system runs small jobs over contiguous data with a
safety system that detects races.
Sources: [Bevy ECS](https://bevy.org/learn/quick-start/getting-started/ecs/), [Bevy B0002](https://bevy.org/learn/errors/b0002/),
[Unity job system](https://docs.unity3d.com/Manual/job-system-overview.html),
[Unity Entities](https://docs.unity3d.com/Packages/com.unity.entities@1.0/manual/index.html).

**What we take:** an ECS rewrite is not an option for a 500 k-line object-oriented code base. Two ideas carry over: (a) a
**declared access contract per `IMapUpdater` / feature** (map-local, global-read, global-write) that a test can check, and
(b) **batched data-parallel kernels** where the data is already flat (visibility candidate distance tests, aura tick
lists, threat decay).

### 3.8 Actor frameworks (Orleans, Akka.NET, Proto.Actor)

Actors give single-threaded state per actor and message passing. That fits "one owner per map", and Orleans grains map to
cluster placement. The costs: every cross-entity interaction becomes asynchronous, latency is per message, scheduling is
non-deterministic, and you take a runtime dependency. A 50 ms lock-step tick with tight in-map interactions (spells, threat,
visibility) does not suit per-entity actors. **Verdict:** use actor-style *mailboxes at map granularity* (§5.2), built on
`Channel<T>` / `ConcurrentQueue<T>`. No framework. Revisit Orleans only for realm-wide services in M15 (guilds, mail, AH).
Sources: [Orleans overview](https://learn.microsoft.com/en-us/dotnet/orleans/overview), [Akka.NET](https://getakka.net/articles/intro/what-is-akka.html),
[Proto.Actor](https://proto.actor/).

---

## 4. .NET techniques: where each one fits

| Technique | Fit for ArcaneCore | Verdict |
|---|---|---|
| Dedicated threads vs ThreadPool for map workers | Map updates are CPU-bound, long (10-60 ms) and run every tick | **Dedicated worker threads** (N fixed, named, priority AboveNormal) driven by a barrier. Avoids ThreadPool injection lag and starving async IO. Use `Parallel.For` only inside short data-parallel kernels |
| `Parallel.For` / partitioners | Visibility candidate tests, per-cell stripes (W7) | Yes, with a range partitioner and a `MaxDegreeOfParallelism` cap ([partitioners](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/custom-partitioners-for-plinq-and-tpl)) |
| `System.Threading.Channels` | Persistence writer, cross-map mailboxes, nav job queue, outbound frames | **Yes.** Bounded with `FullMode` for backpressure where loss is acceptable (telemetry); never drop saves ([Channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)) |
| TPL Dataflow | Multi-stage pipelines | Not needed; Channels plus a loop is simpler and lighter ([Dataflow](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/dataflow-task-parallel-library)) |
| `ConcurrentQueue` / lock-free | Already used for commands and packets | Keep. Prefer per-map single-producer queues to one global contended queue |
| `ValueTask` | Hot async paths that usually complete synchronously | Use in the session read path only if profiling shows Task allocations; low priority |
| `ArrayPool<T>` / `ObjectPool<T>` / `Span<T>` | Packet frames, update blocks, path corners | Yes for packet frames (W8) and per-map scratch ([ArrayPool](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1), [ObjectPool](https://learn.microsoft.com/en-us/aspnet/core/performance/objectpool)) |
| System.IO.Pipelines | Session read loop | Optional (W8): removes the per-packet `byte[]` and the 1-byte first read ([Pipelines](https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines)) |
| Server GC + DATAS | Already on (DATAS is the default with Server GC since .NET 9) | Keep, but **bound it**: test `GCHeapCount` (which turns DATAS adaptation off) or a heap affinitize range below the core count, so GC threads do not fight the map workers and the build workers ([DATAS](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/datas), [GC config](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector), [.NET 10 GC](https://devblogs.microsoft.com/dotnet/preparing-for-dotnet-10-gc/)) |
| `GCLatencyMode.SustainedLowLatency` | Avoids blocking gen2 while it is set (background GC still runs) | Try it as a startup option in W9 and A/B the p99 ([latency modes](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/latency)) |
| Background (concurrent) GC | On | Keep; the 1-4 s pauses were blocking gen1/gen2 under host starvation ([background GC](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/background-gc)) |
| `ThreadPool.SetMinThreads` | Read loops, save queues and nav executor share the pool | Raise the minimum modestly (e.g. 2x cores) once map workers exist, so injection delay never stalls IO ([SetMinThreads](https://learn.microsoft.com/en-us/dotnet/api/system.threading.threadpool.setminthreads), [threading config](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/threading)) |
| NativeAOT | Startup and memory | **No for now.** Hot code reload (`World:HotCode`), reflection and EF Core work against it, and steady-state tick speed would not improve ([NativeAOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)) |
| Optimized build (Release or Optimize=true) | All CPU-bound code | **Highest return for the effort**; Nate's decision (§10) |
| SQLite WAL + single writer + batched transactions | characters.db | **Yes (W1).** "only one writer can append to the WAL at a time" ([WAL](https://sqlite.org/wal.html)); Microsoft.Data.Sqlite async "will execute synchronously" ([async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)). `BEGIN CONCURRENT` is a branch feature, not in stock builds ([begin-concurrent](https://www.sqlite.org/src/doc/begin-concurrent/doc/begin_concurrent.md)) |
| Detour query objects | Pathfinding on several threads | One query object per thread; the navmesh is shared read-only ([dtNavMeshQuery](https://recastnav.com/classdtNavMeshQuery.html)). ArcaneCore's `[ThreadStatic]` scratch already does this |
| BenchmarkDotNet | Micro-benchmarks | Already present (`tools/perf/ArcaneCore.Benchmarks`); add map-update and writer benchmarks ([BenchmarkDotNet](https://benchmarkdotnet.org/)) |

---

## 5. Target architecture

### 5.1 The tick with a parallel map phase

```
world thread                         map workers (N)
------------                         ---------------
S1 serial pre-phase
   commands, WorldTick handlers,
   world scripts, apply mailboxes,
   pending map creates, transfers
S2 publish map list --------------->  P  for each map (one map = one owner for this phase):
   (barrier)                              packets → heartbeat → active cells → updaters
                                          → bot think for this map's bots (W4)
                                          → visibility → values → flush → grid lifecycle
                                          writes only map-local state; anything global
                                          goes to the map's outbox
S3 wait for all <-------------------
S4 serial post-phase
   drain outboxes in (mapId, instanceId, seq) order,
   far teleports and instance switches,
   unload maps, remaining features, autosave
```

- **Unit of ownership:** a `Map` (continent or instance). Later, a continent partition (W7). A map runs on exactly one
  thread per tick, so all code inside `Map.Update` stays single-threaded with respect to that map. That is the invariant
  that keeps 214 k lines of game code correct without per-object locks.
- **Scheduling:** longest map first (by last tick's cost) onto N workers, with the world thread as worker N+1. Map 0 and
  map 1 start first, and instances fill the gaps (LPT scheduling, as vmangos runs instances while continents finish).
- **Default N=1** reproduces today's serial order exactly (same loop, same order), so the flag is safe to ship off.

### 5.2 Cross-map interaction rules

| Interaction | Rule |
|---|---|
| Send a packet to any session | Allowed from any thread (already locked and channelled) |
| Read another map's objects | **Not allowed** in the parallel phase. Use cached snapshots (group member positions and health are published at S4 for the next tick) |
| Mutate global services (groups, guild, mail, AH, channels, BG queue, loot roll across maps) | Post to the world outbox; apply in S4 (or S1 next tick). Most of these are already Post-based or feature-level |
| Teleport / change map / summon across maps | Request recorded on the map; executed in S4 (extends the existing per-map "scheduled after update" list to a global barrier) |
| Create an instance | Queued; created in S4/S1 (never inside P) |
| Writes to the DB | Value snapshots taken on the owner thread, enqueued to the writer (unchanged contract) |

Ordering is deterministic: outboxes drain in stable map order and then sequence order, so N=1 and N=8 apply the same
commands in the same order.

### 5.3 Offloaded pure work (Albion / async-nav pattern)

Pathfinding, bot planning snapshots, quest-candidate scans, LLM chat and DB writes run as **detached value-only jobs**. The
owner validates each result on its thread (generation, map, instance and lifecycle checks, as
`PlayerbotNavigationExecutor` already does), and a pending result never counts as a failure.

---

## 6. Workstreams

Each workstream lists the goal, the expected gain, the risk, the invariants to keep, how to test and measure it, the
rollout flag, and its first steps. Gains are estimates to be confirmed by §8; they are not measurements.

### W0. Measure first; operational quick wins (no game-code change)

- **Goal:** separate code cost from host cost, and get the cheap multipliers.
- **Steps:**
  1. A/B an optimized build (Release, or Debug with Optimize=true) on the same population with the 200-bot terrain
     benchmark and live TickMonitor lines. **Needs Nate's go** (ROADMAP: "live stays on Debug").
  2. Host isolation while builds and live share the PC: run the world server at AboveNormal priority and builds at
     BelowNormal, or give the server a CPU set. **Needs Nate's go** ("leave the build setup as is").
  3. GC experiment matrix (runtimeconfig or env only): current / `DOTNET_GCHeapCount`=6 / `GCHeapAffinitizeRanges` / +
     SustainedLowLatency. Record max pause, p99 tick and working set.
  4. One `dotnet-gcdump` and one `dotnet-trace` CPU sample of the live server to find the LOH contents and the top
     inclusive methods.
  5. Log volume: about 95 MB of text plus 93 MB of JSONL per day at debug level. The writers are already off-thread
     (`QueuedLineWriter`), but formatting cost stays on the caller. Check with the trace.
- **Expected gain:** an optimized build typically gives 1.5-3x on CPU-bound loops; isolation removes most of the 3,652
  host-starved frames; a bounded GC should remove the 1-4 s pauses.
- **Risk:** low. Invariants: none changed. Rollout: deploy-time only.

### W1. A single characters-database writer with batched transactions

- **Goal:** zero `database is locked`, bounded save latency, and higher write throughput on SQLite. MariaDB/MySQL/Postgres
  keep today's behaviour.
- **Design:** a `CharacterDbWriter` hosted service that owns **one** write connection to `characters.db` and drains a
  `Channel<WriteBatchItem>`. The existing queues (`CharacterSaveQueue`, `KeyedStoreWriteQueue` and the ~10 feature write
  queues) keep their APIs, coalescing and priority rules. They stop opening their own write transactions and submit units
  of work (`Func<DbContext, Task>` plus a completion source) to the writer. The writer groups everything ready (up to K
  items or T ms, e.g. 64 items / 20 ms) into **one `BEGIN IMMEDIATE ... COMMIT`**, and completes each unit's TCS after the
  commit. A unit that fails is retried alone, so one bad row never fails a batch. Readers keep their own connections (WAL
  readers do not block the writer).
- **Expected gain:** the lock contention disappears by construction (one writer). Fewer commits means fewer WAL syncs, so
  batching 20-60 units per commit cuts per-save cost by an order of magnitude. Settlement budgets stop expiring behind
  unrelated writers.
- **Risk:** medium (persistence is critical: "never lose a save").
- **Invariants:** per-character order; latest-wins coalescing; settlement and barrier semantics (`FlushCharacterAsync`);
  retained failure merge; the final drain at stop; never report written before commit.
- **Tests:** the existing `CharacterSaveQueueTests` and `SaveQueueLoadBenchmarkTests` (`ARCANECORE_SAVE_BENCH=1`) run against
  the writer. New: N concurrent producers x M characters with a seeded fault injector, checking order and durability (read
  back after a simulated crash: kill before or after commit). Metric: batch size, commit ms, queue age (gauges exist).
- **Rollout:** **Database:Sqlite:SingleWriter** (default off, then on for SQLite after a soak).
- **First steps:** see §9 step 2.

### W2. Thread-ownership foundation (prerequisite for W3/W4/W7; behaviour-neutral)

- **Goal:** make "who may touch this" explicit and machine-checked before anything runs in parallel.
- **Steps:**
  1. `TickOwner` context: the world thread, or "map worker for map M", held in a `[ThreadStatic]` set by the runtime.
     `IsWorldThread` gets a sibling `IsOwner(Map)`. Debug-only `OwnerGuard.Assert(map)` goes on entity mutators (the
     position setter, `Map.Add/Remove`, aura apply, and so on). In tests it fails fast; in Release it counts to the anomaly
     ledger.
  2. Inventory every `IMapUpdater` and feature state field into map-local / global-readonly / global-mutable (§2.8).
     Record it as a `[MapAccess(...)]` attribute plus a reflection test that fails when a new updater lacks one (the
     Bevy-style declared access).
  3. Static mutable audit: a Roslyn analyzer or test that lists non-readonly static fields in Game/World/Playerbots, with
     an allow-list. Fix `PassiveReapply._depth` and similar with `[ThreadStatic]`.
  4. Move map creation out of `GetMap` during the maps phase into a request queue (a no-op while N=1; ordering kept).
  5. Per-map seeded RNG behind the existing calls, for reproducible tests (production may keep `Random.Shared`).
- **Gain:** none directly; it lowers the risk of everything after it. **Risk:** low.
- **Tests:** the guard runs in the entire existing suite (CI is the gate). A new "serial equals parallel" harness (§8) is
  added here with N=1 so it is green before W3.

### W3. Parallel map update (continents and instances)

- **Goal:** the maps phase costs max(map) instead of sum(maps).
- **Design:** §5.1. A `MapUpdateScheduler` with N dedicated workers, `Barrier` or `CountdownEvent` signalling, LPT order,
  per-worker `GC.GetAllocatedBytesForCurrentThread` accounting (today's phase byte counters assume one thread), and
  per-map exception isolation (already per-map try/catch). `TickStats` and the slow-map line gain "worker" and "wait"
  fields. Terrain: make the tile caches thread-safe (`ConcurrentDictionary` with `Lazy<Task<>>` loads) or preload tiles
  (vmangos' warning).
- **Expected gain at today's load:** maps phase p50 29 → 15-18 ms, p99 64 → about 35 ms. With W4 the 17-100 ms bot
  phase splits as well. In real terms, about 2x headroom at 1,000 bots, more as instances fill.
- **Risk:** high: data races in feature singletons. W2 is mandatory first.
- **Invariants:** per-map order of phases unchanged; per-session packet order unchanged; cross-map effects applied in a
  deterministic order; manual-clock tests (`UseManualClock`, `AdvanceClockAsync`) give identical results at N=1 and N>1.
- **Tests:** the serial-equals-parallel state-hash test; stress runs with `ARCANECORE_BOT_BENCH=200/1000` at N=1/2/4; the
  watchdog's position must name the map and worker. Benchmarks: `WorldTickLoadTests` (66 k units) with two continents
  populated.
- **Rollout:** **World:Parallel:MapUpdateThreads** = 1 (default; today's exact behaviour), 2 (continents), then
  min(cores/2, 6). Read at start.

### W4. Bot think: partition per map, then offload planning

- **Goal:** take `ManagedPlayerbotFeature` out of the serial Features phase and remove the think outliers.
- **Steps:**
  1. Run each map's bots inside that map's parallel task (after the updaters, before visibility). The budget becomes per
     map (the adaptive inputs exist). A think acts only on its own player and session plus map-local reads; group/social
     coordinators post to the outbox.
  2. Offload the policy snapshot and the candidate scans (quest, town, trainer, riding, destinations) as detached jobs:
     capture value facts on the owner, compute on the pool, validate on return (the async-nav acceptance contract). A
     think that would block takes "pending" instead.
  3. Add a **time** cap per think, not just a node cap: preempt at a phase boundary and resume on a later tick.
- **Expected gain:** a features phase of 17-100 ms becomes roughly max(per-map share) and runs in parallel; no 1.1-3.6 s
  thinks on the tick.
- **Risk:** medium-high (bot behaviour). Invariants: the pending-is-not-failure rule; quest/backoff policy unchanged; one
  bot is never thought twice in a tick.
- **Tests:** playerbot QA suites, the 200-bot terrain benchmark (think ms per tick, coverage), bot-stall anomaly rate on
  live. **Rollout:** **World:Playerbots:ThinkInMapPhase**, then **World:Playerbots:AsyncPlanning**.

### W5. Creature pathfinding job system

- **Goal:** remove A* time from the map's owner thread entirely, and serve deferred requests sooner.
- **Design:** generalize `PlayerbotNavigationExecutor` into a shared `NavJobSystem`: bounded queue, priority classes
  (combat chase / evade > scripted > waypoint > random), per-map quotas, cancellation, generation fencing. Motion
  generators request a path and hold their current behaviour (hold position, or keep following the previous corner list)
  until the result arrives, usually next tick. The per-update budget becomes a cap on in-flight jobs per map. Add a small
  per-map path cache keyed on (start tile/poly, end poly) for the hot repeated waypoint and random-move legs.
- **Expected gain:** frees the 1-14 ms of searches per map update plus the long-tail single searches (120-560 ms reported
  in the task, 162 ms creature outliers). Throughput rises with cores, so the backlog of 0.3-1.9 million deferred outcomes
  shrinks.
- **Risk:** medium. A one-tick path delay changes chase feel, so chase can keep a synchronous fast path for short searches
  below X nodes.
- **Invariants:** no straight-line fallback for want of budget (an existing rule); NOPATH semantics unchanged; stale results
  dropped.
- **Tests:** `ActiveCellReactivationTests`, the chase fairness tests, `NavMeshSearchCostTests` (identical corridors through
  the job path), and a BenchmarkDotNet path benchmark at 1/4/8 threads. **Rollout:** **World:Navigation:CreatureJobs**.
- **Status (2026-10-24):** bot plans now use the pool too: far queries of the `PlanExact`/`PlanToward`/`PlanFar`/`PlanWander` wrappers run as `NavJob`s with shared tiles (`World:Playerbots:PathJobs`, off by default; see `docs/ASYNC_NAVIGATION_ACCEPTANCE.md`), plus a `SyncPathQueryMaxMs` wall-time deadline for any search still on the world thread. Creature jobs are unchanged. Live and 200-bot benchmark acceptance pending.

### W6. AI level of detail and adaptive activation

- **Goal:** do less work, not just do it on more threads.
- **Steps:**
  1. **Bots activate fewer cells than players.** Today 1,000 bots spread over two continents keep about 4-5 k creatures
     per continent active. Give bots a smaller activation radius (or activation only when a real player is within
     visibility range) behind **World:Maps:BotActivationRadius**.
  2. Time-sliced updates for active-but-distant creatures (update every k ticks with an accumulated diff; vmangos does this
     for inactive players with `Continents.InactivePlayers.SkipUpdates`).
  3. Load-adaptive activation and visibility distance like vmangos `MapUpdate.ReduceGridActivationDist.Tick` /
     `ReduceVisDist.Tick`, with minimum values, driven by the TickMonitor p99.
  4. Per-creature AI time cap with logging: entry 1941 at 162 ms needs a profile, not a thread.
- **Expected gain:** potentially the largest drop in work at bot-heavy loads (active creatures scale with bot spread).
- **Risk:** medium (world liveliness, patrols, escort scripts). Invariants: creatures near real players always update every
  tick; active objects (formations, script command 21) unchanged.
- **Tests:** `ActiveCellUpdateTests`, escort and formation tests, a live A/B of creature-AI ms and bot quest completion rate.

### W7. Intra-map parallelism (after W3 is stable)

- **Goal:** one crowded continent stops being the limit (for example, everyone in Orgrimmar or at a world event).
- **Options, in order:** (a) parallel **visibility and update-packet building** per player, which is read-mostly with
  per-viewer output (vmangos ObjectsUpdate/VisibilityUpdate threads); (b) parallel **motion/spline** updates (vmangos
  `Continents.MotionUpdate.Threads`); (c) **striped cell updates** with a safe distance and two-step red-black scheduling
  (vmangos MTCells), with cross-stripe effects deferred to the stripe barrier; (d) zone-split continents
  (`Continents.Instanciate`-like), which interacts with M15 handoff and is the most invasive.
- **Risk:** high for (c)/(d), moderate for (a)/(b). Same test strategy as W3, plus stripe-boundary fixtures (combat and AoE
  across a stripe line).

### W8. Network buffers

- **Goal:** cut per-packet allocations and make outbound backpressure explicit.
- **Steps:** pooled outbound frames (`ArrayPool<byte>`, returned by the writer after the write); drop the
  `payload.ToArray()` copy when no observer is attached; Pipelines-based read loop (no 1-byte read, no per-packet payload
  array where the handler copies anyway); a bounded outbound channel with a per-session byte cap that disconnects a client
  that never reads (bounded memory).
- **Gain:** fewer gen0 GCs at high packet rates; protects memory against slow clients. **Risk:** low-medium (buffer
  lifetime bugs). Tests: MockClient suites, `WireOracle`, the packet-capture tests.

### W9. Allocation and GC

- **Goal:** no blocking pause over 50 ms in steady state.
- **Steps:** act on the W0 gcdump (LOH contents); pool per-map scratch that is still allocated per tick; keep large stable
  tables in arrays of structs instead of object graphs where the data is hot (aura lists, threat tables); review the
  `MemoryMonitor` opt-in forced compacting collection so it never fires on the live tick path; adopt the winning GC
  configuration from W0.
- **Tests:** the existing per-phase byte counters and `VisibilityAllocationTests`; GC pause histogram in `.server info`.

### W10. Clustering interplay (M15)

W2's ownership contract and W3's mailboxes are exactly what M15's map owner needs: the same rules decide whether a
cross-map effect goes to another thread or another process. Keep the outbox message types serializable (plain records) so
the in-process and gRPC transports share them.

---

## 7. Rollout and observability

| Flag (proposed) | Default | Read | Notes |
|---|---|---|---|
| **Database:Sqlite:SingleWriter** | off → on for SQLite | start | W1 |
| **World:Parallel:MapUpdateThreads** | 1 | start | W3; 1 = today's serial loop |
| **World:Playerbots:ThinkInMapPhase** | off | start | W4 step 1 |
| **World:Playerbots:AsyncPlanning** | off | start | W4 step 2 |
| **World:Navigation:CreatureJobs** | off | start | W5 |
| **World:Maps:BotActivationRadius** | 0 (same as players) | live | W6 |

Metrics to add (Prometheus/OTLP through the existing `ArcaneCore.World` meter): per-map update ms histogram (labelled by
map and instance type); barrier wait ms per worker; outbox depth and drain ms; nav job queue depth, wait and compute ms;
writer batch size and commit ms; GC pause histogram. The slow-map line gains "worker=k wait=x ms".

Every live change ships with: the flag, a before/after table from the same harness, host load and GC mode recorded, and a
rollback (flag off) tested.

---

## 8. Test and benchmark strategy

1. **Serial-equals-parallel state hash (the key correctness test).** Manual clock, fixed seeds, a scripted world (two
   continents plus instances, players, bots and creatures). Run T ticks at N=1 and N=4 and compare a canonical hash of
   every entity's position, health, auras, threat, quest state and the outbound packet stream per session. Any difference
   fails. Added in W2 (N=1 only), turned on in W3.
2. **Race detection.** The debug `OwnerGuard` runs in all CI tests. Run a stress job with random map-to-worker assignment
   and N > cores. .NET has no TSan for managed code, so the guard and the hash test are the substitute.
3. **BenchmarkDotNet** (`tools/perf/ArcaneCore.Benchmarks`): map update of a synthetic continent at 1/2/4/8 workers;
   `CharacterDbWriter` throughput at batch 1/16/64; pathfinding job throughput; frame pooling.
4. **Harness benchmarks already in the repo:** `WorldTickLoadTests`, `ManagedPlayerbotTerrainBenchmarkTests`
   (`ARCANECORE_BOT_BENCH`), `SaveQueueLoadBenchmarkTests` (`ARCANECORE_SAVE_BENCH`), `ActiveCellReactivationTests`.
   Report per-phase numbers, not just whole ticks (the tick-perf report shows why).
5. **Live A/B:** TickMonitor p50/p99, slow-map verdict mix, GC max pause, `database is locked` count, bot-stall anomaly rate
   and quest completions per hour, over at least 1 h per arm with the same bot count. Builds are paused or deprioritized
   during measurement.

CI stays the gate. The box and the PC run only filtered tests (Nate's standing rule).

---

## 9. First three PR-sized steps

1. **"Tick ownership groundwork" (W2.1-W2.4, behaviour-neutral).**
   - `src/ArcaneCore.Game/Maps/TickOwner.cs` (new): a `[ThreadStatic]` owner context; `WorldRuntime` sets it for the world
     thread and around each `map.Update`.
   - `Map`/`WorldRuntime`: `IsOwner(Map)`; a debug `OwnerGuard.Assert` on `Map.Add/Remove/Relocate` and
     `WorldObject` position setters.
   - `WorldRuntime.GetMap`: during the maps phase, queue the create and return the map after the phase (no-op at N=1).
     Add a test that an instance created during a map update appears next tick with the same content.
   - Test `MapUpdaterAccessInventoryTests`: every `IMapUpdater` type carries a `[MapAccess]` attribute; the static-mutable
     field scan with an allow-list; `PassiveReapply._depth` becomes `[ThreadStatic]`.
   - `SerialParallelEquivalenceTests` scaffold (N=1 vs N=1 for now; the hash function and scripted world).
   - Size: about 10 files. No config key; no behaviour change.
2. **"SQLite single writer" (W1), off by default.**
   - `src/ArcaneCore.Data/Writes/CharacterDbWriter.cs` (new): one connection, a `Channel` of units, batching (K/T),
     per-unit TCS, retry-alone-on-failure, drain at stop.
   - `CharacterSaveQueue` and `KeyedStoreWriteQueue` submit through it when **Database:Sqlite:SingleWriter** is on (other
     queues follow in later PRs, one or two per PR).
   - Options class plus `check-config` validation plus a configuration reference entry (so the doc key audit passes).
   - Tests: ordering, coalescing and settlement through the writer; a crash-before/after-commit durability test;
     `SaveQueueLoadBenchmarkTests` with the flag on and off.
3. **"Parallel continents" (W3 first slice), off by default.**
   - `src/ArcaneCore.Game/Maps/MapUpdateScheduler.cs` (new): N dedicated workers, LPT order, barrier, per-worker
     allocation and time accounting into `TickPhases`.
   - `WorldRuntime.RunTick`: when **World:Parallel:MapUpdateThreads** > 1, the maps loop hands off to the scheduler.
     `MapUpdated` observers and `LogIfSlow` move to after the barrier, in map order.
   - The cross-map outbox for the first two consumers (far teleport/transfer and instance creation); everything else that
     W2's inventory marked global-mutable stays guarded or serial. Until it is classified, an unclassified updater makes
     the scheduler fall back to serial for that map.
   - Tests: `SerialParallelEquivalenceTests` at N=2; the 66 k-unit `WorldTickLoadTests` with both continents populated.

Steps 1 and 2 are independent and can run side by side. Step 3 depends on step 1.

---

## 10. Decisions for Nate

1. **Optimized live build.** Measure Release (or Debug plus Optimize=true) on live, or keep Debug? This is the cheapest
   multiplier, and the ROADMAP says it is your call.
2. **Host isolation** while build workers and the live server share the PC: raise the server's priority, lower the builds',
   or give the server a CPU set? This touches the build setup you asked to leave alone.
3. **GC experiment** on live (runtimeconfig/env only, reversible).
4. **Order of W1 (DB writer) vs W2/W3 (parallel maps).** The plan runs them in parallel; W1 is lower risk and fixes visible
   errors now.
5. **Bot activation radius (W6.1):** may bots activate less of the world than real players?

---

## 11. References

- TrinityCore MapUpdater: https://github.com/TrinityCore/TrinityCore/blob/master/src/server/game/Maps/MapUpdater.cpp
- AzerothCore map partitioning request (closed, clustering instead): https://github.com/azerothcore/azerothcore-wotlk/issues/22571
- AzerothCore clustering PR (ToCloud9): https://github.com/azerothcore/azerothcore-wotlk/pull/16832
- AzerothCore thread-count advice: https://github.com/azerothcore/azerothcore-wotlk/issues/22467
- vmangos threading model: https://vmangos.github.io/wiki/Server-Architecture/
- vmangos Map.cpp (MTCells, motion, objects/visibility threads): https://github.com/vmangos/core/blob/development/src/game/Maps/Map.cpp
- vmangos MapManager.cpp (continent threads, instance pool, deferred teleports): https://github.com/vmangos/core/blob/development/src/game/Maps/MapManager.cpp
- cMaNGOS classic config: https://github.com/cmangos/mangos-classic/blob/master/src/mangosd/mangosd.conf.dist.in
- mangosR2 MTMaps: https://github.com/mangosR2/mangos/commit/46e5ddc5cc899e2b065a55fc230d4b8debbb156d
- Albion Online server architecture: https://davidsalz.de/secrets-of-albion-online-part-1/
- EVE Time Dilation: https://www.eveonline.com/news/view/introducing-time-dilation-tidi and https://www.eveonline.com/news/view/time-dilation-hows-that-going
- SpatialOS authority demo: https://github.com/FriendsOfSpatial/Worker-Authority-demo
- Improbable, dynamic interest management: https://medium.com/improbable-engineering/thinking-spatially-using-dynamic-interest-management-to-create-revolutionary-game-designs-80ed0fe7693
- Bevy ECS: https://bevy.org/learn/quick-start/getting-started/ecs/ and https://bevy.org/learn/errors/b0002/
- Unity job system: https://docs.unity3d.com/Manual/job-system-overview.html ; Entities: https://docs.unity3d.com/Packages/com.unity.entities@1.0/manual/index.html
- Orleans: https://learn.microsoft.com/en-us/dotnet/orleans/overview ; Akka.NET: https://getakka.net/articles/intro/what-is-akka.html ; Proto.Actor: https://proto.actor/
- .NET Channels: https://learn.microsoft.com/en-us/dotnet/core/extensions/channels
- System.IO.Pipelines: https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines
- ObjectPool: https://learn.microsoft.com/en-us/aspnet/core/performance/objectpool ; ArrayPool: https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1
- Partitioners: https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/custom-partitioners-for-plinq-and-tpl
- TPL Dataflow: https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/dataflow-task-parallel-library
- GC: DATAS https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/datas ; config https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector ; latency modes https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/latency ; background GC https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/background-gc ; .NET 10 GC https://devblogs.microsoft.com/dotnet/preparing-for-dotnet-10-gc/
- ThreadPool: https://learn.microsoft.com/en-us/dotnet/api/system.threading.threadpool.setminthreads ; https://learn.microsoft.com/en-us/dotnet/core/runtime-config/threading
- NativeAOT: https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/
- SQLite WAL: https://sqlite.org/wal.html ; BEGIN CONCURRENT: https://www.sqlite.org/src/doc/begin-concurrent/doc/begin_concurrent.md
- Microsoft.Data.Sqlite async limitations: https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async
- Detour dtNavMeshQuery: https://recastnav.com/classdtNavMeshQuery.html
- BenchmarkDotNet: https://benchmarkdotnet.org/

Related ArcaneCore pages: [persistence](../areas/persistence.md), [ops-perf](../areas/ops-perf.md),
[tick-perf 2026-10-11](../integration/tick-perf-20261011.md), [async navigation acceptance](../ASYNC_NAVIGATION_ACCEPTANCE.md),
[clustering design](../CLUSTERING_DESIGN.md), [roadmap](../ROADMAP.md).

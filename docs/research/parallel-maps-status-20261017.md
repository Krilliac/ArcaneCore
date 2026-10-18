# Parallel map updates: status and roadmap (2026-10-17, mt-maps lane)

Follows docs/research/multithreading-plan-20261017.md (W2, W3, W4). Everything here is off by default.

## What exists

| Setting | Default | Effect |
|---|---|---|
| `World:Parallel:MapUpdateThreads` | 1 | Above 1, the maps phase runs on N-1 `map-worker-k` threads plus the world thread. Maps are taken longest first (last update time), and a barrier runs before unloads and features. At 1 the serial loop is unchanged. |
| `World:Parallel:OwnerGuard` | Report | A change to a map from a thread that does not own it (`Map.EnsureWorldThread`, `OnObjectMoved`, `QueueValuesUpdate`) is logged as `Parallel map ownership violation:` and recorded in the anomaly ledger (`parallel`/`map-owner`, once per call site, with the stack). `Throw` makes it an exception. |
| `World:Parallel:GateAllStages` | false | Every stage runs under the serial gate. Use it to tell a race in map-local code from a fault in the scheduler. |
| `World:Parallel:Ungated` / `Gated` | "" | Experiment overrides by stage name (Packets, Heartbeat, ActiveCells, Output, Grid, Deferred) or updater type name. |
| `World:Parallel:CreatureSplinePass` | false | Splits the creature walk into a spline pass and the rest. With `Ungated=CreatureMapSystem`, the spline pass runs outside the gate. |

Model: a reentrant serial gate (`WorldRuntime.EnterSerial`), comparable to a big kernel lock. During the parallel phase, every map stage and updater that is not marked `[MapAccess(MapLocal)]` runs inside the gate, so turning N up is safe without further changes. Work moves out of the gate one audited piece at a time. Today only ActiveCells is map-local by default.

Diagnostics:
- One startup line: `Parallel maps phase: N map threads ...`.
- One line per minute: `Parallel maps phase (60 s): ... overlap xN; serial gate E entries, C contended, W ms waiting; barrier wait B ms; owner violations V`.
- Slow-map lines gain `; map threads N, last X ms`.
- The creature breakdown gains `spline pass X ms`.

Tests:
- ParallelMapUpdateTests (determinism at 1 vs N, gated and ungated).
- CreatureSplinePassTests (4 creature maps over 200 ticks at 1, 3 and 4 threads).
- StaticMutableStateAuditTests (every writable static in Game, World and Playerbots is listed with its thread-safety reason).

S-16, the determinism flake under load, was not a race. `UseManualClock` starts at the wall-clock reading, and create blocks carry NowMs+1000, so tick 0 differed by the set-up time. Whole-run comparisons now use `UseManualClockFrom(0)`.

## What blocks real overlap (measured on live, Debug)

Each continent's update is about 45-50 ms, and CreatureMapSystem accounts for about 90% of it. A world-thread CPU sample (mt-jobs, 20:15 CT) shows the world thread busy about 41% of wall time. Of that busy time, playerbot think is 54% (bot A* is 29%) and creature AI is 12.7%.

CreatureMapSystem cannot leave the gate as a whole. It reaches the following world-wide state:
1. **NavMeshPathfinder owner path.** It is one instance for all maps. `FindPath`, `LoadTile`, `_meshes` and the prefetch cache assume a single owner thread, and instances of one map id share a mesh. This is the mt-jobs area.
2. **SpellSystem.** It is one instance. Its runtime-mutable collections are `_states` (about 12 call sites), `_drunkPlayers`, `_deathAuraRemovals` and the `_reportedUnsupported*` sets. The rest is registration that is read-only during ticks. `IsCasting`, visibility (stealth) and AI all read `_states`. A lock that is held only during the phase would keep the enumeration order that a ConcurrentDictionary would change.
3. **AI, script and DB-script hooks**, plus spawn pools, groups and respawn persistence, all reached from AI, motion callbacks (`OnMovementFinished`, waypoint scripts) and evade.
4. **Grid creation and loading** on a cell change (spawns, terrain, navmesh tiles). This is already handled: `Map.OnObjectMoved` takes the gate for a relocation that changes cell.

Next steps, in order:
- Stage 1 on live: `MapUpdateThreads=4`.
- Stage 2: `CreatureSplinePass=true`, `Ungated=CreatureMapSystem`.
- Then ungate creature motion for the Random wanderers, once items 1 and 2 are thread-safe.

W4 (bot think per map) runs into the same wall. Bot thinks handle packets inline and share the action and time budgets, the probe and the coordinators. Splitting them per map gains nothing until the packet handlers they reach are out of the gate. A cheaper win is moving bot A* to the thread-safe `FindPathShared` (AsyncNavigation, which mt-jobs owns).

# Pools audit and per-tick map allocation (wave 17, 2026-10-10)

Branch `grok/pools-perf`, base `integrate/wave17`. No schema change. Follows up two open items of
`docs/integration/wave10-20261008.md`: pool rotation and a whole-world pool-limit audit were not checked live, and map allocation per tick
rose from 1.34 MiB (wave 9) to 4.18 MiB (wave 10).

## 1. Pool audit

`ArcaneCore.Game.Maps.Pools.PoolAudit` checks one map's pools of one kind against the spawns in the world now:

1. no pool counts more members out than its `max_limit` (spawns and child pools counted together, cmangos `SpawnedPoolData`);
2. each pool's counter equals the spawns it has out plus the child pools it has chosen;
3. no pool has more of its spawns in the world, plus child pools with a spawn in the world, than its `max_limit`;
4. every pooled spawn in the world is one its pool has out (nothing leaked past a rotation), none sits under a child pool its mother did
   not choose, and none whose pool row the load dropped is in the world.

`CreatureMapSystem.AuditPools()` / `GameObjectMapSystem.AuditPools()` run it; `RotatePools()` gives every member out its cmangos
`PoolManager::UpdatePool` trigger at once (what a gathered node or a reached respawn time does), so a test or a GM can rotate a whole map.
GM: `.spawngroup poolaudit [creature|gameobject]` (GameMaster, retail level 3, read-only) prints the report and up to 40 issues.

**z2815, measured** (scratch SQLite world from `ClassicDB_1_12_1_z2815.sql.gz`: `arcane-content-importer import`, then `refresh`;
`tests/ArcaneCore.World.Tests/Pools/PoolWorldAuditTests.cs` with `ARCANECORE_TEST_POOLS_WORLD_DB`): every map with a spawn (34), every
grid holding a spawn loaded, seed 20261010, no event gate. 4,244 pools audited; 4,172 pooled spawns in the world after load; 25 rotations
(about 102,100 rolls): **no issue after the load or after any rotation**. The pooled spawns in the world vary across rotations (4,172 →
4,069) because a mother pool that switches to another child switches to that child's own `max_limit`; no chosen pool was ever under-filled
(checked during the run: every pool out had min(max_limit, members) members out).

`PoolSpawnState` was re-read against cmangos-classic `Pools/PoolManager.cpp` (RollOne, SpawnObject with its trigger and `lastDespawned`,
AddSpawn/RemoveSpawn, DespawnObject, `ReSpawn1Object<Pool>` doing nothing, UpdatePool asking the mother): it matches; no limit or
rotation bug was found, so no pool logic changed.

## 2. Allocation per tick

New harness `tests/ArcaneCore.Game.Tests/GridTerrain/CreatureTickAllocationTests.cs` (in the "World tick load" collection): six players
among 1,200 wandering creatures, 120 waypoint patrols, 40 five-member formations on their paths, 50 rare pools (one of four spots) and
150 ore-node pools (one of three, one gathered every 20 ticks), 50 ms manual ticks, 400 warm-up and 800 measured ticks. Bytes come from
`GC.GetAllocatedBytesForCurrentThread`; the top types from the runtime's `GCAllocationTick` events (an in-process `EventListener`), and the
allocating methods were found from the closure types' IL. Same harness, same seed, before and after:

| | before | after |
|---|---|---|
| creature/pool harness, bytes per tick | 198,605 (194.0 KiB) | 13,949 (13.6 KiB), −93 % |
| `WorldTickLoadTests` (66,000 units, 670 movers, 6 players, 2 ships), bytes per tick | 3,442 | 3,260 |

Top allocators before, and the cut:

| B/tick before | what | cut |
|---|---|---|
| 53,566 + 39,974 | `Map.FindUpdater<T>()` was `_updaters.OfType<T>().FirstOrDefault()`: an iterator and a boxed list enumerator per call, called many times per creature per update through the `map.Combat` extension (341 call sites) | a plain indexed loop |
| 50,636 | `CreatureMapSystem.SelectHostileTarget`: its closure (creature, this) and two delegates were created on every call, for every creature, before it knew there was anything to pick | the taunt/threat pick moved to `PickHostileTarget`, called only when the creature has a taunter or a threat list (the old code got null in both cases without calling the lambdas) |
| 36,777 | `CreatureSpline.Points` built a one-element list for every single-segment spline, and `PositionAt` (per moving creature per tick) went through it; the multi-point walk used a boxed `foreach` | `Locate` interpolates a single segment directly and walks the path with an indexed loop |
| 3,597 | `AiRelocationNotifier.Collect`: `RemoveAll` with a capturing lambda per notify | in-place compaction in the same order |
| 1,332 | `GameObjectMapSystem.Update`: `_objects.Values.ToArray()` every tick | a reused snapshot list (a re-entrant update falls back to a new list) |

What remains (13.6 KiB) is mostly packets (`byte[]`, the session queues, `PacketWriter`), new splines (`CreatureSpline` records, one per
move start) and `SpawnGroupState.Spawn`'s working sets for groups with a member waiting. The harness asserts a 64 KiB budget so the old
per-creature LINQ and closures cannot come back unnoticed.

No behaviour changes: every replaced expression returns the same values in the same order; Game.Tests (8,003 passed, 16 skipped) and
World.Tests pass.

## Limits

- The 4.18 MiB wave 10 figure was a live server with the real world, playerbots and features; this harness reproduces the creature,
  formation and pool paths with synthetic content, so the absolute numbers differ. The same cuts apply to the live paths (they are the
  shared per-creature update), but a live before/after on Nate's server was not taken from the box.
- The audit's scratch world has no DBCs and no event gate: event pools spawn as if their events ran (as in `PoolScratchWorldProbeTests`).
- `dotnet-counters`/`dotnet-trace` were not used; the in-process GCAllocationTick sampler gives types (about one sample per 100 KB), and
  the methods were attributed by reading which methods construct the sampled closure types.

## Next slice

- Run `.spawngroup poolaudit` on the live world after a day of rotations.
- `SpawnGroupState.Spawn` rebuilds its eligible list, dictionaries and closure every update while a member of a group waits on an
  unloaded grid; cache "nothing to do" per group until a member's state changes.
- `CreatureMapSystem.MoveTo` wraps every single point in a one-element list before `MovePath`; a point overload of `StartSpline` would
  drop it.

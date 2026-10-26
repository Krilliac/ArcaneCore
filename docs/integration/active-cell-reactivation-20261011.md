# Active-cell spline reactivation — 2026-10-11

Base: `ebdd4a37dd8865bff226a9c3961466bc7ba0caf4`. Lane: `cx6-active-cells`. Uncommitted; coordinator owns integration.

## Finding and limits

Reproduced and fixed a false-arrival/path-search burst when players reactivate dormant patrols. This is **not proof that the
reported live 6.1 s map-0 TickHang or 62 s save-queue age is resolved**. The accessible `.log` files in the supplied live profile
did not contain that event. No server was started, live data changed, or live-client acceptance performed.

The baseline already supplies only `diffMs` to AI and movement generators. There is no loop replaying every missed AI tick.
However, `CreatureMapSystem.UpdateCreatures` skipped inactive creatures while its `_clockMs` continued advancing.
`Creature.AdvanceSpline` then evaluated `CreatureSpline.PositionAt/IsFinished` against that absolute map clock on return.
Every paused in-flight patrol whose nominal duration had elapsed appeared to arrive immediately. Its waypoint generator
ran `OnArrived` and `StartMove`, firing arrival hooks and requesting the next path in the same tick.

Evidence before changing production code:

- One paused patrol requested one new path on reactivation; expected zero.
- 2,048 patrols across 16 player locations requested 2,048 new paths together; expected zero.
- 512 patrols using a preloaded real Northshire navmesh requested 512 paths, spending 80.591 ms inside `FindPath` out of
  88.518 ms in the creature updater. No grid loaded during that measured tick.
- A separate multi-segment spline test expected 50 ms of progress after ten inactive one-second updates, but read 10,050 ms.

This isolates actual navmesh search work from grid/disk loading for this reproduction. It does not rule out other causes of
the live incident, including cold loading, genuine simultaneous respawns, or a different expensive script/path workload.
The fixture reproduces a roughly 89 ms simulation burst, not a six-second stall. Save queues and managed-bot decision work
are not part of this fixture. Naturally simultaneous waypoint arrivals can still produce simultaneous path searches;
this change preserves movement semantics rather than rate-limiting legitimate arrivals.

## Reference behavior

Read-only vmangos checkout: `<refs-root>/vmangos`, revision `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`. Behavior was independently
implemented; no reference code was copied.

- `src/game/Maps/Map.cpp`, `Map::UpdateActiveCellsSynch` (815–838) and `UpdateCellsAroundObject` (711–740): visit cells around
  players and active objects once, with `SetNoCreate`. Cell simulation itself does not load neighboring grids.
- `src/game/Objects/Object.h`, `WorldObject::UpdateHelper::UpdateRealTime` (477–481): elapsed time since the object's previous
  update and the current update delta are distinct arguments.
- `src/game/Objects/Unit.cpp`, `Unit::Update` (322–323) and `UpdateSplineMovement` (10632–10637): movement receives `p_time`;
  the spline advances through `updateState(t_diff)`. Inactive time does not complete its path.
- `src/game/Objects/Creature.cpp`, `Creature::Update`, DEAD (817 onward): compare the absolute respawn deadline and perform
  one respawn. CORPSE (901 onward): compare/subtract elapsed time for decay and skinning, without replaying missed intervals.
- `src/game/Objects/Creature.cpp`, `Creature::RegenerateAll` (1087–1100): an expired regen timer produces one regeneration
  and is reset, rather than looping over missed intervals. This patch does not change regeneration.

Thus the fix is to preserve active movement time, not impose an arbitrary time cap on corpse/respawn deadlines or disable
active-cell simulation.

## Change

`CreatureSpline` keeps an internal scalar amount of paused time. The existing inactive-cell skip branch adds that tick's
time to the spline's pause amount. The addition is bounded by the time since spline creation so a newly launched spline
cannot be delayed by time before it existed. There is no spline replacement/allocation on skipped updates.

Completion, elapsed time, interpolation and remaining-path extraction all use the adjusted time. Consequently both stopping
and observer catch-up packets agree with the frozen server position. The existing always-update selection for active objects,
formations and camera viewers remains in force. The option-off mode retains continuous movement. Waypoint arrival callbacks
wait for actual active movement to finish; escort/script movement follows that same rule.

The map clock, `LastUpdateClockMs`, corpse catch-up, skinning expiry and absolute respawn deadline are unchanged. A new test
checks that a corpse whose respawn deadline passed while inactive is removed on return and respawns on the next update.
No configuration option, schema or generated configuration documentation changed.

## Measurements

Separate fresh test-process runs of the same focused class, on this shared Windows machine. All simulation time is explicit:
50 ms active, players relocate 300 yards away, 600,000 ms inactive, then players return and receive one 50 ms tick. Grid
unloading is disabled. The final fixture checks grid-load counters across both the return relocations and the measured tick.
The 2,048-patrol case spans 16 locations in different grids. The 512-patrol case deliberately overlaps patrols at real
Northshire endpoints from `WorldTickLoadTests`, verifies a real mesh path, and loads the navmesh before measurement.

| Fixture | Path queries before → after | Path time before → after | Creature updater before → after | Simulation before → after | Whole tick before → after |
|---|---:|---:|---:|---:|---:|
| 512 patrols, real navmesh | 512 → 0 | 80.591 → 0.000 ms | 88.518 → 0.818 ms | 88.601 → 0.876 ms | 91.790 → 2.914 ms |
| 2,048 patrols, 16 locations, straight-path control | 2,048 → 0 | below µs-per-call resolution → 0 | 7.754 → 3.325 ms | 7.899 → 3.475 ms | 19.114 → 11.401 ms |

These are observed samples, not p99 estimates or a throughput guarantee. JIT, GC and other lanes affect wall time. In the
broader, more warmed namespace run, fixed creature-update times were 0.144 and 0.678 ms respectively. Correctness gates use
path counts, spline identity/position, arrival state and lifecycle state, never wall-time thresholds. No missed path searches
are queued for a later tick: the current leg remains in progress and will finish after its remaining active travel time.

## Validation and exact commands

Before each build, checked free physical RAM and waited below 3 GiB. `Get-CimInstance Win32_OperatingSystem` was denied by the
sandbox, so the equivalent `GlobalMemoryStatusEx` physical-memory reading was used. No other process was stopped.

```powershell
dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false

$env:ARCANECORE_TEST_TERRAIN_DIR='<data-root>/terrain-5875'
$env:ARCANECORE_TEST_DBC_DIR='<data-root>/client-dbc-5875'
dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build --filter FullyQualifiedName~ActiveCellReactivationTests --logger 'console;verbosity=normal'
dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build --filter 'FullyQualifiedName~GridTerrain|FullyQualifiedName~CreatureMovement|FullyQualifiedName~CreatureAi|FullyQualifiedName~SpawnGroups|FullyQualifiedName~RespawnPersistence' --logger 'console;verbosity=detailed'
dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build --filter FullyQualifiedName~ActiveCellReactivationTests --logger 'console;verbosity=detailed'
git diff --check
```

- Baseline build: succeeded, zero warnings/errors. RED: 4 failed / 3 passed. The three reactivation cases failed on excess
  path counts, and the spline-reader case failed on elapsed movement time. Controls for active objects/option off and
  expired respawn passed before the fix.
- Fixed build and final test-input rebuild: succeeded, zero warnings/errors. A checksum check caught an assertion edited
  after the compiler read the test file; it was rebuilt before GREEN. SHA-256 source hashes for both changed production
  files and the new test file were then found in the PDBs beside the tested DLLs, confirming the exact compiled inputs.
- Focused GREEN: 7 passed, 0 failed. Final isolated measurement rerun: 7 passed, 0 failed.
- Whole affected namespaces: 772 passed, 0 failed, no skips. This includes the existing active-cell/corpse/skinning tests,
  waypoint/movement tests, escort and data-driven script tests, formations, respawn persistence and the world-tick load test.
- No full Game/World suite, real-client run, CI or live performance acceptance is claimed.

Logs are in `<lanes-root>/_codex/w6/runs/active-cells/`: `build-red.log`, `test-red.log`, `build-green.log`,
`build-final.log`, `test-green.log`, `test-affected.log`, `test-green-measured.log`.

## Files changed

- `src/ArcaneCore.Game/Creatures/Creature.cs` — spline active-time accounting and consistent readers.
- `src/ArcaneCore.Game/Creatures/CreatureMapSystem.cs` — pause the spline in the existing inactive skip branch.
- `tests/ArcaneCore.Game.Tests/GridTerrain/ActiveCellReactivationTests.cs` — seven cases, deterministic regressions and measured workloads.
- `docs/areas/ops-perf.md` — workload and live-incident limitation.
- `docs/areas/creature-movement-spawns.md` — movement semantics and provenance.
- `docs/integration/active-cell-reactivation-20261011.md` — this report.

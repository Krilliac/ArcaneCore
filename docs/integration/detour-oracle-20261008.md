# DotRecast path oracle, phase 1 (2026-10-08)

## Scope and provenance

`tests/ArcaneCore.Game.Tests/Collision/DotRecastOracleTests.cs` reads the existing vmangos
`.mmap` and `.mmtile` files from `ARCANECORE_TEST_TERRAIN_DIR`. It removes the 20-byte
`MmapTileHeader`, passes the remaining v7 bytes to `DtMeshDataReader.Read(buf, 6,
is32Bit: false)`, and initializes DotRecast with the map origin, tile dimensions, tile
capacity and `maxPolys = 65536`. The stored vmangos value is zero because the generator
uses 64-bit polygon refs (`D:/refs/vmangos/contrib/mmap/src/MapBuilder.cpp:462`);
`MMapManager::loadMapData` reads those parameters and `MMapManager::loadMap` reads the
tile prefix (`D:/refs/vmangos/src/game/Maps/MoveMap.cpp`). Polygon flags follow
`PathInfo::createFilter` (`D:/refs/vmangos/src/game/Maps/PathFinder.cpp`). The
ArcaneCore side uses `NavMeshQuery.FindCorridor` and `StringPull`, then also checks the
public `NavMeshPathfinder.FindPath` on the same loaded tiles.

The independent implementation is test-only DotRecast Core and Detour at commit
`6fce4ac`, Zlib, copied unmodified under `tests/ThirdParty/DotRecast` (see
`THIRD_PARTY_NOTICES.md`). No production project references it. No navmesh or client
asset is in this change.

## Comparison contract

The test deterministically selects real passable polygon centres near Coldridge Valley,
the Kharanos switchback, Goldshire inn, Deathknell crypt, Valley of Trials and Shadowglen.
It adds each named anchor, including the exact Kharanos `(-5165,-876,507)` to
`(-5384,-716,397)` pair, then evaluates 60 distinct centre pairs per region. There are
368 pairs across maps 0 and 1. Both queries use the same 3-by-3 terrain-tile
neighbourhoods, ground/water/magma/slime include flags, no excludes, nearest-polygon
extents and world-to-Recast axis conversion. Straight points are compared within 0.5
world units. One untimed query per region runs first, so neither engine's timed calls
pay for JIT compilation. File loading is excluded from the timings.

DotRecast's node pool has no limit. ArcaneCore, like vmangos (`MoveMap.cpp:350`,
`navMeshQuery->init(mesh, 2048)`), stops allocating search nodes at 2,048. The
categories account for that:

| Category | Meaning |
|---|---|
| Agree | Both complete, same polygon corridor, same straight points |
| PolygonCorridorDiffers | Both complete, different polygon sequence |
| StraightPathDiffers | Both complete, same corridor, straight points differ by more than 0.5 |
| NodeBudgetPartial | ArcaneCore partial at 2,048 nodes, and DotRecast needed more than 2,048 |
| OneSidePartial | One side partial for any other reason |
| BothPartial | Neither reached the goal on the loaded tiles; best-so-far corridors are not compared |
| OneSideNoPath | A side found no start or end polygon, or DotRecast returned no path |

When `ARCANECORE_ORACLE_REPORT_DIR` is set, the test writes full per-case JSONL there
(`cases.jsonl`, `disagreements.jsonl`). Those files contain positions derived from
client tiles, so keep them out of Git (for example under the ignored `artifacts/`
directory). The adjacent `detour-oracle-disagreements-20261008.csv` names and
categorises every non-agreeing case from the final run, without positions.

## Finding: NavMeshQuery chose different corridors from Detour

The first run (Codex lane, reproduced at intake) reported 82 Agree, 233
PolygonCorridorDiffers and 53 OneSidePartial. That run counted 59 pairs whose goal
neither engine could reach as Agree (29) or PolygonCorridorDiffers (30). Without them,
only **53 of 256** pairs that both engines completed had the same corridor. Over those
256 pairs the straight paths had about the same total length (ArcaneCore 0.13% longer),
so the routes were not worse. They differed in which of several near-equal corridors
was chosen: NavMeshQuery did not pick the one that the Detour `findPath` in vmangos
picks.

The cause was NavMeshQuery's A* node handling. It differed from `dtNavMeshQuery::findPath`
(`D:/refs/vmangos/dep/recastnavigation/Detour/Source/DetourNavMeshQuery.cpp`) in five ways:

* NavMeshQuery made one node per polygon. Detour makes one node per polygon and tile side
  crossed (`getNode(neighbourRef, crossSide)`).
* A cheaper route moved the node's position to the new portal midpoint. Detour fixes the
  position at the portal it was first reached through and only updates cost and parent.
* NavMeshQuery never improved a closed node. Detour reopens it when a cheaper total
  reaches it.
* The search could expand back into the parent polygon. Detour skips the parent.
* The goal node kept a non-zero heuristic. Detour gives it zero.

`NavMeshQuery.FindCorridor` now follows those rules. The results:

| Category | Before | After |
|---|---:|---:|
| Agree | 53 | 254 |
| PolygonCorridorDiffers (both complete) | 203 | 2 |
| NodeBudgetPartial | 52 | 53 |
| OneSidePartial | 1 | 0 |
| BothPartial | 59 | 59 |
| StraightPathDiffers, OneSideNoPath | 0 | 0 |

The two remaining corridor differences are both in Shadowglen: 84 against 85 and 91
against 92 polygons, with ArcaneCore's straight paths 0.6% and 0.5% longer. They look
like near-ties that the two priority queues break differently, but I have not
investigated them further. All 53 NodeBudgetPartial pairs complete with a 16,384-node
ArcaneCore search. DotRecast used 2,154 to 10,896 nodes on them, so vmangos, with its
2,048-node budget, would also return partial paths there. None of the 59 BothPartial
pairs complete at 16,384 nodes. Their goals are polygons that are not connected to the
start within the loaded tiles: 22 in Shadowglen, 18 in Valley of Trials, 13 at Goldshire
and 6 at Deathknell.

The exact Kharanos switchback pair agrees: 55 corridor polygons and 12 straight points
in both engines, and `NavMeshPathfinder.FindPath` returns `Normal` with 12 points.
Over all 368 pairs the public pathfinder returned 255 `Normal` and 113 `Incomplete`.

Two tests now guard this:

* `SyntheticMazes_CorridorMatchesDetourNodeRules` needs no client data. It runs 600 seeded
  random 6-by-6 cell mazes (472 of them connected) and requires NavMeshQuery's corridor
  to equal DotRecast's from the same polygons. It fails on the old search (53 of 472
  differ) and passes now.
* The real-tile test asserts that at least 95% of complete corridors match and that no
  OneSidePartial or OneSideNoPath case appears. It fails on the old search at 53 of 256.

## Timing

The table gives warm per-call times in microseconds, measured in Release in one test
process. Each call covers the nearest-polygon lookup, the corridor and the straight
path. Other agents' builds were running on the machine at the same time, so single
numbers are rough. Each cell shows two runs as "run 1 / run 2".

| Subset | Engine | Mean | Median | p95 |
|---|---|---:|---:|---:|
| 284 pairs DotRecast finished within 2,048 nodes | ArcaneCore | 457 / 683 | 313 / 340 | 1,187 / 1,714 |
| | DotRecast | 359 / 511 | 265 / 410 | 980 / 1,279 |
| All 368 pairs | ArcaneCore | 747 / 914 | 425 / 493 | 1,428 / 2,184 |
| | `NavMeshPathfinder.FindPath` | 774 / 790 | 399 / 472 | 1,483 / 2,186 |
| | DotRecast (unbounded nodes) | 2,903 / 5,662 | 340 / 607 | 27,708 / 59,305 |

On equal work DotRecast's mean is roughly a quarter lower. Over all pairs, its mean and
tail come mostly from the searches that ArcaneCore stops at 2,048 nodes and DotRecast
runs to completion (up to 10,896 nodes). DotRecast's `DtNavMeshQuery` has no per-query
node limit, so it cannot run with the vmangos budget unless the vendored source is
modified.

## Phase 2 recommendation

Keep `NavMeshPathfinder` and `NavMeshQuery` in production. After this fix they return
Detour's corridor on 254 of 256 complete pairs, at a cost close to DotRecast's for the
same work, and they keep the vmangos node budget that DotRecast lacks. Replacing the
pathfinder would gain little for path finding.

In phase 2, use DotRecast as the oracle for the APIs that ArcaneCore does not have yet,
in this order:

1. Smooth paths (`MoveAlongSurface`-style stepping). Test them on the 53
   NodeBudgetPartial pairs and the Kharanos switchback, comparing endpoint error, turning
   distance and node use.
2. Navmesh raycast against walls and doors.
3. Deterministic random points (seeded `IRcRand`) for wander and flee targets.
4. Off-mesh connections. ArcaneCore's tile reader skips them today, so first check
   whether the vmangos tiles in use contain any before building fixtures.

The phase-1 tests do not exercise those four APIs, so nothing here qualifies them for
production. Putting DotRecast itself in production would need two more steps. Its node
pool would need a per-query cap, which is a source change that Zlib clause 2 requires
to be marked as altered. It would also need its own non-test project.

## Verification

* `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false` reported 0 warnings
  and 0 errors.
* The oracle tests with `ARCANECORE_TEST_TERRAIN_DIR=D:/ArcaneCore-data/terrain-5875`:
  3 passed. Without the variable, 2 passed and 1 was skipped (the real-tile test).
* Full `ArcaneCore.Game.Tests` with terrain data: 7,283 passed, 1 skipped, 0 failed.
* Full `ArcaneCore.World.Tests`: 2,963 passed, 14 skipped, 1 failed. The failure is the
  `DocKeyAuditTests` entry for the playerbot movement-packets key named in
  `docs/integration/wave6-20261008.md:12`, which already fails on the base commit.

No ClassicDB rows or schema changes are needed. A non-skipped real-tile run needs a
vmangos 1.12.1 terrain extraction.

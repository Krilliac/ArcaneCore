# Collision and pathing (vmaps, mmaps, path contract)

Lane `pathfinding-collision`, branch `claude/vw2-pathfinding-collision`. The base seam (VMAP_7.0 reader,
Detour v7 reader, `ILineOfSight` / `IPathfinder`, `World:Collision`) is described in
`docs/integration/vmap-los.md`; this document records what the lane changed on top of it, the limits
that remain and the retail references. Everything is implemented from format and algorithm
descriptions; vmangos is read only to confirm layouts and semantics, and no extracted data is committed.

## Delivered

### mmap parameter files written by vmangos load (NavMeshFormat, NavMeshTile)

vmangos' generator builds Detour with 64-bit polygon references and writes `maxPolys = 0` into
`NNN.mmap` (`D:\refs\vmangos\contrib\mmap\src\MapBuilder.cpp:462`, "Unused if DT_POLYREF64 set");
the server reads the 28 bytes raw (`src\game\Maps\MoveMap.cpp:86-92`). `NavMeshParams.Parse` used to reject
`maxPolys <= 0`, so every vmangos-generated map silently degraded to straight lines. It now accepts
`maxPolys >= 0` and ignores the value; length, finite-origin and `maxTiles > 0` checks stay.
A tile with `MMAP_VERSION 8` (mangos-classic, `D:\refs\mangos-classic\src\game\MotionGenerators\MoveMapSharedDefines.h:26`)
is refused with a message that names the cMaNGOS generator; vmangos reads only version 6
(`MoveMapSharedDefines.h:26`, `MoveMap.cpp:170-176`).

### Path contract follows vmangos (CollisionContracts, PathMover)

* `PathType` values are vmangos' (`PathFinder.h:45-57`): Blank 0, Normal 1, Shortcut 2, Incomplete 4,
  NoPath 8, NotUsingPath 0x10, DestForced 0x20, FlyPath 0x40, Underwater 0x80, Caster 0x100. The
  ArcaneCore-only `Short` (path cut to `MaxPoints`) moved from 0x20 to 0x200 so numeric comparisons match.
* `PathMover` (CanWalk, CanSwim, CanFly, IsPlayer) in `PathOptions.Mover` derives the polygon include
  flags exactly like `PathInfo::createFilter` (`PathFinder.cpp:657-675`): ground when it walks; water when
  it swims, plus magma and slime for creatures. `PathOptions.EffectiveIncludeFlags` is what queries use.
* Default `ExcludeFlags` is empty (`PathInfo::createFilter`, `PathFinder.cpp:657-677`). vmangos excludes steep
  slopes from the path search of random, confused, fear and flee movement only (`RandomMovementGenerator.cpp:52`
  through `MoveSplineInit.h:129-130`, `ConfusedMovementGenerator.cpp:79`, `FearMovementGenerator.cpp:38`,
  `FleeingMovementGenerator.cpp:56`, each `PathFinder::ExcludeSteepSlopes`, `PathFinder.h:88`).
  `CreatureMapSystem.PathOptionsFor` gives those creature legs `ExcludeFlags = NavTerrain.SteepSlopes` with
  `ExcludeOnlyInSearch`: vmangos locates the start and end polygons by the include flags alone
  (`getPolyByLocation`, `PathFinder.cpp:145-157`) and Detour's `findPath` filters only the polygons it enters
  (`DetourNavMeshQuery.cpp:1037`), so a creature standing on a slope still paths off it. Other queries that set
  `ExcludeFlags` (the bots') also keep their endpoints off the excluded polygons. Tests:
  `Creatures/SteepSlopeLegTests.cs`.
* `MaxPoints` 256 (`PathFinder.h:39`), `MaxSearchNodes` 2048 (`MoveMap.cpp:350`).
* `ModelAreaInfo.MogpExterior` is 0x8000 (`GridMap.cpp:875-878`; mangos-classic `GridMap.cpp:887-890`); it was 0x8.

### No-navmesh fallback seam (WorldCollision, IMapAwarePathfinder)

vmangos routes a map without a navmesh (or `mmap.enabled = 0`) to `BuildPathWithoutMMaps`
(`PathFinder.cpp:86-90`, `MoveMap.cpp:70-71`). `WorldCollision.InstallFallback(IPathfinder?)` registers that
pathfinder; `WorldCollision.PathfinderFor(mapId)` picks the primary when it has data for the map (an
`IMapAwarePathfinder` says so via `HasNavigationData`; any other enabled pathfinder is assumed to) and the
fallback otherwise. `MapCollision.FindPath` goes through it. The default fallback is the straight line, so
behaviour is unchanged: no terrain-step `IPathfinder` exists yet to install (only this seam; `w44-path-no-straight` found none). The fallback survives
`WorldCollision.Install`. The mover reaches whoever answers in `PathOptions.Mover`.

### Unloaded tiles shortcut (NavMesh.HaveTileAt)

A start or end on a Detour tile that is not loaded answers a straight line typed `Normal | NotUsingPath`
(`PathInfo::HaveTiles`, `PathFinder.cpp:99-105`, `695-706`) instead of `NoPath`. `NoPath` stays for a loaded
tile without a usable polygon.

### Corridor selection, cross-tile portals, and detail heights follow Detour (NavMeshQuery, NavMeshTile)

The A* search keeps the node rules of `dtNavMeshQuery::findPath`
(`D:\refs\vmangos\dep\recastnavigation\Detour\Source\DetourNavMeshQuery.cpp`). A node is a polygon plus the
tile side crossed into it. Its position stays at the first portal midpoint. A cheaper route reopens a closed
node, the parent polygon is not expanded, and the goal's heuristic is zero. Cross-tile portal linking in
`NavMesh.TryOverlap`, `NavMeshTile.BuildCrossPortals`, and `NavMesh.CollectNeighbours` matches `dtNavMesh::connectExtLinks`
(`DetourNavMesh.cpp:31-112, 300-351, 416-428`): `calcSlabEndPoints` projects border edges onto the tile's `Z` or `X` slab axis
within `0.01f` of the border, `overlapSlabs` shrinks horizontal slab endpoints by `px = 0.01f` and checks vertical slab overlap
at `minx`/`maxx` against `(climb * 2)^2`, `BuildCrossPortals` emits at most one portal per target polygon (`DetourNavMesh.cpp:347`),
and `CollectNeighbours` walks each edge's portals in Detour's prepended `firstLink` order. Point-to-polygon
projection in `NavMeshTile.ClosestPointOnPoly` and `NavMeshTile.GetPolyHeight` matches `closestPointOnDetailEdges`
(`DetourNavMesh.cpp:621-674, 718-758`), clamping exterior points onto detail-mesh boundary edges and falling back to internal
detail edges when a point inside a polygon falls on a seam between detail triangles. Before this change the corridor
matched Detour's on 53 of 256 complete real-tile pairs; it now matches on 254. The DotRecast oracle and its
numbers are in `docs/integration/detour-oracle-20261008.md`.

### Fliers (NavMeshPathfinder.FindPath)

A mover with `CanFly` goes straight, typed `Normal | NotUsingPath | FlyPath`, when no collision model blocks the
segment (`PathFinder.cpp:172-186`; the model test is `ILineOfSight.IsInLineOfSight(ignoreM2: false)`, the
equivalent of `Map::FindCollisionModel`, which does not skip doodads). When a model blocks, it follows the mesh
and the destination is forced as vmangos does (`PathFinder.cpp:451-472`: partial subpath kept if it leaves under about 55%
of the way, else a shortcut; `DestForced | FlyPath`), except that with `EndTolerance > 0` a flier whose start or end is
more than 7 yd from its polygon goes straight with `Normal | NotUsingPath` (`PathFinder.cpp:220-225`). A hole in the mesh
(or, with `EndTolerance > 0`, a start or end with no polygon in `FindWalkPoly`'s reach) answers the flying shortcut instead of
`NoPath` (`PathFinder.cpp:190-198`). `WorldCollision.Install` hands the line-of-sight service to the navmesh
pathfinder in either install order. Swim and underwater shortcuts (`PathFinder.cpp:160-170`, `BuildUnderwaterPath`)
are still not implemented: a swimmer takes the mesh path, and with `EndTolerance > 0` a swimmer whose start or end is out of
`FindWalkPoly` reach (more than 10 yd above the lake-bottom mesh, or more than 3 yd under a polygon) gets `NoPath`, so home
teleports (vmangos would swim straight via `IsSwimmable`, `PathFinder.cpp:199-201`).

### Region graph (NavRegionGraph)

Not a vmangos feature: a coarse level over a whole map's mesh for long walks (the playerbots' far goals, issues
#191 and #194). Every `.mmtile` of the map is read once; each tile's polygons that pass a filter (the bots':
ground and water, no steep slopes) are split into regions by the tile's own links, and regions of adjacent tiles
meet at portals where their border edges meet by the rule `NavMesh.CollectNeighbours` crosses a border (one portal per
stretch of at most 64 yards, 1 yard inside the region it enters). `FindRoute` is A* over the portals with
straight-line costs; without a way it returns the way to the portal nearest the goal, as Detour's partial path
does. Chains of regions are chains of polygons a path query can walk, so the graph answers reachability on foot
exactly for its filter. `NavMeshPathfinder.RegionGraph` builds it on the thread pool on first use (null until
then); `RegionAt` locates a position's region as a path query locates its polygon. Real data (2026-10-11):
Kalimdor 704 tiles, 3.0 M polygons, 223,798 regions (most are slivers), 39,330 portal links, built in 3 to 6 s,
about 22 MB kept; Eastern Kingdoms 470 tiles, 151,309 regions, 26,418 links, 3 to 4 s. A route search across
Kalimdor settles in under 1 ms, an unreachable goal's full search of the Eastern Kingdoms in about 14 ms.

## Limits (not done in this lane run)

See the open questions of the lane report for the reasons. Not implemented: the `MapCollision` partial split,
`FindWalkPoly` extents / `farFromPoly` rules for queries without `PathOptions.EndTolerance` (both endpoints of an
`EndTolerance` query now use `FindWalkPoly` extents and `farFromPoly` rules, flier shortcut included; queries without a
tolerance keep MaNGOS Zero's `(3,5,3)`/`(3,200,3)` boxes for both ends), swim/underwater paths, smooth path
(`findSmoothPath`; `CreatureSplineSmoothing` only rounds the corners of creature and charge splines), off-mesh links,
vmap/mmap tile lifecycle shared across instances, the fallback model searches of `GetHeightStatic` (unbounded, upward and
near the terrain, `GridMap.cpp:840-850`), model floors directly in `Map.GetHeight` (`Map.GetHeight` remains raw `.map`
terrain, `TerrainInfo.GetHeight`, while creature movement and summon/point heights via
`MapCreatureHeightProvider.GetHeight` / `MapCreatureHeightProvider.Floor` and `CorpseQuery` ghost-entrance ground heights
now use `MapCollision.GetHeight`, terrain and static model floors falling back to `Map.GetHeight` when no floor lies below
`z + 2`), WMO liquids, the terrain test of the indoor check (`GetAreaInfo`, `GridMap.cpp:894-907`), dynamic gameobject LOS,
DBC-fed WMO areas, the data-directory inspector. Paths on transports and boats (`PathFinder.cpp:77-84`), cMaNGOS mmaps
(version 8) and WMO area names without `WMOAreaTable.dbc` remain unsupported.

Implemented since: the creature mover (`PathMover.For`, from the template's `InhabitType`); navmesh raycast and random
points (`NavMeshQuery.Raycast`, `NavMeshQuery.TryRandomPointAround`, reached through `MapCollision.MeshPoints`;
creature wander and flee points use them); background tile reads (vmap and mmap prefetch when a grid approaches,
`ICollisionTilePrefetch`, and `PathOptions.NonBlockingTiles`); DBC collision heights on units
(`LocomotionState.CollisionHeight`, used for the swim depth, and `Unit.CollisionHeight` / `MapCollision.ObjectTargetHeight`
in line of sight, `Object.cpp:1766-1781`). The base seam already had `MapCollision.GetHeight`
(terrain and model floors, the first search of `GetHeightStatic`) and `MapCollision.IsOutdoors` (the MOGP exterior flag).

## Verification status

CI runs synthetic tiles only (`tests/ArcaneCore.Game.Tests/Collision`). The readers were checked against a real
vmangos extraction of the 1.12.1 client on 2026-10-07 (`RealTerrainDataTests`, run with
`ARCANECORE_TEST_TERRAIN_DIR` set; recipe and results in
[../integration/maps-vmaps-mmaps.md](../integration/maps-vmaps-mmaps.md)): all `.map`, vmap and continent
`.mmtile` files parse, start-position heights match the world database, and 4898 vmap queries agree with
vmangos' native `VMapManager2`. Those tests skip in CI, so rerun them after any reader change.

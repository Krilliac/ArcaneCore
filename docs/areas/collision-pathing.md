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
(`MoveMapSharedDefines.h:23`, `MoveMap.cpp:170-176`).

### Path contract follows vmangos (CollisionContracts, PathMover)

* `PathType` values are vmangos' (`PathFinder.h:45-57`): Blank 0, Normal 1, Shortcut 2, Incomplete 4,
  NoPath 8, NotUsingPath 0x10, DestForced 0x20, FlyPath 0x40, Underwater 0x80, Caster 0x100. The
  ArcaneCore-only `Short` (path cut to `MaxPoints`) moved from 0x20 to 0x200 so numeric comparisons match.
* `PathMover` (CanWalk, CanSwim, CanFly, IsPlayer) in `PathOptions.Mover` derives the polygon include
  flags exactly like `PathInfo::createFilter` (`PathFinder.cpp:657-675`): ground when it walks; water when
  it swims, plus magma and slime for creatures. `PathOptions.EffectiveIncludeFlags` is what queries use.
* Default `ExcludeFlags` is empty. vmangos excludes steep slopes only for fear, flee, confused and random
  movement (`FearMovementGenerator.cpp:38`, `RandomMovementGenerator.cpp:52`); callers that need that set
  `ExcludeFlags = NavTerrain.SteepSlopes`.
* `MaxPoints` 256 (`PathFinder.h:39`), `MaxSearchNodes` 2048 (`MoveMap.cpp:350`).
* `ModelAreaInfo.MogpExterior` is 0x8000 (`GridMap.cpp:875-878`; mangos-classic `GridMap.cpp:887-890`); it was 0x8.

### No-navmesh fallback seam (WorldCollision, IMapAwarePathfinder)

vmangos routes a map without a navmesh (or `mmap.enabled = 0`) to `BuildPathWithoutMMaps`
(`PathFinder.cpp:86-90`, `MoveMap.cpp:70-71`). `WorldCollision.InstallFallback(IPathfinder?)` registers that
pathfinder; `WorldCollision.PathfinderFor(mapId)` picks the primary when it has data for the map (an
`IMapAwarePathfinder` says so via `HasNavigationData`; any other enabled pathfinder is assumed to) and the
fallback otherwise. `MapCollision.FindPath` goes through it. The default fallback is the straight line, so
behaviour is unchanged until the creature-ai lane installs its terrain-step pathfinder. The fallback survives
`WorldCollision.Install`. The mover reaches whoever answers in `PathOptions.Mover`.

### Unloaded tiles shortcut (NavMesh.HaveTileAt)

A start or end on a Detour tile that is not loaded answers a straight line typed `Normal | NotUsingPath`
(`PathInfo::HaveTiles`, `PathFinder.cpp:99-105`, `695-706`) instead of `NoPath`. `NoPath` stays for a loaded
tile without a usable polygon.

### Fliers (NavMeshPathfinder.FindPath)

A mover with `CanFly` goes straight, typed `Normal | NotUsingPath | FlyPath`, when no collision model blocks the
segment (`PathFinder.cpp:172-186`; the model test is `ILineOfSight.IsInLineOfSight(ignoreM2: false)`, the
equivalent of `Map::FindCollisionModel`, which does not skip doodads). When a model blocks, it follows the mesh
and the destination is forced as vmangos does (`PathFinder.cpp:451-472`: partial subpath kept if it covers 70%
of the way, else a shortcut; `DestForced | FlyPath`). A hole in the mesh answers the flying shortcut instead of
`NoPath` (`PathFinder.cpp:190-198`). `WorldCollision.Install` hands the line-of-sight service to the navmesh
pathfinder in either install order. Swim and underwater shortcuts (`PathFinder.cpp:160-170`, `BuildUnderwaterPath`)
are still not implemented; a swimmer takes the mesh path.

## Limits (not done in this lane run)

See the open questions of the lane report for the reasons. Not implemented: the `MapCollision` partial split,
`PathMoverFactory` (needs a Creature-to-template `InhabitType` accessor from the creature-ai lane),
FindWalkPoly extents / `farFromPoly` rules, swim/underwater paths, smooth path,
navmesh raycast and random points, BV tree and off-mesh links, vmap/mmap tile lifecycle shared across instances,
model-aware `GetHeightStatic`, WMO liquids, indoor check, dynamic gameobject LOS, DBC-fed WMO areas and
collision heights, async tile loading, the data-directory inspector. Transports and boats, cMaNGOS mmaps
(version 8) and WMO area names without `WMOAreaTable.dbc` remain unsupported.

## Verification status

Synthetic tiles only (`tests/ArcaneCore.Game.Tests/Collision`). Green CI proves nothing about real extracted
data: the first run against a real vmangos extraction must confirm the `.mmap` / `.mmtile` / `.vmtile`
reading before anything else here is trusted.

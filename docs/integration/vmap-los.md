# Integration notes: vmaps, line of sight and pathfinding (`feat/vmap-los`)

Status: **complete for this slice**. The seam, a vmap reader (line of sight, model heights, area
info), a managed Detour-compatible navmesh reader with path queries, the spell line-of-sight
check and an optional creature pathing hook. Base: `codex/integrate-feature-fleet-20261003`.

## The seam (stable)

Namespace `ArcaneCore.Game.Maps.Collision`:

| Type | Role |
|---|---|
| `ILineOfSight` | World-wide static-model queries (vmangos `IVMapManager`): `IsInLineOfSight`, `TryGetObjectHit`, `GetModelHeight`, `TryGetAreaInfo`. |
| `IPathfinder` | World-wide path queries (vmangos `PathFinder`): `FindPath(mapId, start, end, PathOptions?)` → `PathResult(PathType, Points)`. |
| `OpenLineOfSight`, `StraightLinePathfinder` | Defaults without data: everything in sight; straight line flagged `Normal` + `NotUsingPath`. |
| `WorldCollision.Of(world)` | Side table holding the world's two services; `Install(los, pathfinder)` replaces them. |
| `MapCollision` (`map.Collision`) | Default map updater: `IsInLineOfSight`, `IsWithinLineOfSight(source, target)` (from `EyeHeightOf(source)` — `Unit.CollisionHeight` for a unit, `1.0 yd` for a non-unit — to `target.Z + 1.0 yd`, `ObjectTargetHeight`, vmangos `WorldObject::GetLosCheckPosition`, returning `true` without a ray when 3D distance `< source.BoundingRadius + target.BoundingRadius`), `IsWithinLineOfSight(source, x, y, z, targetHeight = 2.0f)` (from `EyeHeightOf(source)` to `z + targetHeight`), `FindPath`, `GetHeight` (terrain + models, vmangos `GetHeightStatic`), `IsOutdoors`. Forwards grid create/unload to services implementing `ICollisionTileLifecycle`; the services serve every instance of a map id, so an unload is forwarded only once no instance has a grid on the tile (the map id's terrain reference count, as vmangos `TerrainInfo::CleanUpGrids` decides). |
| `unit.IsWithinLineOfSight(other)` | Extension for spells/AI/combat. |
| `ICollisionTileLifecycle` | Optional: services that want grid create/unload forwarded as terrain tile indices. |
| `ICollisionTileDemand` | Optional: services that load a tile for a question about a place no grid covers (`MapCollision.LoadTilesOnDemand(x, y)`: a bot choosing the landing beside a lift far from it). Each holds such tiles among a bounded number per map (`VMapManager.MaxOnDemandTiles` 16, `NavMeshPathfinder.MaxOnDemandTiles` 56, shared with path queries that load tiles), releases the one asked for longest ago to make room, and leaves a tile a grid holds to the grid; a grid created on one takes it over. Never the grid hooks, which would hold the tile until a grid on it unloads. vmangos has no such call: any terrain lookup loads the tile's vmap and mmap (`TerrainInfo::GetGrid`, GridMap.cpp:1132-1144), and the minute's clean-up unloads them while no grid references the tile (`CleanUpGrids`, GridMap.cpp:759-790). |
| `SpellLineOfSight` | The cast-check hook (`SPELL_FAILED_LINE_OF_SIGHT`, honours `SPELL_ATTR_EX2_IGNORE_LOS` 0x4 and triggered casts). |
| `CreaturePathing.MoveTowards(system, creature, destination, run)` | Optional creature hook: asks `map.Collision.FindPath` and launches the spline to the next corner. |
| `CollisionServices.Install` | Builds the services from `CollisionOptions` and the data on disk (called by `World/Collision/CollisionFeature`). |

"No data" always means *open*: callers must never treat a missing service, map, tile or a
corrupt file as blocked. Missing or corrupt data is logged and read as open; nothing throws into
the world thread.

### How other areas use it

* **Spells**: `SpellSystem.CheckCast` calls `SpellLineOfSight.Check` after the unit range check and
  `SpellLineOfSight.CheckDest` after the destination range check (the only edit to the spell area).
* **Creature AI** (owned elsewhere): the chase/follow generators ask `map.Collision.FindPath`
  (through `CreatureMapSystem.PlanPath`) on each re-path and launch the whole path as one
  multi-point spline; they do not use `CreaturePathing.MoveTowards(...)`, which walks corner by
  corner. On `PathType.NoPath` a chaser launches nothing and the AI decides (vmangos evades or moves straight).
  Without mmaps the path is the straight line, so behaviour is unchanged.
* **Combat**: `attacker.IsWithinLineOfSight(victim)` where vmangos checks `IsWithinLOSInMap`.

## Configuration

`World:Collision` (bound by `World/Collision/CollisionFeature`):

| Key | Default | Meaning |
|---|---|---|
| `VMapDirectory` | `""` → `<World:Maps:DataDirectory>/vmaps` | Extracted vmaps (`NNN.vmtree`, `NNN_YY_XX.vmtile`, `*.vmo`). |
| `MMapDirectory` | `""` → `<World:Maps:DataDirectory>/mmaps` | Generated navmeshes (`NNN.mmap`, `NNNXXYY.mmtile`). |
| `EnableLineOfSight` | `true` | vmangos `vmap.enableLOS`. |
| `EnableHeight` | `true` | vmangos `vmap.enableHeight` (also gates area info). |
| `EnablePathfinding` | `true` | vmangos `mmap.enabled`. |
| `NavTileReadConcurrency` | `2` | The most `NNNXXYY.mmtile` files bots' path searches read at the same time, in the background. At least 1, at most 16 (`NavMeshPathfinder` clamps it). |

A directory that does not exist keeps the default service (logged once at startup).

## vmaps (`Maps/Collision/VMaps`)

Reader for the cMaNGOS / vmangos `VMAP_7.0` files written by `vmap_assembler`. Tiles load and
unload with the map grids, or load on demand where no grid stands (`ICollisionTileDemand`, at most 16 per map); queries
see only loaded tiles (vmangos semantics). vmap internal
coordinates are the world rotated half a turn about the map centre (`x' = 17066.67 − x`, same for
y; z unchanged).

| File | Layout |
|---|---|
| `NNN.vmtree` | `"VMAP_7.0"`, u8 tiled, `"NODE"` BIH over all spawns, `"GOBJ"`, then (untiled maps only) spawn + u32 slot until EOF. |
| `NNN_YY_XX.vmtile` | `"VMAP_7.0"`, u32 count, then spawn + u32 slot. (Name order: tile Y first; X/Y are the terrain tile indices.) |
| `name.vmo` | `"VMAP_7.0"`, `"WMOD"` u32 size, u32 root WMO id, then optionally `"GMOD"` u32 count, groups, `"GBIH"` BIH. A spawn name stored with its NUL counted (`"Elfbed01.m2\0"`) names the file `Elfbed01.m2` with no `.vmo` (vmangos builds the path as a C string; 1298 client doodads). |
| group | f32[6] bound, u32 MOGP flags, u32 group WMO id, `"VERT"` u32 size u32 count f32[3]×n (count 0 ends the group), `"TRIM"` u32 size u32 count u32[3]×n, `"MBIH"` BIH, `"LIQU"` u32 size [liquid]. The LIQU size is vmangos' `WmoLiquid::GetFileSize()`, 4 bytes short (no type field); it is only a presence flag and the liquid is read by its own grid, as vmangos does. |
| spawn | u32 flags (M2 1, WORLDSPAWN 2, HAS_BOUND 4), u16 adt id, u32 id, f32[3] position, f32[3] rotation (degrees), f32 scale, [f32[6] bound], u32 name length, name. |
| BIH | f32[3] low, f32[3] high, u32 n + u32 node words, u32 m + u32 object indices. Node: axis bits 30–31 (3 = leaf), BVH2 bit 29, 29-bit offset; leaf: count in word 1; inner: clip planes in words 1–2. A node with an empty left side stores offset = right child − 3 and a left clip of −inf (vmangos `BIH::subdivide`), so its offset can equal the node itself; the "children follow their parent" guard applies to the child actually entered. |

Pieces: `BihTree` (reader, builder for fixtures, bounds-checked ray/point traversal), `WorldModel`
/ `GroupModel` (Möller–Trumbore ray/triangle with vmangos' determinant cut-off of 1e-5, enclosing-group floor), `ModelInstance` (rotation
Rz(rot.y)·Ry(rot.x)·Rx(rot.z), scale), `VMapTree` (per-map slots, refcounted tiles),
`VMapManager` (`ILineOfSight` + `ICollisionTileLifecycle` + `ICollisionTileDemand`). M2 doodads do not break line of
sight but count for heights and object hits. Model names must be plain file names.

## mmaps (`Maps/Collision/MMaps`) — managed Detour-compatible reader (not the grid fallback)

| File | Layout |
|---|---|
| `NNN.mmap` | `dtNavMeshParams`: f32[3] origin, f32 tile width, f32 tile height, i32 max tiles, i32 max polys (28 bytes). |
| `NNNXXYY.mmtile` | `MmapTileHeader` (u32 magic `MMAP`, u32 Detour version 7, u32 mmap version 6, u32 size, u32 uses-liquids) then the Detour tile. X/Y are the terrain tile indices (same order as `.map`). |
| Detour tile | 100-byte `dtMeshHeader`, then 4-byte aligned: verts (f32[3]), polys (32 bytes: first link, u16 verts[6], u16 neis[6], u16 flags, u8 vert count, u8 area/type), links (16 bytes with 64-bit refs as vmangos builds, 12 with 32-bit; inferred from the size), detail meshes (12), detail verts (12), detail tris (4), BV nodes (16), off-mesh connections (36). |

Recast space is (world Y, world Z, world X). Tiles are keyed by the Detour header (x, y).
Connectivity comes from the polygon neighbour entries: `neis − 1` inside the tile, `0x8000 | side`
across a tile border (joined with the adjacent tile's opposite-side edges where they overlap within
the walkable climb). Queries:

* without `EndTolerance`, nearest polygon in a (3, 5, 3) box, then a (3, 200, 3) box (projected end point → `Incomplete`);
  with `EndTolerance > 0` (return home, charge), both ends use vmangos `FindWalkPoly` `{5, 10, 5}`, a polygon more than 3 yd
  above the point is refused (`NoPath`), the path is `Incomplete` only when either end is more than 7 yd (3D) from its
  polygon, and a flier then goes straight with `Normal | NotUsingPath` (`PathFinder.cpp:128-142, :209-235`);
  over a polygon only the height beyond the walkable climb counts (as Detour);
* A* over polygons between portal midpoints with the include/exclude flag filter
  (`PathOptions`, default include ground/water/magma/slime, exclude nothing) and a node
  budget (`MaxSearchNodes`); an unreachable goal gives the path to the closest node
  (`Incomplete`, or `NoPath` with `AllowPartial = false`);
* funnel string-pulling into corners; heights from the detail mesh (or a fan over the polygon);
* more than `MaxPoints` (256) corners → cut and flagged `Short`;
* map without `.mmap`, or start or end on a tile that is not loaded → straight line
  `Normal | NotUsingPath` (vmangos' `HaveTiles` shortcut); start or end on a loaded tile but outside
  the search boxes →
  `NoPath` with a two-point shortcut.

## Provenance

Implemented from the file format descriptions and standard algorithms (BIH after Wächter & Keller
as used by Sunflow; Möller–Trumbore; A*; the "simple stupid funnel" string-pulling). cMaNGOS /
vmangos (GPL) and Recast/Detour sources were read only to confirm byte layouts and query
semantics; no code was copied. Test data is synthetic and authored in the repository
(`tests/ArcaneCore.Game.Tests/Collision/VMapFixture.cs`, `NavMeshFixture.cs`); no client data.

Real-data verification (2026-10-07, `RealTerrainDataTests`, gated on `ARCANECORE_TEST_TERRAIN_DIR`;
recipe in [maps-vmaps-mmaps.md](maps-vmaps-mmaps.md)): every file of a vmangos extraction parses,
and 4898 height / line-of-sight / area queries on maps 0, 1, 34, 36, 43, 189, 389 and 409 agree
exactly with vmangos' own `VMapManager2` on the same files. Getting there fixed three reader bugs
the synthetic tiles could not show: the empty-left-child BIH node (17368 such nodes in 40 trees;
whole subtrees, e.g. the Deathknell crypt, were unreachable), the 4-byte-short LIQU size (58
models with liquid were rejected) and NUL-terminated doodad names (1298 models never loaded).

## Known gaps

* No WMO liquid query (liquid is parsed and kept on `GroupModel.Liquid`). The `WMOAreaTable` lookup is done on the caller side:
  `TryGetAreaInfo` still returns only MOGP flags, root/ADT/group ids and the floor, and `TerrainInfo.GetAreaFlag` maps them to an area
  through `wmo_area_template` (docs/areas/world-state.md, "WMO areas"). It needs a loaded vmap tile at the position, so a place no grid
  holds reads the terrain area.
* No dynamic (game object) line of sight; static models only. Terrain does not block line of sight
  (only static models are checked).
* Line-of-sight heights: units look from `Unit.CollisionHeight` (`LocomotionState.CollisionHeight` times the unit's scale override `TransformScale` / `Creature.NativeScaleOverride`, 2 yd fallback without model data), non-units look from `1.0 yd`, and target objects are checked at `+1.0 yd` (`ObjectTargetHeight`, vmangos `WorldObject::GetLosCheckPosition`; game objects do not load model display bounds, so they also use `+1.0 yd`).
* Off-mesh connections are skipped; diagonal tile sides are ignored (Recast emits none). The BV
  tree drives the nearest-polygon lookup (verified at load, widened by the measured quantization slack; a tile whose tree fails
  verification scans linearly and is counted in `PathSearchCounters.BvTreesRejected`). Cross-tile edge portals are computed once per edge and cached.
* No vmangos `findSmoothPath` stepping: the path is the string-pulled corner list.
* No terrain-grid A* fallback; without mmaps paths are straight lines.

## Shared files touched

| File | Change | Why |
|---|---|---|
| `src/ArcaneCore.World/appsettings.json` | New `World:Collision` block. | Documents the settings. |
| `src/ArcaneCore.Game/Spells/SpellSystem.cs` | Two calls in `CheckCast` (`SpellLineOfSight.Check` / `CheckDest`) and one doc line. | Spells must fail with `LINE_OF_SIGHT`. |

## Schema

None.

## Suggested next slice

Combat `IsWithinLineOfSight` checks for melee/ranged; WMO liquid;
game-object LOS; `findSmoothPath` stepping. (The creature chase/follow generator listed here before
is done: it paths through `CreatureMapSystem.PlanPath`, see
[How other areas use it](#how-other-areas-use-it).)

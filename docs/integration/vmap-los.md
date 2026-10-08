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
| `MapCollision` (`map.Collision`) | Default map updater: `IsInLineOfSight`, `IsWithinLineOfSight(a, b)` (eye height 2 yd), `FindPath`, `GetHeight` (terrain + models, vmangos `GetHeightStatic`), `IsOutdoors`. Forwards grid create/unload to services implementing `ICollisionTileLifecycle`. |
| `unit.IsWithinLineOfSight(other)` | Extension for spells/AI/combat. |
| `ICollisionTileLifecycle` | Optional: services that want grid create/unload forwarded as terrain tile indices. |
| `SpellLineOfSight` | The cast-check hook (`SPELL_FAILED_LINE_OF_SIGHT`, honours `SPELL_ATTR_EX2_IGNORE_LOS` 0x4 and triggered casts). |
| `CreaturePathing.MoveTowards(system, creature, destination, run)` | Optional creature hook: asks `map.Collision.FindPath` and launches the spline to the next corner. |
| `CollisionServices.Install` | Builds the services from `CollisionOptions` and the data on disk (called by `World/Collision/CollisionFeature`). |

"No data" always means *open*: callers must never treat a missing service, map, tile or a
corrupt file as blocked. Missing or corrupt data is logged and read as open; nothing throws into
the world thread.

### How other areas use it

* **Spells**: `SpellSystem.CheckCast` calls `SpellLineOfSight.Check` after the unit range check and
  `SpellLineOfSight.CheckDest` after the destination range check (the only edit to the spell area).
* **Creature AI** (owned elsewhere): a chase/follow generator calls
  `CreaturePathing.MoveTowards(...)` on each re-path; it walks corner by corner. On
  `PathType.NoPath` it launches nothing and the AI decides (vmangos evades or moves straight).
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

A directory that does not exist keeps the default service (logged once at startup).

## vmaps (`Maps/Collision/VMaps`)

Reader for the cMaNGOS / vmangos `VMAP_7.0` files written by `vmap_assembler`. Tiles load and
unload with the map grids; queries see only loaded tiles (vmangos semantics). vmap internal
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
/ `GroupModel` (Möller–Trumbore ray/triangle, enclosing-group floor), `ModelInstance` (rotation
Rz(rot.y)·Ry(rot.x)·Rx(rot.z), scale), `VMapTree` (per-map slots, refcounted tiles),
`VMapManager` (`ILineOfSight` + `ICollisionTileLifecycle`). M2 doodads do not break line of
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

* nearest polygon in a (3, 5, 3) box, then a (3, 200, 3) box (projected point → `Incomplete`);
  over a polygon only the height beyond the walkable climb counts (as Detour);
* A* over polygons between portal midpoints with the include/exclude flag filter
  (`PathOptions`, default include ground/water/magma/slime, exclude steep slopes) and a node
  budget (`MaxSearchNodes`); an unreachable goal gives the path to the closest node
  (`Incomplete`, or `NoPath` with `AllowPartial = false`);
* funnel string-pulling into corners; heights from the detail mesh (or a fan over the polygon);
* more than `MaxPoints` (74) corners → cut and flagged `Short`;
* map without `.mmap` → straight line `Normal | NotUsingPath`; start or end off the loaded mesh →
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

* No WMO liquid query (liquid is parsed and kept on `GroupModel.Liquid`); no `WMOAreaTable` lookup
  (callers get MOGP flags, root/group ids and the floor).
* No dynamic (game object) line of sight; static models only. Terrain does not block line of sight
  (only static models are checked).
* Eye height is a fixed 2 yd above each unit's position (no per-model collision height).
* Off-mesh connections are skipped; diagonal tile sides are ignored (Recast emits none); the BV
  tree is not used (linear polygon scan per loaded tile).
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

Creature chase/follow generator using `CreaturePathing` (creature AI area); combat
`IsWithinLineOfSight` checks for melee/ranged; WMO liquid and `WMOAreaTable` lookups; game-object
LOS; BV-tree polygon lookup and `findSmoothPath` stepping.

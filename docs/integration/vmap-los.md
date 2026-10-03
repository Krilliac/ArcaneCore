# Integration notes: vmaps, line of sight and pathfinding (`feat/vmap-los`)

Status: **in progress** (draft PR). This first slice publishes the seam so spells, creature AI
and combat can code against it; the vmap reader and the navmesh query land in later commits on
the same branch.

## The seam (stable)

Namespace `ArcaneCore.Game.Maps.Collision`:

| Type | Role |
|---|---|
| `ILineOfSight` | World-wide static-model queries (vmangos `IVMapManager`): `IsInLineOfSight`, `TryGetObjectHit`, `GetModelHeight`, `TryGetAreaInfo`. |
| `IPathfinder` | World-wide path queries (vmangos `PathFinder`): `FindPath(mapId, start, end, PathOptions?)` → `PathResult(PathType, Points)`. |
| `OpenLineOfSight`, `StraightLinePathfinder` | Defaults without data: everything in sight; straight line flagged `Normal | NotUsingPath`. |
| `WorldCollision.Of(world)` | Side table holding the world's two services; `Install(los, pathfinder)` replaces them. |
| `MapCollision` (`map.Collision`) | Default map updater: `IsInLineOfSight`, `IsWithinLineOfSight(a, b)` (eye height 2 yd), `FindPath`, `GetHeight` (terrain + models, vmangos `GetHeightStatic`), `IsOutdoors`. |
| `unit.IsWithinLineOfSight(other)` | Extension for spells/AI/combat. |
| `ICollisionTileLifecycle` | Optional: services that want grid create/unload forwarded as terrain tile indices. |
| `SpellLineOfSight` | The cast-check hook (`SPELL_FAILED_LINE_OF_SIGHT`, honours `SPELL_ATTR_EX2_IGNORE_LOS` 0x4 and triggered casts). |

"No data" always means *open*: callers must never treat a missing service as blocked.

## Configuration

`World:Collision` (bound by `World/Collision/CollisionFeature`):

| Key | Default | Meaning |
|---|---|---|
| `VMapDirectory` | `""` → `<World:Maps:DataDirectory>/vmaps` | Extracted vmaps (`NNN.vmtree`, `NNN_YY_XX.vmtile`, `*.vmo`). |
| `MMapDirectory` | `""` → `<World:Maps:DataDirectory>/mmaps` | Generated navmeshes (`NNN.mmap`, `NNNXXYY.mmtile`). |
| `EnableLineOfSight` | `true` | vmangos `vmap.enableLOS`. |
| `EnableHeight` | `true` | vmangos `vmap.enableHeight`. |
| `EnablePathfinding` | `true` | vmangos `mmap.enabled`. |

## Shared files touched

| File | Change | Why |
|---|---|---|
| `src/ArcaneCore.World/appsettings.json` | New `World:Collision` block. | Documents the settings. |

## Schema

None.

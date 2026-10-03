# Integration notes: Grids, maps and terrain (`feat/grid-terrain`)

Built on the seam (`feat/fleet-plan`, 165b885). Everything new lives in owned folders:
`src/ArcaneCore.Game/Maps/Grid/`, `Maps/Terrain/`, `Maps/Templates/` (new: map registry,
instance bindings, per-world map services), `src/ArcaneCore.Game/Teleport/`,
`src/ArcaneCore.World/Teleport/`, `src/ArcaneCore.Data/Content/Maps/`, and tests named
`Grid*`, `Terrain*`, `Teleport*`.

## Schema version (needs the lead's allocation)

| Component | Version | Module | Tables |
|---|---|---|---|
| world | **2** (requested) | `ArcaneCore.Data.Content.Maps.MapDataModule` | `map_template`, `area_template`, `areatrigger_template`, `areatrigger_teleport`, `game_tele` |

World was at v1 with no steps at the seam. If another branch lands a world v2 first, this
module renumbers (one constant: `MapDataModule.Version`).

## Shared files touched (minimal, additive)

| File | Change | Why |
|---|---|---|
| `src/ArcaneCore.Game/Maps/Map.cs` | Internals rewritten on the grid/cell index; public API unchanged. Additive API: `Grids`, `Terrain`, `Template`, `ObjectCount`, `FindObject`, `GetHeight`, `GetZoneAndAreaId`, `GetLiquidStatus`, `AddObject`/`RemoveObject`/`SetActive` (seam for creatures/game objects), `TransitCount`. | Owned by this area (task: "Map.cs internals are yours"). |
| `src/ArcaneCore.Game/Entities/WorldObject.cs` | `X`/`Y` setters notify `Map.OnObjectMoved` (re-file in the grid); new `SetPosition(x, y, z, o)`; internal `MapSequence`. | The grid must always know an object's cell, however it moved. |
| `src/ArcaneCore.Game/Entities/Unit.cs` | `ApplyMovement` / `Relocate` set the position with `SetPosition` (one re-file instead of two). | Same. |
| `src/ArcaneCore.Game/Maps/WorldRuntimeOptions.cs` | One property: `MapOptions Maps` (`World:Maps` section). | Grid unload delay, activation distance, terrain data directory. |
| `src/ArcaneCore.Kernel/WorldData/MapData.cs` | New file: map/area/trigger/tele records and `IMapDataStore`. | Data contract between Data and Game. |
| `src/ArcaneCore.World/Net/WorldSession.cs` | `ProcessWorldPackets`: while the player is in no map (far teleport in flight) only `MSG_MOVE_WORLDPORT_ACK` is handled; everything else is dropped (vmangos STATUS_TRANSFER / STATUS_LOGGEDIN drops packets of a player not in world). | Handlers assume `player.Map` is set. |

No edits to `WorldServiceCollectionExtensions.cs`, `ChatHandlers.cs`, `CharacterHandlers.cs`,
`WorldRuntime.cs`, the DbContexts or `WorldTestHost.cs`: handlers, the teleport feature, the
`.tele`/`.go` commands, the data module and the test doubles all use the seam's discovery.

## Notes for other areas

- **Creatures / game objects:** spawn with `map.AddObject(obj, active)`; remove with
  `map.RemoveObject(obj)`. Subscribe to `map.Grids.GridLoaded` to spawn a grid's content and
  `map.Grids.GridUnloading` to despawn it (objects still in a grid when it unloads are removed
  for you). Moving an object (setting X/Y or `SetPosition`) re-files it and schedules its
  visibility pass for the end of the tick. `Player.VisibleObjects` is maintained by the map;
  do not mutate it directly.
- **Movement:** vmangos ignores client movement while a teleport is pending
  (`HandleMovementOpcodes`: `IsBeingTeleported()`); `MovementHandlers.cs` is not this area's
  file. `TeleportService.IsBeingTeleported(player)` is available if the owner wants the check.
- **Zone updates:** `PlayerHandlers.HandleZoneUpdate` still trusts the client's zone; with terrain
  data loaded, `map.GetZoneAndAreaId(x, y, z)` gives the server's answer.

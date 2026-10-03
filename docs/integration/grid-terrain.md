# Integration notes: Grids, maps and terrain (`feat/grid-terrain`)

Built on the seam (`feat/fleet-plan`, 165b885, merged as #1); verified locally on top of
`claude/friendly-hamilton-cuz4j4` at c03d867 (#9, character hooks — no overlap). Design and
reference notes: `docs/areas/grid-terrain.md`.

Everything new lives in owned folders:

- `src/ArcaneCore.Game/Maps/Grid/`, `Maps/Terrain/`, `Maps/Templates/` (map registry,
  instance bindings, per-world map services)
- `src/ArcaneCore.Game/Teleport/` (teleport state machine, packets, trigger geometry)
- `src/ArcaneCore.World/Teleport/` (`TeleportFeature`, `TeleportHandlers`, `TeleportCommands`)
- `src/ArcaneCore.Data/Content/Maps/` (`MapDataModule`, `EfMapDataStore`)
- `src/ArcaneCore.Kernel/WorldData/MapData.cs` (new file: records + `IMapDataStore`)
- tests: `tests/ArcaneCore.Game.Tests/GridTerrain/`, `tests/ArcaneCore.World.Tests/GridTerrain/`
  (including the `MapTestServices` test double), `tests/ArcaneCore.Data.Tests/MapDataModuleTests.cs`

## Assigned schema version

| Component | Version | Module | Tables |
|---|---|---|---|
| world | **3** | `ArcaneCore.Data.Content.Maps.MapDataModule` | `map_template`, `area_template`, `areatrigger_template`, `areatrigger_teleport`, `game_tele` |

Assigned in the [2026-10-03 integration candidate](fleet-20261003.md), after creatures v2.
The source branch originally requested v2.

## Shared files touched (minimal, additive)

| File | Change | Why |
|---|---|---|
| `src/ArcaneCore.Game/Maps/Map.cs` | Internals rewritten on the grid/cell index; public API unchanged. Additive API: `Grids`, `Terrain`, `Template`, `ObjectCount`, `FindObject`, `GetHeight`, `GetZoneAndAreaId`, `GetLiquidStatus`, `AddObject`/`RemoveObject`/`SetActive` (seam for creatures/game objects), `TransitCount`. | Owned by this area (task: "Map.cs internals are yours"). |
| `src/ArcaneCore.Game/Entities/WorldObject.cs` | `X`/`Y` setters notify `Map.OnObjectMoved` (re-file in the grid); new `SetPosition(x, y, z, o)`; internal `MapSequence`. | The grid must always know an object's cell, however it moved. |
| `src/ArcaneCore.Game/Entities/Unit.cs` | `ApplyMovement` / `Relocate` set the position with `SetPosition` (one re-file instead of two). | Same. |
| `src/ArcaneCore.Game/Maps/WorldRuntimeOptions.cs` | One property: `MapOptions Maps` (`World:Maps` section). | Grid unload delay, activation distance, terrain data directory. |
| `src/ArcaneCore.Kernel/WorldData/MapData.cs` | New file: map/area/trigger/tele records and `IMapDataStore`. | Data contract between Data and Game. |
| `src/ArcaneCore.World/appsettings.json` | New `World:Maps` block (`DataDirectory`, `GridUnload`, `GridCleanUpDelayMs`, `GridActivationDistance`) with the defaults. | Documents the settings; binding needs no code change. |
| `src/ArcaneCore.World/Net/WorldSession.cs` | `ProcessWorldPackets`: while the player is in no map (far teleport in flight) only `MSG_MOVE_WORLDPORT_ACK` is handled; everything else is dropped (vmangos STATUS_TRANSFER / STATUS_LOGGEDIN drops packets of a player not in world). | Handlers assume `player.Map` is set. |

No edits to `WorldServiceCollectionExtensions.cs`, `ChatHandlers.cs`, `CharacterHandlers.cs`,
`WorldRuntime.cs`, the DbContexts or `WorldTestHost.cs`: handlers, the teleport feature, the
`.tele`/`.go` commands, the data module and the test doubles all use the seam's discovery.

## Opcodes and command roots claimed

- Handlers: `MSG_MOVE_TELEPORT_ACK` (199), `MSG_MOVE_WORLDPORT_ACK` (220), `CMSG_AREATRIGGER` (180).
- Sent: `SMSG_NEW_WORLD` (62), `SMSG_TRANSFER_PENDING` (63), `MSG_MOVE_TELEPORT` (197),
  `MSG_MOVE_TELEPORT_ACK` (199), `SMSG_AREA_TRIGGER_MESSAGE` (696); builder for
  `SMSG_TRANSFER_ABORTED` (64).
- Command roots: `.tele`, `.go` (with `.go xyz`). Other `.go` sub-commands (`.go creature`,
  `.go object`, …) belong under this root: add them to `TeleportCommands`, or ask to move the
  root if another area needs to own them.

## Public API for other areas

- `WorldMaps.Of(world)`: `Registry` (map templates), `Terrain`, `Areas`, `Instances`,
  `FindAreaTrigger`, `FindAreaTriggerTeleport`, `FindGameTele`.
- `session.Services.GetRequiredService<TeleportFeature>().Teleports`: `TeleportTo`,
  `TeleportToHomebind`, `IsBeingTeleported(Near|Far)`, `StageOf`, `DestinationOf`.
- `Map`: `AddObject` / `RemoveObject` / `SetActive`, `FindObject`, `GetHeight`,
  `GetZoneAndAreaId`, `GetLiquidStatus`, `Grids` (events `GridCreated`, `GridLoaded`,
  `GridUnloading`, `GridUnloaded`), `Terrain`, `Template`.

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
- **Hearthstone / spells / GM `.recall` etc.:** call `TeleportService.TeleportTo` (or
  `TeleportToHomebind`); it picks near or far by itself and handles the whole transfer.
- **Login of a character saved on an instance map:** the map must be in `map_template`
  (or be a continent); with an empty table only maps 0 and 1 exist.
- **Zone updates:** `PlayerHandlers.HandleZoneUpdate` still trusts the client's zone; with terrain
  data loaded, `map.GetZoneAndAreaId(x, y, z)` gives the server's answer.

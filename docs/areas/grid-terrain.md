# Area: grids, maps and terrain

Branch `feat/grid-terrain`. This area replaces the distance-scan visibility of `Map` with the
vmangos/cmangos-classic cell/grid system, adds grid loading and unloading, reads terrain
`.map` files, holds the map/instance registry from the world database, and implements world
ports, near teleports and area-trigger teleports. Integration details (schema version,
shared-file edits, notes for other areas) are in `docs/integration/grid-terrain.md`.

References: vmangos `development` (primary 1.12 server), cmangos-classic `master`,
gtker/wow_messages (packet layouts). Where they disagree the servers win; every disagreement
is listed in [Reference discrepancies](#reference-discrepancies).

## Design

### Cells and grids (`src/ArcaneCore.Game/Maps/Grid/`)

- `GridDefines`: 64 × 64 grids of 533.33333 yards, 16 × 16 cells per grid (cells of 33.33
  yards, 1024 × 1024 per map), `ComputeGridCoord` / `ComputeCellCoord` (vmangos/cmangos
  `ComputeGridPair` / `ComputeCellPair`: double precision, +0.5, truncation, clamped),
  `CalculateCellArea` (cells under a circle's bounding square), `IsValidMapCoord`.
- `Grid`: one grid's 256 cells, its state (Active / Idle / Removal), timer, active-object
  count and unload locks (`GridUnload = false` sets the explicit lock on every grid, as
  vmangos `setUnloadExplicitLock(!sWorld.getConfig(CONFIG_BOOL_GRID_UNLOAD))`).
- `GridContainer`: the per-map cell index of every object.
  - `Add(obj, active)`: an active non-player object (made active with `SetActive`) loads only
    its own grid; a player loads its grid and every grid within `GridActivationDistance` (vmangos `Map::Add(Player*)` →
    `EnsureGridLoadedAtEnter` + `LoadMapCellsAround`); other objects only create their grid
    (`EnsureGridCreated`).
  - `Relocate(obj)`: re-files an object whose X/Y changed; an active object entering a new
    cell re-activates its grid (timer × 0.1); a player also loads every grid within
    `GridActivationDistance` of its new position (grids up to `GridPrefetchDistance` beyond that,
    which do not exist yet, only get their tiles prefetched).
  - `CollectObjects(x, y, r)`: the objects of the cells a circle touches in loaded grids
    (vmangos `Cell::Visit` with `dont_load`). Callers apply their exact distance test.
  - `CollectPlayers(x, y, r)`: the players of the same cells only. Each cell keeps its
    players in a second list, as vmangos keeps them in the cell's "world" container
    (GridDefines.h `AllWorldObjectTypes`, visited with a `WorldTypeMapContainer` visitor), so a
    query that is only about players does not walk the cell's creatures and game objects.
  - `Update(diff)`: the vmangos `GridStates.cpp` state machine — Active grids check every
    expiry/10 ms and go Idle when nothing active is in or near them (`ActiveObjectsNearGrid`:
    visibility range in cells + 1 around the grid); Idle → Removal with the full delay;
    Removal unloads once the timer passes (refused, and the timer restarted, if something
    active came near). Unloading raises `GridUnloading`, evicts the objects left in the grid,
    then raises `GridUnloaded`.

`WorldObject.X`/`Y` notify their map when they change (`Map.OnObjectMoved`), so an object is
always filed in the right cell however it moved; `SetPosition(x, y, z, o)` changes all four
with a single re-file.

### Position and movement block

A unit has its position twice: `X`/`Y`/`Z`/`Orientation` and the position in its movement block (`Unit.Movement`, written
in create blocks and re-applied by `ApplyMovement`). The two are always equal: every position write, by `SetPosition`,
`Relocate` or a single setter, copies itself into the movement block (`Unit.OnPositionWritten`), as vmangos
`WorldObject::Relocate` and `SetOrientation` write `m_movementInfo` ([Object.cpp:1567-1598](https://github.com/vmangos/core/blob/0e3ff01e76d4758e8a7c3108b2717cc785ed56fa/src/game/Objects/Object.cpp#L1567-L1598)). Only the position follows: the
flags, time stamp (see [Reference discrepancies](#reference-discrepancies)) and transport data stay what the last movement, relocation or flag change made them, so a server-side move
keeps walk mode, and a teleport start (`ResetMovementForTeleport`, which re-applies the block with the moving flags cleared)
leaves the unit where it is.

### Visibility on cells (`src/ArcaneCore.Game/Maps/Map.Visibility.cs`)

The public `Map` API and the visible behaviour are unchanged (all existing visibility tests
pass unmodified). Internally:

- Candidates for a player's visibility pass are the objects in the cells within
  `VisibilityRange + VisibilityGreyDistance + own radius + largest radius in the map`, plus
  everything the player currently sees, plus every player that currently sees it, plus the
  map-wide objects (`AddMapWideObject`: the Deeprun Tram cars) and, when the player's camera
  looks from another object (far sight, possession), the objects in the cells within that
  radius of the camera. They are
  de-duplicated and processed in the order they entered the map, then judged with the same
  `IsWithinVisibilityDistance` as before, measured from the viewer's camera (a map-wide object and
  the camera's own body are always in range) — so the result is identical to the old full scan.
- An observer index (`object GUID → players whose client has it`, the inverse of
  `Player.VisibleObjects`) replaces scans in `BroadcastToObservers` and in non-player
  visibility updates.
- A moved or new non-player object's pass looks only at players: those in the cells within
  the same radius (`CollectPlayers`) plus its observers, in join order — exactly the players
  the all-objects candidate list contained, without walking the creatures and game objects
  around it (vmangos `Map::UpdateObjectVisibility`, Map.cpp, visits only the
  `WorldTypeMapContainer` with `VisibleChangesNotifier`). This was the hot path behind the live
  "Slow map update … visibility 42-58 ms; players 4, moved ~670" warning: every wandering
  creature collected, de-duplicated and sorted the ~460 objects of the 7 × 7 cells around it.
  `VisibilityPerformanceTests` reproduces it (66,000 clustered spawns on map 0, 4 players, 670
  creatures moving each tick) and checks every tick against a brute-force full scan: the
  visibility phase went from a median of 16.4 ms (309,670 candidates a tick) to 0.4 ms (761
  candidates a tick) on the dev box, with identical visible sets. The slow-map warning now
  reports `visibility candidates` too.
- `BroadcastInRange` visits the players of the cells within range plus each player whose camera
  (far sight, mind vision, a possessed unit) is one of the objects of those cells (range ≤ 0
  still means the whole map), and applies the same 3D test from the listener's camera, not their
  feet (vmangos `Map::MessageDistBroadcast` visits the `WorldTypeMapContainer`, cameras included,
  and `MessageDistDeliverer` measures from `Camera::GetBody`).
- Adopted: vmangos' `Visibility.RelocationLowerLimit` as `World:Maps:VisibilityRelocationLowerLimit`
  (default 10 yards, 0 = a pass on every move). As in `Unit::OnRelocated`, a unit's move (a
  creature's or a player's) queues a visibility update only once the unit is more than that far
  (3D) from where the last one was queued; moves within it keep the last visible sets, and the
  grey distance and the next pass cover the difference. A game object gets one on every move.
  Not adopted: the visibility-update timeout (`MapUpdate.VisibilityUpdate.Timeout`, which defers
  the rest of the relocated list to the next tick): it changes who sees whom at the edge of
  sight for a tick or more, and with the players-only mover pass the phase does not need it. The AI
  relocation notify (`Visibility.AIRelocationNotifyDelay`) is the creature AI's
  (`AiRelocationNotifier`, docs/areas/creature-ai.md).
- Non-player objects (`AddObject` / `RemoveObject` / `SetActive`) are the seam for creatures
  and game objects: moving one schedules its visibility pass for the end of the tick (a creature
  only once it is more than `World:Maps:VisibilityRelocationLowerLimit`, 10 yards, from where its
  last pass was scheduled; a game object on every move).
- `Update` order: packets → packets of players in transit from this map (only the world-port
  ack is handled) → due logouts → unit heartbeats → active-cell marking (continents, when
  `World:Maps:UpdateActiveCellsOnly` is on) → the map updaters (`IMapUpdater`: creatures, game
  objects, zones, …) → visibility → values → flush → grid state machine → actions
  scheduled with `RunAfterUpdate` (far teleports). The terrain clean-up is not part of it: the
  world runs it once per tick after the maps (`TerrainManager.Update`, vmangos
  `TerrainManager::Update` at the end of `World::Update`).

### Terrain (`src/ArcaneCore.Game/Maps/Terrain/`)

- `TerrainTile` parses one `maps/MMMXXYY.map` file (format below) and answers
  `GetHeight` (vmangos `GridMap::getHeight` — the two-triangle interpolation for float maps,
  the same on u16/u8 samples scaled between grid height and max height; holes give no
  height on float maps), `GetAreaFlag`, `GetLiquidLevel`, `GetTerrainType` and
  `GetLiquidStatus` (vmangos `GridMap::getLiquidStatus`: none / above / water-walk / in /
  under, by the depth of z below the surface, within 2 yards under the ground).
- `TerrainInfo` (one per map id, shared by all its instances) loads tiles on demand,
  reference-counts them for grids (`Load` / `Unload`; `Map` refs a grid's tile when the grid
  is created and unrefs it when it unloads) and frees unreferenced tiles once a minute
  (`CleanUp`, run once per world tick by `TerrainManager.Update`). Every map of the id holds
  it (`MapRefCount`: `TerrainManager.Acquire` in the `Map` constructor, `Release` in
  `Map.UnloadAll`, vmangos `Referencable` / `Map::~Map`); after the last one the manager drops
  it in the `Update` whose clean-up frees its last tile, and the next instance of the id gets a
  new one (kept with `GridUnload` off, as vmangos `TerrainManager::UnloadTerrain`). Callers
  without a map ask `TerrainManager.For` per use instead of keeping it. It answers `GetHeight`,
  `GetAreaFlag`, `GetZoneAndAreaId` (through `AreaTable`), `GetLiquidStatus` (kept only when
  the surface is above the ground, as vmangos `TerrainInfo::getLiquidStatus` without vmaps)
  and `GetWaterOrGroundLevel`.
- `TerrainManager` reads `<DataDirectory>/maps/`. **Optional and fail-soft:** no data
  directory, a missing directory or a missing file gives empty tiles (no height:
  `TerrainTile.InvalidHeightValue`, area 0, no liquid); a file with the wrong magic/version
  or truncated is logged as an error and treated as missing. The server never stops for
  terrain.
- `AreaTable` maps a tile's area flag to an area/zone the vmangos way
  (`AreaEntry::GetByAreaFlagAndMap`: first entry with the flag on this map, else the last on
  any map, else the map's `linked_zone`).

`Map` exposes `GetHeight`, `GetZoneAndAreaId`, `GetLiquidStatus` and `Terrain`.

#### The `.map` format (z1.4)

As written by the vmangos and cmangos-classic map extractors (identical `GridMapDefines.h`),
little-endian:

| Section | Layout |
|---|---|
| Header | 10 × u32: `"MAPS"`, `"z1.4"`, then offset/size of area, height, liquid, holes (offset 0 = absent) |
| Area | u32 `"AREA"`, u16 flags (0x1 = one area for the grid), u16 grid area, then u16[16 × 16] unless 0x1 |
| Height | u32 `"MHGT"`, u32 flags (0x1 none, 0x2 u16, 0x4 u8), f32 grid height, f32 grid max height, then V9[129 × 129] and V8[128 × 128] as f32/u16/u8 |
| Liquid | u32 `"MLIQ"`, u8 flags (0x1 no type, 0x2 no height), u8 global flags, u16 global entry, u8 offset X/Y, u8 width/height, f32 level, then u16 entry[16 × 16] + u8 flags[16 × 16] unless 0x1, then f32[w × h] unless 0x2 |
| Holes | u16[16 × 16] |

File name: `%03u%02u%02u.map` of map id, tile X, tile Y, where tile X =
`(int)(32 - worldX / 533.33333)` and tile Y likewise from world Y (vmangos
`TerrainInfo::LoadMapAndVMap` builds the name from its `(y, x)` index pair, which is this).

### Maps, instances, teleport tables (`src/ArcaneCore.Game/Maps/Templates/`)

- `MapRegistry`: `map_template` rows; with an empty table the two continents (0 Eastern
  Kingdoms, 1 Kalimdor) so a fresh world database still works.
- Instances: the `InstanceRegistry` stub is gone. `Game/Instances/InstanceManager` is the
  `IMapResolver` that picks or creates the instance, each instance has its own `Map`, and binds
  are persisted (see [instances](../integration/instances.md)).
- `WorldMaps`: the per-world bundle (registry, terrain, area table, area triggers,
  `areatrigger_teleport`, `game_tele`) attached to the `WorldRuntime` through a weak side
  table, so `WorldRuntime.cs` is untouched. `Load(MapContent)` applies the vmangos loader
  rules and returns what it skipped: a teleport row without an `areatrigger_template` row,
  with an unknown target map, or with an all-zero target position.
  `FindGameTele(name)`: exact case-insensitive name, else the first (by id) containing it.

### Teleports (`src/ArcaneCore.Game/Teleport/`, `src/ArcaneCore.World/Teleport/`)

Hooks for other areas: `NearTeleportStarting(player, destination)` before the near teleport ack is sent,
`FarTeleportExecuting(player)` while the player is still in its old map, `TeleportCompleted(player)` after a near ack or the arrival in
the new map. The pets use them to take the player's pet along (`PetTeleportFollow`, docs/integration/pets.md).

`TeleportService.TeleportTo(player, map, x, y, z, o)` (vmangos `Player::TeleportTo`) refuses
invalid coordinates, unknown maps, a battleground map the player is not bound to
(`BattlegroundEntryAllowed`, answered by the battleground system: docs/integration/battlegrounds.md), a player
in no map, a player whose quest settlement is pending (`IsQuestSettlementPending`), a second far
teleport while one is under way, and a map change the instance rules refuse (`IMapResolver.CanEnter`,
vmangos `MapManager::CanPlayerEnter` and `DungeonMap::CanEnter`: raid group, hourly limit, player
cap, pending reset; docs/integration/instances.md). It clears the moving/turning and transport flags and resets the client time stamp, then:

- **Same map (near):** stores the destination and sends `MSG_MOVE_TELEPORT_ACK` (packed
  GUID, u32 movement counter, movement block at the destination). The client's
  `MSG_MOVE_TELEPORT_ACK` (u64 GUID, u32 counter, u32 time) is ignored unless a near
  teleport is pending and the GUID is the player's; then observers near the old position get
  `MSG_MOVE_TELEPORT`, the player is moved, observers near the new position get it too, the
  zone is refreshed from terrain and a visibility pass is scheduled (vmangos
  `ExecuteTeleportNear`). Only the position of the movement block changes (swimming,
  levitating and the other client state stay; the fall in progress ends), and the zone or
  area update runs at once (`Unit::TeleportPositionRelocation`, `Unit.cpp:9845-9883`).
- **Other map (far):** after the current map update (vmangos `ScheduleFarTeleport`): clear
  the selection, `SMSG_TRANSFER_PENDING` (u32 map), leave the map (observers get destroys),
  `SMSG_NEW_WORLD` (u32 map, f32 x, y, z, o). While loading, the player is in no map: the
  session drops every packet except `MSG_MOVE_WORLDPORT_ACK` (vmangos drops logged-in
  opcodes for a player not in world) and a save stores the destination (vmangos saves
  `m_teleportDest` during a far teleport). On the ack (empty packet) the player enters the
  new map at the start of the next tick: zone from terrain,
  `SendInitialPacketsBeforeAddToMap`, map add (self create + visibility),
  `SendInitialPacketsAfterAddToMap` (vmangos `HandleMoveWorldportAckOpcode`). An invalid
  destination falls back to the bind point; a failed map add returns the player to where the
  teleport started, and if that fails too the session is closed.
- Logout or disconnect mid-teleport forgets the teleport (`WorldRuntime.PlayerLoggingOut`).

`TeleportFeature` (an `IWorldFeature`) attaches `WorldMaps` at startup, loads the map tables
through `IMapDataStore` when one is registered, logs skipped rows and owns the
`TeleportService`. `TeleportHandlers` (an `IOpcodeHandlerGroup`) handles
`MSG_MOVE_TELEPORT_ACK`, `MSG_MOVE_WORLDPORT_ACK` and `CMSG_AREATRIGGER`.

**Area triggers** (vmangos `HandleAreaTriggerOpcode`; `CMSG_AREATRIGGER` = u32 id): unknown
ids are ignored; the player must be in the trigger with 5 yards of tolerance
(`IsPointInAreaTriggerZone`: a sphere of `radius + 5`, or the box rotated by
`2π − box_orientation`, grown by 5 on every side); then the `areatrigger_teleport` row, whose
target map must be registered. A player below `required_level` and not in GM mode gets
`SMSG_AREA_TRIGGER_MESSAGE` (u32 length including the terminator, then the string): the row's
message, or mangos_string 49 "You must be at least level %u to enter.". The ghost rules
(`GhostEntryRules`), the key items, a required quest, the instance script and the
conditions-table reference can refuse too (see [area triggers](area-triggers.md)); otherwise
the player is teleported.

**Commands** (`TeleportCommands`, an `ICommandGroup`; both SEC_MODERATOR as in the cmangos /
vmangos command tables):

| Command | Behaviour |
|---|---|
| `.tele #location` | A `game_tele` row by id or name (exact, else contains); "Teleport location not found!" (mangos_string 164) when there is none. |
| `.go xyz #x #y [#z [#mapid]]` | Teleport to a position on the given (default: current) map, keeping the orientation. Without `#z`, the ground or water surface there (vmangos `GetWaterOrGroundLevel`); without terrain data that is refused. |

Both validate like vmangos `HandleGoHelper` and answer
"Target map or coordinates is invalid (X: %f Y: %f MapId: %u)" (mangos_string 263) or
"You cannot teleport to a battleground map." (mangos_string 733).

### Data (`src/ArcaneCore.Data/Content/Maps/`)

`MapDataModule` (`IDataModule`, world schema **version 3**) creates `map_template`,
`area_template`, `areatrigger_template`, `areatrigger_teleport` and `game_tele` (vmangos world
DB column meanings, PascalCase column names as elsewhere in ArcaneCore) and registers
`EfMapDataStore` as `IMapDataStore`. A world database at version 1 is upgraded in place; its
rows are kept. The tables start empty: `arcane-content-importer refresh --dbc-dir` (or `import-map-dbc`) fills `map_template` and
`area_template` with every Map.dbc map and AreaTable.dbc area and the dump's instance rows, `areatrigger_template` from AreaTrigger.dbc,
and `import` the teleports (docs/areas/content-import.md). `WmoAreaDataModule` (world schema **55**) adds `wmo_area_template` (the
client WMOAreaTable.dbc: the area of each WMO group, read by `EfMapDataStore` into `MapContent.WmoAreas`); the same two commands fill it
when `WMOAreaTable.dbc` is given, and a world without its rows runs as before (a warning at start). A world upgraded to step 55
starts with the table empty: run `refresh --dbc-dir` with `WMOAreaTable.dbc` present (only the three-path `import-map-dbc` is the other
way, and it refuses an existing world unless `--replace` is given, which rewrites map_template and area_template too).

## Configuration (`World:Maps`)

| Key | Default | Meaning (vmangos equivalent) |
|---|---|---|
| `DataDirectory` | `""` | Extractor output holding `maps/` (`DataDir`). Empty = no terrain. |
| `GridUnload` | `true` | Unload idle grids (`GridUnload`). |
| `GridCleanUpDelayMs` | `300000` | Idle time before unloading, at least 60000 (`GridCleanUpDelay`, `MIN_GRID_DELAY`). |
| `GridActivationDistance` | `100` | Radius around a player whose grids are loaded (`m_gridActivationDistance` = `Visibility.Distance.Continents`); an active non-player object loads only its own grid. A grid stays active while something active is within the visibility range of it (`ActiveObjectsNearGrid`). |
| `VisibilityRelocationLowerLimit` | `10` | How far (3D) a unit must move from where its last movement-triggered visibility pass was scheduled before the next one is (`Visibility.RelocationLowerLimit`); 0 = a pass on every move. |
| `UpdateActiveCellsOnly` | `true` | On continents, update creatures only in the cells within `GridActivationDistance` of a player or active object (`Map::UpdateCells` → `UpdateActiveCellsSynch`); false updates every creature of the loaded grids. |
| `GridPrefetchDistance` | `200` | How far beyond `GridActivationDistance` a player looks for grids that do not exist yet, to read their terrain, vmap and navmesh tiles on the thread pool first; other objects look half this distance around themselves. 0 = off, which also turns off the arrival prefetch (a teleport reads its destination's tiles when it starts, in a budget of its own). ArcaneCore only. |

## Reference discrepancies

| Topic | vmangos | cmangos-classic | gtker / others | ArcaneCore |
|---|---|---|---|---|
| Cells per grid side | 16 | 16 | TrinityCore uses 8 | 16 (the 1.12 servers) |
| Grid/cell pair from (x, y) | `ComputeGridPair` returns `(y_val, x_val)`: the pair's `x_coord` comes from world Y (`GridDefines.h:154-163`) | returns `(x_val, y_val)`, used as `[x][y]` | — | as cmangos-classic; `CellCoord.X` comes from world X |
| Terrain tile index | `GetGrid` and the grid load/unload both index `m_GridMaps[from Y][from X]` (`GridMap.cpp:1132-1141`, `Map.cpp:330-331`, the pair's `x_coord` being from Y) | both index `[from X][from Y]` | — | as cmangos-classic: a grid refs the tile that covers it (`TerrainTile`: `63 − grid.X`, `63 − grid.Y`, tile X from world X) |
| MSG_MOVE_TELEPORT_ACK (client → server) GUID | u64 (`ObjectGuid >>`) | u64 | packed GUID | u64 |
| MSG_MOVE_TELEPORT to observers on a near teleport | sent before and after the move | not sent | listed only for 2.4.3+ | sent (vmangos) |
| SMSG_TRANSFER_ABORTED | u8 reason | u8 reason | u32 map + u8 reason + u8 argument | u8 reason (sent by the instances area's `InstanceManager` when it refuses a teleport for the hourly limit, the player cap or a pending reset) |
| `.go xyz` z argument | required | optional (terrain height) | — | optional (cmangos), so both forms work |
| Liquid kind | remapped through LiquidType.dbc when loaded | same | — | the `.map` file's flags (no DBC stores in ArcaneCore) |
| Area flag → area fallback | `GetFlagByMapId` path after `GetGrid` (never null there, so dead) | — | — | not implemented (unreachable in vmangos) |
| Terrain clean-up first run | random 20–40 s, then every minute | — | — | every minute from start (deterministic) |
| Terrain of a map id after its last map | deleted with that map, tiles at once (`Map::~Map` → `UnloadTerrain`) | — | — | dropped by the clean-up that frees its last tile (up to a minute later); an instance made meanwhile takes it again |
| `areatrigger_teleport` / `map_template` columns | also `patch`, `build`, `required_condition`, `ghost_entrance_*` | `required_quest_*`, `required_item*`, … | — | the columns ArcaneCore uses, including `ghost_entrance_*`, `required_item`/`required_item2`, `required_quest_done` and the condition reference (docs/areas/area-triggers.md); the importer keeps the newest `patch` up to 10 and stores no patch column (docs/areas/content-import.md) |
| Teleport observers set | `ObjectMessageDeliverer`: players in the cells within visibility distance | — | — | observers within visibility distance (clients without the mover ignore the packet anyway) |
| Movement block on a position write | `WorldObject::Relocate` calls `m_movementInfo.ChangePosition` and `UpdateTime(getMSTime())` ([Object.cpp:1567-1577](https://github.com/vmangos/core/blob/0e3ff01e76d4758e8a7c3108b2717cc785ed56fa/src/game/Objects/Object.cpp#L1567-L1577); `UpdateTime` sets `stime`, [MovementInfo.h:204](https://github.com/vmangos/core/blob/0e3ff01e76d4758e8a7c3108b2717cc785ed56fa/src/game/Objects/MovementInfo.h#L204)); `SetOrientation` calls `ChangeOrientation` ([Object.cpp:1592-1598](https://github.com/vmangos/core/blob/0e3ff01e76d4758e8a7c3108b2717cc785ed56fa/src/game/Objects/Object.cpp#L1592-L1598)) | — | — | the position and orientation are copied (`Unit.OnPositionWritten`); the time stamp is deliberately kept: `FlagAuthorityObserver` measures speed from the stored block's server time to the next client block, and a fresh stamp on every server-side move (a bot floor snap, a creature turning) would shorten that interval and could flag an ordinary move |

## Tests

- `tests/ArcaneCore.Game.Tests/GridTerrain/`: `GridDefinesTests`, `GridContainerTests` (loading,
  state machine, eviction, `GridUnload = false`, queries, relocation), `GridMapTests` (the
  `AddObject` seam, observer index, grey zone, grid/terrain lifetime), `TerrainTests`
  (synthetic `.map` files: float/u16/u8 heights, holes, areas, all liquid statuses, bad and
  truncated files, fail-soft manager, tile ref-counting, terrain released after the last
  instance of its map id), `TeleportServiceTests` (near and
  far flows tick by tick, invalid destinations, logout mid-transfer, instance binding).
- `tests/ArcaneCore.Data.Tests/MapDataModuleTests.cs`: fresh world DB at the current
  version, v1 → current upgrade keeping rows, store round trip — on SQLite, MariaDB and PostgreSQL.
- `tests/ArcaneCore.World.Tests/GridTerrain/`: end to end over sockets — `.go xyz` near
  teleport with the client ack and observers, `.tele` far teleport through the full transfer
  and login packets, packets dropped mid-transfer, command errors and security, area-trigger
  level message / GM-mode bypass / near teleport / unknown id. `MapTestServices` registers an
  in-memory `IMapDataStore` for every `WorldTestHost`.

## What is left (outside this area's shipped scope)

- Instance maps, resets, player limits and `SMSG_TRANSFER_ABORTED` paths are implemented by
  the instances area (see [instances](../integration/instances.md)); nothing instance-specific
  is left in this area.
- vmaps (WMO/model heights, line of sight, indoor areas) and mmaps (pathfinding) are implemented
  by the collision and pathing area (`src/ArcaneCore.Game/Maps/Collision/`, see
  [collision and pathing](collision-pathing.md), which lists what is left there).
- Server-side zone updates from terrain on movement are implemented by the world-state area
  (`ZoneAreaUpdater`, the 1 s zone timer; see [world state](world-state.md)); the client's
  `CMSG_ZONEUPDATE` value is used only while zones cannot be derived or with
  `World:Zones:ClientZoneTrust` = `Always`.
- Grid content loading is done: creatures (`CreatureMapSystem`) and game objects
  (`GameObjectMapSystem`) spawn on `GridLoaded` and despawn on `GridUnloading`.
- Transports, taxi flights, ghosts entering dungeons and area-trigger scripts/quests/taverns are
  implemented by their own areas (see [transports](transports.md), [taxi](taxi.md),
  [graveyards and resurrection](graveyards-resurrection.md), [area triggers](area-triggers.md)
  and [rested XP](rested-xp.md)); the area-trigger scripts are `AreaTriggerScriptsFeature` and
  `SmartAreaTriggerFeature` in `src/ArcaneCore.World/Creatures/`.
- Battleground entrance triggers (`areatrigger_bg_entrance`, the vmangos `HandleAreaTriggerOpcode`
  branch for the portal join): the table is not imported (see [battlegrounds](battlegrounds.md));
  battlegrounds are joined at a battlemaster.

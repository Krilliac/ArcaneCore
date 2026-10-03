# Integration notes: instances and dungeons (`feat/instances`)

Base: `codex/integrate-feature-fleet-20261003` at `0d32fba1070a0be08932a85551a4c1b4ca191cfc`,
brought up to `9346ad4` (#21, character deletion seams) for the delete hook.
References: vmangos `MapManager.cpp`, `Map.cpp` (`DungeonMap`), `MapPersistentStateMgr.cpp`,
`Player.cpp`, `Group.cpp`, `MiscHandler.cpp`, `MovementHandler.cpp`; packet layouts from
gtker/wow_messages (1.12). Behaviour follows vmangos unless a gap below says otherwise.

## What it does

- **Per-instance maps.** `WorldRuntime` keys maps by `(mapId, instanceId)`.
  `GetMap(id)` is instance 0, the shared copy (continents). `GetMap(id, instance)` creates an
  instance map with the default per-map systems (`DefaultMapUpdaters`) and raises `MapCreated`.
  `FindMap(id, instance)` finds one. `UnloadMap(map)` requests an unload after the current map
  pass: only for registered maps with a non-zero instance id, no players and nobody in
  transit. The world then raises `MapUnloading`, removes the map and calls `Map.UnloadAll()`
  (grids unloaded, `IsUnloaded` set, and later `AddPlayer` calls throw). Every map keeps its
  own grids, `IMapUpdater`s, combat and visibility.
- **Map resolver seam.** `IMapResolver` (`WorldRuntime.MapResolver`) has four members:
  - `ResolveLoginMap(player)`: login.
  - `CanEnter(player, mapId)`: checked when a far teleport starts.
  - `ResolveEntry(player, mapId)`: picks the instance at worldport-ack arrival; null refuses
    the entry and the teleport returns the player to its origin.
  - `OnEntered(player, map)`.

  Without a resolver everything uses instance 0, as before.
- **`Game/Instances/InstanceManager`** (the resolver) follows the vmangos rules:
  - **Entry.** The player enters its own bind if it is permanent or the player is ungrouped,
    else the group's bind, else a new instance. Ids start at 101 (vmangos
    `RESERVED_INSTANCES_LAST`).
  - **Binding on entry** (`DungeonMap::BindPlayerOrGroupOnEnter`):
    - An ungrouped player gets a solo bind.
    - A grouped player's group gets the bind. Other members' temporary binds to that map are
      dropped unless they are inside one.
    - A permanent group bind makes the entering player permanently bound and sends
      SMSG_INSTANCE_SAVE_CREATED.
  - **Requirements** (`MapManager::CanPlayerEnter`, `DungeonMap::CanEnter`):
    - Raids need a raid group (SMSG_RAID_GROUP_ONLY 0/1) unless the player is a GM or
      `IgnoreRaidGroup` is set.
    - `map_template.player_limit` caps the players inside. GMs do not count and are not
      capped. A refusal sends SMSG_TRANSFER_ABORTED `MAX_PLAYERS`.
    - An instance waiting to reset refuses entry with `NOT_FOUND`.
    - The cap is checked again on arrival. A player who passed the check at the trigger but
      arrives after the instance filled up gets the abort and is sent back to its origin.
    - The area-trigger level requirement stays in `TeleportHandlers`. The group requirement
      comes from the resolver through `TeleportService.TeleportTo`.
  - **Unload timer.** An instance map starts with the unload timer armed (default 30 min).
    Entry disarms it. The last player out re-arms it, or sets it to 1 ms when the map must
    unload as soon as it is empty. When the timer runs out the map unloads, and the save
    survives while anyone is bound to it.
  - **Normal dungeon reset.** A normal dungeon resets when `now >= created + 2 h` and it is
    empty: the schedule is disarmed on entry and armed by the last player out or by an unload.
  - **CMSG_RESET_INSTANCES:**
    - An ungrouped player resets its own temporary non-raid instances, except the one it is
      in, and gets SMSG_INSTANCE_RESET for each.
    - The group leader resets the group's non-raid instances. If a member is offline it gets
      RESET_FAILED `Offline`. If players are inside it gets RESET_FAILED `General`, and the
      players inside get a system message. Otherwise it gets SMSG_INSTANCE_RESET and the save
      is deleted.
    - Other group members are ignored.
  - **Group events** (`GroupManager` events):
    - Creating a group turns the leader's binds into group binds.
    - Joining resets the joiner's own temporary instances, except the one it is in.
      Rejoining the group that owns the instance you are in makes your presence valid again.
    - Leaving inside an instance without a permanent bind starts the homebind timer:
      SMSG_RAID_GROUP_ONLY(60000, 1), then a teleport to the hearthstone bind point after
      60 s. Rejoining cancels it with SMSG_RAID_GROUP_ONLY(0, 1).
    - Disbanding gives the member left inside the group's instance as a solo bind, drops the
      group's binds and resets resettable ones.
    - A leader change drops permanent group binds. It also drops a temporary group bind if
      the new leader has its own bind there (the old leader keeps that instance). The new
      leader's binds become the group's.
    - A grouped player who worldports out of a dungeon loses its personal temporary save of
      it, which the group's save replaces (`ResetPersonalInstanceOnLeaveDungeon`).
  - **Raid lockouts.**
    - Killing a creature with `ExtraFlags & 0x1` (`CREATURE_FLAG_EXTRA_INSTANCE_BIND`) binds
      everyone inside permanently. The manager listens to `map.Combat.UnitKilled`. The
      killer's group also gets a permanent bind if its leader is inside.
    - A permanent bind makes the save non-resettable.
    - Every dungeon map with `reset_delay > 0` has a global reset time T. The first T is
      today + `reset_delay` days + `ResetTimeHour` (UTC). A stored T that has passed rolls
      forward in whole periods, keeping the weekly phase.
    - Warnings go out at T−1 h, T−15 min and T−5 min to players inside. Missed warnings are
      skipped.
    - At T−1 min every save of the map is unbound and deleted, players inside are teleported
      home and the maps unload. The next T is the reset day + `reset_delay` days + the reset
      hour.
  - **SMSG_RAID_INSTANCE_INFO** lists the player's permanent binds (u32 count, then map,
    seconds to reset, instance id for each). It is sent for CMSG_REQUEST_RAID_INFO, and at
    login to players with at least one permanent bind.
  - **SMSG_RAID_INSTANCE_MESSAGE** goes out on entering a raid. The type depends on the time
    left: more than 1 h is welcome (4), more than 15 min is hours (1), more than 5 min is
    minutes (2), otherwise soon (3).
  - **Login.** A player saved on a dungeon map re-enters its instance if its bind (or its
    group's bind) still exists and matches the instance it last entered. Otherwise it is
    moved to the target of the area trigger on that map that leads to the map's ghost
    entrance map, or to its bind point when there is no such trigger.
  - **Startup load.** Binds of unknown instances, instances of unknown or non-dungeon maps,
    expired raid saves and saves nobody is bound to are dropped and deleted from storage.
- **Spawns per instance.** `CreatureWorldFeature` gives every new instance map of a map that
  has spawns its own `CreatureMapSystem`, so creatures are never shared between instances, and
  drops it on `MapUnloading`. `FindSystem(Map)` / `GetOrCreateSystem(Map)` are new, and
  `FindSystem(uint)` / `GetOrCreateSystem(uint)` still mean instance 0. The creature GM
  commands use the player's own map instance. `QuestNpcFeature` forgets unloaded maps
  (`QuestObjectiveAdapter.Detach`).
- **World daemon.** `World/Instances/InstanceFeature` does the following:
  - Binds `World:Instances` into `InstanceOptions`.
  - Loads `IInstanceStore` at attach.
  - Wires `GroupManager` events, `TeleportFeature.TeleportToHomebind` and system chat. This
    wiring is posted to the world thread, because features attach in type-name order.
  - Runs `UpdateSchedule` every 5 s on a timer posted to the world thread.
  - Persists through an ordered, retrying write queue that is drained in `StopAsync`.
  - Is an `ICharacterDeleteHook` (#21): `OnCharacterDeletingAsync` drains the write queue so
    no queued bind lands after the rows go; `OnCharacterDeletedAsync` calls
    `InstanceManager.DeleteCharacter` on the world thread (binds and last instance dropped)
    and queues `IInstanceStore.DeleteCharacterAsync` after those writes.

  `InstanceHandlers` handles CMSG_RESET_INSTANCES and CMSG_REQUEST_RAID_INFO.
  `InstanceCommands` adds `.instance listbinds | unbind <map|all> | stats` (administrator).

## Persistence: characters schema

`Data/Instances/InstanceDataModule` adds four new tables:

| Table | Columns |
|-------|---------|
| `instance` | id, map, reset_time (Unix seconds) |
| `character_instance` | character, instance, permanent |
| `instance_reset` | map, next global reset |
| `character_last_instance` | character, map, instance |

The last table holds what vmangos keeps in `characters.instance_id`. The tables are new, so
nothing existing changes. `EfInstanceStore` implements `Kernel/Instances/IInstanceStore`.
Every write is an idempotent upsert or delete. `LoadAsync` first deletes binds and
last-instance rows of characters that no longer exist, and binds of missing instances.
`InstanceDataModule` implements `ICharacterDataCleanup` (#21): character deletion removes the
character's `character_instance` and `character_last_instance` rows in the deletion
transaction; an instance nobody is bound to any more is dropped at the next load.

**Integrated allocation: characters v8**, after reputation v7.
`BranchSchemaVersion = 8` is active; the reserved-v9 marker is historical.
Spell state follows at v9 and economy at v10. Source histories retain provisional allocations.

Group binds are kept in memory only, because groups themselves are not persisted in this
codebase. After a restart only character binds remain, so a dungeon only its group was bound
to is dropped at load.

## Durable chest loot (handoff item 4)

The consumed and remaining loot of the chests of an instance is stored with the logical save
([gameobjects-loot.md](gameobjects-loot.md)). The save is the scope: a chest commit needs the `instance` row, the
rows are deleted with it, and nothing but a real reset or deletion clears them.
- `EfInstanceStore.DeleteInstanceAsync` also deletes the instance's `loot_state*` rows inside its existing transaction.
  This is hygiene: on engines whose default isolation does not serialize it against a concurrent loot commit an
  orphan row can survive, and the startup purge in `EfLootStateStore.LoadInstanceStatesAsync` (rows of a missing
  instance go) is what protects a reused id. The purge runs in `GameObjectLootFeature.Attach`, before the post that
  loads the instance saves (features attach in type-name order, so the loot feature attaches first and its install
  post runs before `InstanceManager.Load`).
- `InstanceManager.InstanceDeleted` (new event, `Action<uint>`) is raised on the world thread from `DeleteSave`: a real
  reset, a delete after nobody is bound, and the startup drop of an unbound or expired save. The loot feature drops
  its cached chests of that instance.
- `InstanceWriteQueue.Enqueued` and `WaitForAsync(watermark, token)` (and `InstanceFeature.WriteWatermark` /
  `WaitForWritesAsync`): wait until the first N queued writes were attempted. `FlushAsync` polls until the queue is idle
  and cannot be cancelled, so a busy queue could starve it; a loot commit uses the watermark so the queued
  `InstanceSaved` of a new save lands before the commit looks for the `instance` row. A lost `InstanceSaved` write
  (three attempts, then logged and dropped) leaves that instance's chests refused until restart.
- Group-only binds are not persisted, so a chest of an instance only a group was bound to is dropped at restart with the
  instance (existing semantics, not a loss).

## Shared-file edits

All of these are minimal and additive unless stated otherwise.

- `Game/Maps/WorldRuntime.cs`:
  - Maps are keyed by `(mapId, instanceId)`.
  - New `GetMap(id, instance)`, `FindMap`, `UnloadMap`, the `MapUnloading` event and the
    `MapResolver` property.
  - `AddPlayer` asks the resolver.
  - The tick iterates a snapshot of the maps and then processes unload requests.
- `Game/Maps/Map.cs`: the constructor takes the instance id. New `InstanceId`, `IsUnloaded` and
  the internal `UnloadAll`. `AddPlayer` refuses an unloaded map.
- `Game/Maps/IMapResolver.cs`: new file.
- `Game/Teleport/TeleportService.cs`:
  - `CanEnter` is checked before a far teleport.
  - `ResolveEntry` and `OnEntered` are used on arrival.
  - The `InstanceRegistry` stub call is removed.
- `Game/Maps/Templates/WorldMaps.cs` and `InstanceRegistry.cs`: the stub and
  `WorldMaps.Instances` are removed. Nothing else used them.
- `Game/Groups/GroupManager.cs`:
  - New events `MemberAdded`, `MemberRemoved`, `Disbanding` (raised before members are
    cleared) and `LeaderChanged`.
  - `Disband` takes the initiator.
- `World/Creatures/CreatureWorldFeature.cs` and `CreatureCommands.cs`: per-instance creature
  systems.
- `World/Npc/QuestNpcFeature.cs` and `QuestObjectiveAdapter.cs`: forget unloaded maps.
- `World/Handlers/PlayerHandlers.cs`: the CMSG_REQUEST_RAID_INFO stub moved to
  `InstanceHandlers`. Without the feature it still answers with an empty list.
- Tests:
  - `tests/.../GridTerrain/TeleportServiceTests.cs`: the stub-binding test is replaced.
  - `tests/ArcaneCore.Data.Tests/IntegratedSchemaTests.cs`: the module list and the
    characters version.
- Docs: `docs/integration/grid-terrain.md` no longer lists `WorldMaps.Instances`.
  `docs/areas/grid-terrain.md` (the `InstanceRegistry` bullet) and `docs/CLUSTERING_DESIGN.md`
  (line 82, "shares a Map among dungeon instances") still describe the removed stub. They
  were left untouched to keep this PR's shared edits small; this file supersedes them.

## Coordination with other round-2 branches

- `feat/creature-ai` (world v8) and `feat/gameobjects-loot` (world v7) connect only through
  interfaces:
  - `WorldRuntime.MapCreated` / `MapUnloading` and `Map.InstanceId`: attach per-map systems
    per instance and drop them on unload.
  - `IMapUpdater`: per-map simulation keeps working per instance.
  - `map.Combat.UnitKilled`: used for the instance-bind flag.

  Game objects should follow the creature pattern above, so that spawns load per instance map
  and are never shared.
- The `CreatureWorldFeature` change is small. If `feat/creature-ai` rewrites that file, keep
  the `MapCreated`/`MapUnloading` hooks and `FindSystem(Map)`.

## Tests

- `tests/ArcaneCore.Game.Tests/Instances/InstanceManagerTests.cs` (19 tests) drives real far
  teleports, groups and ticks:
  - Two groups get different instance maps, and members share one.
  - A solo player returns to its own instance.
  - Unload after the delay keeps the save, re-entry recreates the instance in a fresh map,
    and re-entering cancels the unload.
  - `WorldRuntime` only unloads empty instance maps.
  - Solo reset, including while inside.
  - Group reset: by the leader only, failing while occupied or while a member is offline.
  - Raid group requirement, and the GM exemption.
  - Raid welcome, permanent bind, raid info and no reset on request.
  - Global raid reset: warnings, unbind, homebind, next reset time and unload.
  - Normal dungeon 2 h reset, only while empty.
  - Player cap, the arrival race (eject back to origin) and GMs.
  - Leaving the group starts the homebind timer, rejoining cancels it, and expiry teleports
    the player home.
  - Disband keeps the remaining player inside.
  - Group creation converts the leader's binds.
  - Joining resets the joiner's own instance, but not while inside.
  - Login: re-entry, entrance trigger and bind point.
  - Startup load: restoring and cleanup.
  - Killing an instance-bind creature binds everyone inside, an ordinary kill does not.
- `tests/ArcaneCore.Data.Tests/InstanceStoreTests.cs` (3 × provider matrix):
  - Round trip and in-place updates.
  - Deletes and no-op deletes.
  - Deleting a character through `EfCharacterStore` removes its rows (module cleanup), and
    the load drops orphaned binds and binds of missing instances.
  - `CharacterDeletionTests` (#21) also checks that the module declares its cleanup.
- `tests/ArcaneCore.World.Tests/Instances/InstanceLoopbackTests.cs` (4 tests over real
  sessions):
  - Two groups through the Deadmines area trigger, with the full worldport-ack flow, get
    separate instances.
  - Teleporting out, then CMSG_RESET_INSTANCES (skipped while inside, then SMSG_INSTANCE_RESET)
    and CMSG_REQUEST_RAID_INFO. The persisted writes arrive in order, and re-entering creates
    a new instance.
  - Each instance spawns its own creatures.
  - CMSG_CHAR_DELETE of a character bound to a dungeon drops the bind in memory and queues
    unbind, then the character purge, in order.
- Existing tests pass unchanged except for the two edits listed above.

## Gaps (honest)

- **Character deletion** uses the #21 seams (above). A save left without binds is deleted at
  once, or when its map unloads.
- **Group binds are not persisted**, because groups are in-memory only.
- **Instance contents do not persist across an unload or restart.** This covers creature
  deaths, respawn timers and boss state (vmangos `creature_respawn` / instance data, and
  `InstanceData` scripts). A re-created map respawns everything.
- **Homebind on raid-group loss** teleports to the hearthstone bind point. vmangos
  `RepopAtGraveyard` uses graveyards, and there is no graveyard data here.
- The **"leave the instance to reset it" system message** text is ArcaneCore's own wording.
  It is not the vmangos `LANG_LEAVE_TO_RESET_INSTANCE` string, which could not be verified.
- **Not modelled:**
  - Corpses inside instances (`CanPlayerEnter` corpse rules).
  - The 1.12 "too many instances" (5 per hour) limit.
  - Battleground maps, which are still refused by `TeleportTo`.
  - Instance scripts, and `areatrigger_teleport.required_condition` (it waits for the
    conditions system).
  - A permanent bind credited through a pet or totem killer: only a player killer is
    credited.
- **Schedule granularity:** the reset schedule runs every 5 s, so warnings and resets can be
  up to 5 s late.

## Next slice

- Persisted instance state (`creature_respawn`, `gameobject_respawn` per instance) once
  `feat/creature-ai` and `feat/gameobjects-loot` expose respawn seams.
- `InstanceData`-style scripts (boss state, doors) on `MapCreated`.
- Graveyards for the homebind repop.
- The 5-instances-per-hour limit.

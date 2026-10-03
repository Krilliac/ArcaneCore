# Integration notes: creatures (`feat/creatures`)

Built on the seams from `feat/fleet-plan` (docs/integration/seams.md). Almost all wiring is
discovered, so the area does not edit `WorldServiceCollectionExtensions.cs`, `Program.cs`,
`WorldHost.cs`, the DbContexts or `WorldTestHost.cs`.

## Schema version (lead to confirm)

| Component | Version | Owner | Step |
|---|---|---|---|
| world | **2** | `ArcaneCore.Data.World.Creatures.CreatureDataModule` | `CreateTableChange` × 5: `creature_template`, `creature_spawn`, `creature_movement`, `creature_model_info`, `creature_addon` |

The version is the `CreatureDataModule.Version` constant. If another world module lands first
and takes 2, change that constant to the next free number. Nothing else depends on the value.

## Shared-file edits (minimal, additive)

| File | Change | Why |
|---|---|---|
| `src/ArcaneCore.Game/Maps/Map.cs` | `_updaters` list, `AddUpdater`, `FindUpdater<T>`, `Updaters`. A new step (1c) in `Update` runs each updater after the logout timers and before `_inUpdatePhase`, inside a try/catch. `RemovePlayer` calls `OnPlayerRemoved` before the visible set is cleared. | Maps had no hook for non-player objects. `WorldRuntime.RunTick` only calls `Map.Update`, and a self-reposting world command would spin `RunCommands`. |
| `src/ArcaneCore.Game/Maps/IMapUpdater.cs` (new) | `IMapUpdater { Update(Map, uint); OnPlayerRemoved(Map, Player); }` | Generic seam. Game objects, dynamic objects and the grid/map area can reuse it. |

All other files are new, under these paths:
- `src/ArcaneCore.Kernel/WorldData/Creatures/`
- `src/ArcaneCore.Data/World/Creatures/`
- `src/ArcaneCore.Game/Creatures/`
- `src/ArcaneCore.World/Creatures/`
- `tests/**/Creature*` and `tests/ArcaneCore.World.Tests/Creatures/`
- `docs/areas/creatures.md` and this file

## Discovered registrations

- `CreatureDataModule : IDataModule` (world). It maps the five tables in `WorldDbContext` and registers `ICreatureDataStore` → `EfCreatureDataStore` (scoped) through `AddWorldDatabase`.
- `CreatureWorldFeature : IWorldFeature`.
  - `Attach` binds `CreatureOptions` from the `Creatures` config section and loads the content synchronously. It fails closed if the store throws.
  - It then posts `Install` to the world thread, which attaches a `CreatureMapSystem` to every map that has spawns.
  - It uses an optional `ICreatureHeightProvider` from DI (seam for grid/terrain).
- `CreatureHandlers : IOpcodeHandlerGroup` handles CMSG_CREATURE_QUERY (LoggedIn, on the session task).
- `CreatureCommands : ICommandGroup` adds the `.creature add|info|kill|respawn|delete` commands (GM). The root is `.creature`, not `.npc`, so the NPC-services area can own `.npc` without a duplicate-root startup failure.
- Tests: `CreatureTestServices : IWorldTestServices` registers an async-local `ICreatureDataStore` (empty unless a creature test sets content) and a probe feature.

## Seams offered to other areas

- **Combat / spells:**
  - `CreatureWorldFeature.FindSystem(mapId)?.FindCreature(guid)` → `Creature` (a `Unit`).
  - `CreatureMapSystem.KillCreature(creature)` runs the death state: corpse, decay, respawn.
  - `StopMoving`, `MoveTo` (straight spline plus SMSG_MONSTER_MOVE).
  - `Creature.Template` has the damage, attack time and rank fields.
- **Grid/map/terrain:**
  - Register an `ICreatureHeightProvider` singleton in DI to get ground Z for random movement.
  - Grid math is in `CreatureMapSystem.ComputeGrid` (vmangos GridDefines). If a shared grid/cell index lands, the system's per-grid lists can move onto it.
- **Quests / gossip / NPC services:**
  - `Creature.NpcFlags`, `Template.NpcFlags` and `FindCreature` for interaction checks.
- **Items:** equipment (virtual item slots) is not set. Hook point: `Creature.InitializeFields`.

## Merge notes

- `Map.cs` is the only conflict-prone file. The edit is about 35 lines in three places.
- Data tests now see world schema v2. `CreatureDataTests` has a `WorldV1Context` that upgrades a real v1 world DB on all three engines.

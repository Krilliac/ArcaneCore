# Integration notes: creature movement, spawns and respawn (wave 4, lane `creature-movement-spawns`)

Branch `claude/vw5-creature-movement-spawns`, based on `claude/vw4-integration` 7313b9e. The delivered scope, limits and reference citations are in
`docs/areas/creature-movement-spawns.md`; this page is what the integrator needs to merge it.

## Schema (the numbers are placeholders: the integrator renumbers in merge order)

| Context | Version | Module | Change |
|---|---|---|---|
| World | **23** (lane: 21) | `CreatureMovementTemplateDataModule` (`src/ArcaneCore.Data/World/Creatures/`) | `CreateTableChange creature_movement_template` |
| World | **24** (lane: 22) | `CreatureSpawnEntryDataModule` (same folder) | `CreateTableChange creature_spawn_entry` |
| Characters | **24** (lane: 21) | `CreatureRespawnDataModule` (`src/ArcaneCore.Data/Creatures/`) | `CreateTableChange creature_respawn`; implements `ICharacterDataCleanup` as a deliberate no-op |

Each number is one named constant (`Version`); tests read the constants or `Schema.CurrentVersion`. `IntegratedSchemaTests.FeatureModules_HaveAssignedVersions_AndDistinctTables`
lists the three modules (one line each): re-sort it when renumbering.

**Hosted-CI exposure.** MariaDB and PostgreSQL tests run only on hosted CI. Every new table has `AvailableProviders` theories (create from the previous version, re-run,
round trip, delete/replace); on this machine only SQLite ran. The steps are single `CREATE TABLE` statements (MariaDB's non-transactional DDL cannot leave a half step),
`creature_respawn` has explicit lower-case column names, its upsert is an EF read-then-update-or-insert (no `REPLACE INTO`), and nothing relies on a lock.

## Shared files touched (additive unless said)

* `Entities/Unit.cs`: `MovementFlags.WalkMode` joins the flags `Relocate` keeps (a creature's walk mode is server state and every spline step relocates it).
* `Creatures/CreatureMapSystem.cs`: two optional trailing constructor parameters (`ICreatureRespawnPersistence?`, `IRespawnClock?`); `MovePath`/catch-up use the offset option and call `SyncWalkMode`.
* `Creatures/CreatureMapSystem.Lifecycle.cs`: entry choice in `LoadGrid`/`Respawn`, persistence hooks (`SaveRespawnOnDeath`, `DeletePersistedRespawn`, `SaveRespawnOnRemoval`).
* `Creatures/CreatureMapSystem.Evade.cs`: evade no longer sets health/mana (option `EvadeRestoresFullHealth`), and a waypoint mover goes to its last reached node else the spawn point (it used to go to where combat began). **The threat-and-aggro lane also edits evade/leash code: expect a textual merge there.**
* `Creatures/CreatureMapSystem.Host.cs`: `OnMovementFinished` informs the AI of `Waypoint` arrivals.
* `Creatures/Creature.cs`: respawn delay drawn once per object (`DrawRespawnDelay`), corpse decay by rank only (option), `OnAllLootRemoved`, `ChangeTemplate`; the raw `ExtraFlagAlwaysRun` constant is gone (use `Template.Behaviour`).
* `Creatures/CreatureMovement.cs`: the random and waypoint generators moved to `Movement/*.cs`; `MovementGeneratorType` numbers follow vmangos; `ICreatureMover.MovementOptions` added.
* `Loot/LootService.cs` and `Loot/LootSeams.cs`: the looted-corpse decay delegates to `Creature.OnAllLootRemoved`; `LootOptions.LootedCorpseDecayRate` now defaults to 0 (vmangos/cmangos), it was 0.5.
* `Kernel/WorldData/Creatures/CreatureContent.cs`: optional constructor parameters `entryWaypoints`, `spawnEntries`; `ResolveWaypointPath`, `GetEntryWaypoints`, `GetSpawnEntries`.
* `Data/World/Creatures/CreatureDumpImporter.cs`, `EfCreatureDataStore.cs`, `Data/Content/Import/Spec/ContentTableSpecs.cs`, `Cli/ContentImporterCli.cs`: the two world tables and the vmangos `id2..id5` columns.
* `Data/Instances/EfInstanceStore.cs`: `DeleteInstanceAsync` also deletes the instance's `creature_respawn` rows.
* `World/Creatures/CreatureWorldFeature.cs`: passes the respawn persistence to every system, `Systems` accessor, saves an unloading instance's dead creatures. `World/Reload/CreatureContentReloadable.cs`: a spawn with entry rows is orphaned only when none of its entries has a template.

## Configuration (`Creatures:*`, every default is retail)

`Movement:MonsterMoveOffsetBase` (Destination | Midpoint), `Movement:RunDuringWanderChancePercent` (15), `Movement:EvadeRestoresFullHealth` (false),
`Movement:HonorWaypointRunColumn` (false), `Respawn:DrawDelayAtLoad` (true), `Respawn:HonorTemplateCorpseDecay` (false), `Respawn:AlternateEntries` (true),
`Respawn:Persist` (true), `Respawn:SaveImmediately` (true).

## Behaviour changes existing tests had to follow (deliberately, not weakened)

* Evade leaves health alone (`CreatureAiHostTests`): the assertion is now "below maximum", and `EvadeHealthTests` proves the regeneration.
* Corpse decay ignores the template column (`CreatureTests.CorpseDecay_...`), looted corpses decay by a third of the respawn delay (`LootServiceTests`).
* A waypoint mover's evade target is the last reached node (replaces `WaypointMover_EvadesToWhereCombatBegan_ThenResumesThePath`); `Waypoints_RunFlagWaitTimesAndLoop` sets `HonorWaypointRunColumn`.
* `CreatureDataTests`: the vmangos `id2` warning is replaced by entry rows.

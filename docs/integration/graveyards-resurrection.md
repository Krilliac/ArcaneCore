# Graveyards and resurrection: integration notes

For the integrator of wave 4. The behaviour is in `docs/areas/graveyards-resurrection.md`; this page is what the merge needs.

## Schema

- **World:** one new step, `GraveyardDataModule` (`src/ArcaneCore.Data/Graveyards/GraveyardDataModule.cs`), tables `world_safe_locs`
  and `game_graveyard_zone`. The version is the single constant `GraveyardDataModule.Version`, **28** after wave-4 integration, allocated as 21 in the lane (the base's last world
  step is `StartActionWorldModule` = 20). Renumber that one constant when other lanes' world steps merge first; the tests refer to the
  constant (`IntegratedSchemaTests` has its row), never to a literal.
- **Characters, Auth:** none. No `ICharacterDataCleanup` (no per-character rows). Resurrection requests are in memory.
- Only the graveyard store touches a database. Its tests (`GraveyardStoreTests`) are `TestDatabases.AvailableProviders` theories written
  for MariaDB and PostgreSQL semantics (DDL first in the bootstrapper, the import in its own transaction; lower-case snake_case names; float
  tolerance; ids above `int.MaxValue`; the exception type of a key conflict is not asserted). They ran on SQLite only here: wait for the
  hosted run before merging.

## Files other lanes also touch (narrow, additive edits)

| File | Edit | Likely overlap |
|---|---|---|
| `Combat/MapCombat.Death.cs`, `MapCombat.cs`, `MapCombat.Melee.cs`, `CombatHooks.cs`, `UnitCombat.cs` | scheduled repop, ghost-form seam, instanceable by template, death durability, request clear | battlegrounds, honor-pvp, aura-engine |
| `Entities/Player.cs` | `LogoutLocation` and its use in `CreateSnapshot` | any lane editing the snapshot |
| `Spells/SpellSystem.cs`, `SpellSystem.Targeting.cs`, `SpellSystem.Death.cs` | corpse-owner target, resurrect effects take the explicit unit, self-resurrection choice at death | aura-engine-completeness, spell-modifier-engine |
| `Teleport/TeleportService.cs` | `TeleportCompleted` event, revive on entering the corpse's map | battlegrounds (it refuses BG maps) |
| `World/Teleport/TeleportHandlers.cs` | the ghost check before the level check | battlegrounds |
| `Data/Content/Import/Spec/ContentTableSpecs.cs`, `Cli/ContentImporterCli.cs` | three table specs; the importer in the import run | content-import lanes |
| `Npc/SpiritHealerResurrection.cs`, `MapCombat.SpiritHealer.cs` | corpse-graveyard teleport, `SicknessLevel` | npc/quest lanes |
| `tests/.../CombatTestKit.cs` | `AckPendingMovement` (answers the water-walk and speed orders) | every combat test |

## Collisions to resolve at the merge

- **Aura 95 (`AuraType.Ghost`)** is registered by `GhostAuras`. A second registration (aura-engine-completeness) makes `SpellSystem` throw at
  startup; drop one. Aura 56 (the wisp's transform) and 79/80/101 (the sickness stats) have no handler yet.
- **`SpellInfo` flags:** the ghost spells need `AttributesEx3 0x100000` and `Attributes 0x800000` (castable while dead) in the spell store, which
  classic-db has; a hand-made spell store without them cannot apply a ghost aura to a dead player (the feature then falls back to the flag).
- **`IGraveyardOverride`** is for the battleground lane: register on `GraveyardFeature.Service.AddOverride`.
- **Hot reload:** `WorldGraveyards.Of(world).Build(content, out diagnostics)` and `.Replace(catalog)` are the pieces for a
  `game_graveyard_zone` reloadable.

## Configuration (`World:Death`)

`GraveyardFallbackToDefaults` (false: retail; true: mangos-classic default graveyards 4 and 10), `SicknessLevel` (11), `GhostFormAura` (true).
The two reclaim-delay options already existed.

## Tests that moved

`CombatDeathTests.Repop_MakesAGhostWithAVisibleCorpse` and `UndermapTests` (the death and ghost cases) now answer the ghost's movement orders
before expecting the graveyard trip (`CombatTestKit.AckPendingMovement`): the repop is scheduled as in vmangos.
`GhostPersistenceTests.LogoutWhileDead_RelogsAsAGhostAtTheBody` waits for the ghost snapshot instead of any snapshot.

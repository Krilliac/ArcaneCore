# Honor: integration points

Area document: [areas/honor.md](../areas/honor.md). This lists what the honor lane touches outside its own files, so the integrator can merge it with the
other wave-4 lanes.

## Schema

- **Characters schema version 23** (the lane allocated 21; renumbered at wave-4 integration): `CharacterHonorDataModule.Version` (`src/ArcaneCore.Data/Honor/CharacterHonorDataModule.cs`). Renumber that one constant.
  `tests/ArcaneCore.Data.Tests/IntegratedSchemaTests.cs` lists the module by constant; add or move its line when renumbering. The module implements
  `ICharacterDataCleanup`.

## Shared files edited (narrow, additive)

| File | Edit | Why |
|---|---|---|
| `Game/Combat/MapCombat.DamageEvents.cs` | new event `DamageTaken(attacker, victim, damage)` | the killing blow must reach the PvP damage ledger (`DamageDealt` is non-lethal only) |
| `Game/Combat/MapCombat.Melee.cs` | one raise of `DamageTaken` in `DealDamage`, after `ApplyDuelClamp`, before the lethal check, never for self damage | vmangos `Unit.cpp:788-796` records the history before the lethal check |
| `Game/Channels/Channel.cs` | `honorRank` read from `HonorHooks.For(world).InternalRank` instead of the constant 0 | WorldDefense speech and the rank byte in channel messages |
| `Game/Npc/QuestNpcServices.Vendor.cs` | rank gate uses `Deps.Honor` (current rank + required level); `PriceDiscount(player, npc, taxi)` applies the honor discount | rank items and the honor discount |
| `Game/Npc/QuestNpcServices.Travel.cs` | the flight price asks for the taxi variant of the discount | flight master discount |
| `Game/Npc/QuestNpcServices.cs` | optional `Honor` on `QuestNpcDependencies` | the vendor gate |
| `Game/Npc/NpcServiceContracts.cs`, `Game/Npc/InventoryItemService.cs` | `ItemInfo.RequiredLevel` (optional last parameter) | the vendor level requirement |
| `World/Npc/NpcServicesFeature.cs` | `Honor = dependencies.Honor ?? HonorFeature.ActiveService` | wires the gate |
| `World/Npc/ConditionFeature.cs` | `HonorRank` in the condition context | the PvP_RANK condition |
| `tests/ArcaneCore.Game.Tests/CombatTestKit.cs` | `CombatTestUnit` is no longer sealed | a controlled-unit (pet) test |

Everything else is new files under `*/Honor/`. The spell effect module (`HonorSpellEffects`) is discovered by `SpellHandlerModules`; the handlers
(`InspectHandlers`), commands (`HonorCommands`, `HonorModifyExtension`) and features (`HonorFeature`, `HonorMaintenanceFeature`,
`HonorCharacterDeleteHook`) are discovered by their existing mechanisms.

## Seams other lanes use

- `IHonorAwards.Add(player, cp, HonorKind, source)` (battlegrounds: bonus honor, quest honor).
- `IPlayerHonor` (`CurrentRank`, `HighestRank`, `VisualRank`): areatrigger rank gates, anything that needs a rank.
- `HonorFeature.ActiveService` is null while `World:Honor:Enabled` is false; consumers then keep their old behaviour.
- `HonorService.RestorePvpFlags/CapturePvpFlags` are the only touch points with the PvP flag.

## Collisions to expect

- `MapCombat.Melee.cs` `DealDamage` (threat, aura engine, ranged combat): the honor edit is the one raise line.
- Spell handler registration: `HonorSpellEffects` registers effect 45 (`AddHonor`) and aura 159 (`NoPvpCredit`, a no-op: only its presence matters). A
  module may only add handlers, so a second lane registering the same effect or aura fails `SpellSystem` startup; check that the aura-engine lane does not
  also register `NoPvpCredit`.
- `QuestNpcDependencies` gained a parameter at the end; positional construction elsewhere is unaffected.

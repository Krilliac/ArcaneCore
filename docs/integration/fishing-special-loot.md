# Integration: fishing and special loot

Lane `claude/vw4-fishing-special-loot` (wave 3). Area description: [docs/areas/fishing-special-loot.md](../areas/fishing-special-loot.md).

## Schema (integrator renumbers one constant each)

| Constant | Component | Number at this base | Tables |
|---|---|---|---|
| `SpecialLootDataModule.Version` | World | 15 (base ends at 14) | `fishing_loot_template`, `pickpocketing_loot_template`, `disenchant_loot_template`, `skill_fishing_base_level`, `creature_pickpocket_loot` |
| `ItemLootDataModule.Version` | Characters | 16 (base ends at 15) | `item_loot_state`, `item_loot` |

Both are `CreateTableChange`s only (no ALTER, so MariaDB's implicit-commit DDL stays resumable). `IntegratedSchemaTests` lists both modules through the constants; tests never use the
literals. The characters module implements `ICharacterDataCleanup`. Provider theories were written against real semantics but only SQLite ran on this machine.

## Behaviour changes the integrator must know

- **Loot roll change** (`LootGenerator`): reference rows are no longer group members; a reference row's `groupid` selects one group of the referenced template; `maxcount 0` is zero repeats. This changes
  creature, chest and item drop distributions wherever a template mixes reference rows with groups.
- **Wire loot type**: skinning windows are now type 2 (was 6), fishing holes/fail 3.
- **Skinning**: the corpse is skinnable from death (not when looted out); the cast answers `TARGET_NOT_LOOTED` until the corpse is looted out and inside the tapper's 5 s head start.
  `GatheringWorldTests.Skinning...` and `LootServiceTests.Skinning...` were updated for that.
- **Equipped item check**: `EquippedItemCastCheck` is registered by `SpecialLootFeature` for every spell with `EquippedItemClass` (weapon/armor), passive spells excepted. If the class or item-mechanics lanes register
  an Equipment-phase check of their own the two agree; drop one.
- **`CMSG_OPEN_ITEM`** opens lockboxes and clams when `SpecialLootFeature` is attached (it used to refuse them all).

## Shared files touched (narrow, additive)

`LootService.cs` (partial, hooks in Release/TakeItem/TakeMoney/Prune/OnCreatureKilled/CMSG_OPEN_ITEM delegation; the group-loot lane also edits this file), `LootModel.cs`, `LootPackets.cs`, `GameObject.cs`
(partial), `GameObjectMapSystem.cs` (use-handler seam, `FindTemplate`, `FindLock`; the gameobject-types lane may add a similar seam: keep one), `SpellDefines.cs` (+1 enum value), `SpellSystem.Targeting.cs` (+1 case),
`Item.cs` (`Loot`, `ToData`, `Load`; the item-mechanics lane also edits it), `CreatureMapSystem.cs` / `.Lifecycle.cs` (skinning timer), `GatheringSpells.cs`, `ItemPersistence.cs`, `ContentTableSpecs.cs`,
`ContentImporterCli.cs`, `GameObjectLootDumpImporter.cs`, `EfGameObjectLootStores.cs`, `IntegratedSchemaTests.cs`, `TableSpecDriftTests.cs`. New feature class: `SpecialLootFeature` (does not touch `GameObjectLootFeature`).

## Configuration

`SpecialLoot:Fishing:FailLoot` (false), `FailGain` (false), `FailPossibleFishingPool` (true), `SpecialLoot:Items:ConsumeWholeStack` (true). All defaults are the vmangos/retail behaviour.

## Open questions for the developer

1. Stacked lootable items (6644, 6646, 16783): vmangos destroys the whole stack when the loot is taken; is that retail? (`ConsumeWholeStack`.)
2. The `SMSG_LOOT_RESPONSE` error layout needs one real-client capture (vmangos vs wow_messages differ) before the error replies are built.
3. Who owns the `MOD_SKILL` aura and the temporary-enchant effect (fishing pole bonuses, lures)?
4. Chest gold (`gameobject_template` mingold/maxgold, 39 objects) and vein multi-use: confirm the gameobject-types lane owns them.
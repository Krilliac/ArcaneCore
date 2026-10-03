# Area: Fishing and special loot

Lane `claude/vw4-fishing-special-loot`, base `claude/vw-integration` (41babaf). The cross-area contract (schema numbers, shared-file edits,
configuration, behaviour changes the integrator must know) is in [docs/integration/fishing-special-loot.md](../integration/fishing-special-loot.md).

Standing directive: as close to vanilla 1.12.1 as possible, taken from the references, every deviation behind a configuration option that defaults to
retail or listed under **Limits**. References, in order of authority: **vmangos** (`D:\refs\vmangos`, the 1.12.1 branch of its `#if SUPPORTED_CLIENT_BUILD`
blocks), **mangos-classic**, **gtker wow_messages**, **classic-db** (read only, ids and numbers as fixtures, nothing copied). Each piece of code cites
file:line. Where vmangos and the other references disagree the choice is stated.

## Delivered

**Reference/group roll and money** (`Game/Loot/LootGenerator.cs`, `LootMoneyRules.cs`)
- Reference rows are never group members: only `groupid > 0 && mincountOrRef > 0` joins a group; a reference row rolls by its own chance and its
  `groupid` selects only that group of the referenced template; `maxcount 0` processes it zero times (vmangos `LootMgr.cpp:1193-1244`). Groups are built by
  group id, not by row order. This **changes creature, chest and item drop distributions** wherever a template mixes reference rows with groups (the lane's design scan of classic-db z2815
  counted 82 reference rows pointing at a template with other groups or ungrouped entries and 71 entries with several reference rows in one group; those counts were not
  re-run in this session; see the integration doc).
- `LootMoneyRules.Generate` ports `Loot::GenerateMoneyLoot` (`LootMgr.cpp:735-746`): max<=min pays max, a range under 32700 is uniform, a larger one is rolled
  on `>> 8` values and shifted back (boss purses are multiples of 256); the money rate truncates before the shift. Creature gold uses it.

**Special loot data** (`Kernel/WorldData/Loot`, `Data/World/SpecialLoot`, `GameObjectLootDumpImporter`, `ContentTableSpecs`)
- `LootTableKind` gains `Fishing`, `Pickpocketing`, `Disenchant`; `LootContent` carries the signed `skill_fishing_base_level` per area (0 reads as "no row", like
  vmangos) and the creature pickpocket loot id. World schema `SpecialLootDataModule` (version **15**, five new tables, no ALTER). The importer reads both dialects
  (classic-db `PickpocketLootId`, vmangos `pickpocket_loot_id` with the patch filter). Prospecting and milling tables are not read (guard test).

**Loot core seams** (`Game/Loot/LootService.Special.cs`, `LootModel.cs`, `LootPackets.cs`, `SpecialLootSeams.cs`)
- Wire loot type follows `Player::SendLoot` (`Player.cpp:7980-7995`): skinning and insignia are sent as 2, fishing hole / fail as 3 (`LootTypes.ToWire`).
- `LootService` is partial; `ShowSpecial`, `RemoveSpecial`, `LootBag.ReleaseHandler` / `IgnoreDistance` / `ShareMoney` / `SourceCheck` / `Changed` let a source that is
  none of corpse, chest or item open, validate and settle its own window. `Generate` can roll fishing entry 0 (the failed-cast junk table).

**Fishing** (`Game/Fishing`, `World/GameObjects/SpecialLootFeature.cs`)
- Spell side (`Spell::EffectTransmitted`, `SpellEffects.cpp:5648-5790`): target 39 (`TARGET_LOCATION_CASTER_FISHING_SPOT`, `Spell.cpp:2859`) selects the caster; the
  `TRANS_DOOR` effect finds the landing point (explicit destination, else effect radius ahead of the caster, else a random point in the spell range, each with the
  `GetLosHitPosition` pull-back), probes the water twice (`TerrainInfo::IsSwimmable`, `GridMap.cpp:1054-1070`, ported literally) and refuses with `NOT_FISHABLE` when
  `|depth_level| < 1` or the water is out of sight, else summons the bobber at the water level owned by the caster (`SetOwner` also fills `OBJECT_FIELD_CREATED_BY`),
  makes it the channel object and schedules the bite: ready at `duration - lastSec`, `lastSec` one of 3/7/13/17, five seconds to click (`GameObject.cpp:359-379`,
  `GameObjectDefines.h:197`), then `SMSG_FISH_NOT_HOOKED`, channel end and removal (`GameObject.cpp:411-424`). The splash sends sound 3355 and the custom animation.
  The bobber goes when the channel ends (cancel, move, recast, timeout). `SpellSystem.FinishChannel` ends a channel without an interrupt.
- Click side (`GameObject::Use`, `GameObject.cpp:1635-1731`; `GameObjectMapSystem.RegisterUseHandler`): owner only; a ready bobber rolls `skill >= zoneSkill &&
  skill - zoneSkill + 5 >= irand(1,100)` (zone skill from the sub-zone row, else the zone's, signed), searches a hole (20.5 yd and the hole's own radius, nearest),
  raises the skill on success (`FailGain` also on failure), opens personal loot as type 3 exempt from the loot distance: the hole's loot when one is found, else the
  sub-zone table when that template **exists** (not when a roll came up empty), else the zone's; the hidden Wetlands lake (sub-zone 11 within 100 yd of the
  player) gives nothing; a failure is `SMSG_FISH_ESCAPED` or, with `FailLoot`, the junk table. A bobber clicked before the bite answers `SMSG_FISH_NOT_HOOKED`. The channel
  ends after every branch. Closing the window removes the bobber; a fully looted hole counts a use and despawns at `urand(data2, data3)` uses (`LootHandler.cpp:489-495`).
- `EquippedItemCastCheck` (`Game/Spells/Checks`, Equipment phase, `Spell::CheckItems` `Spell.cpp:7208-7236` over `Player::HasItemFitToSpellReqirements`
  `Player.cpp:19753-19797`): the fishing spell needs a fishing pole; the rule is general (every spell with `EquippedItemClass`), skips passive spells
  (`Spell.cpp:5696`, the proficiency spells carry the class they grant) and ignores the other hand's weapon for main-hand and ranged attack types.
- Config `SpecialLoot:Fishing:FailLoot=false`, `FailGain=false`, `FailPossibleFishingPool=true` (vmangos `World.cpp:718-720`; mind the inverted name: only `false`
  turns a failed roll near a hole into a catch).

**Pick Pocket** (`Game/Loot/PickpocketLoot.cs`, `PickpocketSpells.cs`)
- Effect 71 check (`Spell.cpp:6062-6071`: `BAD_TARGETS` for a non-creature or a player's creature, `TARGET_NO_POCKETS` without a pocket loot id) and effect
  (`SpellEffects.cpp:2651-2661`). First pick: the `pickpocketing_loot_template` plus `10 * (urand(0, mobLevel/2) + urand(0, playerLevel/2)) * MoneyRate`
  (`Player.cpp:7833-7872`); a later pick by a non-original looter or after the loot was cleared shows only the quest items that player needs, no money; the original
  looters reopen the leftovers. Owner permission, wire type 2, money not shared (`LootHandler.cpp:274-290`), distance checked while the creature lives, state dropped
  when it dies (`Creature.cpp:1637`). Combat does not stop a pick (the in-combat refusal is compiled out for build 5875, `Spell.cpp:5466-5470`).

**Disenchant** (`Game/Loot/DisenchantLoot.cs`, `DisenchantSpells.cs`)
- Check (`Spell.cpp:7376-7392`: own item, no open window, `DisenchantID`, not `NO_DISENCHANT 0x8000`), effect (`SpellEffects.cpp:5059-5073`: bind, craft skill-up of the
  casting spell, `disenchant_loot_template` as temporary loot, wire type 4), release (`LootHandler.cpp:543-553`: everything left is auto-stored, lost without room, then the item is
  destroyed). The quality/class gate is a template-load rule in vmangos (`ObjectMgr.cpp:4183-4195`) and is not repeated per cast.

**Skinning lifecycle** (`LootService`, `Creature.Skinning.cs`, `GatheringSpells.CheckSkinning`)
- Skinnable from death when the creature has a skinning template and a loot recipient (`Creature.cpp:2274-2277`; it used to be set only when looted out). The cast follows
  `Spell.cpp:5940-5969`: `TARGET_UNSKINNABLE`, `TARGET_NOT_LOOTED` for a non-tapper inside the 5 s head start (`skinningForOthersTimer`: counts down on the corpse, restarts when
  it is looted out), `LOW_CASTLEVEL`, `TARGET_NOT_LOOTED` unless a critter or a looted-out unskinned corpse, then the orange roll. Skin loot is flagged lootable so
  partly taken loot can be reopened (`Player.cpp:7914-7928`); a skinned and emptied corpse decays at once, an unskinned looted-out one at the corpse decay rate
  (`Creature.cpp:3355-3395`).

**Container items** (`Game/Loot/ItemLootSource.cs`, `Kernel/Items/ItemLootData.cs`, `Data/Characters/Items`)
- `CMSG_OPEN_ITEM` (`SpellHandler.cpp:142-226`): the lock rule (`ITEM_DYNFLAG_UNLOCKED`; only a lock with a skill requirement or a lock id with no Lock.dbc row refuses with
  `ITEM_LOCKED`, key locks open), spell interrupt, `item_loot_template` plus the template's money range rolled once (`Player.cpp:7716-7756`) and kept on the item
  (`Item.Loot`); a reopen shows what is left with stable slots; owner-only window, no money share; a looted-out item is destroyed (`LootHandler.cpp:556-563`).
  Pick Lock (`OPEN_LOCK_ITEM`) now pays out: the box was marked unlocked and then refused before.
- Persistence: Characters schema `ItemLootDataModule` (version **16**): `item_loot_state` (the `generated_loot` flag and the money) and `item_loot` (key item + slot, so two rolls of the
  same item both survive; vmangos' key is item + item id), no ALTER of `item_instance`. Staged by `ItemPersistence.StageReplaceAsync` in the **same SaveChanges** as the inventory
  rows, loaded with them, removed with the item and the character (`ICharacterDataCleanup`).
- Config `SpecialLoot:Items:ConsumeWholeStack=true` (vmangos destroys the whole stack, three classic-db lootable items stack; retail unverified).

**Prospecting and milling** are not vanilla and are absent; `NoProspectingOrMillingTests` pins that.

## Limits (documented, nothing stubbed)

- **Loot error replies** (`SMSG_LOOT_RESPONSE` with an error byte for not standing, stunned, too far, ...; `LootHandler.cpp:341-381`) are not done: vmangos writes `guid, 0, error`,
  wow_messages writes gold and a count after the error, and no real client was available to settle it. Refusals still answer `SMSG_LOOT_RELEASE_RESPONSE`; CMSG_LOOT does not
  yet interrupt a cast or check standing/stun.
- **Real-client verification**: the loot window types (2, 3, 4), the fishing visuals (bobber state, splash, custom animation), the NOT_FISHABLE guard against real ADT water and
  the item window were exercised over the loopback client only.
- **Terrain**: water comes from the map's ADT liquid layer (`MapFishingTerrain`); fishing from WMO water is unsupported, and `|depth_level| < 1` is ported literally (shallow water that
  is not deep enough still takes the bobber, as in vmangos).
- **Tap list**: combat keeps none; the corpse loot's recipients stand in for `IsTappedBy` (skinning head start). Skin loot reopen is the skinner's only.
- **Fishing**: no pole skill bonus (`MOD_SKILL` aura) and no lures (temporary enchant): neither effect exists on the base; a hole in use by another fisher falls back to the
  zone loot (vmangos shows its shared loot); a hole with leftover loot stays activated like in vmangos.
- **Pick Pocket**: a missed cast does not break stealth or start combat (`Spell.cpp:1225-1243`; needs the stealth and threat primitives); the player-owned creature test
  (`PickpocketLoot.IsPlayerOwned`) is a seam for the pets lane; `LOOT_ERROR_ALREADY_PICKPOCKETED` is never sent (vmangos neither).
- **Container items**: no wrapped gift opening, no taxi refusal, no immediate save at generation (a crash before the next character save can reroll, as in vmangos), and none of
  the interlocks that stop splitting, moving, trading, selling or merging an item that holds generated loot (`Item.cpp:947-952,1156-1158,1198-1205`; belongs to the item mechanics lane).
  Persisted loot of items in mail or auction transit is kept by item guid but not read by the economy stores.
- **Chest and object gold** (`gameobject_template` mingold/maxgold) needs fields on the template (gameobject-types lane); vein multi-use and the 5-minute respawn of partly looted chests are the same
  release code as fishing holes and were left to that lane.
- **Equipment check**: item classes other than weapon and armor pass (vmangos fails them with a logged error; synthetic test rows leave the field at 0 where DBC data has -1).

## Provenance and tests

References used: `D:\refs\vmangos` (LootMgr.cpp, Player.cpp, GameObject.cpp, SpellEffects.cpp, Spell.cpp, LootHandler.cpp, SpellHandler.cpp, Creature.cpp/h, GridMap.cpp, World.cpp), `D:\refs\mangos-classic`
(`mangos.sql`, `SpellEffects.cpp:185`), `D:\refs\wow_messages` (SMSG_FISH_*, SMSG_PLAY_OBJECT_SOUND, SMSG_LOOT_RESPONSE), `D:\refs\classic-db` (data shapes). Tests: Game.Tests (rules and flows with real-data-shaped
fixtures), World.Tests (loopback: fishing, Pick Pocket, Disenchant, Pick Lock, Skinning), Data.Tests (provider theories for the new world and characters modules; SQLite only on a machine without
MariaDB/PostgreSQL, the hosted CI runs the rest), plus the guard tests for prospecting and milling.
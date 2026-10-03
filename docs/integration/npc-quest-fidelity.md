# NPC services and quest fidelity (lane `npc-services-quests`)

Standing directive: vanilla 1.12.1 behaviour, mechanics and data, verified against D:\refs
(`vmangos` primary, `mangos-classic` = cmangos, `wow_messages`, `classic-db`). Nothing here copies
reference code or data; every rule cites `file:line`. Any deliberate deviation is behind a config
switch that defaults to retail and is listed in "Deviations" below.

This file is the lane's delivered-scope record. The integrator owns `seams.md` and the handoff
document; schema numbers, if any slice allocates one, are listed under "Schema".

## Slice NQ2: condition evaluator

### Delivered

* `ArcaneCore.Game.Conditions.ConditionEvaluator` implements the existing seam
  `IConditionEvaluator.IsSatisfied(conditionId, player, npc)`
  (`src/ArcaneCore.Game/Npc/NpcServiceContracts.cs`). Before this slice the seam had no
  implementer, so every conditioned gossip option, gossip text, vendor row and quest
  `RequiredCondition` evaluated false (`QuestNpcServices.Gossip.cs`, `.QuestMenu.cs`, `.Vendor.cs`).
* `ConditionTable` loads `conditions` rows (`ConditionRecord`, `Kernel/Npc/ConditionRecord.cs`) with the
  validation cmangos applies at load: rows are checked in ascending id order and an invalid row is
  erased (`mangos-classic/src/game/Globals/ObjectMgr.cpp:5526-5543`, `Conditions.cpp:600-1000`).
  AND/OR/NOT operands must be lower, still-valid ids; argument ranges (level 1..60, modes, rank <8,
  team 469/67, race/class masks, skill 1..300, gender <3, flags 0..3) are enforced.
* `ConditionFeature` (World) is discovered as an `IWorldFeature`, binds `Conditions:*`, loads the table
  from the optional `IConditionContentStore`, builds the evaluator context from the live world
  (inventory incl. bank, spellbook, auras, reputation, terrain areas and `AreaTemplate.Flags`, the quest
  service) and is handed to the NPC services by `NpcServicesFeature.Extend`. It forwards to the current
  inner evaluator, so a services rebuild never holds a stale one.
* `QuestNpcServices` implements `IConditionQuests` (`QuestNpcServices.Conditions.cs`):
  `IsCurrentQuest` modes 0/1/2, `GetQuestRewardStatus` (repeatable quests are never "rewarded"),
  `CanTakeQuest`.

### Numbering: cmangos, not vmangos

classic-db is written in the cmangos numbering (`mangos-classic/src/game/Globals/Conditions.h:30-82`).
vmangos `Conditions.h` reuses 11, 13, 27, 28, 31, 35 and 40 for other meanings (11 SAVED_VARIABLE,
13 CANT_PATH_TO_VICTIM, 27 GENDER, 35 MAP_EVENT_DATA, 40 HAS_PET), so evaluating classic-db rows with
vmangos numbering would silently misread gender (35) and area-flag (13) rows. A test pins every id.

Semantics follow `Conditions.cpp:109-514` (`Meets`, `Evaluate`, `CheckParamRequirements`,
`ConditionTargets` at 54-106): AND/OR take two ids plus optional value3/value4; flag 0x1 reverses; flag
0x2 swaps source and target first; a type whose parameter requirement is then unmet is false and is
not reversed (115-122); a missing condition id is false (`IsConditionSatisfied`, 1023-1028).

### Types

| Evaluated | Types (cmangos id) |
|---|---|
| Always | NOT -3, OR -2, AND -1, NONE 0, ITEM_EQUIPPED 3, TEAM 6, RACE_CLASS 14, LEVEL 15, GENDER 35 |
| Needs a collaborator that the daemon supplies | AURA 1, ITEM 2, ITEM_WITH_BANK 23, AREAID 4, AREA_FLAG 13, REPUTATION_RANK_MIN 5 / MAX 30, QUESTREWARDED 8, QUESTTAKEN 9, QUESTAVAILABLE 19, QUEST_NONE 22, SPELL 17, ACTIVE_GAME_EVENT 12, ACTIVE_HOLIDAY 26, AD_COMMISSION_AURA 10 |
| Collaborator missing in the daemon: fail closed | SKILL 7 and SKILL_BELOW 29 (there is no skills owner, see `npc-services.md`), PVP_RANK 11 (no honor system) |
| Not decidable from an NPC interaction: fail closed | INSTANCE_SCRIPT 18, LEARNABLE_ABILITY 28, COMPLETED_ENCOUNTER 31, LAST_WAYPOINT 33, DEAD_OR_AWAY 36, CREATURE_IN_RANGE 37, PVP_SCRIPT 38, SPAWN_COUNT 39, WORLD_SCRIPT 40, WORLDSTATE 42, IS_IN_COMBAT 43 |

Counts of classic-db rows by type (sizing only, from the z2815 dump's `conditions` INSERT; never a
test constant): 1x33, 2x45, 4x11, 5x26, 6x4, 7x57, 8x150, 9x96, 10x1, 11x3, 12x24, 13x1, 14x35,
15x30, 17x33, 18x7, 19x2, 22x63, 23x47, 26x2, 29x2, 30x3, 31x2, 33x11, 35x2, 36x2, 37x6, 39x2, 40x37,
42x16, AND 269, OR 66, NOT 45.

### Fail closed means unknown, not false

An undecidable leaf yields *unknown* and unknown propagates with three-valued logic (NOT unknown is
unknown, AND with a false is false, OR with a true is true, the reverse flag keeps unknown). The final
answer is "not satisfied". It is never reversed into a pass: `NOT(skill condition)` must not show an
option merely because there is no skills system. Each undecidable evaluation is counted in
`ConditionEvaluator.Unavailable` and `ConditionEvaluator.Summarize()` reports how many table rows are
decidable and which leaf types are dead; `ConditionFeature` logs it at startup.

### Limits (documented, not hidden)

* No `IConditionContentStore` implementation exists yet. Importing the `conditions` table is slice NQ0
  (not delivered in this lane run); until then the daemon runs with an empty table, which is what a
  missing row means in cmangos.
* Existence validation that needs other content (the item, spell, quest, faction, skill or game event
  must exist, `IsValid` in `Conditions.cpp`) is not applied. A reference to missing content evaluates
  as that content's absence (an unknown faction is false, like cmangos).
* A creature as the *target* (only via the swap flag) has no race, class, level, gender or auras in the
  `NpcInfo` contract, so those types are unknown there.
* Corpse targets (`..._OR_CORPSE` requirements, PVP_RANK on a corpse) do not exist.
* `Conditions:ActiveGameEvents` and `Conditions:ActiveHolidays` default to empty: retail game events are
  date driven and there is no scheduler. This is a placeholder, not retail behaviour.
* Loot conditions are not wired (`LootService.Conditions` belongs to the durable-loot lane).

### Config

| Key | Default | Meaning |
|---|---|---|
| `Conditions:ActiveGameEvents` | `[]` | event ids reported active to type 12 |
| `Conditions:ActiveHolidays` | `[]` | holiday ids reported active to type 26 |

### Tests

`tests/ArcaneCore.Game.Tests/Conditions/*` (truth tables, table validation, gossip/text/vendor through
the real services, quest facts through the real quest log) and
`tests/ArcaneCore.World.Tests/Npc/ConditionFeatureTests.cs` (daemon wiring, config, rebuild order).
Needs the real client: the Llane Beshere (Northshire) class-variant gossip once NQ0 imports the table.

## Slice NQ11a: CMSG_BUY_ITEM_IN_SLOT

### Delivered

* The handler used to read the bag and slot and drop them ("the item goes wherever it fits"). It now
  follows vmangos `WorldSession::HandleBuyItemInSlotOpcode` (`D:\refs\vmangos\src\game\Handlers\ItemHandler.cpp:659-686`):
  the bag GUID resolves to the backpack side (the player's own GUID, bag byte 255) or to the worn bag
  in slots 19-22 with that GUID; an unknown bag is ignored with no reply, as a cheat.
* The placement half of `Player::BuyItemFromVendor` (`Player.cpp:18453-18496`) is
  `PlayerInventory.CheckAddItemAt` / `AddItemAt`: an inventory position (or none) goes through the
  existing `CanStoreItem(bag, slot, ...)` port (specific slot, then specific bag); an equipment position
  needs a count of one (`ITEM_CANT_BE_EQUIPPED` otherwise), `CanEquipItem(slot, ...)` and equips the new
  item (`EquipNewItem` + `AutoUnequipOffhandIfNeed`); every other position is `ITEM_DOESNT_GO_TO_SLOT`.
  Nothing changes on a refusal, and money is only taken after placement succeeds, so a refused
  placement never charges.
* The vendor checks that run before the position (vendor list and visibility, stock, reputation, honor
  rank, money) are unchanged and still run first, as in `BuyItemFromVendor`.
* `IItemService` gained three default members (`FindBagSlot`, `CanStoreNewItemAt`, `StoreNewItemAt`);
  the defaults keep the old position-less behaviour for an owner that has not implemented them, and
  `InventoryItemService` implements them.
* `NpcServiceHandlers.ReadBuyItemInSlot` is the exact 22-byte parser (wow_messages
  `cmsg_buy_item_in_slot`, versions "1 2": u64 vendor, u32 item, u64 bag, u8 bag slot, u8 amount).

### Limits

* The error packet carries the item id only for `CANT_EQUIP_LEVEL_I`, so the position errors are
  byte-identical to vmangos's `SendEquipError(msg, nullptr, nullptr)` form.
* A bank bag GUID is not a valid bag here (vmangos loops 19-22 only); a bank position is
  `ITEM_DOESNT_GO_TO_SLOT`.
* BUY_BANK_SLOT result packets, AUTOBANK / AUTOSTORE_BANK handlers and the price-rounding switch
  (rest of design slice NQ11) are not part of this slice.
* Needs the real client: dragging a vendor item to a bag slot / worn slot.

# NPC services and quest fidelity (lane `npc-services-quests`)

Standing directive: vanilla 1.12.1 behaviour, mechanics and data, verified against D:\refs
(`vmangos` primary, `mangos-classic` = cmangos, `wow_messages`, `classic-db`). Nothing here copies
reference code or data; every rule cites `file:line`. A deliberate deviation is listed in "Deviations" at the end;
each says whether it is behind a config switch (defaulting to retail) or is a stated limit with no switch.

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

## Slice NQ3: quest refusals, source items, abandon, cancel, swap, chain offer

### Delivered

* **Refusal packets.** `CanTakeQuest` is now `RefuseTakeQuest`, which returns the first failing check in
  vmangos's order (`Player.cpp:12565-12577`): status (ALREADY_ON 13), exclusive group, class, race
  (WRONG_RACE 6), level, skill, condition, reputation, previous quest, timed (ONLY_ONE_TIMED 12), next chain,
  previous chain, breadcrumb, dependent breadcrumbs, `IsActive`. MaxLevel and `IsActive` refuse without a
  message. Accepting a refused quest sends `SMSG_QUESTGIVER_QUEST_INVALID` (u32 reason; gtker/wow_messages
  `smsg_questgiver_quest_invalid.wowm`, vmangos `SendCanTakeQuestResponse` `Player.cpp:14400-14405`) and then
  closes the gossip, as `HandleQuestgiverAcceptQuestOpcode` does (`QuestHandler.cpp:108-196`). Level, class,
  quest-line, exclusive-group, skill, condition, reputation and breadcrumb failures all answer
  DONT_HAVE_REQ (0), exactly as vmangos does (not LOW_LEVEL).
* **Source items** (`CanGiveQuestSourceItemIfNeed`, `Player.cpp:13643-13676`): what the player already owns,
  bank included, counts towards `SrcItemCount`; only the missing part must fit and is given. A full bag sends
  `SMSG_QUESTGIVER_QUEST_FAILED` (u32 quest, u32 reason 4), a unique item already carried reason 17, anything
  else the item's `SMSG_INVENTORY_CHANGE_FAILURE`. `SrcItemCount` 0 counts as 1 (`ObjectMgr.cpp:5769-5773`).
* **Abandon** (`RemoveQuestAtSlot`, `Player.cpp:13033-13066`): the source item is taken back first
  (`TakeOrReplaceQuestStartItems`, `13696-13759`): left alone when it is the quest's own start item, refused
  with the equip error when a worn copy cannot come off (`CanUnequipItems`, `8326-8397`, new
  `PlayerInventory.CanUnequipItems`), otherwise `SrcItemCount` is destroyed when owned (bank included, no
  "also required" exception) and replaced by the quest's starting item
  (`ObjectMgr::GetQuestStartingItemID`, `ObjectMgr.cpp:4224-4225, 6248-6256`; new
  `IItemTemplateStore.QuestStartingItem`). Then every required item with `BIND_QUEST_ITEM` (4) or
  `BIND_QUEST_ITEM1` (5) is destroyed, bank included (1.12.1 behaviour, `Player.cpp:13046-13062`).
  The old rule ("keep the source item when the quest also requires it") was not in vmangos and is gone.
* **`CMSG_QUESTGIVER_CANCEL`** (no body) closes the gossip; **`CMSG_QUESTLOG_SWAP_QUEST`** (u8, u8) swaps the
  three slot fields, ignoring equal or out-of-range slots (`QuestHandler.cpp:314-325`).
* **Next quest in chain**: after a turn-in, when the finished quest has `NextQuestInChain` and the same
  creature starts it, `SMSG_QUESTGIVER_QUEST_DETAILS` follows `SMSG_QUESTGIVER_QUEST_COMPLETE`
  (`QuestHandler.cpp:275-277`, `Player::GetNextQuest` `Player.cpp:12514-12541`; `OfferNextQuest`). vmangos does
  not check `CanTakeQuest` for the offer; accepting is where a refusal is reported.

### Limits

* The quest list greeting and emote from `questgiver_greeting` (design NQ3) need the content import (NQ0);
  `SMSG_QUESTGIVER_QUEST_LIST` still carries an empty greeting.
* The unsupported-objective gate (`AcceptableQuest`: source spells, party accept, auto-rewarded, stay-alive,
  reqsource items, exploration without a trigger row) still refuses silently at accept. That is a temporary
  fail-closed deviation from retail, not a switch yet; NQ5 removes the cases it implements. Quests refused
  that way never send the invalid packet.
* `GameObject` quest givers are not covered (slice NQ7).

## Slice NQ7: quest-giving game objects (GAMEOBJECT_TYPE_QUESTGIVER)

### Delivered

* **Relations.** `QuestContent` carries `GameObjectStarters` / `GameObjectEnders` (init-only, default empty, so
  existing callers compile); `EfQuestContentStore` reads them from `gameobject_questrelation` /
  `gameobject_involvedrelation` (the game object world module's tables); `QuestStore.GameObjectStartersOf` /
  `GameObjectEndersOf` are separate maps from the creature ones (vmangos `GetGOQuestRelationsMapBounds` /
  `GetCreatureQuestRelationsMapBounds`, `Player.cpp:12349-12418`). A creature and an object with the same entry
  number never share relations.
* **Source.** `NpcInfo.IsGameObject` marks a game object source. `CreatureQuestLookup` resolves
  `HighGuid.GameObject` GUIDs the player can see, spawned in its map and not `NoInteract`
  (`CanInteractWithGameObject`, `Player.cpp:2540-2565`); the interaction distance is the object's centre within
  the object type's own interaction distance, 5.55556 for quest givers, compared with `<=` (`GameObjectInfo::GetInteractionDistance`, `GameObjectDefines.h:759-785`; `GameObject::IsAtInteractDistance`, `GameObject.cpp:2584-2609`), shared with `GameObjectMapSystem.InteractionDistanceFor`. A dead or taxi-flying player is refused (`Player.cpp:2540-2565`). `NpcFlags` carries `QuestGiver` only for
  type-2 objects.
* **Use.** `GameObjectQuestGiverFeature` fills the existing `GameObjectMapSystem.QuestGiver` seam on every map, so
  `CMSG_GAMEOBJ_USE` runs `QuestNpcServices.OpenGameObjectQuestMenu`: `PrepareGossipMenu(go, questgiver.gossipID)`
  (data3, `GameObjectDefines.h:245-258`) and `SendPreparedGossip` (`GameObject.cpp:1457-1471`, `Player.cpp:12134-12177`).
  A menu-less object with quests opens the quest list or the single quest's window; an object with nothing to say
  stays silent ("Gameobjects should not greet players"). Only plain gossip lines show for a game object
  (`Player.cpp:12067-12086`); any other option id is ignored on select (`Player.cpp:12185-12192`); the object never
  uses `npc_gossip` (creature spawn ids).
* **The rest of the quest flow is the creature flow** over that source: status query (`QuestHandler.cpp:34-70`),
  query/accept/complete/request-reward/choose-reward through `InteractableNpc` + the source-aware
  `StartersOf` / `EndersOf`, the next-quest-in-chain offer, refusal packets.
* `CMSG_GOSSIP_HELLO` / `CMSG_QUESTGIVER_HELLO` stay creature-only (`GetNPCIfCanInteractWith`).

### Limits

* Game object quest-giver status icons in the object's own update fields (dynamic flags / sparkle,
  `UpdateForQuestWorldObjects`) are not sent; the client asks `CMSG_QUESTGIVER_STATUS_QUERY` (answered) but the
  sparkle is the game object area's.
* The display-bounds oriented box test of `IsAtInteractDistance` (`GameObjectDisplayInfoAddon.HasBounds`, padded
  by the interaction distance and rotated by the object) is not implemented: model bounds are not loaded, so only the
  no-bounds centre-distance branch runs. Large models with bounds can be reachable from further than the radius.
* Objects with a lock (`questgiver.lockId`) go through `GameObjectMapSystem`'s existing checks only.
* Needs the real client: clicking the Wanted poster (GO 68 -> quest 176) and Rolf's corpse (GO 56 ends 45 and
  starts 71) once the content import (NQ0) supplies the relations.

## Slice NQ0a: the `conditions` table content (narrowed NQ0)

The design's NQ0 imports eleven new tables. Only the table with a finished consumer in this lane, `conditions`
(the NQ2 evaluator), is delivered here; the others have no consumer wiring yet and stay open (see "Not delivered").

### Delivered

* **Schema.** `ConditionsWorldModule` (`src/ArcaneCore.Data/Npc/`) creates `conditions` at
  `ConditionsWorldModule.Version` (**World 11**, the next free number after 9 index repair and 10 quest reputation
  columns; one constant, tests use the constant or `WorldDbContext.Schema.CurrentVersion`, never a literal; the
  integrator renumbers). The allocation table of `IntegratedSchemaTests` lists it. No cleanup registration: the world
  schema has no per-character rows.
* **Columns** are the classic-db layout: `condition_entry` (key), `type` (signed), `value1..value4`, `flags`
  (the `comments` column is not kept).
* **Importer.** `ConditionsDumpImporter` reads the cmangos/classic-db `conditions` INSERTs by column name through the
  existing `MySqlDumpReader`, keeps later rows over earlier ones (counted as `Replaced`), reads the `db_version`
  row text into the report so an un-updated dump is visible, rejects a bad value (non-numeric, negative unsigned,
  flags above 255) instead of writing a zero, and **refuses the vmangos layout** (no `value3/value4` columns) because
  vmangos's type numbering differs from the cmangos numbering the evaluator implements. `WriteAsync(db, replace)` is
  atomic with the same transaction contract as the other importers (caller transaction protected by a savepoint, a
  failure restores the previous rows).
* **Store.** `EfConditionContentStore : IConditionContentStore` is registered by the module, so `ConditionFeature`
  now loads real rows when the table is populated (until then the table is empty, as before).
* **Real data, optional.** `ConditionsRealDumpTests` reads the dump named by `ARCANECORE_CLASSICDB_SQL` (`.sql` or
  `.sql.gz`; never copied into the repository) and prints sizing; without the variable it is skipped with an explicit
  message ("did NOT run"). Against the classic-db z2815 dump used while developing this slice
  (`D:\refs\classic-db\Full_DB\ClassicDB_1_12_1_z2815.sql.gz`) the importer read 1133 `conditions` rows, all of them
  passed the cmangos validation (`ConditionTable`), and every type used is a known cmangos type. These counts are sizing,
  not test constants.

### Not delivered (still open from design NQ0/NQ1)

`npc_vendor_template`, `npc_trainer_template`, `areatrigger_involvedrelation`, `questgiver_greeting`,
`trainer_greeting`, `spell_chain`, `game_graveyard_zone`, `world_safe_locs`, `taxi_shortcuts`, `creature_template_npc`,
the quest_template column mapping report (`RewMail*`, scripts, emote delays are not model columns), the item_template
importer (NQ0b) and the loading of any of them into the runtime stores. `UPDATE` statements in a dump are skipped by the
shared reader without a count, so un-applied classic-db `Updates` are visible only through `db_version`.

## Slice NQ4a: quest experience for classic-db data (float32)

### Delivered

* `QuestExperienceRules` (`src/ArcaneCore.Game/Quests/`) computes quest XP in single precision exactly as the references:
  the RewXP column (vmangos `QuestDef.cpp:180-202`) or, for data without that column, cmangos's derivation from
  `RewMoneyMaxLevel` (`mangos-classic/src/game/Quests/QuestDef.cpp:171-206`): `/ 0.6` for quest levels 1..60 and
  `/ 1.2, 2.4, 3.6, 4.8, 6.0` for 61..65 and above, each step `ceil` of a float32 product (0.8, 0.6, 0.4, 0.2, 0.1 above
  quest level + 5). A double implementation differs for a few hundred combinations (for example RewMoneyMaxLevel 7,
  quest level 1, player level 8: float32 7, double 8); a test pins those. Both references copy the signed quest level
  into a `uint32`, so a level of -1 behaves as 4294967295 (wrap-around of `+ 5` included); this is reproduced, with a test.
* **`Quests:XpSource`** (`QuestXpSource`): `Auto` (default) picks per dataset, the column when any loaded quest has a
  RewXP value (`QuestStore.HasRewXpColumn`, vmangos data), the derivation otherwise (classic-db data); `RewXpColumn` and
  `Derived` force one. The reward code (`TryRewardExperience`) uses it; `Quest.XpValue` remains the vmangos column value.
* Before this slice a classic-db quest (RewXP always 0) rewarded no XP at all.

### Limits / honesty

* The derivation is cmangos's, the column vmangos's; both approximate retail and neither is proven against a retail
  capture here, so `Auto` is a documented choice, not a retail guarantee. The 783 -> 40/24/8/4 golden values in the design
  are reproduced by the formula but their input (RewMoneyMaxLevel 24) was not re-verified against the dump row.
* `QUEST_FLAGS_NO_MONEY_FROM_XP` (0x100) gating of `RewMoneyMaxLevel` (cmangos `GetRewMoneyMaxLevel`) is NOT applied:
  vmangos names 0x100 `UNK2` (repeatable dialog) and no reference proves the cmangos meaning for retail data. The
  allowlist replacement ("computed reward support"), Method 0 turn-ins and the reward-slot rule of design NQ4 are not
  delivered.

## Slice NQ5a: which quest items a player wants (`HasQuestForItem`)

### Delivered

* `QuestNpcServices.HasQuestForItem(player, itemId, inRaidGroup)` ports vmangos `Player::HasQuestForItem`
  (`Player.cpp:14267-14320`) and `QuestJournalAdapter.NeedsQuestItem` (the loot and game object seam) now calls it instead of
  its own approximation:
  * a required item (`ReqItem`) is wanted while the quest's own **counter** is below the requirement (the old rule
    compared against the bags, so a counter and the bags could disagree);
  * a source item (`ReqSource`) is wanted while the player, **bank included**, holds fewer than the item's `maxcount`
    (unique items), then fewer than `ReqSourceCount` when set, **else fewer than one stack** (`Stackable`), the rule the old
    code lacked;
  * a **raid group** member does not want items of a quest that is not allowed in raids (`Quest::IsAllowedInRaid`,
    `QuestDef.cpp:227-235`: quest type 62, flag `0x40`, or `Quests:IgnoreRaid`); the same rule now gates kill credit.
* **`Quests:IgnoreRaid`** (vmangos `Quests.IgnoreRaid`, default 0, `mangosd.conf.dist.in:1168-1172`) is a config switch
  that defaults to the retail/vmangos behaviour.
* The social feature supplies the raid-group fact in the daemon (the same groups the loot code uses).

### Limits

* Battlegrounds exempt raid hiding in vmangos (`!InBattleGround()`); there is no battleground system, so the exemption is
  not modelled.
* The rest of design NQ5 (quest-slot item counters with 63-batched `SMSG_QUESTUPDATE_ADD_ITEM`, `ReqSource` / `SrcSpell`
  accept support and removing those `AcceptableQuest` refusals) is not delivered; such quests are still refused at accept.

## Deviations

Single list of every place this lane differs from vmangos/retail. "Switch" means a config option defaulting to retail.

* **Conditions type numbering is cmangos only; there is no switch.** The evaluator, `ConditionTable` and
  `ConditionsDumpImporter` implement one numbering (cmangos, `Conditions.h:30-82`) because the classic-db
  dataset is written in it. vmangos numbers 11, 13, 27, 35 and 51 differently, so a vmangos world database's
  `conditions` rows (and `Quest.RequiredCondition` ids that point into them) are not evaluated; the importer
  refuses the vmangos layout (no `value3/value4` columns) instead of misreading it. Choosing a numbering per
  dataset (a `Conditions:Numbering` option) is not implemented; the default is therefore dataset-dependent: classic-db
  works, vmangos data does not.
* **Game object interaction ignores the display-bounds box**: no switch, see "Slice NQ7 / Limits".
* **Honor-rank vendor restrictions fail closed**: pre-existing, no switch (see NQ11a limits).
* **Battleground exemption from raid quest-item hiding** is not modelled (no battleground system).
* `Quests:IgnoreRaid` is a switch, default 0 as in vmangos.

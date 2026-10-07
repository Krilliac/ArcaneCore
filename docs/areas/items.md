# Area: items and inventory

Branch `feat/items`. Integration notes (schema versions, shared-file edits, seams): `docs/integration/items.md`.
For the build-5875 bags, bank and move-error audit, see [inventory-bags.md](inventory-bags.md).

## What is in

| Piece | Where | Reference |
|---|---|---|
| `item_template` (vmangos columns, world v2) and `playercreateinfo_item` | `Data/Content/Items/` | vmangos `world` schema, `ItemPrototype.h`, `ObjectMgr::LoadItemPrototypes` |
| Item instances (`item_instance`, `character_inventory`, characters v3) | `Data/Characters/Items/` | vmangos `characters` schema, `Item::SaveToDB`, `Player::_SaveInventory` |
| Template records, instance records, `IItemStore`, `IItemTemplateSource` | `Kernel/Items/` | |
| Template load checks (stack sizes, containers, bonding, sheath defaults) | `Game/Items/ItemTemplateRules.cs` | vmangos `ObjectMgr::LoadItemPrototypes` |
| `Item` and `Container` objects with ITEM_* / CONTAINER_* update fields | `Game/Items/Item.cs` | vmangos `Item.cpp`, `Bag.cpp`; fields from the codegen'd `UpdateFields` |
| Slot model: equipment 0-18, bags 19-22, backpack 23-38, bank 39-62, bank bags 63-68, buyback 69-80, keyring 81-96 | `Game/Items/InventorySlots.cs` | vmangos `Player.h` (`EquipmentSlots`, `InventorySlots`, `BankItemSlots`...) |
| Store/bank/equip checks in vmangos order | `Game/Items/PlayerInventory.Rules.cs` | vmangos `Player::_CanStoreItem`, `CanStoreItems`, `CanEquipItem`, `CanUnequipItem`, `CanBankItem`, `CanUseItem`, `FindEquipSlot` |
| Store/equip/remove/destroy, item push, starting outfit | `Game/Items/PlayerInventory.Storage.cs` | vmangos `Player::StoreItem`, `EquipItem`, `RemoveItem`, `DestroyItem`, `DestroyItemCount`, `SendNewItem`, `Player::Create` (`playercreateinfo_item`) |
| Swap, auto-equip, auto-store, split, destroy requests | `Game/Items/PlayerInventory.Operations.cs` | vmangos `Player::SwapItem`, `SplitItem`; `ItemHandler.cpp` handlers |
| Opcode handlers: CMSG_USE_ITEM, CMSG_SWAP_ITEM, CMSG_SWAP_INV_ITEM, CMSG_AUTOEQUIP_ITEM, CMSG_AUTOSTORE_BAG_ITEM, CMSG_SPLIT_ITEM, CMSG_DESTROYITEM, CMSG_ITEM_QUERY_SINGLE | `World/Items/ItemHandlers.cs`, `World/Items/ItemUseFeature.cs` | vmangos `SpellHandler.cpp:36-139`, `Player.cpp:7362-7403`; payloads from gtker `world/item/cmsg_use_item.wowm` (1.12) and inventory wowm definitions |
| SMSG_INVENTORY_CHANGE_FAILURE, SMSG_ITEM_PUSH_RESULT, SMSG_ITEM_QUERY_SINGLE_RESPONSE | `Game/Items/ItemPackets.cs` | vmangos `Player::SendEquipError`, `SendNewItem`, `PacketsItem.cpp`; gtker wowm layouts and the 7230 test vector |
| Visible item fields (`PLAYER_VISIBLE_ITEM_n_0`, stride 12) and bank bag count | `PlayerInventory.Storage.cs` | vmangos `Player::SetVisibleItemSlot` |
| Stat application hook | `IItemStatsApplier` / `EquipmentStatsApplier` in `Game/Items/ItemSeams.cs` | vmangos `Player::_ApplyItemMods` / `_ApplyItemBonuses` |
| Character list equipment | `CharacterPackets.BuildCharEnum` + `ICharacterHooks.GetCharEnumEquipmentAsync` | vmangos `Player::BuildEnumData` (20 × display id + inventory type) |
| Login: item creates before the self create, item values to the owner only | `Map.AddPlayer`, `UpdateBlockWriter` | vmangos `Player::BuildCreateUpdateBlockForPlayer`, `GetUpdateFieldFlagsForTarget` |
| Persistence on save/logout and character delete | `ItemPersistence.cs`, `EfCharacterStore` | vmangos `Player::_SaveInventory`, `Player::DeleteFromDB` |

## Verified against

- **vmangos** (development branch): `Player.cpp`, `Player.h`, `Item.cpp`, `Item.h`, `Bag.cpp`, `ItemHandler.cpp`, `ItemPrototype.h`, `ItemDefines.h`, `PacketsItem.cpp`, `ObjectMgr.cpp`, `CharacterHandler.cpp`. Primary source; servers win over docs.
- **cmangos-classic**: `ItemHandler.cpp`, `Item.cpp`, `Bag.cpp`, `CharacterHandler.cpp`, cross-check of the item query spell defaults and handler validation.
- **gtker wow_messages**: wowm definitions of all 6 inventory CMSGs, SMSG_INVENTORY_CHANGE_FAILURE, SMSG_ITEM_PUSH_RESULT and SMSG_ITEM_QUERY_SINGLE_RESPONSE. The 478-byte SMSG_ITEM_QUERY_SINGLE_RESPONSE test vector for item 7230 is a known-answer test (`ItemPacketTests`).
- vmangos human warrior `playercreateinfo_item` rows (25, 38, 39, 40, 117 ×4, 2362, 6948) drive the starting-outfit tests in Game and World tests.

## Discrepancies and deliberate differences

- **Item query empty spell slots.** gtker's test vector has zeros in unused spell slots; vmangos writes `spellid 0, trigger 0, charges 0, cooldown -1, category 0, category cooldown -1` (`PacketsItem.cpp`, cmangos agrees). We follow the servers; the KAT patches the vector's spell blocks to `-1` and documents why.
- **Consumable subclass.** vmangos sends subclass 0 for `ITEM_CLASS_CONSUMABLE` in the query response (client quirk). We do the same.
- **Unknown entry.** SMSG_ITEM_QUERY_SINGLE_RESPONSE with only `entry | 0x80000000` (vmangos), not a full empty block.
- **`patch` column dropped.** vmangos keys `item_template` on `(entry, patch)` for progression. We load one row per entry (latest patch content when imported).
- **Moves send values updates.** A moved item gets a values update (`ITEM_FIELD_CONTAINED`, the slot fields of its old and new holder); the create block goes to the owner once, the first time the item exists for the client (login or `SMSG_ITEM_PUSH_RESULT`). vmangos may re-send create blocks on some bag moves (`Bag::StoreItem` / `SendCreateUpdateToPlayer`); not needed for correctness here.
- **No SMSG_OPEN_CONTAINER** after auto-equipping a bag; vmangos does not send one either (cmangos does in some builds).
- **Unloadable rows are kept.** vmangos deletes inventory rows it cannot place (bad slot, missing template) with an error log. We keep them in memory and write them back on save, so a content import mistake does not destroy items.

## Limitations (left for other areas or later)

- **Requirements are permissive** until skills, spells, reputation and honor exist: `DefaultItemRequirements` grants skill 300 in every weapon/armor skill, knows every spell, no dual wield, honor rank 0, reputation Friendly. Replace by registering an `IItemRequirements` (see the integration notes).
- **Dual wield** is off by default, so a one-handed weapon never auto-equips to the off hand.
- **No mail fallback** when the off hand cannot be stored after equipping a two-hander (vmangos mails it); the equip fails with `InventoryFull` instead.
- **Keyring** holds keys only within the level's keyring size (vmangos `GetMaxKeyringSize`); explicit moves into buyback slots or keyring slots past the size are refused with `ItemDoesntGoIntoBag2` (hardening, vmangos does not check).
- **Not implemented here** (see "Item mechanics lane" below for what has since been delivered and what is still open): buyback (vendor area), the `ITEM_FLAG_*` "discovered" gate, item loot containers (`generated_loot`), item enchantments and random properties, item text/pages, and exact persisted item-cooldown owner metadata. The build-5875 `CMSG_USE_ITEM` path, charges, bind-on-use, item cooldown selection, and full-resource consumable refusal are implemented in the item-use slice.
- **Stat hook** applies template stats, armor, resistances and health/mana pools to the update fields directly; it does not do combat ratings or auras (no aura system yet).

## Tests

- `tests/ArcaneCore.Game.Tests/Item*`: storage rules, swaps, splits, merges, bag moves, destroy, equip rules, starting outfit, packet KATs, update-block ordering and owner-only visibility, save snapshots.
- `tests/ArcaneCore.Data.Tests/ItemStoreTests.cs`: schema, store round trip and replace, ownership move, character save/delete, template source mapping, on SQLite, MariaDB and PostgreSQL.
- `tests/ArcaneCore.World.Tests/Items/`: loopback end to end (character create gives the outfit, CMSG_CHAR_ENUM shows it, login item creates before the self create, the six inventory opcodes, the failure packet, item query known/unknown, persistence across relog).

## Item mechanics lane (vw2/item-mechanics)

Branch `claude/vw2-item-mechanics`, five commits on top of main 49448fd (load/trade/durability fixes, small handlers, timed items and area limits, spell CreateItem, ammo). Standing directive: vanilla/retail 1.12.1 as in vmangos; every rule below cites `D:\refs\vmangos` (read-only, nothing copied). Deliberate deviations are listed; every config option defaults to retail.

### Delivered

| Slice | What | vmangos reference |
|---|---|---|
| Load corrections | A stored duration that disagrees with the template about "timed or not" is replaced by the template's; the bound flag is cleared on a NO_BIND template; the wrapped flag is stripped unless the template is a non-stackable wrapper. `ItemTemplateFlags` now models every `ITEM_FLAG_*` bit. | `Item.cpp:403-410, 430-435, 462-476`; `ItemPrototype.h:64-84` |
| Trade vs mail/auction | `PlayerInventory.CanBeTraded` (soulbound, bags, carried position) is what trading checks; `CanTransferOut` adds the mail/auction rule (conjured or timed items refused). Conjured water and food can be traded again. `TryStageEconomyTransfer(..., trade: true)` for trade settlement. | `Item.cpp:932-952`; `TradeHandler.cpp:313,322,702`; `MailHandler.cpp:301-307`; `AuctionHouseHandler.cpp:332-342` |
| Durability | `Items:DurabilityLossEnable` gate in `DurabilityPointsLoss`; the loss primitives (`DurabilityPointsLossAll`, `DurabilityPointLossForEquipSlot`), death wear (`MapCombat.DeathDurability.cs`) and spell effects 111/115 share it. Breaking equipment removes its bonuses before setting zero durability; a signed repair reapplies them once. `Items:DurabilityLossChanceDamage` is rolled per damage event by `MapCombat` (see "Combat durability" below). See [durability spells](../integration/durability-spells-20261004.md) and [death durability](../integration/death-durability-20261004.md). | `Player.cpp:4794-4898`; `mangosd.conf.dist.in:2847-2848`; `World.cpp:553-554` |
| Small handlers | CMSG_ITEM_NAME_QUERY, CMSG_READ_ITEM, CMSG_AUTOBANK_ITEM, CMSG_AUTOSTORE_BANK_ITEM, CMSG_AUTOEQUIP_ITEM_SLOT (`World/Items/ItemMiscHandlers.cs`, `PlayerInventory.Bank.cs`, `ItemMiscPackets.cs`). | `ItemHandler.cpp:92-106, 417-441, 911-986, 1022-1047` |
| Timed items and area limits | Per-map `ItemMaintenanceUpdater` (interval `Items:ZoneLimitCheckMs`, 1000): timed items lose elapsed whole seconds and are destroyed at 0 through the normal destroy path, `SMSG_ITEM_TIME_UPDATE` when an item is first seen; map/area-limited items are destroyed when the player is alive and the map or zone changed, on resurrection and at first observation (login), not for a ghost. | `Item.cpp:243-262, 1087-1107`; `Player.cpp:1155, 6643-6656, 10881-10909, 19676-19695, 15497-15503` |
| Spell CreateItem inventory side | `PlayerInventory.CreateItemFromSpell` (stack clamp, partial store when full or unique-limited, other errors reported, crafter signature via `HasSignature`, push result with received and created). For the spell-effect handler of another lane to call. | `SpellEffects.cpp:1885-1990`; `ItemPrototype.h:535` |
| Ammo | CMSG_SET_AMMO, `PLAYER_AMMO_ID`, `CanUseAmmo`/`SetAmmo`/`RemoveAmmo`, ammo selected from the starting backpack, `CheckAmmoCompatibility`, `AmmoDps` (read by the stats lane), `ConsumeRangedAmmo(spellId)` (Spell::TakeAmmo), `TryGetAmmoVisual` (for SMSG_SPELL_START/GO, Spell::WriteAmmoToPacket), persistence in `character_item_state`. | `Player.cpp:566-575, 7514-7570, 10100-10160, 14703, 16501`; `Spell.cpp:4563-4587, 5130-5175`; `ItemHandler.cpp:988-1008` |

Schema: Characters **14** (`CharacterItemStateDataModule.Version`, table `character_item_state(character_id, ammo_id)`, with an `ICharacterDataCleanup`). The integrator renumbers on a collision. `InventorySnapshot` gained `AmmoId` (null means "not carried, leave the stored selection"); the three places that copy a snapshot (`CharacterSaveQueue.Copy`, `EfEconomyStore.Copy`, `EfCharacterQuestRewardStore`) pass it through.

Config (section `Items`, `ItemMechanicsOptions`): `DurabilityLossEnable=true`, `DurabilityLossChanceDamage=0.5`, `ZoneLimitCheckMs=1000`.

### Combat durability

Implemented in `Combat/MapCombat.Durability.cs`, called from two places in `Combat/MapCombat.Melee.cs`; all of it runs on the map's world thread, rolls with `MapCombat.Random` (`ICombatRandom`, the same scripted source the combat tests use) and allocates nothing.

- **Hit taken** (`DealDamage`, survivor path): when a player victim survives a damage event (melee, spell, environmental, any attacker), `Items:DurabilityLossChanceDamage` percent of the time one of the worn armor pieces with durability is chosen uniformly and loses one point. The pool is `PlayerInventory.CollectWornArmorWithDurability` (every equipment slot holding an `ItemClass.Armor` item whose template has `MaxDurability > 0`, in slot order, stack-allocated), the pick is `Random.Next(0, count - 1)`, the loss is `DurabilityPointLossForEquipSlot`; with no armor worn nothing is drawn. Reference: `mangosserver/server` `Unit::DealDamage` ("random durability for items (HIT TAKEN)", `Unit.cpp:1093-1098`) rolls `urand(0, EQUIPMENT_SLOT_END - 1)` over all 19 slots with equal weight (`Unit.cpp:1096`) and lets an empty slot (`PlayerDurability.cpp:253-259`) or an item without durability (`PlayerDurability.cpp:213-228`, old and new durability both 0) absorb the roll, so its effective per-slot chance is chance / 19 and the weapons and ranged slot are in its pool. The armor-only pool is the deliberate difference listed below: every successful percent roll wears a piece of armor, a shield counts as armor (class 4), a weapon does not (it wears on the hit done), and a broken piece stays in the pool so its roll does nothing, as in the reference.
- **Hit done** (`DealMeleeDamage`): after a white swing of a player attacker deals damage and the victim is still alive, the same percent roll takes one point off the weapon of the swinging hand (`BaseAttack` is the main hand, `OffAttack` the off hand). The reference core rolls a random slot for hit done as well (`Unit.cpp:1103-1108`); the weapon-specific slot is a deliberate deviation (the lane goal is weapon wear on hit). A miss, dodge or parry deals nothing and wears nothing; a spell or ranged hit is not a swing and wears no weapon (limits below).
- **Not on the killing blow**, as in the reference core (the roll sits after the kill branch). The death penalty (`ApplyDeathDurabilityLoss`: 10% of every worn item once, not when a player tapped the kill, not in a battleground) is untouched.
- **Options**: `Items:DurabilityLossEnable=false` overrides everything; a chance of 0 or less never rolls (the random source is not advanced), so a disabled configuration costs one comparison per hit. The percent roll is `chance > frand(0, 100)`, vmangos `roll_chance_f`.
- **A broken item stops contributing.** `PlayerInventory.DurabilityPointsLoss` now takes a worn item's stat deltas off *before* writing durability 0 (vmangos `Player.cpp:4879-4881`, which spells out that order). Before this change the order was reversed, and the stat system, which ignores a broken item in both directions (`Player::_ApplyItemMods`), left its weapon damage, shield block value and armor applied after the item broke. `RepairDurability` already wrote durability first, so repairing puts the item back. Spell checks, `PlayerCombatSkills` and `PlayerStatSystem` already skip a broken item.

Known gaps and UNVERIFIED points:

- UNVERIFIED: whether retail 1.12.1 weights the armor slots on a hit taken. No reference core does: `mangosserver/server` picks uniformly over all 19 slots (`Unit.cpp:1096`, `urand(0, EQUIPMENT_SLOT_END - 1)`), and a keyword search of the other cores (arcemu, WCell, mangossharp) found no per-slot weights either (WCell lists the whole feature as a TODO); azerothcore and TrinityCore were only searched for the config keys. The implemented pool is therefore uniform over the worn armor with durability (the lane acceptance's "retail slot weights" is not demonstrated by any source; this is what the code does). If a captured retail source shows weights, `CollectWornArmorWithDurability` is where the pool is built and `RollHitTakenDurability` is the single pick.
- UNVERIFIED: whether retail wears the weapon only for white swings, whether a ranged weapon wears on ranged hits, and whether environmental damage (falling, lava) wears armor; the code follows the reference core for the last (self damage is damage taken).
- `DurabilityLossChance.Absorb`, `.Parry` and `.Block` of the reference core's config are not modelled (they exist in `WorldConfig.cpp` but nothing in `Unit.cpp` reads them), nor is the `SPELL_ATTR_EX3_NO_DURABILITY_LOSS` exception.
- The new `Items:DurabilityLossChanceDamage` description in `ItemMechanicsOptions` changes the generated `docs/reference/configuration.md` row, so `ConfigReferenceTests` in `ArcaneCore.World.Tests` fails until the orchestrator regenerates it.

### Deliberate differences

- **Hit-taken wear draws from the worn armor only.** The reference core rolls one of the 19 equipment slots with equal weight and wastes the roll on an empty slot, a durability-less item or a weapon (`Unit.cpp:1096`, `PlayerDurability.cpp:253-259`); `RollHitTakenDurability` rolls uniformly over the equipped `ItemClass.Armor` items with a durability maximum (`CollectWornArmorWithDurability`), so the configured chance is the chance that *some* armor piece wears, independent of how many slots are filled, and the weapons wear only through the hit-done roll. Within the pool the weights are equal: no source shows retail weights (UNVERIFIED above).
- **Zone limits are polled** (`Items:ZoneLimitCheckMs`) because `Player.ZoneId` has no change event; vmangos reacts inside `UpdateZone`. The result is the same within one interval. Switch to the event if the exploration lane adds one.
- **Ammo storage** is a separate table instead of a `characters` column (no behavioural change).
- **`AmmoDps` is computed** from the current equipment on every read; vmangos caches `m_ammoDPS` and can leave it stale after unequipping the ranged weapon.
- **`CanBeTraded` keeps the carried-position rule** (backpack or carried bag only), which is stricter than vmangos' `CanUnequipItem` check; the client never offers equipped items.
- **READ_ITEM packet layouts follow vmangos** (OK: item guid twice; FAILED: guid, u8 reason always 0, guid), while wow_messages lists one guid for both. Unverified against a real client. No page text is sent (see limits).

### Not delivered (needs another lane's primitive, data or a decision)

- Exact persisted item-cooldown owner metadata for relog/UI reconstruction: current server rows preserve durations/category IDs, while `ItemId` and effective item-category ownership remain pending. References: `SpellHandler.cpp:36-140`, `Spell.cpp:4991-5046, 7109-7175`, `Player.cpp:22139-22214`; proposed contract is documented in `docs/integration/item-use-20261004.md` and the cooldown reconnaissance report.
- Random properties (`ItemRandomProperties`): need the developer-supplied build-5875 DBC. CMSG_USE_ITEM, item charges and consumption, the item cooldown pick, bind-on-use and the consumable refusal are delivered (`SpellSystem.HandleItemUse`, docs/integration/item-use-20261004.md); ON_EQUIP item spells and item sets are delivered (next section); the enchantment engine is the crafting lane's (docs/areas/crafting.md), and weapon chance-on-hit spells and enchantment combat spells proc through `SpellSystem.ItemCombatProcs`.
- Item loot containers (`CMSG_OPEN_ITEM`, `generated_loot`), lockboxes: need coordination with the loot-service owner (group-loot-xp).
- Equip cooldown and combat weapon-swap GCD, food/drink sit and `MOD_REGEN` (aura 84), elixir exclusivity, offline rules (conjured items after 15 minutes offline, REAL_DURATION offline tick; need `logout_time` from death-persistence).
- Gift wrapping (`CMSG_WRAP_ITEM`): classic-db `item_template` has no `WrappedGift` column, so no wrapper can be created from the data; READ_ITEM page text needs `page_text` content and `CMSG_PAGE_TEXT_QUERY`.
- Spell `SummonChangeItem` (item transform keeping enchantments).

### Real-client acceptance still pending

The item-use request and spell start/go layouts have not been captured against a real 1.12.1 client; they are grounded in vmangos and wow_messages and covered by socket regressions. The item-use slice still needs real-client acceptance, proprietary build-5875 content validation, and exact relog/UI verification for pending cooldown owner metadata.

### Tests (this lane)

`tests/ArcaneCore.Game.Tests/ItemMechanics/` (load fixes, trade and durability, misc handlers, maintenance, spell create, ammo, `CombatDurabilityTests` for the combat triggers), `tests/ArcaneCore.Game.Tests/Spells/ItemUseTests.cs`, `tests/ArcaneCore.Data.Tests/CharacterItemStateStoreTests.cs` (provider matrix), and `tests/ArcaneCore.World.Tests/Items/` (`ItemUseWorldTests`, `ItemMiscWorldTests`, `ItemMaintenanceWorldTests`, `AmmoWorldTests`). The Game test namespace is `...Tests.ItemMechanics`, not `...Tests.Items`, because the latter would shadow `ArcaneCore.Game.Items` in older tests.

## Item sets and ON_EQUIP item spells (L5-item-sets-and-equip-spells)

### Implemented

- **One equip hook.** `PlayerInventory.EquipmentChanged(item, slot, EquipmentChange)` is raised for the equipment slots from `EquipItem`, `RemoveItem` (so `DestroyItem` too) and from the stat hook. `Worn` / `Removed` are placement, `ModsApplied` / `ModsRemoved` are the stat-mod flips (equipped and unbroken, repaired, broken, taken off). Not raised while the inventory loads or for a shadow inventory; login replays instead. The four equipped bag slots keep the existing `BagEquipChanged`.
- **ON_EQUIP item spells** (`Items/ItemUse/ItemEquipSpells.cs`, mangos `ApplyItemEquipSpell` / `ApplyEquipSpell`). Spells with trigger 1 are cast triggered on the wearer with the item as cast item when the item starts counting as worn (so a broken item has none, and repairing it brings them back). When it stops counting, every aura of every spell of the item that was cast from that item is removed, except an on-use spell with negative charges. No charge is consumed. Non-quiver equipped bags take part too.
- **Item sets** (`Items/ItemSets/ItemSetBonuses.cs`, mangos `AddItemsSetItem` / `RemoveItemsSetItem`). Each worn piece counts once (two rings of one set count two); a bonus spell is cast when its threshold is reached and removed as soon as the count drops below it, so unequipping a piece removes the highest bonus. A broken piece still counts. A set whose required skill (ItemSet.dbc) the wearer lacks when the piece is worn does not count that piece, and removing it is a no-op (mangos checks only then). Per player state is `PlayerItemSets` (`ItemEquipSpells.SetsOf(inventory)`), world thread only, changed per equip event, nothing per tick.
- **Content.** `Kernel/Items/ItemSetContent.cs` (`ItemSetRecord`, immutable `ItemSetCatalog`) and `Data/Items/ItemSetDbcReader.cs`: ItemSet.dbc, 45 fields (id, names, 17 item ids, 8 spell ids at 27, 8 thresholds at 35, required skill 43, rank 44; mangos `ItemSetEntryfmt`). Strict field count, duplicate ids and a string offset outside the string block refuse the file. `item_template.set_id` already existed.
- **World.** `ItemEquipSpellFeature` loads the catalog once at startup and binds `ItemEquipSpells.Attach` to `SpellFeature.PlayerSpellsRestored`, which `SpellFeature` raises from its own `PlayerLoggedIn` handler once the saved auras are restored and the passives cast. Features attach in full-name order (`ArcaneCore.World.Items` before `ArcaneCore.World.Spells`), so a `PlayerLoggedIn` handler on this feature would run before the restore; the explicit event puts the replay after it. Attach replays: sets for every worn piece, then the spells of the unbroken ones, removing a saved aura of the same spell id first so logout and login never stack (a saved non-passive Equip: aura is restored like any other and then replaced by the item-bound cast). A set id missing from the catalog is logged once per set and applies nothing (mangos logs the same).
- **Quivers** are left to `QuiverHaste` (class Quiver bags are skipped here), so its ranged-weapon condition and aura guard are unchanged. A test attaches both and checks the aura exists once.

### Options

| Key | Default | Meaning |
|---|---|---|
| `ItemSets:DbcPath` | unset | Developer-supplied build-5875 `ItemSet.dbc`. Unset: no set bonuses (a warning is logged), Equip: spells still work. Configured but unreadable or another layout: startup is refused. |

### Known gaps

- **Shapeshift form checks cover the Equip: spells only.** An Equip: spell the current form forbids is not cast, and a form change re-checks every worn item (`ItemEquipSpells.ReconcileAtFormChange`, an `IFormChangeListener` of the stance feature; mangos `ApplyEquipSpell` and `UpdateEquipSpellsAtFormChange`, merged from the Codex line at the 2026-10-07 integration). Set bonus spells are not re-checked at a form change.
- The catalog is not hot-reloadable (it mirrors the client's own DBC); a changed file needs a restart.
- UNVERIFIED against a real 1.12.1 client: the tooltip text of set bonuses and the set item list come from the client's own DBC; the server only supplies the spells. The ItemSet.dbc field layout is taken from the mangos reference (`DBCStructure.h` / `DBCfmt.h`), not from a developer file in this repo.

### Tests (this lane)

`ItemSetsAndEquipSpellsTests` (Game.Tests/ItemUse: 2 then 4 pieces, unequip and destroy removal, broken pieces, skill requirement, unknown set, equip spell apply/remove/break/repair, on-use negative-charge exemption, bags, login replay without stacking, quiver haste next to it), `ItemSetDbcReaderTests` (Data.Tests/Items), `ItemEquipSpellFeatureTests` (World.Tests/Items: options, catalog loading, and a real relog through the feature host where a saved buff and a saved non-passive stacking Equip: aura both come back once, the equip aura bound to its item and gone when it is taken off).

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
- **Not implemented here** (see "Item mechanics lane" below for what has since been delivered and what is still open): buyback (vendor area), the `ITEM_FLAG_*` "discovered" gate, item loot containers (`generated_loot`), item enchantments, and exact persisted item-cooldown owner metadata (random properties, page text and gift wrapping: see "Economy-items lane" below). The build-5875 `CMSG_USE_ITEM` path, charges, bind-on-use, item cooldown selection, and full-resource consumable refusal are implemented in the item-use slice.
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
- `DurabilityLossChance.Absorb`, `.Parry` and `.Block` are modelled since the economy-items lane (see below), off by default because vmangos does not have them; the slots they wear are a reconstruction, since no reference core still reads them. The `SPELL_ATTR_EX3_NO_DURABILITY_LOSS` exception is not modelled.
- The new `Items:DurabilityLossChanceDamage` description in `ItemMechanicsOptions` changes the generated `docs/reference/configuration.md` row, so `ConfigReferenceTests` in `ArcaneCore.World.Tests` fails until the orchestrator regenerates it.

### Deliberate differences

- **Hit-taken wear draws from the worn armor only.** The reference core rolls one of the 19 equipment slots with equal weight and wastes the roll on an empty slot, a durability-less item or a weapon (`Unit.cpp:1096`, `PlayerDurability.cpp:253-259`); `RollHitTakenDurability` rolls uniformly over the equipped `ItemClass.Armor` items with a durability maximum (`CollectWornArmorWithDurability`), so the configured chance is the chance that *some* armor piece wears, independent of how many slots are filled, and the weapons wear only through the hit-done roll. Within the pool the weights are equal: no source shows retail weights (UNVERIFIED above).
- **Zone limits are polled** (`Items:ZoneLimitCheckMs`) because `Player.ZoneId` has no change event; vmangos reacts inside `UpdateZone`. The result is the same within one interval. Switch to the event if the exploration lane adds one.
- **Ammo storage** is a separate table instead of a `characters` column (no behavioural change).
- **`AmmoDps` is computed** from the current equipment on every read; vmangos caches `m_ammoDPS` and can leave it stale after unequipping the ranged weapon.
- **`CanBeTraded` keeps the carried-position rule** (backpack or carried bag only), which is stricter than vmangos' `CanUnequipItem` check; the client never offers equipped items.
- **READ_ITEM packet layouts follow vmangos** (OK: item guid twice; FAILED: guid, u8 reason always 0, guid), while wow_messages lists one guid for both. Unverified against a real client. The pages themselves come from CMSG_PAGE_TEXT_QUERY (see "Economy-items lane").

### Not delivered (needs another lane's primitive, data or a decision)

- Exact persisted item-cooldown owner metadata for relog/UI reconstruction: current server rows preserve durations/category IDs, while `ItemId` and effective item-category ownership remain pending. References: `SpellHandler.cpp:36-140`, `Spell.cpp:4991-5046, 7109-7175`, `Player.cpp:22139-22214`; proposed contract is documented in `docs/integration/item-use-20261004.md` and the cooldown reconnaissance report.
- Random properties are delivered by the economy-items lane (code path; the content needs the developer-supplied ItemRandomProperties.dbc and an `item_enchantment_template` dump). CMSG_USE_ITEM, item charges and consumption, the item cooldown pick, bind-on-use and the consumable refusal are delivered (`SpellSystem.HandleItemUse`, docs/integration/item-use-20261004.md); ON_EQUIP item spells and item sets are delivered (next section); the enchantment engine is the crafting lane's (docs/areas/crafting.md), and weapon chance-on-hit spells and enchantment combat spells proc through `SpellSystem.ItemCombatProcs`.
- Item loot containers (`CMSG_OPEN_ITEM`, `generated_loot`), lockboxes: need coordination with the loot-service owner (group-loot-xp).
- Equip cooldown (the 30 s on-use cooldown of an item just put on, vmangos Player::ApplyEquipCooldown), food/drink sit and `MOD_REGEN` (aura 84), elixir exclusivity. The combat weapon-swap GCD and the 15-minute conjured rule are delivered by the economy-items lane; REAL_DURATION does not exist in 1.12.
- Gift wrapping and page text are delivered by the economy-items lane (classic-db papers use the cmangos paper-to-gift pairs; page text needs a `page_text` dump).
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

- **Shapeshift form checks.** An Equip: spell the current form forbids is not cast, and a form change re-checks every worn item (`ItemEquipSpells.ReconcileAtFormChange`, an `IFormChangeListener` of the stance feature; mangos `ApplyEquipSpell` and `UpdateEquipSpellsAtFormChange`, merged from the Codex line at the 2026-10-07 integration). Set bonus spells follow the same rule since the economy-items lane (`ItemSetBonuses.ReconcileAtFormChange`; AddItemsSetItem casts a bonus only when the form fits).
- The catalog is not hot-reloadable (it mirrors the client's own DBC); a changed file needs a restart.
- UNVERIFIED against a real 1.12.1 client: the tooltip text of set bonuses and the set item list come from the client's own DBC; the server only supplies the spells. The ItemSet.dbc field layout is taken from the mangos reference (`DBCStructure.h` / `DBCfmt.h`), not from a developer file in this repo.

### Tests (this lane)

`ItemSetsAndEquipSpellsTests` (Game.Tests/ItemUse: 2 then 4 pieces, unequip and destroy removal, broken pieces, skill requirement, unknown set, equip spell apply/remove/break/repair, on-use negative-charge exemption, bags, login replay without stacking, quiver haste next to it), `ItemSetDbcReaderTests` (Data.Tests/Items), `ItemEquipSpellFeatureTests` (World.Tests/Items: options, catalog loading, and a real relog through the feature host where a saved buff and a saved non-passive stacking Equip: aura both come back once, the equip aura bound to its item and gone when it is taken off).

## Economy-items lane (wave 2, 2026-10-07)

Branch `claude/w2-economy-items`. vmangos is the reference (`D:\refs\vmangos`, read only); where vmangos has nothing, the cmangos trees are cited.

### Delivered

| Piece | What | Where | Reference |
|---|---|---|---|
| Orphaned container loot | `item_loot_state` / `item_loot` rows whose `item_instance` row is gone are deleted when item content first loads, before any character (one set-based DELETE per table). The escrow paths already stopped making new ones (wave-3 F8); this removes what older builds left. | `EfItemLootMaintenance` (`IItemLootMaintenance`), `ItemsFeature.EnsureLoadedAsync` | `CharacterDatabaseCleaner::CleanOrphanedItemData`, `ObjectMgr::SetHighestGuids` (ObjectMgr.cpp:7943-7956) |
| Defense durability | `Items:DurabilityLossChanceParry`, `Block`, `Absorb`, default 0 (off: vmangos has no defense wear); the mangos values are 0.05, 0.05 and 0.5. When set, after a white swing a surviving player victim that parried wears its main hand, one that blocked wears its off hand (the shield), and absorbed damage wears a worn armor piece from the hit-taken pool. `DurabilityLossEnable=false` overrides; a zero chance draws nothing. | `MapCombat.Durability.cs` (`RollDefenseDurability`) | mangos-classic World.cpp:460-462, mangoszero WorldConfig.cpp:233-235 (settings and defaults) |
| Combat weapon switch | A weapon put on while alive and in combat starts the weapon change timer and the global cooldown of spell 6119 (rogue 6123) and sends SMSG_SPELL_COOLDOWN; while the timer runs, equipping another weapon in combat is CANT_DO_RIGHT_NOW. The swing timer reset on a swap was already in the stat system. | `SpellSystem.WeaponSwap.cs`, `PlayerInventory.WeaponChangeLocked`, `ItemEquipSpells` (Worn) | Player.cpp:10340-10369 (EquipItem), 9710-9711 (CanEquipItem), SharedDefines.h:1173-1176 |
| Conjured items offline | An item whose template has ITEM_FLAG_CONJURED is dropped at login when more than 900 s passed since the stored logout second (`character_rest.logout_time`; a queued logout write is flushed first). No stored second: nothing vanishes. | `ConjuredItems`, `ItemsFeature.OnPlayerLoadingAsync` | Player.cpp:15533-15540 (_LoadInventory) |
| Set bonuses and forms | A reached set bonus whose spell the current form forbids is recorded but not cast; a form change casts or removes every active bonus spell to match. | `ItemSetBonuses.ReconcileAtFormChange` | Item.cpp:85-87 (AddItemsSetItem), Player.cpp:7242-7253 (UpdateEquipSpellsAtFormChange) |
| Gift wrapping | CMSG_WRAP_ITEM with vmangos' refusals in order; the item keeps guid and fields, takes the paper's gift entry, ITEM_DYNFLAG_WRAPPED and the gift creator; one paper is used. CMSG_OPEN_ITEM on a gift restores the item's own entry and flags (unknown contents: the gift is destroyed). Papers without `wrapped_gift` (cmangos classic-db) use the cmangos pairs. | `PlayerInventory.Gifts.cs`, `ItemMiscHandlers` (CMSG_WRAP_ITEM), `GameObjectLootHandlers.OpenItem` | ItemHandler.cpp:1049-1139, SpellHandler.cpp:200-227; mangos-classic ItemHandler.cpp:1152-1160 |
| Page text | CMSG_PAGE_TEXT_QUERY answers one SMSG_PAGE_TEXT_QUERY_RESPONSE per page along the chain, "Item page missing." for an unknown page; the load cuts loops and reports missing next pages. Serves READ_ITEM books and text objects. | `PageTextFeature`, `PageTextCatalog`, `PageTextDumpReader` | QueryHandler.cpp:263-299, ObjectMgr.cpp:6647-6687 |
| Random properties | A new item whose template has `random_property` rolls an `item_enchantment_template` row by chance (GetItemEnchantMod) and writes the ItemRandomProperties.dbc row's id and three enchantments (slots 3-5). Rolled in StoreNewItem (loot pickup, vendors, spell creation, quest rewards); a caller may pass an id. | `ItemRandomProperties`, `PlayerInventory.RandomProperties.cs`, `ItemRandomPropertyFeature` | Item.cpp:792-834, ItemEnchantmentMgr.cpp |

The auction outbid notification and bidder list are in [economy fidelity](../integration/economy-fidelity.md).

### Data contracts (what the code needs and the repo does not have)

| Setting | Content | Without it |
|---|---|---|
| `PageText:DumpPath` | A vmangos or cmangos classic-db world dump (plain or .gz) with `page_text` (entry, text, next_page); only that table is read. The full classic-db z2815 dump loads its 1427 pages in about 1 s. | Every page answers "Item page missing." |
| `ItemRandomProperties:DbcPath` | The developer-supplied build-5875 ItemRandomProperties.dbc (16 fields, strict). | No random properties. |
| `ItemRandomProperties:EnchantmentTemplateDumpPath` | A world dump with `item_enchantment_template` (entry, ench, chance; vmangos rows filtered to patch 10). z2815: 772 entries, about 1 s. | No random properties. |

### Optional client data (one command)

`tools/content/set-optional-data.ps1 -AppSettings <appsettings.json> -DbcDirectory D:\refs\client-dbc-5875-effective [-Dump <dump>]` (or
`refresh-world-content.ps1 ... -AppSettings <appsettings.json>`, which runs it after the content refresh) checks every file (WDBC header and
build-5875 field count, the directory's `SHA256SUMS`, both tables present in the dump), keeps the old `appsettings.json` as a `.bak` and sets:

| Key | File (D:\refs\client-dbc-5875-effective, patch-2 over patch over dbc) | Rows | Feature |
|---|---|---|---|
| `ItemSets:DbcPath` | ItemSet.dbc (patch-2) | 172 sets | item set bonuses |
| `ItemRandomProperties:DbcPath` | ItemRandomProperties.dbc (patch) | 2012 suffixes | random suffixes |
| `ItemRandomProperties:EnchantmentTemplateDumpPath` | the classic-db dump (`item_enchantment_template`) | 772 entries | random suffixes |
| `Enchanting:SpellItemEnchantmentDbcPath` | SpellItemEnchantment.dbc (patch-2) | 1460 enchantments | enchanting, and the stats of suffix enchantments (slots 3-5) |
| `PageText:DumpPath` | the classic-db dump (`page_text`) | 1427 pages | readable items and text objects |
| `CharacterCreation:CharSectionsDbcPath` | CharSections.dbc (patch) | 3603 available rows | the appearance check of a new character |
| `CharacterCreation:CharacterFacialHairStylesDbcPath` | CharacterFacialHairStyles.dbc (patch) | 136 styles | the appearance check of a new character |

Each is read once at startup (not hot-reloadable); a configured file that cannot be read refuses startup. With all seven the world logs none
of the five "not set" warnings. 1.12.1 has no barber shop, so CharSections serves character creation only.

`page_text` and `item_enchantment_template` are read from dumps because a world-database table for them needs a world schema number this lane did not have; moving them into the world database is a follow-up (a table module plus a content-import spec). Until then the world reads them from the dump at every start (about 2 s in all for z2815).

### Schema

Characters **38** (`ItemGiftDataModule.Version`; reserved as 39, renumbered at the wave-2 integration): `item_instance.gift_entry` and `gift_flags`, the values vmangos keeps in `character_gifts`. Deliberately on the item row: the wrapped state then travels through mail, auction and trade escrow (which move `item_instance` rows) and goes with the item, with no extra row to move or delete.

### Deliberate differences and UNVERIFIED points

- **Defense durability slots** are a reconstruction: the mangos settings exist, but no reference core reads them any more (vmangos keeps only `.Damage`), so parry → main hand, block → off hand and absorb → a random worn armor piece are this code's reading of the names. UNVERIFIED against retail. The three chances default to 0, so a default server matches vmangos (no defense wear); setting them is an operator's deviation.
- **Absorb** counts damage taken by absorb auras on a white swing (`MeleeDamageInfo.Absorbed`), not armor mitigation, which the hit-taken roll already covers. Spells and ranged attacks do not roll the defense chances.
- **A wrapped item keeps its own durability** across a save; vmangos clamps durability to the gift template's maximum (0) when it loads a wrapped item, which would break the item on opening. Opening restores the item template's maximum.
- **The page chain is bounded** by the page count as well as by the load-time loop cut (vmangos relies on the cut alone).
- **Loot shows no random property**: vmangos rolls when the loot is generated and shows it in the loot window and roll packets (LootItem::randomPropertyId); here the roll happens when the item is stored, so the window shows 0 and the name suffix appears once it is in the bags. Group-loot roll packets likewise send 0.
- **The weapon switch timer** is per Player object (a relog starts at 0, as in vmangos) and needs spells 6119/6123 in the spell store (Spell.dbc); without them nothing starts, as vmangos logs.
- The equip cooldown (30 s on-use cooldown on equip, Player::ApplyEquipCooldown) is still not delivered.

### Tests

Game: `ItemMechanics/DefenseDurabilityTests`, `ItemMechanics/GiftWrapTests`, `ItemMechanics/ItemRandomPropertyTests`, `ItemUse/WeaponSwapCooldownTests`, `ItemUse/ItemSetFormChangeTests`. Data: `ItemLootOrphanSweepTests`, `ItemGiftStoreTests` (round trip and the upgrade from step 38), `Items/PageTextDumpReaderTests`, `Items/ItemRandomPropertyReaderTests`, `IntegratedSchemaTests`. World: `Items/ConjuredLogoutWorldTests`, `Items/PageTextWorldTests`, `Items/ItemRandomPropertyWorldTests`, `Items/OptionalClientDataRealTests` (every optional key against the real DBCs and dump; runs with `ARCANECORE_TEST_DBC_DIR` and `ARCANECORE_CLASSICDB_DUMP`), and the playerbot scenarios `Playerbots/Scenarios/AuctionOutbidScenarioTests` (three bots) and `GiftWrapScenarioTests` (wrap, mail with the escrow row checked, open). Harness additions: `ScenarioAuctionWire`, `ScenarioItemWire`, `ScenarioTestWorld.StartAsync(configure)`.

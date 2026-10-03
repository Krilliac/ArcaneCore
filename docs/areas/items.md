# Area: items and inventory

Branch `feat/items`. Integration notes (schema versions, shared-file edits, seams): `docs/integration/items.md`.

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
| Opcode handlers: CMSG_SWAP_ITEM, CMSG_SWAP_INV_ITEM, CMSG_AUTOEQUIP_ITEM, CMSG_AUTOSTORE_BAG_ITEM, CMSG_SPLIT_ITEM, CMSG_DESTROYITEM, CMSG_ITEM_QUERY_SINGLE | `World/Items/ItemHandlers.cs` | vmangos `ItemHandler.cpp`; payloads from gtker wowm `cmsg_*.wowm` |
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
- **Not implemented here** (see "Item mechanics lane" below for what has since been delivered and what is still open): buyback (vendor area), the `ITEM_FLAG_*` "discovered" gate, item loot containers (`generated_loot`), item enchantments and random properties, item cooldowns and `CMSG_USE_ITEM`, soulbound-on-equip updates beyond the bonding flag, item text/pages.
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
| Durability | `Items:DurabilityLossEnable` gate in `DurabilityPointsLoss`; `DurabilityPointsLossAll`, `DurabilityPointLossForEquipSlot`; `Items:DurabilityLossChanceDamage` exposed for the combat triggers (death-persistence owns the hit/death triggers and should read `PlayerInventory.Options` instead of its own keys). | `Player.cpp:4794-4898`; `mangosd.conf.dist.in:2847-2848`; `World.cpp:553-554` |
| Small handlers | CMSG_ITEM_NAME_QUERY, CMSG_READ_ITEM, CMSG_AUTOBANK_ITEM, CMSG_AUTOSTORE_BANK_ITEM, CMSG_AUTOEQUIP_ITEM_SLOT (`World/Items/ItemMiscHandlers.cs`, `PlayerInventory.Bank.cs`, `ItemMiscPackets.cs`). | `ItemHandler.cpp:92-106, 417-441, 911-986, 1022-1047` |
| Timed items and area limits | Per-map `ItemMaintenanceUpdater` (interval `Items:ZoneLimitCheckMs`, 1000): timed items lose elapsed whole seconds and are destroyed at 0 through the normal destroy path, `SMSG_ITEM_TIME_UPDATE` when an item is first seen; map/area-limited items are destroyed when the player is alive and the map or zone changed, on resurrection and at first observation (login), not for a ghost. | `Item.cpp:243-262, 1087-1107`; `Player.cpp:1155, 6643-6656, 10881-10909, 19676-19695, 15497-15503` |
| Spell CreateItem inventory side | `PlayerInventory.CreateItemFromSpell` (stack clamp, partial store when full or unique-limited, other errors reported, crafter signature via `HasSignature`, push result with received and created). For the spell-effect handler of another lane to call. | `SpellEffects.cpp:1885-1990`; `ItemPrototype.h:535` |
| Ammo | CMSG_SET_AMMO, `PLAYER_AMMO_ID`, `CanUseAmmo`/`SetAmmo`/`RemoveAmmo`, ammo selected from the starting backpack, `CheckAmmoCompatibility`, `AmmoDps` (read by the stats lane), `ConsumeRangedAmmo(spellId)` (Spell::TakeAmmo), `TryGetAmmoVisual` (for SMSG_SPELL_START/GO, Spell::WriteAmmoToPacket), persistence in `character_item_state`. | `Player.cpp:566-575, 7514-7570, 10100-10160, 14703, 16501`; `Spell.cpp:4563-4587, 5130-5175`; `ItemHandler.cpp:988-1008` |

Schema: Characters **14** (`CharacterItemStateDataModule.Version`, table `character_item_state(character_id, ammo_id)`, with an `ICharacterDataCleanup`). The integrator renumbers on a collision. `InventorySnapshot` gained `AmmoId` (null means "not carried, leave the stored selection"); the three places that copy a snapshot (`CharacterSaveQueue.Copy`, `EfEconomyStore.Copy`, `EfCharacterQuestRewardStore`) pass it through.

Config (section `Items`, `ItemMechanicsOptions`): `DurabilityLossEnable=true`, `DurabilityLossChanceDamage=0.5`, `ZoneLimitCheckMs=1000`.

### Deliberate differences

- **Zone limits are polled** (`Items:ZoneLimitCheckMs`) because `Player.ZoneId` has no change event; vmangos reacts inside `UpdateZone`. The result is the same within one interval. Switch to the event if the exploration lane adds one.
- **Ammo storage** is a separate table instead of a `characters` column (no behavioural change).
- **`AmmoDps` is computed** from the current equipment on every read; vmangos caches `m_ammoDPS` and can leave it stale after unequipping the ranged weapon.
- **`CanBeTraded` keeps the carried-position rule** (backpack or carried bag only), which is stricter than vmangos' `CanUnequipItem` check; the client never offers equipped items.
- **READ_ITEM packet layouts follow vmangos** (OK: item guid twice; FAILED: guid, u8 reason always 0, guid), while wow_messages lists one guid for both. Unverified against a real client. No page text is sent (see limits).

### Not delivered (needs another lane's primitive, data or a decision)

- `CMSG_USE_ITEM`, item charges and consumption (`TakeCastItem`), item-defined cooldown category/duration pick, bind-on-use, the 1.11 full-health/power consumable refusal: need `SpellCast.CastItem` (skills-professions `skills-spell-items`). References: `SpellHandler.cpp:36-140`, `Spell.cpp:4991-5046, 7109-7175`, `Player.cpp:22139-22214`.
- ON_EQUIP item spells, item sets, chance-on-hit item spells and temp-enchant procs, the enchantment engine, random properties: need the `ItemSet`, `ItemRandomProperties` and `SpellItemEnchantment` DBCs (developer-supplied build-5875 files, not in any reference) and spell-aura breadth plus the melee-outcome event of the warrior lane.
- Item loot containers (`CMSG_OPEN_ITEM`, `generated_loot`), lockboxes: need coordination with the loot-service owner (group-loot-xp).
- Equip cooldown and combat weapon-swap GCD, food/drink sit and `MOD_REGEN` (aura 84), elixir exclusivity, offline rules (conjured items after 15 minutes offline, REAL_DURATION offline tick; need `logout_time` from death-persistence).
- Gift wrapping (`CMSG_WRAP_ITEM`): classic-db `item_template` has no `WrappedGift` column, so no wrapper can be created from the data; READ_ITEM page text needs `page_text` content and `CMSG_PAGE_TEXT_QUERY`.
- Spell `SummonChangeItem` (item transform keeping enchantments).

### Real-client acceptance still pending

None of the new packets (`SMSG_ITEM_TIME_UPDATE`, `SMSG_READ_ITEM_*`, `SMSG_ITEM_NAME_QUERY_RESPONSE`, the ammo field) has been captured against a real 1.12.1 client; layouts are from vmangos and wow_messages and the tests are known-answer tests against those layouts.

### Tests (this lane)

`tests/ArcaneCore.Game.Tests/ItemMechanics/` (load fixes, trade and durability, misc handlers, maintenance, spell create, ammo), `tests/ArcaneCore.Data.Tests/CharacterItemStateStoreTests.cs` (provider matrix), `tests/ArcaneCore.World.Tests/Items/` (`ItemMiscWorldTests`, `ItemMaintenanceWorldTests`, `AmmoWorldTests`). The Game test namespace is `...Tests.ItemMechanics`, not `...Tests.Items`, because the latter would shadow `ArcaneCore.Game.Items` in older tests.

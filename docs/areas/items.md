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
- **Not implemented:** buyback (vendor area), the `ITEM_FLAG_*` "discovered" gate, `generated_loot` / item loot state, ammo (`PLAYER_AMMO_ID`), item enchantments and random properties, durability loss, item cooldowns with the Spell.dbc fallback, trading/mail locks, soulbound-on-equip updates beyond the bonding flag, item text/pages.
- **Stat hook** applies template stats, armor, resistances and health/mana pools to the update fields directly; it does not do combat ratings or auras (no aura system yet).

## Tests

- `tests/ArcaneCore.Game.Tests/Item*`: storage rules, swaps, splits, merges, bag moves, destroy, equip rules, starting outfit, packet KATs, update-block ordering and owner-only visibility, save snapshots.
- `tests/ArcaneCore.Data.Tests/ItemStoreTests.cs`: schema, store round trip and replace, ownership move, character save/delete, template source mapping, on SQLite, MariaDB and PostgreSQL.
- `tests/ArcaneCore.World.Tests/Items/`: loopback end to end (character create gives the outfit, CMSG_CHAR_ENUM shows it, login item creates before the self create, the six inventory opcodes, the failure packet, item query known/unknown, persistence across relog).

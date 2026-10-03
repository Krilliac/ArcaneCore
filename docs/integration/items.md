# Items and inventory: integration notes

Status: ready for review. Area doc: `docs/areas/items.md`.

## Schema versions (need the lead's allocation)

| Component | Version | Module | Changes |
|---|---|---|---|
| World | **2** | `Data/Content/Items/ItemWorldDataModule.cs` | create `item_template` (vmangos columns), `playercreateinfo_item` |
| Characters | **3** | `Data/Characters/Items/ItemCharacterDataModule.cs` | create `item_instance`, `character_inventory` |

These are the next free numbers at the base (world v1, characters v2). Per `seams.md` the lead
assigns the final numbers at merge time; each is a single constant (`SchemaVersion` in the
module), and no test asserts a literal version.

## Owned paths (nearest equivalents)

- `src/ArcaneCore.Kernel/Items/` (templates, item instance records, `IItemStore`, `IItemTemplateSource`)
- `src/ArcaneCore.Data/Content/Items/`, `src/ArcaneCore.Data/Characters/Items/`
- `src/ArcaneCore.Game/Items/`
- `src/ArcaneCore.World/Items/`
- `tests/ArcaneCore.Game.Tests/Item*`, `tests/ArcaneCore.Data.Tests/Item*`, `tests/ArcaneCore.World.Tests/Items/`

## Shared-file edits (minimal, additive)

| File | Change | Why |
|---|---|---|
| `Kernel/Characters/CharacterState.cs` | trailing optional `InventorySnapshot? Inventory = null` | the save snapshot carries the inventory |
| `Data/Stores/EfCharacterStore.cs` | `SaveStateAsync` stages the inventory diff when `Inventory` is set; `DeleteAsync` deletes the character's items | same transaction as the character save/delete |
| `Game/Entities/Player.cs` | `Inventory` property; `CreateSnapshot` passes `Inventory.TakeSnapshotIfChanged()` | |
| `Game/Entities/WorldObject.cs` | `internal virtual Map? ValuesUpdateMap => Map`; `MarkChanged` queues on it | items are not on the map; their values updates go through the owner's map |
| `Game/Maps/Map.cs` | `AddPlayer` writes the inventory create blocks before the player's own; `SendValuesUpdate` routes an `Item` to its owner only | vmangos `Player::BuildCreateUpdateBlockForPlayer`, item fields are owner-only |
| `Game/Updates/UpdateBlockWriter.cs` | `VisibleFieldsFor` adds OwnerOnly + ItemOwner for the item's owner | vmangos `GetUpdateFieldFlagsForTarget` (UF_FLAG_OWNER_ONLY / UF_FLAG_UNK2) |

## Character hooks used

Items plug into character creation, the character list and loading through the base's
`ICharacterHooks` seam (#9, `World/Characters/CharacterHooks.cs`), implemented by
`World/Items/ItemsFeature.cs`; no edit to `CharacterHandlers.cs` or `CharacterPackets.cs`.

- `OnCharacterCreatedAsync`: stores the `playercreateinfo_item` outfit (vmangos `Player::Create` → `StoreNewItemInBestSlots`, then `SaveToDB`).
- `GetCharEnumEquipmentAsync`: display id + inventory type of equipment slots 0-18 and the first bag (vmangos `Player::BuildEnumData`); one query for all characters of the account.
- `OnPlayerLoadingAsync`: loads `character_inventory` into `Player.Inventory` (vmangos `Player::_LoadInventory`); a store failure fails the login (fail closed).

All three do nothing when no `IItemStore` is registered.

## Seams for other areas (vendors, loot, quests, spells)

All on `Player.Inventory` (`Game/Items/PlayerInventory*.cs`), world thread only:

| Need | API |
|---|---|
| Can the player take N of an entry? | `CanStoreNewItem(entry, count, dest, out noSpaceCount)` → `InventoryResult` (vmangos `CanStoreNewItem(NULL_BAG, NULL_SLOT, …)`) |
| Give an item (vendor buy, loot, quest reward) | `AddItem(entry, count, out item, received, created, showInChat)` → `InventoryResult`; stores in the best slots, sends SMSG_ITEM_PUSH_RESULT or the failure. Lower level: `StoreNewItem(dest, template, count)`, `StoreItem(dest, item)` |
| Take items (vendor sell, quest turn-in, reagents) | `DestroyItemCount(entry, count, includeBank)`, `DestroyItemCount(item, count)` (both return the number removed), `RemoveItem(bag, slot)` / `DestroyItem(bag, slot)` |
| Counts (quest objectives) | `GetItemCount(entry, inBankAlso)`; event `ItemCountChanged(entry, delta)` fires on every store/remove/split/destroy |
| Error to the client | `SendEquipError(result, item1, item2, bagSlot, entry)` |
| Bank (bank NPC area) | `CanUseBank` (`Func<bool>?`, set by the banker area; `BankUsable` reads it and the handlers refuse bank moves while it is false), `CanBankItem`, `BankBagSlotCount` |
| Requirements (skills/spells/reputation/honor areas) | implement `IItemRequirements` (`Game/Items/ItemSeams.cs`) and assign it to `Inventory.Requirements` (e.g. from an `ICharacterHooks.OnPlayerLoadingAsync`); default is permissive |
| Stats (stats/combat area) | `IItemStatsApplier` (`Inventory.StatsApplier`), called on equip/unequip and load; default `EquipmentStatsApplier` writes stats, armor/resistances and health/mana |
| Equipped items | `Equipped` (slot, item) enumeration, `GetItem(bag, slot)`, `GetItemByGuid` |
| Templates and new item guids | `ItemsFeature.Templates` (`IItemTemplateStore.Find(entry)`; `EnsureLoadedAsync` before first use off the world thread) and `ItemsFeature.GuidAllocator`; get the feature with `session.Services.GetRequiredService<ItemsFeature>()` |
| Item data in the DB | `IItemStore` (Kernel) / `IItemTemplateSource`; the EF implementations come from the data modules |

## Notes for the lead

- The items feature does nothing when no `IItemStore` is registered (e.g. an end-to-end host without a database), so other areas' tests are unaffected.
- World tests register their doubles through `IWorldTestServices` only inside `ItemTestContent.Use()` (an `AsyncLocal`), so other world tests do not see items at all.
- `EfCharacterStore.SaveStateAsync` writes the inventory in the same `SaveChanges` as the character when `CharacterState.Inventory` is non-null; `Player.CreateSnapshot` only sets it when the inventory changed since the last save.

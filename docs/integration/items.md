# Items and inventory: integration notes

Status: work in progress (draft PR; code lands in follow-up pushes). Area doc: `docs/areas/items.md`.

## Schema versions (need the lead's allocation)

| Component | Version | Module | Changes |
|---|---|---|---|
| World | **2** | `Data/Content/Items/ItemWorldDataModule.cs` | create `item_template` (vmangos columns), `playercreateinfo_item` |
| Characters | **3** | `Data/Characters/Items/ItemCharacterDataModule.cs` | create `item_instance`, `character_inventory` |

These are the next free numbers at the seam (world v1, characters v2). If another area takes
them first, this PR renumbers.

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
| `tests/ArcaneCore.Data.Tests/M6StoreTests.cs` | the M5→current characters upgrade asserts `CharacterDbContext.Schema.CurrentVersion` instead of a literal 2 | any characters module bumps the version |

## Planned (needs the lead's eye)

The seams have no hook for character creation, the character list or loading a character's
data before it enters the map, and the deliverables (starting outfit, equipment on the
character list, inventory load) need all three. Proposal: a generic `ICharacterHooks`
(`World/Characters/CharacterHooks.cs`, default interface methods: `OnCharacterCreatedAsync`,
`GetCharEnumEquipmentAsync`, `OnPlayerLoadingAsync`) implemented by `IWorldFeature`s, with
three one-line call sites in `CharacterHandlers.cs` and an optional equipment argument on
`CharacterPackets.BuildCharEnum`. Other areas (spells, skills, reputation, quests) can load
their per-character data through the same hook.

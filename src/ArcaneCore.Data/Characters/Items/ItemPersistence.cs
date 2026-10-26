using ArcaneCore.Kernel.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ArcaneCore.Data.Characters.Items;

/// <summary>
/// Inventory writes shared by <see cref="EfItemStore"/> and the character save path
/// (EfCharacterStore), staged on the caller's context so they commit with its SaveChanges.
/// </summary>
public static class ItemPersistence
{
    /// <summary>
    /// Stage the replacement of a character's items with <paramref name="snapshot"/>: rows are
    /// updated in place, added or removed (vmangos tracks per-item ITEM_NEW/CHANGED/REMOVED states;
    /// a whole-inventory diff gives the same end state). An item GUID stored under another owner
    /// (a future trade/mail transfer) moves to this character.
    /// </summary>
    public static async Task StageReplaceAsync(CharacterDbContext db, int characterId, InventorySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(snapshot);

        Dictionary<uint, ItemInstanceRow>? trackedItems = null;
        foreach (EntityEntry<ItemInstanceRow> entry in db.ChangeTracker.Entries<ItemInstanceRow>())
        {
            trackedItems ??= new Dictionary<uint, ItemInstanceRow>();
            trackedItems[entry.Entity.Guid] = entry.Entity;
        }

        Dictionary<uint, CharacterInventoryRow>? trackedSlots = null;
        foreach (EntityEntry<CharacterInventoryRow> entry in db.ChangeTracker.Entries<CharacterInventoryRow>())
        {
            trackedSlots ??= new Dictionary<uint, CharacterInventoryRow>();
            trackedSlots[entry.Entity.ItemGuid] = entry.Entity;
        }

        List<ItemInstanceRow> ownedItems = await db.Set<ItemInstanceRow>().AsNoTracking()
            .Where(r => r.OwnerGuid == characterId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CharacterInventoryRow> ownedSlots = await db.Set<CharacterInventoryRow>().AsNoTracking()
            .Where(r => r.Guid == characterId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        int itemCount = snapshot.Items.Count;
        var items = new Dictionary<uint, ItemInstanceRow>(Math.Max(ownedItems.Count, itemCount));
        for (int i = 0; i < ownedItems.Count; i++)
        {
            ItemInstanceRow row = ownedItems[i];
            items[row.Guid] = trackedItems is not null && trackedItems.TryGetValue(row.Guid, out ItemInstanceRow? tracked) ? tracked : row;
        }

        var slots = new Dictionary<uint, CharacterInventoryRow>(Math.Max(ownedSlots.Count, itemCount));
        for (int i = 0; i < ownedSlots.Count; i++)
        {
            CharacterInventoryRow row = ownedSlots[i];
            slots[row.ItemGuid] = trackedSlots is not null && trackedSlots.TryGetValue(row.ItemGuid, out CharacterInventoryRow? tracked) ? tracked : row;
        }

        List<uint>? unseenItemGuids = null;
        List<uint>? unseenSlotGuids = null;
        var keep = new HashSet<uint>(itemCount);
        for (int i = 0; i < itemCount; i++)
        {
            uint guid = snapshot.Items[i].Item.Guid;
            if (!keep.Add(guid))
            {
                throw new InvalidOperationException($"item {guid} appears twice in the inventory of character {characterId}");
            }

            if (!items.ContainsKey(guid))
            {
                if (trackedItems is not null && trackedItems.TryGetValue(guid, out ItemInstanceRow? trackedItem))
                {
                    items[guid] = trackedItem;
                }
                else
                {
                    unseenItemGuids ??= [];
                    unseenItemGuids.Add(guid);
                }
            }

            if (!slots.ContainsKey(guid))
            {
                if (trackedSlots is not null && trackedSlots.TryGetValue(guid, out CharacterInventoryRow? trackedSlot))
                {
                    slots[guid] = trackedSlot;
                }
                else
                {
                    unseenSlotGuids ??= [];
                    unseenSlotGuids.Add(guid);
                }
            }
        }

        bool hasForeignOwnerItems = trackedItems is not null;
        if (unseenItemGuids is { Count: 1 })
        {
            uint singleGuid = unseenItemGuids[0];
            ItemInstanceRow? extra = await db.Set<ItemInstanceRow>().AsNoTracking()
                .FirstOrDefaultAsync(r => r.Guid == singleGuid, cancellationToken).ConfigureAwait(false);
            if (extra is not null)
            {
                items[extra.Guid] = extra;
                hasForeignOwnerItems = true;
            }
        }
        else if (unseenItemGuids is { Count: > 1 })
        {
            foreach (ItemInstanceRow extra in await db.Set<ItemInstanceRow>().AsNoTracking()
                .Where(r => unseenItemGuids.Contains(r.Guid))
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                items[extra.Guid] = extra;
                hasForeignOwnerItems = true;
            }
        }

        if (unseenSlotGuids is { Count: 1 })
        {
            uint singleGuid = unseenSlotGuids[0];
            CharacterInventoryRow? extra = await db.Set<CharacterInventoryRow>().AsNoTracking()
                .FirstOrDefaultAsync(r => r.ItemGuid == singleGuid, cancellationToken).ConfigureAwait(false);
            if (extra is not null)
            {
                slots[extra.ItemGuid] = extra;
            }
        }
        else if (unseenSlotGuids is { Count: > 1 })
        {
            foreach (CharacterInventoryRow extra in await db.Set<CharacterInventoryRow>().AsNoTracking()
                .Where(r => unseenSlotGuids.Contains(r.ItemGuid))
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                slots[extra.ItemGuid] = extra;
            }
        }

        for (int i = 0; i < itemCount; i++)
        {
            InventoryItemData entry = snapshot.Items[i];
            uint guid = entry.Item.Guid;

            if (!items.TryGetValue(guid, out ItemInstanceRow? item))
            {
                item = new ItemInstanceRow { Guid = guid };
                item.CopyFrom(characterId, entry.Item);
                db.Add(item);
            }
            else if (trackedItems is not null && trackedItems.ContainsKey(guid))
            {
                item.CopyFrom(characterId, entry.Item);
            }
            else if (!item.Matches(characterId, entry.Item))
            {
                db.Attach(item);
                item.CopyFrom(characterId, entry.Item);
            }

            if (!slots.TryGetValue(guid, out CharacterInventoryRow? slot))
            {
                slot = new CharacterInventoryRow
                {
                    ItemGuid = guid,
                    Guid = characterId,
                    Bag = entry.ContainerGuid,
                    Slot = entry.Slot,
                    ItemId = entry.Item.Entry,
                };
                db.Add(slot);
            }
            else if (trackedSlots is not null && trackedSlots.ContainsKey(guid))
            {
                slot.Guid = characterId;
                slot.Bag = entry.ContainerGuid;
                slot.Slot = entry.Slot;
                slot.ItemId = entry.Item.Entry;
            }
            else if (slot.Guid != characterId || slot.Bag != entry.ContainerGuid || slot.Slot != entry.Slot || slot.ItemId != entry.Item.Entry)
            {
                db.Attach(slot);
                slot.Guid = characterId;
                slot.Bag = entry.ContainerGuid;
                slot.Slot = entry.Slot;
                slot.ItemId = entry.Item.Entry;
            }
        }

        foreach (ItemInstanceRow row in items.Values)
        {
            if (!keep.Contains(row.Guid))
            {
                db.Remove(row);
            }
        }

        foreach (CharacterInventoryRow row in slots.Values)
        {
            if (!keep.Contains(row.ItemGuid))
            {
                db.Remove(row);
            }
        }

        // The selected ammo rides with the inventory (a null selection means "not carried": leave the stored one alone).
        if (snapshot.AmmoId is { } ammo)
        {
            CharacterItemStateRow? state = await db.Set<CharacterItemStateRow>().AsNoTracking()
                .FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                db.Add(new CharacterItemStateRow { CharacterId = characterId, AmmoId = ammo });
            }
            else if (state.AmmoId != ammo)
            {
                db.Attach(state);
                state.AmmoId = ammo;
            }
        }

        // The generated loot of container items commits with the inventory (same SaveChanges).
        await ItemLootPersistence.StageReplaceForOwnerAsync(db, characterId, snapshot.Items, items.Keys, hasForeignOwnerItems, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Stage the removal of every item a character owns (character deletion).</summary>
    public static async Task StageDeleteAllAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.RemoveRange(await db.Set<ItemInstanceRow>().Where(r => r.OwnerGuid == characterId)
            .ToListAsync(cancellationToken).ConfigureAwait(false));
        db.RemoveRange(await db.Set<CharacterInventoryRow>().Where(r => r.Guid == characterId)
            .ToListAsync(cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>EF Core implementation of <see cref="IItemStore"/>.</summary>
public sealed class EfItemStore(CharacterDbContext db) : IItemStore
{
    public async Task<IReadOnlyList<InventoryItemData>> GetInventoryAsync(int characterId, CancellationToken cancellationToken = default)
    {
        List<CharacterInventoryRow> slots = await db.Set<CharacterInventoryRow>().AsNoTracking()
            .Where(r => r.Guid == characterId).ToListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<uint, ItemInstanceRow> items = await db.Set<ItemInstanceRow>().AsNoTracking()
            .Where(r => r.OwnerGuid == characterId).ToDictionaryAsync(r => r.Guid, cancellationToken).ConfigureAwait(false);

        Dictionary<uint, ItemLootData> loot = await ItemLootPersistence.LoadAsync(db, [.. items.Keys], cancellationToken).ConfigureAwait(false);

        // vmangos _LoadInventory orders by bag, slot: the character's own slots (bag 0) first,
        // so every bag exists before its contents.
        return slots
            .Where(s => items.ContainsKey(s.ItemGuid))
            .OrderBy(s => s.Bag == 0 ? 0 : 1).ThenBy(s => s.Bag).ThenBy(s => s.Slot)
            .Select(s => new InventoryItemData(s.Bag, s.Slot, items[s.ItemGuid].ToData() with { Loot = loot.GetValueOrDefault(s.ItemGuid) }))
            .ToList();
    }

    public async Task SaveInventoryAsync(int characterId, InventorySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await ItemPersistence.StageReplaceAsync(db, characterId, snapshot, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyDictionary<byte, uint>>> GetEquippedEntriesAsync(
        IReadOnlyCollection<int> characterIds, Func<uint, bool>? templateExists = null, CancellationToken cancellationToken = default)
    {
        List<int> ids = [.. characterIds];
        var rows = await db.Set<CharacterInventoryRow>().AsNoTracking()
            .Where(r => ids.Contains(r.Guid) && r.Bag == 0 && r.Slot < 20)
            .OrderBy(r => r.ItemGuid)
            .Select(r => new { r.Guid, r.Slot, r.ItemId, r.ItemGuid })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // character_inventory is keyed by item_guid only, so two rows can share (guid, bag 0, slot).
        // Prefer the row whose item template exists, then lowest item guid, so a stale unloadable row
        // does not hide the equipped item or fail the whole account's character list.

        var result = new Dictionary<int, IReadOnlyDictionary<byte, uint>>();
        foreach (var group in rows.GroupBy(r => r.Guid))
        {
            var slots = new Dictionary<byte, uint>();
            var ordered = group
                .OrderByDescending(r => templateExists?.Invoke(r.ItemId) ?? true)
                .ThenBy(r => r.ItemGuid);
            foreach (var row in ordered)
            {
                slots.TryAdd(row.Slot, row.ItemId);
            }

            result[group.Key] = slots;
        }

        return result;
    }

    public async Task<uint> GetMaxItemGuidAsync(CancellationToken cancellationToken = default)
        => await db.Set<ItemInstanceRow>().MaxAsync(r => (uint?)r.Guid, cancellationToken).ConfigureAwait(false) ?? 0;
}

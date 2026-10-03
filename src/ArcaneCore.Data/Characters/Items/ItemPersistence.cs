using ArcaneCore.Kernel.Items;
using Microsoft.EntityFrameworkCore;

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

        // A List, not an array: C# 14 binds array.Contains to the span overload, which EF cannot translate.
        List<uint> guids = snapshot.Items.Select(i => i.Item.Guid).ToList();
        Dictionary<uint, ItemInstanceRow> items = await db.Set<ItemInstanceRow>()
            .Where(r => r.OwnerGuid == characterId || guids.Contains(r.Guid))
            .ToDictionaryAsync(r => r.Guid, cancellationToken).ConfigureAwait(false);
        Dictionary<uint, CharacterInventoryRow> slots = await db.Set<CharacterInventoryRow>()
            .Where(r => r.Guid == characterId || guids.Contains(r.ItemGuid))
            .ToDictionaryAsync(r => r.ItemGuid, cancellationToken).ConfigureAwait(false);

        var keep = new HashSet<uint>();
        foreach (InventoryItemData entry in snapshot.Items)
        {
            uint guid = entry.Item.Guid;
            if (!keep.Add(guid))
            {
                throw new InvalidOperationException($"item {guid} appears twice in the inventory of character {characterId}");
            }

            if (!items.TryGetValue(guid, out ItemInstanceRow? item))
            {
                item = new ItemInstanceRow { Guid = guid };
                db.Add(item);
            }

            item.CopyFrom(characterId, entry.Item);

            if (!slots.TryGetValue(guid, out CharacterInventoryRow? slot))
            {
                slot = new CharacterInventoryRow { ItemGuid = guid };
                db.Add(slot);
            }

            slot.Guid = characterId;
            slot.Bag = entry.ContainerGuid;
            slot.Slot = entry.Slot;
            slot.ItemId = entry.Item.Entry;
        }

        db.RemoveRange(items.Values.Where(r => !keep.Contains(r.Guid)));
        db.RemoveRange(slots.Values.Where(r => !keep.Contains(r.ItemGuid)));

        // The selected ammo rides with the inventory (a null selection means "not carried": leave the stored one alone).
        if (snapshot.AmmoId is { } ammo)
        {
            CharacterItemStateRow? state = await db.Set<CharacterItemStateRow>()
                .FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                db.Add(new CharacterItemStateRow { CharacterId = characterId, AmmoId = ammo });
            }
            else
            {
                state.AmmoId = ammo;
            }
        }
        // The generated loot of container items commits with the inventory (same SaveChanges).
        await ItemLootPersistence.StageReplaceAsync(db, snapshot.Items.Select(i => i.Item), [.. items.Keys], cancellationToken).ConfigureAwait(false);
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
        IReadOnlyCollection<int> characterIds, CancellationToken cancellationToken = default)
    {
        List<int> ids = [.. characterIds];
        var rows = await db.Set<CharacterInventoryRow>().AsNoTracking()
            .Where(r => ids.Contains(r.Guid) && r.Bag == 0 && r.Slot < 20)
            .Select(r => new { r.Guid, r.Slot, r.ItemId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.GroupBy(r => r.Guid).ToDictionary(
            g => g.Key,
            g => (IReadOnlyDictionary<byte, uint>)g.ToDictionary(r => r.Slot, r => r.ItemId));
    }

    public async Task<uint> GetMaxItemGuidAsync(CancellationToken cancellationToken = default)
        => await db.Set<ItemInstanceRow>().MaxAsync(r => (uint?)r.Guid, cancellationToken).ConfigureAwait(false) ?? 0;
}

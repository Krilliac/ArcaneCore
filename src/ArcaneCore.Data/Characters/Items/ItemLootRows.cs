using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArcaneCore.Data.Characters.Items;

/// <summary>
/// The generated loot of one container item (vmangos <c>item_instance.generated_loot</c> and the money row of <c>item_loot</c>): a row exists while the
/// item holds generated loot. A sidecar table instead of a column of <c>item_instance</c>, so no ALTER is needed (MariaDB DDL is not transactional).
/// </summary>
public sealed class ItemLootStateRow
{
    public uint ItemGuid { get; set; }

    /// <summary>Money left in copper.</summary>
    public uint Gold { get; set; }

    internal static void Configure(EntityTypeBuilder<ItemLootStateRow> entity)
    {
        entity.ToTable("item_loot_state");
        entity.HasKey(r => r.ItemGuid);
        entity.Property(r => r.ItemGuid).HasColumnName("item_guid").ValueGeneratedNever();
        entity.Property(r => r.Gold).HasColumnName("gold");
    }
}

/// <summary>
/// One stack of that loot (vmangos <c>item_loot</c>: guid, item_id, amount). The key is (item, slot), not (item, item id) as in vmangos: two rolls
/// of the same item (a reference processed twice) must both survive.
/// </summary>
public sealed class ItemLootRow
{
    public uint ItemGuid { get; set; }

    public byte Slot { get; set; }

    public uint ItemId { get; set; }

    public uint Amount { get; set; }

    public bool Quest { get; set; }

    internal static void Configure(EntityTypeBuilder<ItemLootRow> entity)
    {
        entity.ToTable("item_loot");
        entity.HasKey(r => new { r.ItemGuid, r.Slot });
        entity.Property(r => r.ItemGuid).HasColumnName("item_guid").ValueGeneratedNever();
        entity.Property(r => r.Slot).HasColumnName("slot").ValueGeneratedNever();
        entity.Property(r => r.ItemId).HasColumnName("item_id");
        entity.Property(r => r.Amount).HasColumnName("amount");
        entity.Property(r => r.Quest).HasColumnName("quest");
    }
}

/// <summary>
/// The orphan sweep (vmangos CharacterDatabaseCleaner::CleanOrphanedItemData, <c>RemoveOrphanedRows("item_loot", "guid", "item_instance", "guid")</c>):
/// one set-based DELETE per table removes the loot of every item guid without an <c>item_instance</c> row. Such rows were left by builds before the
/// escrow paths deleted loot with their items (docs/integration/wave3-followups-20261004.md F8); loot of owned and escrowed (owner 0) items stays.
/// </summary>
public sealed class EfItemLootMaintenance(CharacterDbContext db) : IItemLootMaintenance
{
    public async Task<int> DeleteOrphanedLootAsync(CancellationToken cancellationToken = default)
    {
        IQueryable<ItemInstanceRow> items = db.Set<ItemInstanceRow>();
        return await CharacterWrites.InTransactionAsync(db, async () =>
        {
            int stacks = await db.Set<ItemLootRow>().Where(r => !items.Any(i => i.Guid == r.ItemGuid))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            int states = await db.Set<ItemLootStateRow>().Where(r => !items.Any(i => i.Guid == r.ItemGuid))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            return stacks + states;
        }, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Reads and stages the loot rows of container items; used by the inventory store so loot and inventory commit in one SaveChanges.</summary>
public static class ItemLootPersistence
{
    /// <summary>The loot of each of <paramref name="itemGuids"/> that has any (items without generated loot are absent).</summary>
    public static async Task<Dictionary<uint, ItemLootData>> LoadAsync(CharacterDbContext db, IReadOnlyCollection<uint> itemGuids, CancellationToken cancellationToken)
    {
        if (itemGuids.Count == 0)
        {
            return [];
        }

        List<uint> guids = [.. itemGuids];
        List<ItemLootStateRow> states = await db.Set<ItemLootStateRow>().AsNoTracking().Where(r => guids.Contains(r.ItemGuid))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<ItemLootRow> rows = await db.Set<ItemLootRow>().AsNoTracking().Where(r => guids.Contains(r.ItemGuid))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        ILookup<uint, ItemLootRow> byItem = rows.ToLookup(r => r.ItemGuid);
        return states.ToDictionary(
            s => s.ItemGuid,
            s => new ItemLootData(s.Gold, [.. byItem[s.ItemGuid].OrderBy(r => r.Slot).Select(r => new ItemLootEntry(r.Slot, r.ItemId, r.Amount, r.Quest))]));
    }

    internal static async Task StageReplaceForOwnerAsync(
        CharacterDbContext db,
        int characterId,
        IReadOnlyList<InventoryItemData> snapshotItems,
        Dictionary<uint, ItemInstanceRow>.KeyCollection knownItemGuids,
        bool hasForeignOwnerItems,
        CancellationToken cancellationToken)
    {
        Dictionary<uint, ItemLootData>? wanted = null;
        for (int i = 0; i < snapshotItems.Count; i++)
        {
            ItemInstanceData item = snapshotItems[i].Item;
            if (item.Loot is { } loot)
            {
                wanted ??= new Dictionary<uint, ItemLootData>();
                wanted[item.Guid] = loot;
            }
        }

        if (wanted is null)
        {
            if (knownItemGuids.Count == 0)
            {
                return;
            }

            if (!hasForeignOwnerItems)
            {
                IQueryable<uint> ownerGuids = db.Set<ItemInstanceRow>()
                    .Where(i => i.OwnerGuid == characterId)
                    .Select(i => i.Guid);
                List<ItemLootStateRow> staleStates = await db.Set<ItemLootStateRow>()
                    .Where(r => ownerGuids.Contains(r.ItemGuid))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                List<ItemLootRow> staleRows = await db.Set<ItemLootRow>()
                    .Where(r => ownerGuids.Contains(r.ItemGuid))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                if (staleStates.Count > 0)
                {
                    db.RemoveRange(staleStates);
                }

                if (staleRows.Count > 0)
                {
                    db.RemoveRange(staleRows);
                }

                return;
            }
        }

        var guids = new List<uint>(knownItemGuids.Count + (wanted?.Count ?? 0));
        var seen = new HashSet<uint>(knownItemGuids.Count + (wanted?.Count ?? 0));
        foreach (uint guid in knownItemGuids)
        {
            if (seen.Add(guid))
            {
                guids.Add(guid);
            }
        }

        if (wanted is not null)
        {
            foreach (uint guid in wanted.Keys)
            {
                if (seen.Add(guid))
                {
                    guids.Add(guid);
                }
            }
        }

        await StageReplaceCoreAsync(db, wanted, guids, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stage the loot of the items of <paramref name="snapshotItems"/> and drop the loot of every other guid in <paramref name="knownItemGuids"/>
    /// (items that left the inventory or whose loot was taken completely), on the caller's context.
    /// </summary>
    public static async Task StageReplaceAsync(
        CharacterDbContext db, IEnumerable<ItemInstanceData> snapshotItems, IReadOnlyCollection<uint> knownItemGuids, CancellationToken cancellationToken)
    {
        Dictionary<uint, ItemLootData>? wanted = null;
        foreach (ItemInstanceData item in snapshotItems)
        {
            if (item.Loot is { } loot)
            {
                wanted ??= new Dictionary<uint, ItemLootData>();
                wanted[item.Guid] = loot;
            }
        }

        if (wanted is null && knownItemGuids.Count == 0)
        {
            return;
        }

        var guids = new List<uint>(knownItemGuids.Count + (wanted?.Count ?? 0));
        var seen = new HashSet<uint>(knownItemGuids.Count + (wanted?.Count ?? 0));
        foreach (uint guid in knownItemGuids)
        {
            if (seen.Add(guid))
            {
                guids.Add(guid);
            }
        }

        if (wanted is not null)
        {
            foreach (uint guid in wanted.Keys)
            {
                if (seen.Add(guid))
                {
                    guids.Add(guid);
                }
            }
        }

        await StageReplaceCoreAsync(db, wanted, guids, cancellationToken).ConfigureAwait(false);
    }

    private static async Task StageReplaceCoreAsync(
        CharacterDbContext db,
        Dictionary<uint, ItemLootData>? wanted,
        List<uint> guids,
        CancellationToken cancellationToken)
    {
        if (guids.Count == 0)
        {
            return;
        }

        Dictionary<uint, ItemLootStateRow> states;
        List<ItemLootRow> existing;
        if (guids.Count == 1)
        {
            uint singleGuid = guids[0];
            states = await db.Set<ItemLootStateRow>().Where(r => r.ItemGuid == singleGuid)
                .ToDictionaryAsync(r => r.ItemGuid, cancellationToken).ConfigureAwait(false);
            existing = await db.Set<ItemLootRow>().Where(r => r.ItemGuid == singleGuid)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            states = await db.Set<ItemLootStateRow>().Where(r => guids.Contains(r.ItemGuid))
                .ToDictionaryAsync(r => r.ItemGuid, cancellationToken).ConfigureAwait(false);
            existing = await db.Set<ItemLootRow>().Where(r => guids.Contains(r.ItemGuid))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (ItemLootStateRow stale in states.Values)
        {
            if (wanted is null || !wanted.ContainsKey(stale.ItemGuid))
            {
                db.Remove(stale);
            }
        }

        for (int i = 0; i < existing.Count; i++)
        {
            ItemLootRow row = existing[i];
            if (wanted is null || !wanted.ContainsKey(row.ItemGuid))
            {
                db.Remove(row);
            }
        }

        if (wanted is null)
        {
            return;
        }

        foreach ((uint guid, ItemLootData loot) in wanted)
        {
            if (!states.TryGetValue(guid, out ItemLootStateRow? state))
            {
                db.Add(new ItemLootStateRow { ItemGuid = guid, Gold = loot.Gold });
            }
            else
            {
                state.Gold = loot.Gold;
            }

            Dictionary<byte, ItemLootRow>? have = null;
            for (int i = 0; i < existing.Count; i++)
            {
                ItemLootRow row = existing[i];
                if (row.ItemGuid == guid)
                {
                    have ??= new Dictionary<byte, ItemLootRow>();
                    have[row.Slot] = row;
                }
            }

            foreach (ItemLootEntry entry in loot.Items)
            {
                if (have is not null && have.Remove(entry.Slot, out ItemLootRow? row))
                {
                    (row.ItemId, row.Amount, row.Quest) = (entry.ItemId, entry.Count, entry.IsQuest);
                }
                else
                {
                    db.Add(new ItemLootRow { ItemGuid = guid, Slot = entry.Slot, ItemId = entry.ItemId, Amount = entry.Count, Quest = entry.IsQuest });
                }
            }

            if (have is { Count: > 0 })
            {
                db.RemoveRange(have.Values);
            }
        }
    }
}
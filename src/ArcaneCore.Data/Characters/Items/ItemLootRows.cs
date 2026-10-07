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
        int stacks = await db.Set<ItemLootRow>().Where(r => !items.Any(i => i.Guid == r.ItemGuid))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        int states = await db.Set<ItemLootStateRow>().Where(r => !items.Any(i => i.Guid == r.ItemGuid))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return stacks + states;
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

    /// <summary>
    /// Stage the loot of the items of <paramref name="snapshotItems"/> and drop the loot of every other guid in <paramref name="knownItemGuids"/>
    /// (items that left the inventory or whose loot was taken completely), on the caller's context.
    /// </summary>
    public static async Task StageReplaceAsync(
        CharacterDbContext db, IEnumerable<ItemInstanceData> snapshotItems, IReadOnlyCollection<uint> knownItemGuids, CancellationToken cancellationToken)
    {
        Dictionary<uint, ItemLootData> wanted = snapshotItems.Where(i => i.Loot is not null).ToDictionary(i => i.Guid, i => i.Loot!);
        List<uint> guids = [.. knownItemGuids.Union(wanted.Keys)];
        if (guids.Count == 0)
        {
            return;
        }

        Dictionary<uint, ItemLootStateRow> states = await db.Set<ItemLootStateRow>().Where(r => guids.Contains(r.ItemGuid))
            .ToDictionaryAsync(r => r.ItemGuid, cancellationToken).ConfigureAwait(false);
        List<ItemLootRow> existing = await db.Set<ItemLootRow>().Where(r => guids.Contains(r.ItemGuid)).ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (ItemLootStateRow stale in states.Values.Where(s => !wanted.ContainsKey(s.ItemGuid)))
        {
            db.Remove(stale);
        }

        db.RemoveRange(existing.Where(r => !wanted.ContainsKey(r.ItemGuid)));
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

            Dictionary<byte, ItemLootRow> have = existing.Where(r => r.ItemGuid == guid).ToDictionary(r => r.Slot);
            foreach (ItemLootEntry entry in loot.Items)
            {
                if (have.Remove(entry.Slot, out ItemLootRow? row))
                {
                    (row.ItemId, row.Amount, row.Quest) = (entry.ItemId, entry.Count, entry.IsQuest);
                }
                else
                {
                    db.Add(new ItemLootRow { ItemGuid = guid, Slot = entry.Slot, ItemId = entry.ItemId, Amount = entry.Count, Quest = entry.IsQuest });
                }
            }

            db.RemoveRange(have.Values);
        }
    }
}
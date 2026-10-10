using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Economy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Economy;

/// <summary>An <c>ahbot_custody</c> row (MaNGOS Zero custody ledger table, reduced to the bot's movements).</summary>
public sealed class AhBotCustodyRow
{
    public string IdemKey { get; set; } = string.Empty;
    public byte Kind { get; set; }
    public byte Role { get; set; }
    public byte State { get; set; }
    public uint HouseId { get; set; }
    public uint AuctionId { get; set; }
    public uint ItemGuid { get; set; }
    public uint ItemEntry { get; set; }
    public uint ItemCount { get; set; }
    public uint Amount { get; set; }
    public long CreatedTime { get; set; }
    public long ResolvedTime { get; set; }
}

/// <summary>
/// Characters schema 50 (wave 18): the auction house bot custody ledger, so a reservation whose outcome was unknown at shutdown is
/// rechecked against the economy operation ledger after a restart, and the daily budgets survive it.
/// </summary>
public sealed class AuctionBotCustodyDataModule : IDataModule
{
    public const int Version = 50;
    public const string Table = "ahbot_custody";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<AhBotCustodyRow>(e =>
        {
            e.ToTable(Table);
            e.HasKey(r => r.IdemKey);
            e.Property(r => r.IdemKey).HasColumnName("idem_key").HasMaxLength(64);
            e.Property(r => r.Kind).HasColumnName("kind");
            e.Property(r => r.Role).HasColumnName("role");
            e.Property(r => r.State).HasColumnName("state");
            e.Property(r => r.HouseId).HasColumnName("house_id");
            e.Property(r => r.AuctionId).HasColumnName("auction_id");
            e.Property(r => r.ItemGuid).HasColumnName("item_guid");
            e.Property(r => r.ItemEntry).HasColumnName("item_entry");
            e.Property(r => r.ItemCount).HasColumnName("item_count");
            e.Property(r => r.Amount).HasColumnName("amount");
            e.Property(r => r.CreatedTime).HasColumnName("created_time");
            e.Property(r => r.ResolvedTime).HasColumnName("resolved_time");
            e.HasIndex(r => r.State);
        });

    public void AddServices(IServiceCollection services) => services.AddScoped<IAuctionBotCustodyStore, EfAuctionBotCustodyStore>();
}

public sealed class EfAuctionBotCustodyStore(CharacterDbContext db) : IAuctionBotCustodyStore
{
    public async Task<IReadOnlyList<AuctionBotCustodyRecord>> LoadAsync(CancellationToken cancellationToken = default)
        => [.. (await db.Set<AhBotCustodyRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new AuctionBotCustodyRecord(r.IdemKey, r.Kind, r.Role, r.State, r.HouseId, r.AuctionId, r.ItemGuid, r.ItemEntry,
                r.ItemCount, r.Amount, r.CreatedTime, r.ResolvedTime))];

    public async Task SaveAsync(IReadOnlyCollection<AuctionBotCustodyRecord> rows, IReadOnlyCollection<string> deletedKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(deletedKeys);
        string[] keys = [.. rows.Select(r => r.IdemKey).Concat(deletedKeys).Distinct(StringComparer.Ordinal)];
        Dictionary<string, AhBotCustodyRow> existing = await db.Set<AhBotCustodyRow>().Where(r => keys.Contains(r.IdemKey))
            .ToDictionaryAsync(r => r.IdemKey, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        foreach (string key in deletedKeys)
        {
            if (existing.Remove(key, out AhBotCustodyRow? gone))
            {
                db.Remove(gone);
            }
        }

        foreach (AuctionBotCustodyRecord r in rows)
        {
            if (!existing.TryGetValue(r.IdemKey, out AhBotCustodyRow? row))
            {
                db.Add(row = new AhBotCustodyRow { IdemKey = r.IdemKey });
                existing[r.IdemKey] = row;
            }

            row.Kind = r.Kind;
            row.Role = r.Role;
            row.State = r.State;
            row.HouseId = r.HouseId;
            row.AuctionId = r.AuctionId;
            row.ItemGuid = r.ItemGuid;
            row.ItemEntry = r.ItemEntry;
            row.ItemCount = r.ItemCount;
            row.Amount = r.Amount;
            row.CreatedTime = r.CreatedTime;
            row.ResolvedTime = r.ResolvedTime;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }
}

/// <summary>An <c>ahbot_items</c> row (cMaNGOS characters <c>ahbot_items</c>; kept in the world database here, with the other content).</summary>
public sealed class AhBotItemRow
{
    public uint Item { get; set; }
    public uint Value { get; set; }
    public uint AddChance { get; set; }
    public uint MinAmount { get; set; }
    public uint MaxAmount { get; set; }
}

/// <summary>World schema 48 (wave 18): the auction house bot's per-item overrides (cMaNGOS <c>ahbot_items</c>). Empty by default.</summary>
public sealed class AuctionBotItemWorldDataModule : IDataModule
{
    public const int Version = 48;
    public const string Table = "ahbot_items";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<AhBotItemRow>(e =>
        {
            e.ToTable(Table);
            e.HasKey(r => r.Item);
            e.Property(r => r.Item).HasColumnName("item").ValueGeneratedNever();
            e.Property(r => r.Value).HasColumnName("value");
            e.Property(r => r.AddChance).HasColumnName("add_chance");
            e.Property(r => r.MinAmount).HasColumnName("min_amount");
            e.Property(r => r.MaxAmount).HasColumnName("max_amount");
        });

    public void AddServices(IServiceCollection services) => services.AddScoped<IAuctionBotItemStore, EfAuctionBotItemStore>();
}

public sealed class EfAuctionBotItemStore(IDbContextFactory<WorldDbContext> factory) : IAuctionBotItemStore
{
    public async Task<IReadOnlyList<AuctionBotItemOverride>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using WorldDbContext db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return [.. (await db.Set<AhBotItemRow>().AsNoTracking().OrderBy(r => r.Item).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new AuctionBotItemOverride(r.Item, r.Value, r.AddChance, r.MinAmount, r.MaxAmount))];
    }

    public async Task SaveAsync(AuctionBotItemOverride value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        await using WorldDbContext db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        AhBotItemRow? row = await db.Set<AhBotItemRow>().FirstOrDefaultAsync(r => r.Item == value.Item, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            db.Add(row = new AhBotItemRow { Item = value.Item });
        }

        row.Value = value.Value;
        row.AddChance = value.AddChance;
        row.MinAmount = value.MinAmount;
        row.MaxAmount = value.MaxAmount;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(uint item, CancellationToken cancellationToken = default)
    {
        await using WorldDbContext db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Set<AhBotItemRow>().Where(r => r.Item == item).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
    }
}

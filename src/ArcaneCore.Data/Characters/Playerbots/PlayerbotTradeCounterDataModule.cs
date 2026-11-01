using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Playerbots;

public sealed class PlayerbotTradeCounterRow
{
    public byte Kind { get; set; }
    public Guid BotId { get; set; }
    public ulong Key { get; set; }
    public long Stamp { get; set; }
    public long Amount { get; set; }
}

/// <summary>Characters schema 71: the playerbot trade desk's hourly trade counts, daily outflow totals and the received-gift counts, so they survive a restart.</summary>
public sealed class PlayerbotTradeCounterDataModule : IDataModule
{
    public const int Version = 71;
    public const string Table = "playerbot_trade_counter";

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlayerbotTradeCounterRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(row => new { row.Kind, row.BotId, row.Key, row.Stamp });
            entity.Property(row => row.Kind).ValueGeneratedNever();
            entity.Property(row => row.BotId).ValueGeneratedNever();
            entity.Property(row => row.Key).ValueGeneratedNever();
            entity.Property(row => row.Stamp).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services)
        => services.AddScoped<IPlayerbotTradeCounterStore, EfPlayerbotTradeCounterStore>();
}

public sealed class EfPlayerbotTradeCounterStore(CharacterDbContext db, IRealmLeaseFenceSource? leaseFences = null) : IPlayerbotTradeCounterStore
{
    public async Task<IReadOnlyList<PlayerbotTradeCounter>> LoadAllAsync(CancellationToken cancellationToken = default)
        => (await db.Set<PlayerbotTradeCounterRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(row => new PlayerbotTradeCounter((PlayerbotTradeCounterKind)row.Kind, row.BotId, row.Key, row.Stamp, row.Amount)).ToArray();

    public async Task ReplaceAllAsync(IReadOnlyList<PlayerbotTradeCounter> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        LeaseFencedWrite.RequireHeld(leaseFences, RealmAuthorityScopes.Maintenance); // a realm bot ledger: the maintenance lease holder writes it (writer-lock design section 2b; decision Q2)
        if (rows.Count > PlayerbotTradeCounter.MaxRows
            || rows.Any(row => row is null || !Enum.IsDefined(row.Kind) || row.Stamp < 0 || row.Amount <= 0))
            throw new ArgumentException("Invalid or unbounded playerbot trade counter snapshot.", nameof(rows));
        if (rows.Select(row => (row.Kind, row.BotId, row.Key, row.Stamp)).Distinct().Count() != rows.Count)
            throw new ArgumentException("Duplicate playerbot trade counter rows.", nameof(rows));

        // Same shape as the trade record store: track the inserts first so the write lock covers only the delete and the inserts.
        db.Set<PlayerbotTradeCounterRow>().AddRange(rows.Select(row => new PlayerbotTradeCounterRow
        {
            Kind = (byte)row.Kind, BotId = row.BotId, Key = row.Key, Stamp = row.Stamp, Amount = row.Amount,
        }));
        db.ChangeTracker.DetectChanges();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await db.Set<PlayerbotTradeCounterRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { db.ChangeTracker.Clear(); }
    }
}

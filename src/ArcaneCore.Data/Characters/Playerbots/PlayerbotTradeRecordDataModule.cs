using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Playerbots;

public sealed class PlayerbotTradeRecordRow
{
    public Guid BotId { get; set; }
    public int CharacterId { get; set; }
    public ulong OtherGuid { get; set; }
    public int FairDeals { get; set; }
    public int Scams { get; set; }
    public long LastScamUnixMs { get; set; }
    public long LastNinjaUnixMs { get; set; }
    public int LastGiftDay { get; set; }
}

/// <summary>Characters schema 66: each managed playerbot's trade ledger (fair deals, scams, ninja loot, last gift) per partner.</summary>
public sealed class PlayerbotTradeRecordDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 66;
    public const string Table = "playerbot_trade_record";

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlayerbotTradeRecordRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(row => new { row.BotId, row.OtherGuid });
            entity.Property(row => row.BotId).ValueGeneratedNever();
            entity.Property(row => row.OtherGuid).ValueGeneratedNever();
            entity.HasIndex(row => row.CharacterId);
        });
    }

    public void AddServices(IServiceCollection services)
        => services.AddScoped<IPlayerbotTradeRecordStore, EfPlayerbotTradeRecordStore>();

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => db.Set<PlayerbotTradeRecordRow>().Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken);
}

public sealed class EfPlayerbotTradeRecordStore(CharacterDbContext db) : IPlayerbotTradeRecordStore
{
    internal CharacterDbContext Db => db;

    public async Task<IReadOnlyList<PlayerbotTradeRecord>> LoadAllAsync(CancellationToken cancellationToken = default)
        => (await db.Set<PlayerbotTradeRecordRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(row => new PlayerbotTradeRecord(row.BotId, row.CharacterId, row.OtherGuid, row.FairDeals, row.Scams,
                row.LastScamUnixMs, row.LastNinjaUnixMs, row.LastGiftDay)).ToArray();

    public async Task ReplaceAsync(Guid botId, IReadOnlyList<PlayerbotTradeRecord> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > PlayerbotTradeRecord.MaxRowsPerBot
            || rows.Any(row => row is null || row.BotId != botId || botId == Guid.Empty || row.CharacterId <= 0
                || row.FairDeals < 0 || row.Scams < 0 || row.LastScamUnixMs < 0 || row.LastNinjaUnixMs < 0 || row.LastGiftDay < 0))
            throw new ArgumentException("Invalid or unbounded playerbot trade record snapshot.", nameof(rows));
        if (rows.Select(row => row.OtherGuid).Distinct().Count() != rows.Count)
            throw new ArgumentException("Duplicate playerbot trade record targets.", nameof(rows));

        // Same shape as the relation store: track the inserts first so the write lock covers only the delete and the inserts.
        db.Set<PlayerbotTradeRecordRow>().AddRange(rows.Select(row => new PlayerbotTradeRecordRow
        {
            BotId = row.BotId, CharacterId = row.CharacterId, OtherGuid = row.OtherGuid, FairDeals = row.FairDeals, Scams = row.Scams,
            LastScamUnixMs = row.LastScamUnixMs, LastNinjaUnixMs = row.LastNinjaUnixMs, LastGiftDay = row.LastGiftDay,
        }));
        db.ChangeTracker.DetectChanges();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await db.Set<PlayerbotTradeRecordRow>().Where(row => row.BotId == botId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { db.ChangeTracker.Clear(); }
    }
}

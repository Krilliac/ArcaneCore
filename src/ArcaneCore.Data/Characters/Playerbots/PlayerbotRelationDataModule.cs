using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Playerbots;

public sealed class PlayerbotRelationRow
{
    public Guid BotId { get; set; }
    public int CharacterId { get; set; }
    public ulong OtherGuid { get; set; }
    public bool OtherIsBot { get; set; }
    public short Affinity { get; set; }
    public int Familiarity { get; set; }
    public short TrustPermille { get; set; }
    public int Flags { get; set; }
    public long LastSeenUnixMs { get; set; }
    public string LastTopic { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

/// <summary>Characters schema 64: each managed playerbot's bounded memory of the characters it has dealt with.</summary>
public sealed class PlayerbotRelationDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 64;
    public const string Table = "playerbot_relation";

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlayerbotRelationRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(row => new { row.BotId, row.OtherGuid });
            entity.Property(row => row.BotId).ValueGeneratedNever();
            entity.Property(row => row.OtherGuid).ValueGeneratedNever();
            entity.Property(row => row.LastTopic).HasMaxLength(PlayerbotRelation.MaxTopicLength);
            entity.Property(row => row.Summary).HasMaxLength(PlayerbotRelation.MaxSummaryLength);
            entity.HasIndex(row => row.CharacterId);
        });
    }

    public void AddServices(IServiceCollection services)
        => services.AddScoped<IPlayerbotRelationStore, EfPlayerbotRelationStore>();

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => db.Set<PlayerbotRelationRow>().Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken);
}

public sealed class EfPlayerbotRelationStore(CharacterDbContext db) : IPlayerbotRelationStore
{
    public async Task<IReadOnlyList<PlayerbotRelation>> LoadAllAsync(CancellationToken cancellationToken = default)
        => (await db.Set<PlayerbotRelationRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(row => new PlayerbotRelation(row.BotId, row.CharacterId, row.OtherGuid, row.OtherIsBot, row.Affinity,
                row.Familiarity, row.TrustPermille, (PlayerbotRelationFlags)row.Flags, row.LastSeenUnixMs,
                row.LastTopic, row.Summary)).ToArray();

    public async Task ReplaceAsync(Guid botId, IReadOnlyList<PlayerbotRelation> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > PlayerbotRelation.MaxRowsPerBot
            || rows.Any(row => row is null || row.BotId != botId || botId == Guid.Empty || row.CharacterId <= 0
                || row.Affinity is < -100 or > 100 || row.Familiarity < 0 || row.TrustPermille is < 0 or > 1000
                || row.LastSeenUnixMs < 0 || row.LastTopic is null || row.Summary is null
                || row.LastTopic.Length > PlayerbotRelation.MaxTopicLength || row.Summary.Length > PlayerbotRelation.MaxSummaryLength))
            throw new ArgumentException("Invalid or unbounded playerbot relation snapshot.", nameof(rows));
        if (rows.Select(row => row.OtherGuid).Distinct().Count() != rows.Count)
            throw new ArgumentException("Duplicate playerbot relation targets.", nameof(rows));

        // Same shape as the set-aside store: track the inserts first so the write lock covers only the delete and the inserts.
        db.Set<PlayerbotRelationRow>().AddRange(rows.Select(row => new PlayerbotRelationRow
        {
            BotId = row.BotId, CharacterId = row.CharacterId, OtherGuid = row.OtherGuid, OtherIsBot = row.OtherIsBot,
            Affinity = row.Affinity, Familiarity = row.Familiarity, TrustPermille = row.TrustPermille, Flags = (int)row.Flags,
            LastSeenUnixMs = row.LastSeenUnixMs, LastTopic = row.LastTopic, Summary = row.Summary,
        }));
        db.ChangeTracker.DetectChanges();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await db.Set<PlayerbotRelationRow>().Where(row => row.BotId == botId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { db.ChangeTracker.Clear(); }
    }
}

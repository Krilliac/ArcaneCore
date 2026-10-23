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
    public short WarAffinity { get; set; }
    public int WarFlags { get; set; }
    public double FamiliarityExact { get; set; }
    public double AffinityExact { get; set; }
    public double WarAffinityExact { get; set; }
    public long DecayAnchorUnixMs { get; set; }
    public long WarDecayAnchorUnixMs { get; set; }
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
                row.LastTopic, row.Summary, row.WarAffinity, (PlayerbotRelationFlags)row.WarFlags,
                row.FamiliarityExact, row.AffinityExact, row.WarAffinityExact, row.DecayAnchorUnixMs, row.WarDecayAnchorUnixMs)).ToArray();

    // The bits a stored row may carry: Flags never holds WarSeeded, WarFlags only Rival and WarSeeded.
    private const PlayerbotRelationFlags KnownFlags = PlayerbotRelationFlags.PrefersTank | PlayerbotRelationFlags.PrefersHealer | PlayerbotRelationFlags.PrefersDamage
        | PlayerbotRelationFlags.WantsInvites | PlayerbotRelationFlags.NoWhispers | PlayerbotRelationFlags.OwesFavour | PlayerbotRelationFlags.Grouped
        | PlayerbotRelationFlags.OtherFaction | PlayerbotRelationFlags.Rival | PlayerbotRelationFlags.WarOnly
        | PlayerbotRelationFlags.Interacted;

    private const PlayerbotRelationFlags KnownWarFlags = PlayerbotRelationFlags.Rival | PlayerbotRelationFlags.WarSeeded;

    public async Task ReplaceAsync(Guid botId, IReadOnlyList<PlayerbotRelation> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > PlayerbotRelation.MaxRowsPerBot
            || rows.Any(row => row is null || row.BotId != botId || botId == Guid.Empty || row.CharacterId <= 0
                || row.Affinity is < -100 or > 100 || row.WarAffinity is < -100 or > 100 || row.Familiarity < 0 || row.TrustPermille is < 0 or > 1000
                || row.LastSeenUnixMs < 0 || row.DecayAnchorUnixMs < 0 || row.WarDecayAnchorUnixMs < 0
                || double.IsInfinity(row.FamiliarityExact) || double.IsInfinity(row.AffinityExact) || double.IsInfinity(row.WarAffinityExact)
                || !double.IsNaN(row.FamiliarityExact) && row.FamiliarityExact < 0 || !double.IsNaN(row.AffinityExact) && Math.Abs(row.AffinityExact) > 100
                || !double.IsNaN(row.WarAffinityExact) && Math.Abs(row.WarAffinityExact) > 100 || row.LastTopic is null || row.Summary is null
                || row.LastTopic.Length > PlayerbotRelation.MaxTopicLength || row.Summary.Length > PlayerbotRelation.MaxSummaryLength
                || (row.Flags & ~KnownFlags) != 0 || (row.WarFlags & ~KnownWarFlags) != 0
                // An exact value is as of its anchor: without one it can only be the whole value (what a load takes the whole value to be).
                || row.DecayAnchorUnixMs == 0 && (!double.IsNaN(row.FamiliarityExact) && row.FamiliarityExact != row.Familiarity
                    || !double.IsNaN(row.AffinityExact) && row.AffinityExact != row.Affinity)
                || row.WarDecayAnchorUnixMs == 0 && !double.IsNaN(row.WarAffinityExact) && row.WarAffinityExact != row.WarAffinity))
            throw new ArgumentException("Invalid or unbounded playerbot relation snapshot.", nameof(rows));
        if (rows.Select(row => row.OtherGuid).Distinct().Count() != rows.Count)
            throw new ArgumentException("Duplicate playerbot relation targets.", nameof(rows));

        // Same shape as the set-aside store: track the inserts first so the write lock covers only the delete and the inserts.
        db.Set<PlayerbotRelationRow>().AddRange(rows.Select(row => new PlayerbotRelationRow
        {
            BotId = row.BotId, CharacterId = row.CharacterId, OtherGuid = row.OtherGuid, OtherIsBot = row.OtherIsBot,
            Affinity = row.Affinity, Familiarity = row.Familiarity, TrustPermille = row.TrustPermille, Flags = (int)row.Flags,
            WarAffinity = row.WarAffinity, WarFlags = (int)row.WarFlags,
            // Without the exact values (a row built from whole numbers) the whole ones are exact, as of the last-seen time.
            FamiliarityExact = double.IsNaN(row.FamiliarityExact) ? row.Familiarity : row.FamiliarityExact,
            AffinityExact = double.IsNaN(row.AffinityExact) ? row.Affinity : row.AffinityExact,
            WarAffinityExact = double.IsNaN(row.WarAffinityExact) ? row.WarAffinity : row.WarAffinityExact,
            DecayAnchorUnixMs = row.DecayAnchorUnixMs > 0 ? row.DecayAnchorUnixMs : row.LastSeenUnixMs,
            WarDecayAnchorUnixMs = row.WarDecayAnchorUnixMs > 0 ? row.WarDecayAnchorUnixMs : row.LastSeenUnixMs,
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

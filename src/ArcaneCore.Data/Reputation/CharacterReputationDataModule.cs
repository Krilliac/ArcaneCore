using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Reputation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Reputation;

/// <summary>One stored faction state (vmangos character_reputation).</summary>
public sealed class CharacterReputationEntity
{
    public int CharacterId { get; set; }

    public uint Faction { get; set; }

    public int Standing { get; set; }

    public uint Flags { get; set; }
}

/// <summary>The watched reputation-list slot (vmangos characters.watchedFaction, kept in this module's own table).</summary>
public sealed class CharacterReputationWatchEntity
{
    public int CharacterId { get; set; }

    public int WatchedFaction { get; set; }
}

/// <summary>
/// Characters schema v7 (reputation, docs/integration/reputation.md): per-character faction
/// standing/flags and the watched faction. Both tables are new; nothing existing changes.
/// </summary>
public sealed class CharacterReputationDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 7;
    public const string FactionTable = "character_reputation";
    public const string WatchTable = "character_reputation_watch";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(FactionTable),
        new CreateTableChange(WatchTable),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<CharacterReputationEntity>(entity =>
        {
            entity.ToTable(FactionTable);
            entity.HasKey(r => new { r.CharacterId, r.Faction });
            entity.Property(r => r.CharacterId).HasColumnName("guid");
            entity.Property(r => r.Faction).HasColumnName("faction");
            entity.Property(r => r.Standing).HasColumnName("standing");
            entity.Property(r => r.Flags).HasColumnName("flags");
        });

        modelBuilder.Entity<CharacterReputationWatchEntity>(entity =>
        {
            entity.ToTable(WatchTable);
            entity.HasKey(r => r.CharacterId);
            entity.Property(r => r.CharacterId).HasColumnName("guid").ValueGeneratedNever();
            entity.Property(r => r.WatchedFaction).HasColumnName("watched_faction");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ICharacterReputationStore, EfCharacterReputationStore>();

    /// <summary>Faction standings and the watched faction (vmangos DeleteFromDB: character_reputation; docs/integration/character-delete.md).</summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Set<CharacterReputationEntity>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterReputationWatchEntity>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>EF Core implementation of <see cref="ICharacterReputationStore"/>.</summary>
public sealed class EfCharacterReputationStore(CharacterDbContext db) : ICharacterReputationStore
{
    public async Task<CharacterReputationData> LoadAsync(int characterId, CancellationToken cancellationToken = default)
    {
        List<CharacterReputationRow> rows = await db.Set<CharacterReputationEntity>().AsNoTracking()
            .Where(r => r.CharacterId == characterId)
            .OrderBy(r => r.Faction)
            .Select(r => new CharacterReputationRow(r.CharacterId, r.Faction, r.Standing, r.Flags))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        CharacterReputationWatchEntity? watch = await db.Set<CharacterReputationWatchEntity>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        return new CharacterReputationData(rows, watch?.WatchedFaction ?? -1);
    }

    public async Task SaveFactionsAsync(int characterId, IReadOnlyList<CharacterReputationRow> upserts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        if (upserts.Count == 0 || !await CharacterExistsAsync(characterId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // Last write per faction wins; a List (not an array) keeps Contains translatable by EF.
        Dictionary<uint, CharacterReputationRow> latest = [];
        foreach (CharacterReputationRow row in upserts)
        {
            if (row.CharacterId != characterId)
            {
                throw new ArgumentException("every row must belong to the saved character", nameof(upserts));
            }

            latest[row.Faction] = row;
        }

        List<uint> touched = [.. latest.Keys];
        DbSet<CharacterReputationEntity> set = db.Set<CharacterReputationEntity>();
        Dictionary<uint, CharacterReputationEntity> existing = await set
            .Where(r => r.CharacterId == characterId && touched.Contains(r.Faction))
            .ToDictionaryAsync(r => r.Faction, cancellationToken).ConfigureAwait(false);
        foreach (CharacterReputationRow row in latest.Values)
        {
            if (existing.TryGetValue(row.Faction, out CharacterReputationEntity? entity))
            {
                entity.Standing = row.Standing;
                entity.Flags = row.Flags;
            }
            else
            {
                set.Add(new CharacterReputationEntity { CharacterId = characterId, Faction = row.Faction, Standing = row.Standing, Flags = row.Flags });
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public async Task SaveWatchedFactionAsync(int characterId, int watchedFaction, CancellationToken cancellationToken = default)
    {
        if (!await CharacterExistsAsync(characterId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        DbSet<CharacterReputationWatchEntity> set = db.Set<CharacterReputationWatchEntity>();
        CharacterReputationWatchEntity? entity = await set.FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            set.Add(new CharacterReputationWatchEntity { CharacterId = characterId, WatchedFaction = watchedFaction });
        }
        else
        {
            entity.WatchedFaction = watchedFaction;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public async Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await db.Set<CharacterReputationEntity>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterReputationWatchEntity>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> CharacterExistsAsync(int characterId, CancellationToken cancellationToken)
        => db.Characters.AnyAsync(c => c.Id == characterId, cancellationToken);
}

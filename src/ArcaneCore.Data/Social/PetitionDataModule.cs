using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Social;

/// <summary>A guild charter petition (vmangos petition: owner_guid, petition_guid, charter_guid, name; the team is the owner's).</summary>
public sealed class PetitionRow
{
    public int Id { get; set; }
    public int OwnerId { get; set; }
    public int CharterItemId { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>A signature on a petition (vmangos petition_sign: petition_guid, player_guid, player_account).</summary>
public sealed class PetitionSignRow
{
    public int PetitionId { get; set; }
    public int PlayerId { get; set; }
    public int AccountId { get; set; }
}

/// <summary>
/// Guild charter petitions in the characters database: <c>petition</c> and <c>petition_sign</c>.
/// Both tables are new, so the step is additive and re-runnable (the bootstrapper adopts an existing
/// table that has exactly the model's columns: MariaDB DDL commits implicitly, so a half-applied
/// upgrade is a real state). There is no database uniqueness on the name: collations differ per
/// engine, so uniqueness is checked in memory on the world thread. The one unique index is the owner
/// (a character owns at most one petition, vmangos PetitionsHandler.cpp:70-71).
/// </summary>
public sealed class PetitionDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>
    /// The characters schema version of this module: the next free number in this tree. The one
    /// constant the integrator renumbers (docs/integration/seams.md "Schema versions"); tests refer to it.
    /// </summary>
    public const int Version = 16;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange("petition"),
        new CreateTableChange("petition_sign"),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PetitionRow>(entity =>
        {
            entity.ToTable("petition");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.HasIndex(r => r.OwnerId).IsUnique();
            entity.Property(r => r.Name).HasMaxLength(24).IsRequired();
        });

        modelBuilder.Entity<PetitionSignRow>(entity =>
        {
            entity.ToTable("petition_sign");
            entity.HasKey(r => new { r.PetitionId, r.PlayerId });
            entity.HasIndex(r => r.PlayerId);
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IPetitionStore, EfPetitionStore>();

    /// <summary>
    /// vmangos Player::DeleteFromDB → RemovePetitionsAndSigns (Player.cpp:4353-4354, 17796-17806): the
    /// character's own petition with every signature on it, and its signatures on other petitions.
    /// </summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        List<int> owned = await db.Set<PetitionRow>().Where(p => p.OwnerId == characterId).Select(p => p.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<PetitionSignRow>().Where(s => owned.Contains(s.PetitionId) || s.PlayerId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<PetitionRow>().Where(p => p.OwnerId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>EF Core implementation of <see cref="IPetitionStore"/> over the characters database.</summary>
public sealed class EfPetitionStore(CharacterDbContext db) : IPetitionStore
{
    public async Task<IReadOnlyList<PetitionData>> GetPetitionsAsync(CancellationToken cancellationToken = default)
    {
        List<PetitionRow> petitions = await db.Set<PetitionRow>().AsNoTracking().OrderBy(p => p.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<PetitionSignRow> signatures = await db.Set<PetitionSignRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        ILookup<int, PetitionSignRow> byPetition = signatures.ToLookup(s => s.PetitionId);
        return
        [
            .. petitions.Select(p => new PetitionData(
                p.Id, p.OwnerId, p.CharterItemId, p.Name,
                [.. byPetition[p.Id].OrderBy(s => s.PlayerId).Select(s => new PetitionSignatureData(s.PlayerId, s.AccountId))])),
        ];
    }

    public async Task SavePetitionAsync(PetitionData petition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(petition);
        PetitionRow? row = await db.Set<PetitionRow>().FirstOrDefaultAsync(p => p.Id == petition.Id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new PetitionRow { Id = petition.Id };
            db.Set<PetitionRow>().Add(row);
        }

        row.OwnerId = petition.OwnerId;
        row.CharterItemId = petition.CharterItemId;
        row.Name = petition.Name;

        Dictionary<int, PetitionSignRow> stored = await db.Set<PetitionSignRow>()
            .Where(s => s.PetitionId == petition.Id)
            .ToDictionaryAsync(s => s.PlayerId, cancellationToken)
            .ConfigureAwait(false);
        foreach (PetitionSignatureData signature in petition.Signatures)
        {
            if (!stored.Remove(signature.PlayerId, out PetitionSignRow? signRow))
            {
                signRow = new PetitionSignRow { PetitionId = petition.Id, PlayerId = signature.PlayerId };
                db.Set<PetitionSignRow>().Add(signRow);
            }

            signRow.AccountId = signature.AccountId;
        }

        db.Set<PetitionSignRow>().RemoveRange(stored.Values);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public async Task DeletePetitionAsync(int petitionId, CancellationToken cancellationToken = default)
    {
        StageDelete(await FindForDeleteAsync(petitionId, cancellationToken).ConfigureAwait(false));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public async Task CompletePetitionAsync(GuildData guild, int petitionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guild);
        try
        {
            await EfSocialStore.StageGuildAsync(db, guild, cancellationToken).ConfigureAwait(false);
            StageDelete(await FindForDeleteAsync(petitionId, cancellationToken).ConfigureAwait(false));

            // One SaveChanges is one transaction on every engine: a member who joined another guild
            // meanwhile violates the guild_member unique character index and nothing is stored.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // After a failed save the tracked entities are stale (and, on PostgreSQL, the scope's
            // connection must not be reused for more work); the caller discards the scope.
            db.ChangeTracker.Clear();
        }
    }

    public Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default)
        => PurgeAsync(characterId, cancellationToken);

    private async Task PurgeAsync(int characterId, CancellationToken cancellationToken)
    {
        // Conditional on the id having no character row, per statement, like the social purge.
        List<int> owned = await db.Set<PetitionRow>()
            .Where(p => p.OwnerId == characterId && !db.Characters.Any(c => c.Id == characterId))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<PetitionSignRow>()
            .Where(s => (owned.Contains(s.PetitionId) || s.PlayerId == characterId) && !db.Characters.Any(c => c.Id == characterId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<PetitionRow>()
            .Where(p => p.OwnerId == characterId && !db.Characters.Any(c => c.Id == characterId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<(PetitionRow? Petition, List<PetitionSignRow> Signatures)> FindForDeleteAsync(int petitionId, CancellationToken cancellationToken)
    {
        PetitionRow? petition = await db.Set<PetitionRow>().FirstOrDefaultAsync(p => p.Id == petitionId, cancellationToken).ConfigureAwait(false);
        List<PetitionSignRow> signatures = await db.Set<PetitionSignRow>().Where(s => s.PetitionId == petitionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return (petition, signatures);
    }

    private void StageDelete((PetitionRow? Petition, List<PetitionSignRow> Signatures) found)
    {
        db.Set<PetitionSignRow>().RemoveRange(found.Signatures);
        if (found.Petition is not null)
        {
            db.Set<PetitionRow>().Remove(found.Petition);
        }
    }
}

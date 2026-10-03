using System.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Skills;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Skills;

/// <summary>One stored skill (vmangos character_skills: guid, skill, value, max).</summary>
public sealed class CharacterSkillEntity
{
    public int CharacterId { get; set; }

    public uint Skill { get; set; }

    public uint Value { get; set; }

    public uint Max { get; set; }
}

/// <summary>One remembered weapon skill value (vmangos character_forgotten_skills: guid, skill, value).</summary>
public sealed class CharacterForgottenSkillEntity
{
    public int CharacterId { get; set; }

    public uint Skill { get; set; }

    public uint Value { get; set; }
}

/// <summary>
/// Character skills (docs/areas/skills.md): <c>character_skills</c> and <c>character_forgotten_skills</c>, both
/// new tables keyed by the character and the skill (vmangos characters.sql:199-204, 344-350).
/// <para>Schema allocation: <see cref="Version"/> is the next contiguous characters version at this branch's
/// base (after loot state v13); the lead renumbers it at merge time (docs/integration/seams.md, "Schema
/// versions"). It is the only place the number is written.</para>
/// </summary>
public sealed class CharacterSkillsDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The single version constant the lead renumbers.</summary>
    public const int Version = 14;

    public const string SkillTable = "character_skills";
    public const string ForgottenTable = "character_forgotten_skills";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(SkillTable),
        new CreateTableChange(ForgottenTable),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<CharacterSkillEntity>(entity =>
        {
            entity.ToTable(SkillTable);
            entity.HasKey(r => new { r.CharacterId, r.Skill });
            entity.Property(r => r.CharacterId).HasColumnName("guid").ValueGeneratedNever();
            entity.Property(r => r.Skill).HasColumnName("skill").ValueGeneratedNever();
            entity.Property(r => r.Value).HasColumnName("value");
            entity.Property(r => r.Max).HasColumnName("max");
        });

        modelBuilder.Entity<CharacterForgottenSkillEntity>(entity =>
        {
            entity.ToTable(ForgottenTable);
            entity.HasKey(r => new { r.CharacterId, r.Skill });
            entity.Property(r => r.CharacterId).HasColumnName("guid").ValueGeneratedNever();
            entity.Property(r => r.Skill).HasColumnName("skill").ValueGeneratedNever();
            entity.Property(r => r.Value).HasColumnName("value");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ICharacterSkillStore, EfCharacterSkillStore>();

    /// <summary>Skills and forgotten values go with the character (vmangos DeleteFromDB: character_skills and character_forgotten_skills).</summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Set<CharacterSkillEntity>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterForgottenSkillEntity>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>EF Core implementation of <see cref="ICharacterSkillStore"/>.</summary>
public sealed class EfCharacterSkillStore(CharacterDbContext db) : ICharacterSkillStore
{
    public async Task<CharacterSkillSnapshot> LoadAsync(int characterId, CancellationToken cancellationToken = default)
    {
        List<CharacterSkillRow> skills = (await db.Set<CharacterSkillEntity>().AsNoTracking()
                .Where(r => r.CharacterId == characterId)
                .OrderBy(r => r.Skill)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new CharacterSkillRow(checked((ushort)r.Skill), checked((ushort)r.Value), checked((ushort)r.Max)))
            .ToList();
        List<ForgottenSkillRow> forgotten = (await db.Set<CharacterForgottenSkillEntity>().AsNoTracking()
                .Where(r => r.CharacterId == characterId)
                .OrderBy(r => r.Skill)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new ForgottenSkillRow(checked((ushort)r.Skill), checked((ushort)r.Value)))
            .ToList();
        return new CharacterSkillSnapshot(skills, forgotten);
    }

    public async Task<bool> ReplaceSnapshotAsync(int characterId, CharacterSkillSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (db.ChangeTracker.Entries().Any() || db.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Skill snapshots require a dedicated context without tracked caller state or a caller transaction.");
        }

        // Last row per skill wins, so a repeated skill in one snapshot cannot violate the key.
        Dictionary<ushort, CharacterSkillRow> skills = [];
        foreach (CharacterSkillRow row in snapshot.Skills)
        {
            skills[row.Skill] = row;
        }

        Dictionary<ushort, ForgottenSkillRow> forgotten = [];
        foreach (ForgottenSkillRow row in snapshot.Forgotten)
        {
            forgotten[row.Skill] = row;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await db.Characters.AnyAsync(c => c.Id == characterId, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await db.Set<CharacterSkillEntity>().Where(r => r.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Set<CharacterForgottenSkillEntity>().Where(r => r.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            db.Set<CharacterSkillEntity>().AddRange(skills.Values.Select(r => new CharacterSkillEntity
            {
                CharacterId = characterId,
                Skill = r.Skill,
                Value = r.Value,
                Max = r.Max,
            }));
            db.Set<CharacterForgottenSkillEntity>().AddRange(forgotten.Values.Select(r => new CharacterForgottenSkillEntity
            {
                CharacterId = characterId,
                Skill = r.Skill,
                Value = r.Value,
            }));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }
}

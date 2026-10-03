using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Life;

/// <summary>
/// Health, power, experience, the recent-death window and the ghost flag of a character
/// (vmangos <c>characters.health, power1-5, xp, death_expire_time</c> and the ghost flag;
/// Player.cpp:16470-16476, 14915-14917). One row per character; an absent row means the
/// character was never saved with a life (fresh values apply).
/// </summary>
public sealed class CharacterVitalsRow
{
    public int CharacterId { get; set; }

    public uint Health { get; set; }

    public uint Power1 { get; set; }

    public uint Power2 { get; set; }

    public uint Power3 { get; set; }

    public uint Power4 { get; set; }

    public uint Power5 { get; set; }

    public uint Xp { get; set; }

    /// <summary>Unix seconds (vmangos m_deathExpireTime is a time_t).</summary>
    public long DeathExpireTime { get; set; }

    public bool IsGhost { get; set; }
}

/// <summary>
/// A released body (vmangos <c>corpse</c>: map, position, orientation, time, type). One row per
/// character; present only while the character is a ghost with a body.
/// </summary>
public sealed class CharacterCorpseRow
{
    public int CharacterId { get; set; }

    public uint MapId { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public float Orientation { get; set; }

    /// <summary>Unix seconds (vmangos Corpse::m_time).</summary>
    public long GhostTime { get; set; }

    /// <summary>CorpseType: 1 = resurrectable PvE, 2 = resurrectable PvP.</summary>
    public byte Type { get; set; }
}

/// <summary>
/// The <c>character_vitals</c> and <c>character_corpse</c> tables of the characters database.
/// <see cref="Version"/> is the single constant the integrator renumbers (next free characters
/// version at this branch's base: loot state was 13; docs/integration/seams.md, "Schema versions").
/// </summary>
public sealed class CharacterLifeDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The single place the schema version is set.</summary>
    public const int Version = 15;

    public const string VitalsTable = "character_vitals";

    public const string CorpseTable = "character_corpse";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(VitalsTable), new CreateTableChange(CorpseTable)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<CharacterVitalsRow>(entity =>
        {
            entity.ToTable(VitalsTable);
            entity.HasKey(r => r.CharacterId);
            entity.Property(r => r.CharacterId).ValueGeneratedNever();
        });

        modelBuilder.Entity<CharacterCorpseRow>(entity =>
        {
            entity.ToTable(CorpseTable);
            entity.HasKey(r => r.CharacterId);
            entity.Property(r => r.CharacterId).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ICharacterLifeStore, EfCharacterLifeStore>();

    /// <summary>Both rows of the character (vmangos Player::DeleteFromDB clears the corpse and the character row's vitals).</summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Set<CharacterVitalsRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterCorpseRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Staging of a <see cref="CharacterLife"/> inside the state-save transaction.</summary>
internal static class CharacterLifePersistence
{
    /// <summary>Make the stored life of <paramref name="characterId"/> equal <paramref name="life"/>; the caller saves and commits.</summary>
    public static async Task StageAsync(CharacterDbContext db, int characterId, CharacterLife life, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(life);
        if (life.Powers.Count != 5)
        {
            throw new ArgumentException("a life carries exactly five power values", nameof(life));
        }

        CharacterVitalsRow? vitals = await db.Set<CharacterVitalsRow>()
            .FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        if (vitals is null)
        {
            vitals = new CharacterVitalsRow { CharacterId = characterId };
            db.Set<CharacterVitalsRow>().Add(vitals);
        }

        vitals.Health = life.Health;
        vitals.Power1 = life.Powers[0];
        vitals.Power2 = life.Powers[1];
        vitals.Power3 = life.Powers[2];
        vitals.Power4 = life.Powers[3];
        vitals.Power5 = life.Powers[4];
        vitals.Xp = life.Xp;
        vitals.DeathExpireTime = life.DeathExpireUnix;
        vitals.IsGhost = life.IsGhost;

        CharacterCorpseRow? row = await db.Set<CharacterCorpseRow>()
            .FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        if (life.Corpse is not { } corpse)
        {
            if (row is not null)
            {
                db.Set<CharacterCorpseRow>().Remove(row);
            }

            return;
        }

        if (row is null)
        {
            row = new CharacterCorpseRow { CharacterId = characterId };
            db.Set<CharacterCorpseRow>().Add(row);
        }

        row.MapId = corpse.MapId;
        row.X = corpse.X;
        row.Y = corpse.Y;
        row.Z = corpse.Z;
        row.Orientation = corpse.Orientation;
        row.GhostTime = corpse.GhostTimeUnix;
        row.Type = corpse.Type;
    }
}

/// <summary>EF Core implementation of <see cref="ICharacterLifeStore"/>.</summary>
public sealed class EfCharacterLifeStore(CharacterDbContext db) : ICharacterLifeStore
{
    public async Task<CharacterLife?> LoadAsync(int characterId, CancellationToken cancellationToken = default)
    {
        CharacterVitalsRow? vitals = await db.Set<CharacterVitalsRow>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        if (vitals is null)
        {
            return null;
        }

        CharacterCorpseRow? corpse = await db.Set<CharacterCorpseRow>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        return new CharacterLife(
            vitals.Health,
            [vitals.Power1, vitals.Power2, vitals.Power3, vitals.Power4, vitals.Power5],
            vitals.Xp,
            vitals.DeathExpireTime,
            vitals.IsGhost,
            corpse is null ? null : new CorpseSnapshot(corpse.MapId, corpse.X, corpse.Y, corpse.Z, corpse.Orientation, corpse.GhostTime, corpse.Type));
    }
}

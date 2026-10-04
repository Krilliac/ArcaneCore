using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Life;

/// <summary>
/// What survives a logout of the rested-experience state (vmangos <c>characters.rest_bonus</c>, <c>logout_time</c>,
/// <c>is_logout_resting</c>; Player::SaveToDB, PlayerSave.cpp:240-243): the pool, the moment it was written and whether
/// the character was resting (in an inn or a capital city) then, which decides the offline accrual rate on the next login.
/// </summary>
/// <param name="RestBonus">The rested pool in experience points (a float, as vmangos m_rest_bonus).</param>
/// <param name="LogoutUnixSeconds">The Unix second of the write; offline accrual runs from it.</param>
/// <param name="WasResting">PLAYER_FLAGS_RESTING was set when the state was captured.</param>
public readonly record struct CharacterRestState(float RestBonus, long LogoutUnixSeconds, bool WasResting);

/// <summary>Read and write side of <see cref="CharacterRestState"/> (the rest feature queues the writes; see docs/areas/rested-xp.md).</summary>
public interface ICharacterRestStore
{
    /// <summary>The stored state, or null when the character was never saved with one (a fresh character: an empty pool).</summary>
    Task<CharacterRestState?> LoadAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Replace the stored state. A character that no longer exists is ignored (deleted meanwhile).</summary>
    Task SaveAsync(int characterId, CharacterRestState state, CancellationToken cancellationToken = default);

    /// <summary>Remove the stored state (a reused character id must not inherit it).</summary>
    Task DeleteAsync(int characterId, CancellationToken cancellationToken = default);
}

/// <summary>One <c>character_rest</c> row (one per character).</summary>
public sealed class CharacterRestRow
{
    public int CharacterId { get; set; }

    public float RestBonus { get; set; }

    /// <summary>Unix seconds of the write (vmangos <c>logout_time</c>).</summary>
    public long LogoutTime { get; set; }

    public bool IsLogoutResting { get; set; }
}

/// <summary>
/// The <c>character_rest</c> table of the characters database: the rested-experience pool, the logout time and the
/// resting flag, in a table of their own so the shared <c>characters</c> row and its state-save transaction stay
/// untouched (the rest feature writes it through its own retained write queue).
/// <see cref="Version"/> is the single constant the integrator renumbers (next free characters version at this
/// branch's base: game event status was 25; docs/integration/seams.md, "Schema versions").
/// </summary>
public sealed class CharacterRestDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The single place the schema version is set.</summary>
    public const int Version = 27;

    public const string Table = "character_rest";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<CharacterRestRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.CharacterId);
            entity.Property(r => r.CharacterId).HasColumnName("guid").ValueGeneratedNever();
            entity.Property(r => r.RestBonus).HasColumnName("rest_bonus");
            entity.Property(r => r.LogoutTime).HasColumnName("logout_time");
            entity.Property(r => r.IsLogoutResting).HasColumnName("is_logout_resting");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ICharacterRestStore, EfCharacterRestStore>();

    /// <summary>The row of the character (vmangos DeleteFromDB clears the character's rest columns with the character).</summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Set<CharacterRestRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>EF Core implementation of <see cref="ICharacterRestStore"/>.</summary>
public sealed class EfCharacterRestStore(CharacterDbContext db) : ICharacterRestStore
{
    public async Task<CharacterRestState?> LoadAsync(int characterId, CancellationToken cancellationToken = default)
    {
        CharacterRestRow? row = await db.Set<CharacterRestRow>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : new CharacterRestState(row.RestBonus, row.LogoutTime, row.IsLogoutResting);
    }

    public async Task SaveAsync(int characterId, CharacterRestState state, CancellationToken cancellationToken = default)
    {
        if (!await db.Characters.AnyAsync(c => c.Id == characterId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        DbSet<CharacterRestRow> set = db.Set<CharacterRestRow>();
        CharacterRestRow? row = await set.FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new CharacterRestRow { CharacterId = characterId };
            set.Add(row);
        }

        row.RestBonus = state.RestBonus;
        row.LogoutTime = state.LogoutUnixSeconds;
        row.IsLogoutResting = state.WasResting;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public async Task DeleteAsync(int characterId, CancellationToken cancellationToken = default)
        => await db.Set<CharacterRestRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
}

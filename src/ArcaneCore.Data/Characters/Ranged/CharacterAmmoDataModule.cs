using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Ranged;

/// <summary>
/// A character's selected ammunition (vmangos <c>characters.ammo_id</c>, sql/characters.sql:94,
/// kept in its own table so the character row stays untouched): one row per character that has
/// ammo set.
/// </summary>
public sealed class CharacterAmmoRow
{
    public int CharacterId { get; set; }

    /// <summary>The item entry in PLAYER_AMMO_ID.</summary>
    public uint ItemId { get; set; }
}

/// <summary>Persistence of PLAYER_AMMO_ID across logout (characters database).</summary>
public interface ICharacterAmmoStore
{
    /// <summary>The saved ammo item entry of a character (0 = none).</summary>
    Task<uint> GetAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Save the ammo item entry of a character; 0 removes the row.</summary>
    Task SetAsync(int characterId, uint itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forget the saved ammo of a deleted character. Conditional on the id having no
    /// <c>characters</c> row, so a late or retried removal never wipes a recreated character.
    /// </summary>
    Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The <c>character_ammo</c> table: the hunter lane's characters schema step (provisional number
/// after durable loot's 13; the integrator renumbers this one constant).
/// </summary>
public sealed class CharacterAmmoDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The single place the schema version is set.</summary>
    public const int Version = 14;

    public const string Table = "character_ammo";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<CharacterAmmoRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.CharacterId);
            entity.Property(r => r.CharacterId).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ICharacterAmmoStore, EfCharacterAmmoStore>();

    /// <summary>The character's ammo row (vmangos Player::DeleteFromDB removes the character row that held ammo_id).</summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Set<CharacterAmmoRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>EF Core implementation of <see cref="ICharacterAmmoStore"/>.</summary>
public sealed class EfCharacterAmmoStore(CharacterDbContext db) : ICharacterAmmoStore
{
    public async Task<uint> GetAsync(int characterId, CancellationToken cancellationToken = default)
        => await db.Set<CharacterAmmoRow>().AsNoTracking()
            .Where(r => r.CharacterId == characterId)
            .Select(r => r.ItemId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public async Task SetAsync(int characterId, uint itemId, CancellationToken cancellationToken = default)
    {
        try
        {
            CharacterAmmoRow? row = await db.Set<CharacterAmmoRow>().FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
            if (itemId == 0)
            {
                if (row is not null)
                {
                    db.Set<CharacterAmmoRow>().Remove(row);
                }
            }
            else if (row is null)
            {
                db.Set<CharacterAmmoRow>().Add(new CharacterAmmoRow { CharacterId = characterId, ItemId = itemId });
            }
            else
            {
                row.ItemId = itemId;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
        => await db.Set<CharacterAmmoRow>()
            .Where(r => r.CharacterId == characterId && !db.Characters.Any(c => c.Id == characterId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
}

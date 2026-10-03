using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Items;

/// <summary>
/// The per-character item state that lives outside the item rows: the selected ammo
/// (PLAYER_AMMO_ID). vmangos keeps it in a <c>characters</c> column (Player.cpp:14703, 16501); a
/// separate table lets items persist it without editing the character record (a storage
/// difference only, the behaviour is the same).
/// </summary>
public sealed class CharacterItemStateRow
{
    public int CharacterId { get; set; }

    public uint AmmoId { get; set; }
}

/// <summary>
/// The <c>character_item_state</c> table (characters database). One named version constant (built as 14,
/// renumbered to 16 by the wave-2 integrator; the hunter lane's duplicate ammo table was removed in its favour).
/// </summary>
public sealed class CharacterItemStateDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The single place the schema version is set.</summary>
    public const int Version = 16;

    public const string Table = "character_item_state";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<CharacterItemStateRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.CharacterId);
            entity.Property(r => r.CharacterId).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IItemStateStore, EfItemStateStore>();

    /// <summary>The stored item state of a deleted character (vmangos keeps ammo in the characters row, which DeleteFromDB removes).</summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Set<CharacterItemStateRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>EF Core implementation of <see cref="IItemStateStore"/>.</summary>
public sealed class EfItemStateStore(CharacterDbContext db) : IItemStateStore
{
    public async Task<uint> GetAmmoAsync(int characterId, CancellationToken cancellationToken = default)
        => await db.Set<CharacterItemStateRow>().AsNoTracking()
            .Where(r => r.CharacterId == characterId)
            .Select(r => r.AmmoId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
}

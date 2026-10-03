using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.WorldState;

/// <summary>One stored explored-zones row (one per character; the vmangos text form of the 64 words).</summary>
public sealed class ExploredZonesEntity
{
    public int CharacterId { get; set; }

    public string Zones { get; set; } = string.Empty;
}

/// <summary>
/// Characters schema module for explored zones (docs/areas/world-state.md): one row per character in
/// <c>character_explored_zones</c>. The version is a single constant the integrator renumbers
/// (next free after the loot state step at 13).
/// </summary>
public sealed class ExploredZonesDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 18;
    public const string Table = "character_explored_zones";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ExploredZonesEntity>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.CharacterId);
            entity.Property(r => r.CharacterId).HasColumnName("guid").ValueGeneratedNever();
            entity.Property(r => r.Zones).HasColumnName("explored_zones").IsRequired();
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IExploredZonesStore, EfExploredZonesStore>();

    /// <summary>The explored-zones row (vmangos DeleteFromDB clears the character's data fields with the character).</summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Set<ExploredZonesEntity>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>EF Core implementation of <see cref="IExploredZonesStore"/>.</summary>
public sealed class EfExploredZonesStore(CharacterDbContext db) : IExploredZonesStore
{
    public async Task<uint[]?> LoadAsync(int characterId, CancellationToken cancellationToken = default)
    {
        string? text = await db.Set<ExploredZonesEntity>().AsNoTracking().Where(r => r.CharacterId == characterId)
            .Select(r => r.Zones).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return text is null ? null : ExploredZonesText.Parse(text);
    }

    public async Task SaveAsync(int characterId, IReadOnlyList<uint> words, CancellationToken cancellationToken = default)
    {
        string text = ExploredZonesText.Format(words);
        if (!await db.Characters.AnyAsync(c => c.Id == characterId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        DbSet<ExploredZonesEntity> set = db.Set<ExploredZonesEntity>();
        ExploredZonesEntity? row = await set.FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            set.Add(new ExploredZonesEntity { CharacterId = characterId, Zones = text });
        }
        else
        {
            row.Zones = text;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public async Task DeleteAsync(int characterId, CancellationToken cancellationToken = default)
        => await db.Set<ExploredZonesEntity>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
}

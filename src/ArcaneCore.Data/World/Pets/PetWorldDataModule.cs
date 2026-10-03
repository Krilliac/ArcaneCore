using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Pets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Pets;

/// <summary><c>pet_levelstats</c> (vmangos column names): the stats of a summoned pet by creature entry and level.</summary>
public sealed class PetLevelStatsRow
{
    public uint Entry { get; set; }

    public byte Level { get; set; }

    public uint Health { get; set; }

    public uint Mana { get; set; }

    public uint Armor { get; set; }

    public float DmgMin { get; set; }

    public float DmgMax { get; set; }

    public uint Strength { get; set; }

    public uint Agility { get; set; }

    public uint Stamina { get; set; }

    public uint Intellect { get; set; }

    public uint Spirit { get; set; }
}

/// <summary><c>petcreateinfo_spell</c> (vmangos): up to four spells a freshly summoned pet knows; 0 ends the list.</summary>
public sealed class PetCreateSpellRow
{
    public uint Entry { get; set; }

    public uint Spell1 { get; set; }

    public uint Spell2 { get; set; }

    public uint Spell3 { get; set; }

    public uint Spell4 { get; set; }
}

/// <summary>
/// The pet world-schema step (<see cref="IDataModule"/>): <c>pet_levelstats</c> and
/// <c>petcreateinfo_spell</c> (docs/integration/pets.md). <see cref="Version"/> is World 16 (built as 11,
/// renumbered by the wave-2 integrator in merge order; nothing else depends on the number).
/// </summary>
public sealed class PetWorldDataModule : IDataModule
{
    /// <summary>The world schema version of the pet tables.</summary>
    public const int Version = 16;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange("pet_levelstats"),
        new CreateTableChange("petcreateinfo_spell"),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PetLevelStatsRow>(entity =>
        {
            entity.ToTable("pet_levelstats");
            entity.HasKey(r => new { r.Entry, r.Level });
        });

        modelBuilder.Entity<PetCreateSpellRow>(entity =>
        {
            entity.ToTable("petcreateinfo_spell");
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IPetDataStore, EfPetDataStore>();
}

/// <summary>Reads the pet tables into an immutable <see cref="PetContent"/>.</summary>
public sealed class EfPetDataStore(WorldDbContext db) : IPetDataStore
{
    public async Task<PetContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<PetLevelStatsRow> stats = await db.Set<PetLevelStatsRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<PetCreateSpellRow> spells = await db.Set<PetCreateSpellRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PetContent(
            stats.Select(r => new PetLevelStats(r.Entry, r.Level, r.Health, r.Mana, r.Armor, r.DmgMin, r.DmgMax, r.Strength, r.Agility, r.Stamina, r.Intellect, r.Spirit)),
            spells.Select(r => new PetCreateSpells(r.Entry, [r.Spell1, r.Spell2, r.Spell3, r.Spell4])));
    }
}

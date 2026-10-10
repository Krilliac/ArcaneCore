using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.WorldState;

/// <summary>One element's row of the vmangos elemental invasion saved variables.</summary>
public sealed class ElementalInvasionRow
{
    public int Element { get; set; }
    public int Stage { get; set; }
    public int Kills { get; set; }
}

/// <summary>Characters schema 49: the elemental invasion stage and kill count per element, so a restart keeps the rifts' stage.</summary>
public sealed class ElementalInvasionDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 49;
    public const string Table = "world_elemental_invasion";
    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<ElementalInvasionRow>(e =>
        {
            e.ToTable(Table);
            e.HasKey(r => r.Element);
            e.Property(r => r.Element).HasColumnName("element").ValueGeneratedNever();
            e.Property(r => r.Stage).HasColumnName("stage");
            e.Property(r => r.Kills).HasColumnName("kills");
        });

    public void AddServices(IServiceCollection services) => services.AddScoped<IElementalInvasionStore, EfElementalInvasionStore>();
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class EfElementalInvasionStore(CharacterDbContext db) : IElementalInvasionStore
{
    public async Task<IReadOnlyList<ElementalInvasionState>> LoadAsync(CancellationToken cancellationToken = default)
        => (await db.Set<ElementalInvasionRow>().AsNoTracking().ToListAsync(cancellationToken))
            .Select(r => new ElementalInvasionState(r.Element, r.Stage, r.Kills)).ToList();

    public async Task SaveAsync(ElementalInvasionState state, CancellationToken cancellationToken = default)
    {
        ElementalInvasionRow? row = await db.Set<ElementalInvasionRow>().FirstOrDefaultAsync(r => r.Element == state.Element, cancellationToken);
        if (row is null) db.Add(row = new ElementalInvasionRow { Element = state.Element });
        row.Stage = state.Stage;
        row.Kills = state.Kills;
        await db.SaveChangesAsync(cancellationToken);
    }
}

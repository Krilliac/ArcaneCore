using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Items;

/// <summary>
/// Items' characters-database tables: <c>item_instance</c> and <c>character_inventory</c>
/// (vmangos-shaped). Characters schema version 3 — allocated per docs/integration/items.md;
/// the lead renumbers on a collision.
/// </summary>
public sealed class ItemCharacterDataModule : IDataModule, ICharacterDataCleanup
{
    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => 3;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange("item_instance"),
        new CreateTableChange("character_inventory"),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ItemInstanceRow>(ItemInstanceRow.Configure);
        modelBuilder.Entity<CharacterInventoryRow>(CharacterInventoryRow.Configure);
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IItemStore, EfItemStore>();

    /// <summary>Every item the character owns and every inventory slot it holds (vmangos DeleteFromDB: item_instance, character_inventory).</summary>
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => ItemPersistence.StageDeleteAllAsync(db, characterId, cancellationToken);
}

using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Loot;

/// <summary>
/// Characters schema 70: <c>loot_state_item.random_property_id</c>, the random property (positive) or suffix (negative) a durable chest
/// stack rolled at generation (vmangos LootMgr.cpp:343-344 rolls it for every LootItem), so the window and the taken item agree after a
/// restart. The column is declared by <see cref="LootStateDataModule"/>'s model; this step adds it to databases created before it.
/// </summary>
public sealed class LootStateRandomPropertyDataModule : IDataModule
{
    public const int Version = 70;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
        [new AddColumnChange("loot_state_item", "random_property_id")];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        // The row and column mapping lives with the table's owner, LootStateDataModule.
    }

    public void AddServices(IServiceCollection services)
    {
        // The chest store reads and writes the column with the rest of the stack.
    }
}

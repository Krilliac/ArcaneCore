using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Loot;

/// <summary>
/// Characters schema 70: <c>loot_state_item.random_property_id</c> and <c>item_loot.random_property_id</c>, the random property id a durable
/// chest stack or a container item's stack rolled at generation (vmangos LootMgr.cpp:343-344 rolls it for every LootItem; Item.cpp:350 keeps it in
/// item_loot), so the window and the taken item agree after a restart. The columns are declared by the models of <see cref="LootStateDataModule"/>
/// and <see cref="ArcaneCore.Data.Characters.Items.ItemLootDataModule"/>; this step adds them to databases created before it.
/// </summary>
public sealed class LootStateRandomPropertyDataModule : IDataModule
{
    public const int Version = 70;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
        [new AddColumnChange("loot_state_item", "random_property_id"), new AddColumnChange("item_loot", "random_property_id")];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        // The row and column mappings live with the tables' owners, LootStateDataModule and ItemLootDataModule.
    }

    public void AddServices(IServiceCollection services)
    {
        // The chest store reads and writes the column with the rest of the stack.
    }
}

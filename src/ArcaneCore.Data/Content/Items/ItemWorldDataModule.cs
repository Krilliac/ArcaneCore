using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Content.Items;

/// <summary>
/// Items' world-database tables: <c>item_template</c> and <c>playercreateinfo_item</c>
/// (vmangos-shaped). World schema version 4 — allocated per docs/integration/items.md; the lead
/// renumbers on a collision. The tables ship empty: content comes from the importer (no GPL data
/// is committed).
/// </summary>
public sealed class ItemWorldDataModule : IDataModule
{
    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => 4;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange("item_template"),
        new CreateTableChange("playercreateinfo_item"),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ItemTemplateRow>(ItemTemplateRow.Configure);
        modelBuilder.Entity<PlayerCreateInfoItemRow>(PlayerCreateInfoItemRow.Configure);
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IItemTemplateSource, EfItemTemplateSource>();
}

/// <summary>Reads item content from the world database (once, at startup).</summary>
public sealed class EfItemTemplateSource(WorldDbContext db) : IItemTemplateSource
{
    public async Task<IReadOnlyList<ItemTemplate>> LoadTemplatesAsync(CancellationToken cancellationToken = default)
    {
        List<ItemTemplateRow> rows = await db.Set<ItemTemplateRow>().AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r => r.ToTemplate()).ToList();
    }

    public async Task<IReadOnlyList<StartingItem>> LoadStartingItemsAsync(CancellationToken cancellationToken = default)
    {
        List<PlayerCreateInfoItemRow> rows = await db.Set<PlayerCreateInfoItemRow>().AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // vmangos ObjectMgr::LoadPlayerInfo keeps the rows in table order per race/class; order
        // by key so the outfit is deterministic on every engine.
        return rows.OrderBy(r => r.Race).ThenBy(r => r.Class).ThenBy(r => r.ItemId)
            .Select(r => r.ToStartingItem()).ToList();
    }
}

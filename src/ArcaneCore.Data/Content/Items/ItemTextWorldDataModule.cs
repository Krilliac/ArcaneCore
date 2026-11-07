using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Content.Items;

/// <summary><c>page_text</c>: one readable page (vmangos and cmangos classic-db: <c>entry</c>, <c>text</c>, <c>next_page</c>; vmangos ObjectMgr::LoadPageTexts).</summary>
public sealed class PageTextRow
{
    public uint Entry { get; set; }

    public string Text { get; set; } = string.Empty;

    public uint NextPage { get; set; }
}

/// <summary><c>item_enchantment_template</c>: one random-property candidate of a group (vmangos LoadRandomEnchantmentsTable: <c>entry</c>, <c>ench</c>, <c>chance</c>).</summary>
public sealed class ItemEnchantmentTemplateRow
{
    public uint Entry { get; set; }

    public uint Ench { get; set; }

    public float Chance { get; set; }
}

/// <summary>Reads the page texts of the world database (a scoped service; the world feature reads it once at startup).</summary>
public interface IPageTextStore
{
    Task<IReadOnlyList<PageTextRecord>> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reads the <c>item_enchantment_template</c> rows of the world database (a scoped service; the world feature reads it once at startup).</summary>
public interface IItemEnchantmentTemplateStore
{
    Task<IReadOnlyList<ItemEnchantmentChance>> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// World schema 70: the world
/// database carries <c>page_text</c> and <c>item_enchantment_template</c>, which the world used to read from a MySQL dump at every start
/// (<c>PageText:DumpPath</c>, <c>ItemRandomProperties:EnchantmentTemplateDumpPath</c>, both retired). Both tables are GPL content the operator
/// imports (<c>arcane-content-importer import</c> or <c>refresh</c>, <see cref="ArcaneCore.Data.Content.Import.ItemTextDumpImporter"/>); nothing
/// is seeded, so a database upgraded to 70 has none until then. Tables and columns follow vmangos and cmangos classic-db; the column names are
/// the property names.
/// </summary>
public sealed class ItemTextWorldDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 70;

    public const string PageTextTable = "page_text";
    public const string EnchantmentTemplateTable = "item_enchantment_template";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(PageTextTable),
        new CreateTableChange(EnchantmentTemplateTable),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PageTextRow>(entity =>
        {
            entity.ToTable(PageTextTable);
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).ValueGeneratedNever();
            entity.Property(r => r.Text).IsRequired();
        });
        modelBuilder.Entity<ItemEnchantmentTemplateRow>(entity =>
        {
            entity.ToTable(EnchantmentTemplateTable);
            entity.HasKey(r => new { r.Entry, r.Ench });
        });
    }

    public void AddServices(IServiceCollection services)
    {
        services.AddScoped<IPageTextStore, EfPageTextStore>();
        services.AddScoped<IItemEnchantmentTemplateStore, EfItemEnchantmentTemplateStore>();
    }
}

public sealed class EfPageTextStore(WorldDbContext db) : IPageTextStore
{
    public async Task<IReadOnlyList<PageTextRecord>> LoadAsync(CancellationToken cancellationToken = default)
        => (await db.Set<PageTextRow>().AsNoTracking().OrderBy(r => r.Entry).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new PageTextRecord(r.Entry, r.Text, r.NextPage)).ToArray();
}

public sealed class EfItemEnchantmentTemplateStore(WorldDbContext db) : IItemEnchantmentTemplateStore
{
    public async Task<IReadOnlyList<ItemEnchantmentChance>> LoadAsync(CancellationToken cancellationToken = default)
        => (await db.Set<ItemEnchantmentTemplateRow>().AsNoTracking().OrderBy(r => r.Entry).ThenBy(r => r.Ench).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new ItemEnchantmentChance(r.Entry, r.Ench, r.Chance)).ToArray();
}

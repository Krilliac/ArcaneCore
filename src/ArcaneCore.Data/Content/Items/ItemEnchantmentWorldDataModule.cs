using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Content.Items;

public sealed class ItemEnchantProcRow
{
    public uint Entry { get; set; }
    public float PpmRate { get; set; }
}

/// <summary>World schema 22 for the optional spell_proc_item_enchant PPM overrides.</summary>
public sealed class ItemEnchantmentWorldDataModule : IDataModule
{
    public const int Version = 22;
    public DatabaseComponent Component => DatabaseComponent.World;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange("spell_proc_item_enchant")];

    public void ConfigureModel(ModelBuilder modelBuilder) => modelBuilder.Entity<ItemEnchantProcRow>(entity =>
    {
        entity.ToTable("spell_proc_item_enchant");
        entity.HasKey(row => row.Entry);
        entity.Property(row => row.Entry).HasColumnName("entry").ValueGeneratedNever();
        entity.Property(row => row.PpmRate).HasColumnName("ppmRate");
    });

    public void AddServices(IServiceCollection services) => services.AddScoped<IItemEnchantProcStore, EfItemEnchantProcStore>();
}

public sealed class EfItemEnchantProcStore(WorldDbContext db) : IItemEnchantProcStore
{
    public async Task<IReadOnlyList<ItemEnchantProc>> LoadAsync(CancellationToken cancellationToken = default)
        => (await db.Set<ItemEnchantProcRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(row => new ItemEnchantProc(row.Entry, row.PpmRate)).ToArray();
}

using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Content.Items;

public sealed class SpellEnchantChargesRow
{
    public uint Entry { get; set; }
    public uint Charges { get; set; }
}

/// <summary>World schema 24: vmangos spell_enchant_charges(entry, charges).</summary>
public sealed class SpellEnchantChargesWorldDataModule : IDataModule
{
    public const int Version = 24;
    public DatabaseComponent Component => DatabaseComponent.World;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange("spell_enchant_charges")];
    public void ConfigureModel(ModelBuilder modelBuilder) => modelBuilder.Entity<SpellEnchantChargesRow>(entity =>
    {
        entity.ToTable("spell_enchant_charges");
        entity.HasKey(row => row.Entry);
        entity.Property(row => row.Entry).HasColumnName("entry").ValueGeneratedNever();
        entity.Property(row => row.Charges).HasColumnName("charges");
    });
    public void AddServices(IServiceCollection services) => services.AddScoped<ISpellEnchantChargesStore, EfSpellEnchantChargesStore>();
}

public interface ISpellEnchantChargesStore
{
    Task<IReadOnlyList<SpellEnchantCharges>> LoadAsync(CancellationToken cancellationToken = default);
}

public sealed class EfSpellEnchantChargesStore(WorldDbContext db) : ISpellEnchantChargesStore
{
    public async Task<IReadOnlyList<SpellEnchantCharges>> LoadAsync(CancellationToken cancellationToken = default)
        => (await db.Set<SpellEnchantChargesRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(row => new SpellEnchantCharges(row.Entry, row.Charges)).ToArray();
}

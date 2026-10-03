using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Totems;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Totems;

/// <summary><c>totem_spell</c>: the spell a totem creature template casts (see <see cref="TotemContent"/>).</summary>
public sealed class TotemSpellRow
{
    public uint CreatureEntry { get; set; }

    public uint SpellId { get; set; }
}

/// <summary>
/// The totem world-schema step (<see cref="IDataModule"/>): one new table, <c>totem_spell</c>.
/// <para>
/// <b>World version 11</b> is the next free world version in this lane's tree (after 10, quest reputation
/// rewards); the integrator renumbers at merge. Tests use <see cref="Version"/> or
/// <c>WorldDbContext.Schema.CurrentVersion</c>, never the literal.
/// </para>
/// </summary>
public sealed class TotemWorldDataModule : IDataModule
{
    public const int Version = 11;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange("totem_spell")];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<TotemSpellRow>(entity =>
        {
            entity.ToTable("totem_spell");
            entity.HasKey(r => r.CreatureEntry);
            entity.Property(r => r.CreatureEntry).ValueGeneratedNever();
        });

    public void AddServices(IServiceCollection services) => services.AddScoped<ITotemDataStore, EfTotemDataStore>();
}

/// <summary>Reads <c>totem_spell</c> into an immutable <see cref="TotemContent"/>.</summary>
public sealed class EfTotemDataStore(WorldDbContext db) : ITotemDataStore
{
    public async Task<TotemContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<TotemSpellRow> rows = await db.Set<TotemSpellRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return new TotemContent(rows.Select(r => (r.CreatureEntry, r.SpellId)));
    }
}

using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Rest;

/// <summary>Read access to the world database's <c>areatrigger_tavern</c> table (docs/areas/rested-xp.md).</summary>
public interface IAreaTriggerTavernStore
{
    /// <summary>The area trigger ids of every row, ascending (vmangos ObjectMgr::LoadTavernAreaTriggers: <c>SELECT id FROM areatrigger_tavern</c>).</summary>
    Task<IReadOnlyList<uint>> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary><c>areatrigger_tavern</c> row: the id of an area trigger that marks an inn (vmangos/mangos world DB, one column <c>id</c>).</summary>
public sealed class AreaTriggerTavernRow
{
    public uint Id { get; set; }
}

/// <summary>
/// The <c>areatrigger_tavern</c> table of the world database. Whether a row names a real area trigger is checked when
/// the rest feature loads it (vmangos does the same, ObjectMgr.cpp:1686-1691), because the trigger volumes are map
/// content, not part of this table. The world schema version is the next free one after the graveyards (28); the
/// integrator renumbers this one constant when other world steps merge first (tests refer to it, never to a literal).
/// No cleanup registration: the world schema holds no per-character rows.
/// </summary>
public sealed class AreaTriggerTavernDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 30;

    public const string Table = "areatrigger_tavern";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<AreaTriggerTavernRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IAreaTriggerTavernStore, EfAreaTriggerTavernStore>();
}

/// <summary>EF Core implementation of <see cref="IAreaTriggerTavernStore"/>.</summary>
public sealed class EfAreaTriggerTavernStore(WorldDbContext db) : IAreaTriggerTavernStore
{
    public async Task<IReadOnlyList<uint>> LoadAsync(CancellationToken cancellationToken = default)
        => await db.Set<AreaTriggerTavernRow>().AsNoTracking().OrderBy(r => r.Id).Select(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}

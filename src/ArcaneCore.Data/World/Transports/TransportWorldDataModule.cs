using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Transports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Transports;

/// <summary>One <c>transports</c> row (vmangos world database: <c>entry, build, name, period</c>; primary key entry + build).</summary>
public sealed class TransportRow
{
    /// <summary>The <c>gameobject_template</c> entry of the ship (type 15, MO_TRANSPORT).</summary>
    public uint Entry { get; set; }

    /// <summary>The first client build the period applies to (0: every build).</summary>
    public ushort Build { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The round trip in milliseconds; 0 keeps the period computed from the path.</summary>
    public uint Period { get; set; }
}

/// <summary>
/// The ship route periods of the world schema (<see cref="IDataModule"/>): vmangos' <c>transports</c> table, read by
/// <c>TransportMgr::LoadTransportTemplates</c> (TransportMgr.cpp:62-80) to replace the computed period of a route with the
/// measured one for the newest build at or below 5875 (<see cref="TransportPeriods.Select"/>). The routes themselves come from
/// <c>gameobject_template</c> type 15 rows and TaxiPathNode.dbc, so an empty table is valid: every route keeps its computed period.
/// <para>
/// <b>World version 45</b>: the transports lane's reserved number in the wave-2 plan. Named once here; tests read
/// <see cref="Version"/>.
/// </para>
/// </summary>
public sealed class TransportWorldDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 45;

    public const string Table = "transports";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<TransportRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => new { r.Entry, r.Build });
            entity.Property(r => r.Entry).HasColumnName("entry").ValueGeneratedNever();
            entity.Property(r => r.Build).HasColumnName("build").ValueGeneratedNever();
            entity.Property(r => r.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
            entity.Property(r => r.Period).HasColumnName("period");
        });

    public void AddServices(IServiceCollection services) => services.AddScoped<ITransportDataStore, EfTransportDataStore>();
}

/// <summary>Reads every <c>transports</c> row (ordered by entry, then build).</summary>
public sealed class EfTransportDataStore(IDbContextFactory<WorldDbContext> factory) : ITransportDataStore
{
    public async Task<IReadOnlyList<TransportPeriodRow>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using WorldDbContext db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        List<TransportRow> rows = await db.Set<TransportRow>().AsNoTracking()
            .OrderBy(r => r.Entry).ThenBy(r => r.Build)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new TransportPeriodRow(r.Entry, r.Build, r.Name, r.Period))];
    }
}

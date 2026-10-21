using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Content.Maps;

/// <summary>
/// World schema 55: <c>wmo_area_template</c>, the client's WMOAreaTable.dbc (the area of each WMO group; vmangos reads it into
/// <c>sWMOAreaInfoByTripple</c>). Like <c>area_template</c> it is filled from the client DBC by the content importer
/// (<c>refresh --dbc-dir</c>, <c>import-map-dbc</c>) and never shipped as rows; the world reads it with the other map tables
/// (<see cref="EfMapDataStore"/>) and runs as before while it is empty (docs/areas/world-state.md).
/// </summary>
public sealed class WmoAreaDataModule : IDataModule
{
    /// <summary>The world schema version this module's table arrives in.</summary>
    public const int Version = 55;

    public const string WmoAreaTemplateTable = "wmo_area_template";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(WmoAreaTemplateTable)];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<WmoAreaTemplateRow>(entity =>
        {
            entity.ToTable(WmoAreaTemplateTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
        });

    public void AddServices(IServiceCollection services)
    {
    }
}

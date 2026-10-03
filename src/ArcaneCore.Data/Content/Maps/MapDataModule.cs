using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Content.Maps;

/// <summary>
/// The map tables of the world database (docs/areas/grid-terrain.md): <c>map_template</c>,
/// <c>area_template</c>, <c>areatrigger_template</c>, <c>areatrigger_teleport</c> and
/// <c>game_tele</c>, introduced by world schema <see cref="Version"/> (allocation recorded in
/// docs/integration/grid-terrain.md).
/// </summary>
public sealed class MapDataModule : IDataModule
{
    /// <summary>The world schema version this module's tables arrive in.</summary>
    public const int Version = 2;

    public const string MapTemplateTable = "map_template";
    public const string AreaTemplateTable = "area_template";
    public const string AreaTriggerTemplateTable = "areatrigger_template";
    public const string AreaTriggerTeleportTable = "areatrigger_teleport";
    public const string GameTeleTable = "game_tele";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(MapTemplateTable),
        new CreateTableChange(AreaTemplateTable),
        new CreateTableChange(AreaTriggerTemplateTable),
        new CreateTableChange(AreaTriggerTeleportTable),
        new CreateTableChange(GameTeleTable),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MapTemplateRow>(entity =>
        {
            entity.ToTable(MapTemplateTable);
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).ValueGeneratedNever();
            entity.Property(r => r.MapName).HasMaxLength(128);
            entity.Property(r => r.ScriptName).HasMaxLength(128);
        });

        modelBuilder.Entity<AreaTemplateRow>(entity =>
        {
            entity.ToTable(AreaTemplateTable);
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).ValueGeneratedNever();
            entity.Property(r => r.Name).HasMaxLength(128);
        });

        modelBuilder.Entity<AreaTriggerTemplateRow>(entity =>
        {
            entity.ToTable(AreaTriggerTemplateTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Name).HasMaxLength(128);
        });

        modelBuilder.Entity<AreaTriggerTeleportRow>(entity =>
        {
            entity.ToTable(AreaTriggerTeleportTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Name).HasMaxLength(128);
            entity.Property(r => r.Message).HasMaxLength(255);
        });

        modelBuilder.Entity<GameTeleRow>(entity =>
        {
            entity.ToTable(GameTeleTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Name).HasMaxLength(100);
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IMapDataStore, EfMapDataStore>();
}

/// <summary>EF Core implementation of <see cref="IMapDataStore"/>.</summary>
public sealed class EfMapDataStore(WorldDbContext db) : IMapDataStore
{
    public async Task<MapContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<MapTemplate> maps = (await db.Set<MapTemplateRow>().AsNoTracking().OrderBy(r => r.Entry)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new MapTemplate(r.Entry, r.Parent, (MapType)r.MapType, r.LinkedZone, r.PlayerLimit, r.ResetDelay,
                r.GhostEntranceMap, r.GhostEntranceX, r.GhostEntranceY, r.MapName, r.ScriptName))
            .ToList();

        List<AreaTemplate> areas = (await db.Set<AreaTemplateRow>().AsNoTracking().OrderBy(r => r.Entry)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new AreaTemplate(r.Entry, r.MapId, r.ZoneId, r.ExploreFlag, r.Flags, r.AreaLevel, r.Name, r.Team, r.LiquidTypeId))
            .ToList();

        List<AreaTriggerTemplate> triggers = (await db.Set<AreaTriggerTemplateRow>().AsNoTracking().OrderBy(r => r.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new AreaTriggerTemplate(r.Id, r.MapId, r.X, r.Y, r.Z, r.Radius, r.BoxX, r.BoxY, r.BoxZ, r.BoxOrientation, r.Name))
            .ToList();

        List<AreaTriggerTeleport> teleports = (await db.Set<AreaTriggerTeleportRow>().AsNoTracking().OrderBy(r => r.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new AreaTriggerTeleport(r.Id, r.Name, r.Message, r.RequiredLevel, r.TargetMap,
                r.TargetPositionX, r.TargetPositionY, r.TargetPositionZ, r.TargetOrientation))
            .ToList();

        List<GameTele> teles = (await db.Set<GameTeleRow>().AsNoTracking().OrderBy(r => r.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new GameTele(r.Id, r.PositionX, r.PositionY, r.PositionZ, r.Orientation, r.Map, r.Name))
            .ToList();

        return new MapContent(maps, areas, triggers, teleports, teles);
    }
}

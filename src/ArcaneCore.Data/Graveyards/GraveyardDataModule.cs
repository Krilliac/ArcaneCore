using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Graveyards;

/// <summary><c>world_safe_locs</c> row (cmangos classic-db layout: <c>id, map, x, y, z, o, name</c>). Keyed by <see cref="Id"/>.</summary>
public sealed class WorldSafeLocRow
{
    public uint Id { get; set; }

    public uint MapId { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    /// <summary>The facing (<c>o</c>; vmangos <c>world_safe_locs_facing.orientation</c>).</summary>
    public float Orientation { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// <c>game_graveyard_zone</c> row. Keyed by (<see cref="Id"/>, <see cref="GhostZone"/>): classic-db's key also holds
/// <c>link_kind</c>, but only kind 0 (a zone or area link) is imported, so the pair identifies a row.
/// </summary>
public sealed class GraveyardZoneRow
{
    public uint Id { get; set; }

    public uint GhostZone { get; set; }

    /// <summary><c>faction</c>: 0 = both teams, 67 = Horde, 469 = Alliance.</summary>
    public uint Team { get; set; }
}

/// <summary>
/// The graveyard tables of the world database: <c>world_safe_locs</c> and <c>game_graveyard_zone</c>. The world schema
/// version is allocated as the next free one after <c>playercreateinfo_action</c> (20); the integrator renumbers this
/// one constant when other world steps merge first (tests refer to it, never to a literal). No cleanup registration:
/// the world schema holds no per-character rows.
/// </summary>
public sealed class GraveyardDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 28; // allocated as 21 in the lane; renumbered at wave-4 integration

    public const string SafeLocsTable = "world_safe_locs";
    public const string GraveyardZoneTable = "game_graveyard_zone";

    /// <summary>The <c>name</c> column length (classic-db: varchar(50)).</summary>
    public const int NameLength = 50;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(SafeLocsTable),
        new CreateTableChange(GraveyardZoneTable),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorldSafeLocRow>(entity =>
        {
            entity.ToTable(SafeLocsTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.MapId).HasColumnName("map");
            entity.Property(r => r.X).HasColumnName("x");
            entity.Property(r => r.Y).HasColumnName("y");
            entity.Property(r => r.Z).HasColumnName("z");
            entity.Property(r => r.Orientation).HasColumnName("o");
            entity.Property(r => r.Name).HasColumnName("name").HasMaxLength(NameLength);
        });

        modelBuilder.Entity<GraveyardZoneRow>(entity =>
        {
            entity.ToTable(GraveyardZoneTable);
            entity.HasKey(r => new { r.Id, r.GhostZone });
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.GhostZone).HasColumnName("ghost_zone").ValueGeneratedNever();
            entity.Property(r => r.Team).HasColumnName("faction");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IGraveyardDataStore, EfGraveyardDataStore>();
}

/// <summary>EF Core implementation of <see cref="IGraveyardDataStore"/>.</summary>
public sealed class EfGraveyardDataStore(WorldDbContext db) : IGraveyardDataStore
{
    public async Task<GraveyardContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<WorldSafeLoc> locs = (await db.Set<WorldSafeLocRow>().AsNoTracking().OrderBy(r => r.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new WorldSafeLoc(r.Id, r.MapId, r.X, r.Y, r.Z, r.Orientation, r.Name))
            .ToList();

        List<GraveyardLink> links = (await db.Set<GraveyardZoneRow>().AsNoTracking().OrderBy(r => r.Id).ThenBy(r => r.GhostZone)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new GraveyardLink(r.Id, r.GhostZone, r.Team))
            .ToList();

        return new GraveyardContent(locs, links);
    }
}

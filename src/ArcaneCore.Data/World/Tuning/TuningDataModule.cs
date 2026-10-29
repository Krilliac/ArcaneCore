using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Tuning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Tuning;

/// <summary><c>auction_bot_value</c> row: percent of the vendor price for one quality and item class.</summary>
public sealed class AuctionBotValueRow
{
    public byte Quality { get; set; }

    public byte ItemClass { get; set; }

    public int Percent { get; set; }
}

/// <summary><c>auction_bot_loot</c> row: the "minTemplates, maxTemplates, minRolls, maxRolls" setting of one loot source.</summary>
public sealed class AuctionBotLootRow
{
    public string Source { get; set; } = string.Empty;

    public int MinTemplates { get; set; }

    public int MaxTemplates { get; set; }

    public int MinRolls { get; set; }

    public int MaxRolls { get; set; }
}

/// <summary><c>playerbot_ship_route</c> row.</summary>
public sealed class PlayerbotShipRouteRow
{
    public uint Entry { get; set; }

    public byte Means { get; set; }

    public byte Access { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary><c>playerbot_elevator_region</c> row.</summary>
public sealed class PlayerbotElevatorRegionRow
{
    public int Id { get; set; }

    public int MapId { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public int LowZone { get; set; }

    public int LowArea { get; set; }

    public int HighZone { get; set; }

    public int HighArea { get; set; }

    /// <summary>1 when the upper level is a mesa (a tinyint, so content files insert plain numbers on every provider).</summary>
    public byte MesaTop { get; set; }

    /// <summary>1 for an enclosed vault lift (Undercity).</summary>
    public byte VaultLow { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary><c>playerbot_tram_network</c> row.</summary>
public sealed class PlayerbotTramNetworkRow
{
    public int MapId { get; set; }

    public float PairYards { get; set; }
}

/// <summary>
/// World schema 63: the plain tuning tables of rule engine phase P5 (docs/superpowers/specs/2026-10-28-rule-engine-design.md; data, not
/// rules): <c>auction_bot_value</c> and <c>auction_bot_loot</c> (the auction bot's value and loot matrices, formerly CSV strings in
/// <c>AuctionHouseBot</c>), <c>playerbot_ship_route</c>, <c>playerbot_elevator_region</c> and <c>playerbot_tram_network</c> (the bots'
/// transport networks). The schema step bundles no rows: the world seeds an empty table with the shipped rows at start
/// (<see cref="TuningDefaults"/>), and <c>sql/content/world</c> carries the same rows for a manual install. The version is the next free
/// world step (the integrator renumbers this one constant).
/// </summary>
public sealed class TuningDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 63;

    public const string ValueTable = "auction_bot_value";
    public const string LootTable = "auction_bot_loot";
    public const string ShipTable = "playerbot_ship_route";
    public const string ElevatorTable = "playerbot_elevator_region";
    public const string TramTable = "playerbot_tram_network";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(ValueTable),
        new CreateTableChange(LootTable),
        new CreateTableChange(ShipTable),
        new CreateTableChange(ElevatorTable),
        new CreateTableChange(TramTable),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<AuctionBotValueRow>(entity =>
        {
            entity.ToTable(ValueTable);
            entity.HasKey(r => new { r.Quality, r.ItemClass });
            entity.Property(r => r.Quality).HasColumnName("quality").ValueGeneratedNever();
            entity.Property(r => r.ItemClass).HasColumnName("item_class").ValueGeneratedNever();
            entity.Property(r => r.Percent).HasColumnName("percent");
        });

        modelBuilder.Entity<AuctionBotLootRow>(entity =>
        {
            entity.ToTable(LootTable);
            entity.HasKey(r => r.Source);
            entity.Property(r => r.Source).HasColumnName("source").HasMaxLength(32).ValueGeneratedNever();
            entity.Property(r => r.MinTemplates).HasColumnName("min_templates");
            entity.Property(r => r.MaxTemplates).HasColumnName("max_templates");
            entity.Property(r => r.MinRolls).HasColumnName("min_rolls");
            entity.Property(r => r.MaxRolls).HasColumnName("max_rolls");
        });

        modelBuilder.Entity<PlayerbotShipRouteRow>(entity =>
        {
            entity.ToTable(ShipTable);
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).HasColumnName("entry").ValueGeneratedNever();
            entity.Property(r => r.Means).HasColumnName("means");
            entity.Property(r => r.Access).HasColumnName("access");
            entity.Property(r => r.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<PlayerbotElevatorRegionRow>(entity =>
        {
            entity.ToTable(ElevatorTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.MapId).HasColumnName("map_id");
            entity.Property(r => r.X).HasColumnName("x");
            entity.Property(r => r.Y).HasColumnName("y");
            entity.Property(r => r.LowZone).HasColumnName("low_zone");
            entity.Property(r => r.LowArea).HasColumnName("low_area");
            entity.Property(r => r.HighZone).HasColumnName("high_zone");
            entity.Property(r => r.HighArea).HasColumnName("high_area");
            entity.Property(r => r.MesaTop).HasColumnName("mesa_top");
            entity.Property(r => r.VaultLow).HasColumnName("vault_low");
            entity.Property(r => r.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<PlayerbotTramNetworkRow>(entity =>
        {
            entity.ToTable(TramTable);
            entity.HasKey(r => r.MapId);
            entity.Property(r => r.MapId).HasColumnName("map_id").ValueGeneratedNever();
            entity.Property(r => r.PairYards).HasColumnName("pair_yards");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ITuningContentStore, EfTuningContentStore>();
}

/// <summary>EF Core implementation of <see cref="ITuningContentStore"/>.</summary>
public sealed class EfTuningContentStore(WorldDbContext db) : ITuningContentStore
{
    public async Task<TuningContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<AuctionBotValueRow> values = await db.Set<AuctionBotValueRow>().AsNoTracking().OrderBy(r => r.Quality).ThenBy(r => r.ItemClass)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<AuctionBotLootRow> loot = await db.Set<AuctionBotLootRow>().AsNoTracking().OrderBy(r => r.Source)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<PlayerbotShipRouteRow> ships = await db.Set<PlayerbotShipRouteRow>().AsNoTracking().OrderBy(r => r.Entry)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<PlayerbotElevatorRegionRow> elevators = await db.Set<PlayerbotElevatorRegionRow>().AsNoTracking().OrderBy(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<PlayerbotTramNetworkRow> trams = await db.Set<PlayerbotTramNetworkRow>().AsNoTracking().OrderBy(r => r.MapId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new TuningContent
        {
            Values = [.. values.Select(r => new AuctionBotValueRecord(r.Quality, r.ItemClass, r.Percent))],
            Loot = [.. loot.Select(r => new AuctionBotLootRecord(r.Source, r.MinTemplates, r.MaxTemplates, r.MinRolls, r.MaxRolls))],
            Ships = [.. ships.Select(r => new ShipRouteRecord(r.Entry, r.Means, r.Access, r.Name))],
            Elevators = [.. elevators.Select(r => new ElevatorRegionRecord(r.Id, (uint)r.MapId, r.X, r.Y, (uint)r.LowZone, (uint)r.LowArea,
                (uint)r.HighZone, (uint)r.HighArea, r.MesaTop != 0, r.VaultLow != 0, r.Name))],
            Trams = [.. trams.Select(r => new TramNetworkRecord((uint)r.MapId, r.PairYards))],
        };
    }

    public async Task<int> SeedEmptyTablesAsync(TuningContent defaults, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        int written = 0;
        if (!await db.Set<AuctionBotValueRow>().AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            db.Set<AuctionBotValueRow>().AddRange(defaults.Values.Select(r => new AuctionBotValueRow { Quality = r.Quality, ItemClass = r.ItemClass, Percent = r.Percent }));
            written += defaults.Values.Count;
        }

        if (!await db.Set<AuctionBotLootRow>().AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            db.Set<AuctionBotLootRow>().AddRange(defaults.Loot.Select(r => new AuctionBotLootRow
            {
                Source = r.Source, MinTemplates = r.MinTemplates, MaxTemplates = r.MaxTemplates, MinRolls = r.MinRolls, MaxRolls = r.MaxRolls,
            }));
            written += defaults.Loot.Count;
        }

        if (!await db.Set<PlayerbotShipRouteRow>().AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            db.Set<PlayerbotShipRouteRow>().AddRange(defaults.Ships.Select(r => new PlayerbotShipRouteRow { Entry = r.Entry, Means = r.Means, Access = r.Access, Name = r.Name }));
            written += defaults.Ships.Count;
        }

        if (!await db.Set<PlayerbotElevatorRegionRow>().AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            db.Set<PlayerbotElevatorRegionRow>().AddRange(defaults.Elevators.Select(r => new PlayerbotElevatorRegionRow
            {
                Id = r.Id, MapId = (int)r.MapId, X = r.X, Y = r.Y, LowZone = (int)r.LowZone, LowArea = (int)r.LowArea, HighZone = (int)r.HighZone,
                HighArea = (int)r.HighArea, MesaTop = (byte)(r.MesaTop ? 1 : 0), VaultLow = (byte)(r.VaultLow ? 1 : 0), Name = r.Name,
            }));
            written += defaults.Elevators.Count;
        }

        if (!await db.Set<PlayerbotTramNetworkRow>().AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            db.Set<PlayerbotTramNetworkRow>().AddRange(defaults.Trams.Select(r => new PlayerbotTramNetworkRow { MapId = (int)r.MapId, PairYards = r.PairYards }));
            written += defaults.Trams.Count;
        }

        if (written > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return written;
    }
}

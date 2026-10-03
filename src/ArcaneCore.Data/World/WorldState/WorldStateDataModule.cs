using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.WorldState;

/// <summary>
/// <c>game_weather</c> row: the zone and its 12 chances (percent), named like the vmangos / classic-db
/// columns (<c>spring_rain_chance</c> ... <c>winter_storm_chance</c>).
/// </summary>
public sealed class GameWeatherRow
{
    public uint Zone { get; set; }
    public byte SpringRainChance { get; set; }
    public byte SpringSnowChance { get; set; }
    public byte SpringStormChance { get; set; }
    public byte SummerRainChance { get; set; }
    public byte SummerSnowChance { get; set; }
    public byte SummerStormChance { get; set; }
    public byte FallRainChance { get; set; }
    public byte FallSnowChance { get; set; }
    public byte FallStormChance { get; set; }
    public byte WinterRainChance { get; set; }
    public byte WinterSnowChance { get; set; }
    public byte WinterStormChance { get; set; }

    internal uint[] ToChances() =>
    [
        SpringRainChance, SpringSnowChance, SpringStormChance,
        SummerRainChance, SummerSnowChance, SummerStormChance,
        FallRainChance, FallSnowChance, FallStormChance,
        WinterRainChance, WinterSnowChance, WinterStormChance,
    ];

    internal static GameWeatherRow From(GameWeatherRecord record)
    {
        if (record.Chances.Count != 12 || record.Chances.Any(c => c > byte.MaxValue))
        {
            throw new InvalidDataException($"game_weather zone {record.Zone}: expected 12 chances of at most 255");
        }

        IReadOnlyList<uint> c = record.Chances;
        return new GameWeatherRow
        {
            Zone = record.Zone,
            SpringRainChance = (byte)c[0], SpringSnowChance = (byte)c[1], SpringStormChance = (byte)c[2],
            SummerRainChance = (byte)c[3], SummerSnowChance = (byte)c[4], SummerStormChance = (byte)c[5],
            FallRainChance = (byte)c[6], FallSnowChance = (byte)c[7], FallStormChance = (byte)c[8],
            WinterRainChance = (byte)c[9], WinterSnowChance = (byte)c[10], WinterStormChance = (byte)c[11],
        };
    }
}

/// <summary><c>exploration_basexp</c> row.</summary>
public sealed class ExplorationBaseXpRow
{
    public uint Level { get; set; }
    public uint BaseXp { get; set; }
}

/// <summary>
/// The world-state tables of the world database (docs/areas/world-state.md): <c>game_weather</c> and
/// <c>exploration_basexp</c>. The data itself is imported from the operator's vmangos / classic-db
/// content (<see cref="WorldStateDumpImporter"/>); nothing is bundled. The version is a single
/// constant the integrator renumbers (docs/integration/world-state.md).
/// </summary>
public sealed class WorldStateDataModule : IDataModule
{
    /// <summary>The world schema version this module's tables arrive in (next free after the quest reputation columns at 10).</summary>
    public const int Version = 11;

    public const string GameWeatherTable = "game_weather";
    public const string ExplorationBaseXpTable = "exploration_basexp";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(GameWeatherTable),
        new CreateTableChange(ExplorationBaseXpTable),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GameWeatherRow>(entity =>
        {
            entity.ToTable(GameWeatherTable);
            entity.HasKey(r => r.Zone);
            entity.Property(r => r.Zone).ValueGeneratedNever();
        });

        modelBuilder.Entity<ExplorationBaseXpRow>(entity =>
        {
            entity.ToTable(ExplorationBaseXpTable);
            entity.HasKey(r => r.Level);
            entity.Property(r => r.Level).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IWorldStateDataStore, EfWorldStateDataStore>();
}

/// <summary>EF Core implementation of <see cref="IWorldStateDataStore"/>.</summary>
public sealed class EfWorldStateDataStore(WorldDbContext db) : IWorldStateDataStore
{
    public async Task<WorldStateContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<GameWeatherRecord> weather = (await db.Set<GameWeatherRow>().AsNoTracking().OrderBy(r => r.Zone)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new GameWeatherRecord(r.Zone, r.ToChances()))
            .ToList();
        List<ExplorationBaseXpRecord> baseXp = (await db.Set<ExplorationBaseXpRow>().AsNoTracking().OrderBy(r => r.Level)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new ExplorationBaseXpRecord(r.Level, r.BaseXp))
            .ToList();
        return new WorldStateContent(weather, baseXp);
    }
}

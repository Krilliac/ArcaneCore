using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.PlayerStats;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.PlayerStats;

/// <summary><c>player_classlevelstats</c> (vmangos ObjectMgr.cpp:4801; classic-db <c>player_classlevelstats</c>).</summary>
public sealed class PlayerClassLevelStatsRow
{
    public byte Class { get; set; }

    public byte Level { get; set; }

    public uint BaseHealth { get; set; }

    public uint BaseMana { get; set; }
}

/// <summary><c>player_levelstats</c> (vmangos ObjectMgr.cpp:4898; classic-db columns str, agi, sta, inte, spi).</summary>
public sealed class PlayerLevelStatsRow
{
    public byte Race { get; set; }

    public byte Class { get; set; }

    public byte Level { get; set; }

    public byte Strength { get; set; }

    public byte Agility { get; set; }

    public byte Stamina { get; set; }

    public byte Intellect { get; set; }

    public byte Spirit { get; set; }
}

/// <summary><c>player_xp_for_level</c> (vmangos ObjectMgr.cpp:5015: lvl, xp_for_next_level).</summary>
public sealed class PlayerXpForLevelRow
{
    public uint Level { get; set; }

    public uint XpForNextLevel { get; set; }
}

/// <summary><c>player_crit_per_agility</c> (vmangos ObjectMgr.cpp:5087; migration 20260703210621).</summary>
public sealed class PlayerCritPerAgilityRow
{
    public byte Class { get; set; }

    public byte Level { get; set; }

    public float Rate { get; set; }
}

/// <summary><c>player_dodge_per_agility</c> (vmangos migration 20260711022757).</summary>
public sealed class PlayerDodgePerAgilityRow
{
    public byte Class { get; set; }

    public byte Level { get; set; }

    public float Rate { get; set; }
}

/// <summary>
/// The player base data tables of the world database, contributed as a world-schema step through the
/// <see cref="IDataModule"/> seam: class health/mana, race/class base stats, XP per level, and the crit and
/// dodge per agility rates. Table and column names follow the vmangos/classic-db tables so the
/// <see cref="PlayerStatsDumpImporter"/> maps by name.
/// <para>
/// The version is the single constant <see cref="Version"/>; the integrator renumbers it at merge time
/// (docs/integration/seams.md). Tests refer to the constant or to <c>WorldDbContext.Schema.CurrentVersion</c>.
/// </para>
/// </summary>
public sealed class PlayerStatsDataModule : IDataModule
{
    /// <summary>The world schema version that introduces the player base data tables.</summary>
    public const int Version = 11;

    public static readonly IReadOnlyList<string> Tables =
        ["player_classlevelstats", "player_levelstats", "player_xp_for_level", "player_crit_per_agility", "player_dodge_per_agility"];

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [.. Tables.Select(t => new CreateTableChange(t))];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlayerClassLevelStatsRow>(entity =>
        {
            entity.ToTable("player_classlevelstats");
            entity.HasKey(r => new { r.Class, r.Level });
        });

        modelBuilder.Entity<PlayerLevelStatsRow>(entity =>
        {
            entity.ToTable("player_levelstats");
            entity.HasKey(r => new { r.Race, r.Class, r.Level });
        });

        modelBuilder.Entity<PlayerXpForLevelRow>(entity =>
        {
            entity.ToTable("player_xp_for_level");
            entity.HasKey(r => r.Level);
            entity.Property(r => r.Level).ValueGeneratedNever();
        });

        modelBuilder.Entity<PlayerCritPerAgilityRow>(entity =>
        {
            entity.ToTable("player_crit_per_agility");
            entity.HasKey(r => new { r.Class, r.Level });
        });

        modelBuilder.Entity<PlayerDodgePerAgilityRow>(entity =>
        {
            entity.ToTable("player_dodge_per_agility");
            entity.HasKey(r => new { r.Class, r.Level });
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IPlayerStatsContentStore, EfPlayerStatsContentStore>();
}

/// <summary>Reads the player base data tables into an immutable <see cref="PlayerStatsContent"/>.</summary>
public sealed class EfPlayerStatsContentStore(WorldDbContext db) : IPlayerStatsContentStore
{
    public async Task<PlayerStatsContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<PlayerClassLevelStatsRow> classLevel = await db.Set<PlayerClassLevelStatsRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<PlayerLevelStatsRow> levelStats = await db.Set<PlayerLevelStatsRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<PlayerXpForLevelRow> xp = await db.Set<PlayerXpForLevelRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<PlayerCritPerAgilityRow> crit = await db.Set<PlayerCritPerAgilityRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<PlayerDodgePerAgilityRow> dodge = await db.Set<PlayerDodgePerAgilityRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

        return new PlayerStatsContent(
            classLevel.Select(r => new ClassLevelStats(r.Class, r.Level, r.BaseHealth, r.BaseMana)),
            levelStats.Select(r => new LevelStats(r.Race, r.Class, r.Level, r.Strength, r.Agility, r.Stamina, r.Intellect, r.Spirit)),
            xp.Select(r => (r.Level, r.XpForNextLevel)),
            crit.Select(r => new AgilityRateRow(r.Class, r.Level, r.Rate)),
            dodge.Select(r => new AgilityRateRow(r.Class, r.Level, r.Rate)));
    }
}

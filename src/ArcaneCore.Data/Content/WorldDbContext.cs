using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content;

/// <summary>Start position row (DB-driven playercreateinfo). Keyed by (race, class).</summary>
public sealed class PlayerCreateInfoRow
{
    public byte Race { get; set; }
    public byte Class { get; set; }
    public uint MapId { get; set; }
    public uint ZoneId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Orientation { get; set; }
}

/// <summary>Per-race appearance/faction row (DB-driven ChrRaces). Keyed by (race, gender).</summary>
public sealed class RaceInfoRow
{
    public byte Race { get; set; }
    public byte Gender { get; set; }
    public uint DisplayId { get; set; }
    public uint FactionTemplate { get; set; }
}

/// <summary>Per-class base stats row. Keyed by class.</summary>
public sealed class ClassInfoRow
{
    public byte Class { get; set; }
    public uint BaseHealth { get; set; }
    public uint BaseMana { get; set; }
    public byte PowerType { get; set; }
}

/// <summary>
/// EF Core context for the world-content database: static data the server reads at startup.
/// Until the content importer lands (M8) it holds the M3 seed tables.
/// </summary>
public sealed class WorldDbContext(DbContextOptions<WorldDbContext> options) : DbContext(options)
{
    /// <summary>
    /// The world version of the forward index repair (the single constant the lead renumbers at merge time,
    /// docs/integration/seams.md). Tests refer to it or to <c>Schema.CurrentVersion</c>, never to a literal.
    /// </summary>
    public const int IndexRepairVersion = 9;

    /// <summary>Schema history of the world-content database.</summary>
    /// <remarks>Versions 2+ come from <see cref="IDataModule"/>s of <see cref="DatabaseComponent.World"/>.</remarks>
    public static readonly SchemaDefinition Schema = DataModules.Compose(
        DatabaseComponent.World,
        "world",
        ["player_create_info", "race_info", "class_info"],
        [
            // Forward index repair: spawn and script tables created by upgrade steps before the bootstrapper
            // carried the model's indexes (docs/integration/schema-index-repair.md).
            new SchemaStep(IndexRepairVersion,
            [
                new EnsureIndexesChange("creature_spawn"),
                new EnsureIndexesChange("gameobject_spawn"),
                new EnsureIndexesChange("creature_ai_scripts"),
            ]),
        ]);

    public DbSet<PlayerCreateInfoRow> PlayerCreateInfo => Set<PlayerCreateInfoRow>();

    public DbSet<RaceInfoRow> RaceInfo => Set<RaceInfoRow>();

    public DbSet<ClassInfoRow> ClassInfo => Set<ClassInfoRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        SchemaBootstrapper.MapVersionTable(modelBuilder, Schema);

        modelBuilder.Entity<PlayerCreateInfoRow>(entity =>
        {
            entity.ToTable("player_create_info");
            entity.HasKey(r => new { r.Race, r.Class });
        });

        modelBuilder.Entity<RaceInfoRow>(entity =>
        {
            entity.ToTable("race_info");
            entity.HasKey(r => new { r.Race, r.Gender });
        });

        modelBuilder.Entity<ClassInfoRow>(entity =>
        {
            entity.ToTable("class_info");
            entity.HasKey(r => r.Class);
        });

        DataModules.ConfigureModel(modelBuilder, DatabaseComponent.World);
    }
}

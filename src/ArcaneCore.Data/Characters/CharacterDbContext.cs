using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Characters;

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
/// EF Core context for characters and the DB-driven world data. Shares the configured
/// connection with the auth context but owns its own tables.
/// </summary>
public sealed class CharacterDbContext(DbContextOptions<CharacterDbContext> options) : DbContext(options)
{
    public DbSet<CharacterRecord> Characters => Set<CharacterRecord>();

    public DbSet<PlayerCreateInfoRow> PlayerCreateInfo => Set<PlayerCreateInfoRow>();

    public DbSet<RaceInfoRow> RaceInfo => Set<RaceInfoRow>();

    public DbSet<ClassInfoRow> ClassInfo => Set<ClassInfoRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CharacterRecord>(entity =>
        {
            entity.ToTable("characters");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).ValueGeneratedOnAdd();
            entity.Property(c => c.Name).HasMaxLength(12).IsRequired();
            entity.HasIndex(c => c.Name).IsUnique();
            entity.HasIndex(c => c.AccountId);
        });

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
    }
}

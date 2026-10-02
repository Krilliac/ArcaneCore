using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Characters;

/// <summary>One action-bar slot of a character (vmangos character_action).</summary>
public sealed class ActionButtonRow
{
    public int CharacterId { get; set; }
    public byte Button { get; set; }
    public uint Action { get; set; }
    public byte Type { get; set; }
}

/// <summary>One client settings blob of an account (vmangos account_data), stored as raw bytes.</summary>
public sealed class AccountDataRow
{
    public int AccountId { get; set; }
    public byte Type { get; set; }
    public uint Time { get; set; }
    public byte[] Data { get; set; } = [];
}

/// <summary>An account's eight tutorial-flag words (vmangos character_tutorial).</summary>
public sealed class AccountTutorialRow
{
    public int AccountId { get; set; }
    public uint Tut0 { get; set; }
    public uint Tut1 { get; set; }
    public uint Tut2 { get; set; }
    public uint Tut3 { get; set; }
    public uint Tut4 { get; set; }
    public uint Tut5 { get; set; }
    public uint Tut6 { get; set; }
    public uint Tut7 { get; set; }
}

/// <summary>EF Core context for the characters database (one per realm).</summary>
public sealed class CharacterDbContext(DbContextOptions<CharacterDbContext> options) : DbContext(options)
{
    /// <summary>Schema history of the characters database.</summary>
    public static readonly SchemaDefinition Schema = new()
    {
        Component = "characters",
        CurrentVersion = 2,
        Version1Tables = ["characters"],
        Steps =
        [
            // M6: played time per level, money, action bars, bind point, account settings.
            new SchemaStep(2,
            [
                new AddColumnChange("characters", nameof(CharacterRecord.LevelPlayedTime)),
                new AddColumnChange("characters", nameof(CharacterRecord.Money)),
                new AddColumnChange("characters", nameof(CharacterRecord.ActionBarToggles)),
                new AddColumnChange("characters", nameof(CharacterRecord.HomeMapId)),
                new AddColumnChange("characters", nameof(CharacterRecord.HomeZoneId)),
                new AddColumnChange("characters", nameof(CharacterRecord.HomeX)),
                new AddColumnChange("characters", nameof(CharacterRecord.HomeY)),
                new AddColumnChange("characters", nameof(CharacterRecord.HomeZ)),
                new CreateTableChange("character_action"),
                new CreateTableChange("account_data"),
                new CreateTableChange("account_tutorial"),
            ]),
        ],
    };

    public DbSet<CharacterRecord> Characters => Set<CharacterRecord>();

    public DbSet<ActionButtonRow> ActionButtons => Set<ActionButtonRow>();

    public DbSet<AccountDataRow> AccountData => Set<AccountDataRow>();

    public DbSet<AccountTutorialRow> AccountTutorials => Set<AccountTutorialRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        SchemaBootstrapper.MapVersionTable(modelBuilder, Schema);

        modelBuilder.Entity<CharacterRecord>(entity =>
        {
            entity.ToTable("characters");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).ValueGeneratedOnAdd();
            entity.Property(c => c.Name).HasMaxLength(12).IsRequired();
            entity.HasIndex(c => c.Name).IsUnique();
            entity.HasIndex(c => c.AccountId);
        });

        modelBuilder.Entity<ActionButtonRow>(entity =>
        {
            entity.ToTable("character_action");
            entity.HasKey(r => new { r.CharacterId, r.Button });
        });

        modelBuilder.Entity<AccountDataRow>(entity =>
        {
            entity.ToTable("account_data");
            entity.HasKey(r => new { r.AccountId, r.Type });
            entity.Property(r => r.Data).IsRequired();
        });

        modelBuilder.Entity<AccountTutorialRow>(entity =>
        {
            entity.ToTable("account_tutorial");
            entity.HasKey(r => r.AccountId);
            entity.Property(r => r.AccountId).ValueGeneratedNever();
        });
    }
}

using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.GameObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.SpecialLoot;

/// <summary>
/// The fishing, pickpocketing and disenchanting loot tables plus the fishing base levels and creature pickpocket ids,
/// as one world-schema step (five <see cref="CreateTableChange"/>s, no ALTER: MariaDB DDL commits implicitly and is not
/// transactional, so a partially applied step is resumed by the bootstrapper; PostgreSQL DDL is transactional).
/// The loaders live in <see cref="EfLootDataStore"/>, which fills the same <c>LootContent</c> as the other loot tables so
/// reference rows (133 of the 155 classic-db fishing rows) resolve in one place.
/// <para>
/// <see cref="Version"/> is the next free world version at the base of this lane (the base ends at 14); the integrator
/// renumbers this one constant at merge and tests read the constant, never a literal.
/// </para>
/// </summary>
public sealed class SpecialLootDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 19;

    public static readonly IReadOnlyList<string> Tables =
    [
        "fishing_loot_template", "pickpocketing_loot_template", "disenchant_loot_template",
        "skill_fishing_base_level", "creature_pickpocket_loot",
    ];

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [.. Tables.Select(t => new CreateTableChange(t))];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ConfigureLoot<FishingLootTemplateRow>(modelBuilder, "fishing_loot_template");
        ConfigureLoot<PickpocketingLootTemplateRow>(modelBuilder, "pickpocketing_loot_template");
        ConfigureLoot<DisenchantLootTemplateRow>(modelBuilder, "disenchant_loot_template");

        modelBuilder.Entity<SkillFishingBaseLevelRow>(entity =>
        {
            entity.ToTable("skill_fishing_base_level");
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).ValueGeneratedNever();
        });

        modelBuilder.Entity<CreaturePickpocketLootRow>(entity =>
        {
            entity.ToTable("creature_pickpocket_loot");
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).ValueGeneratedNever();
        });
    }

    // The stores are registered by the game object loot module (EfLootDataStore reads these rows too).
    public void AddServices(IServiceCollection services)
    {
    }

    /// <summary>cmangos/vmangos primary key (entry, item).</summary>
    private static void ConfigureLoot<T>(ModelBuilder modelBuilder, string table)
        where T : LootTemplateRowBase
        => modelBuilder.Entity<T>(entity =>
        {
            entity.ToTable(table);
            entity.HasKey(r => new { r.Entry, r.Item });
        });
}
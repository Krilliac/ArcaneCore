using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.GameObjects;

/// <summary>
/// The game object and loot tables of the world database, contributed as one world-schema step
/// through the <see cref="IDataModule"/> seam (docs/integration/seams.md). World schema
/// version 7 is reserved for this area (docs/integration/gameobjects-loot.md); the lead may
/// renumber at merge time.
/// <para>
/// Creature loot columns live in <c>creature_loot_info</c> instead of new
/// <c>creature_template</c> columns, so the creatures module and its importer are untouched.
/// </para>
/// </summary>
public sealed class GameObjectLootDataModule : IDataModule
{
    /// <summary>The world schema version that introduces the game object and loot tables.</summary>
    public const int Version = 7;

    public static readonly IReadOnlyList<string> Tables =
    [
        "gameobject_template", "gameobject_spawn", "gameobject_questrelation", "gameobject_involvedrelation",
        "lock_template", "creature_loot_template", "gameobject_loot_template", "item_loot_template",
        "skinning_loot_template", "reference_loot_template", "creature_loot_info",
    ];

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [.. Tables.Select(t => new CreateTableChange(t))];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GameObjectTemplateRow>(entity =>
        {
            entity.ToTable("gameobject_template");
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).ValueGeneratedNever();

            // cmangos-classic: name varchar(100) NOT NULL.
            entity.Property(r => r.Name).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<GameObjectSpawnRow>(entity =>
        {
            entity.ToTable("gameobject_spawn");
            entity.HasKey(r => r.Guid);
            entity.Property(r => r.Guid).ValueGeneratedNever();
            entity.HasIndex(r => r.MapId);
        });

        modelBuilder.Entity<GameObjectQuestStarterRow>(entity =>
        {
            entity.ToTable("gameobject_questrelation");
            entity.HasKey(r => new { r.Id, r.Quest });
        });

        modelBuilder.Entity<GameObjectQuestEnderRow>(entity =>
        {
            entity.ToTable("gameobject_involvedrelation");
            entity.HasKey(r => new { r.Id, r.Quest });
        });

        modelBuilder.Entity<LockTemplateRow>(entity =>
        {
            entity.ToTable("lock_template");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
        });

        ConfigureLoot<CreatureLootTemplateRow>(modelBuilder, "creature_loot_template");
        ConfigureLoot<GameObjectLootTemplateRow>(modelBuilder, "gameobject_loot_template");
        ConfigureLoot<ItemLootTemplateRow>(modelBuilder, "item_loot_template");
        ConfigureLoot<SkinningLootTemplateRow>(modelBuilder, "skinning_loot_template");
        ConfigureLoot<ReferenceLootTemplateRow>(modelBuilder, "reference_loot_template");

        modelBuilder.Entity<CreatureLootInfoRow>(entity =>
        {
            entity.ToTable("creature_loot_info");
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services)
    {
        services.AddScoped<IGameObjectDataStore, EfGameObjectDataStore>();
        services.AddScoped<ILootDataStore, EfLootDataStore>();
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

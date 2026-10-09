using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.SpawnGroups;

/// <summary>
/// <c>gameobject_spawn_entry</c>: one game object entry a spawn row can become (cmangos-classic <c>guid, entry</c>,
/// ObjectMgr::LoadGameObjectSpawnEntry). classic-db z2815 has 5,001 rows for 1,809 spawns, every one with <c>gameobject.id = 0</c>.
/// </summary>
public sealed class GameObjectSpawnEntryRow
{
    public uint SpawnGuid { get; set; }

    public uint Entry { get; set; }
}

/// <summary>A cmangos <c>spawn_group</c> row (Maps/SpawnGroupDefines.h SpawnGroupEntry; ObjectMgr::LoadSpawnGroups).</summary>
public sealed class SpawnGroupRow
{
    public uint Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>0 creatures, 1 game objects.</summary>
    public uint Type { get; set; }

    public uint MaxCount { get; set; }

    /// <summary>A <c>conditions.condition_entry</c> the group needs (cmangos <c>WorldState</c>).</summary>
    public uint WorldState { get; set; }

    public uint WorldStateExpression { get; set; }

    public uint Flags { get; set; }

    public uint StringId { get; set; }
}

/// <summary>A <c>spawn_group_spawn</c> row: a creature or game object spawn of a group.</summary>
public sealed class SpawnGroupSpawnRow
{
    public uint Id { get; set; }

    public uint Guid { get; set; }

    /// <summary>The formation slot (0 the leader, -1 none).</summary>
    public int SlotId { get; set; } = -1;

    public uint Chance { get; set; }
}

/// <summary>A <c>spawn_group_entry</c> row: an entry the group's entry-less spawns may become.</summary>
public sealed class SpawnGroupEntryRow
{
    public uint Id { get; set; }

    public uint Entry { get; set; }

    public uint MinCount { get; set; }

    public uint MaxCount { get; set; }

    public uint Chance { get; set; }
}

/// <summary>A <c>spawn_group_formation</c> row (read by the creature map system's formations).</summary>
public sealed class SpawnGroupFormationRow
{
    public uint Id { get; set; }

    public byte FormationType { get; set; }

    public float FormationSpread { get; set; }

    public uint FormationOptions { get; set; }

    public uint PathId { get; set; }

    public byte MovementType { get; set; }

    public string? Comment { get; set; }
}

/// <summary>A <c>spawn_group_linked_group</c> row (empty in classic-db z2815).</summary>
public sealed class SpawnGroupLinkedGroupRow
{
    public uint Id { get; set; }

    public uint LinkedId { get; set; }
}

/// <summary>
/// The spawn-group world-schema step (<see cref="IDataModule"/>): <c>gameobject_spawn_entry</c> and the five cmangos spawn group tables
/// classic-db z2815 carries (<c>spawn_group</c>, <c>spawn_group_spawn</c>, <c>spawn_group_entry</c>, <c>spawn_group_formation</c>,
/// <c>spawn_group_linked_group</c>; it has no <c>spawn_group_squad</c> and no <c>RespawnOverrideMin/Max</c> columns).
/// <para>
/// <b>World version 44</b>: allocated in the spawn-groups lane after <c>DbScriptDataModule</c> (42) as 43; the script-engine lane also took 43 and
/// kept it (<c>CreatureScriptNameDataModule</c>), so wave 10 renumbered this step to 44 (docs/integration/seams.md). The number is named once here; tests read <see cref="Version"/>.
/// </para>
/// </summary>
public sealed class SpawnGroupDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 44;

    public const string GameObjectSpawnEntryTable = "gameobject_spawn_entry";
    public const string GroupTable = "spawn_group";
    public const string SpawnTable = "spawn_group_spawn";
    public const string EntryTable = "spawn_group_entry";
    public const string FormationTable = "spawn_group_formation";
    public const string LinkedGroupTable = "spawn_group_linked_group";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(GameObjectSpawnEntryTable),
        new CreateTableChange(GroupTable),
        new CreateTableChange(SpawnTable),
        new CreateTableChange(EntryTable),
        new CreateTableChange(FormationTable),
        new CreateTableChange(LinkedGroupTable),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GameObjectSpawnEntryRow>(entity =>
        {
            entity.ToTable(GameObjectSpawnEntryTable);
            entity.HasKey(r => new { r.SpawnGuid, r.Entry });
        });
        modelBuilder.Entity<SpawnGroupRow>(entity =>
        {
            entity.ToTable(GroupTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Name).HasMaxLength(200);
        });
        modelBuilder.Entity<SpawnGroupSpawnRow>(entity =>
        {
            entity.ToTable(SpawnTable);
            entity.HasKey(r => new { r.Id, r.Guid });
        });
        modelBuilder.Entity<SpawnGroupEntryRow>(entity =>
        {
            entity.ToTable(EntryTable);
            entity.HasKey(r => new { r.Id, r.Entry });
        });
        modelBuilder.Entity<SpawnGroupFormationRow>(entity =>
        {
            entity.ToTable(FormationTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Comment).HasMaxLength(255);
        });
        modelBuilder.Entity<SpawnGroupLinkedGroupRow>(entity =>
        {
            entity.ToTable(LinkedGroupTable);
            entity.HasKey(r => new { r.Id, r.LinkedId });
        });
    }

    public void AddServices(IServiceCollection services)
    {
    }
}

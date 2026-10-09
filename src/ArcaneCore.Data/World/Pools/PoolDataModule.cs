using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Pools;

/// <summary>
/// A cmangos <c>pool_template</c> row (Pools/PoolManager.cpp PoolManager::LoadFromDB: <c>SELECT entry, max_limit, description</c>): how many
/// of the pool's members (spawns and child pools together) may be in the world at once.
/// </summary>
public sealed class PoolTemplateRow
{
    public uint Entry { get; set; }

    /// <summary>The most members spawned at once. 0 spawns nothing (cmangos counts <c>limit - spawned</c>; the column comment's "no limit" is not what the code does).</summary>
    public uint MaxLimit { get; set; }

    public string Description { get; set; } = string.Empty;
}

/// <summary>A <c>pool_creature</c> or <c>pool_gameobject</c> row: one database spawn of a pool.</summary>
public abstract class PoolSpawnRowBase
{
    /// <summary>The <c>creature.guid</c> or <c>gameobject.guid</c>.</summary>
    public uint Guid { get; set; }

    public uint PoolEntry { get; set; }

    /// <summary>Percent; 0 is "equally chanced". A non-zero chance is explicit only in a pool whose max_limit is 1 (PoolGroup::AddEntry).</summary>
    public float Chance { get; set; }

    public string Description { get; set; } = string.Empty;
}

/// <summary><c>pool_creature</c>.</summary>
public sealed class PoolCreatureRow : PoolSpawnRowBase;

/// <summary><c>pool_gameobject</c>.</summary>
public sealed class PoolGameObjectRow : PoolSpawnRowBase;

/// <summary>A <c>pool_creature_template</c> or <c>pool_gameobject_template</c> row: every spawn of an entry belongs to the pool.</summary>
public abstract class PoolEntryRowBase
{
    /// <summary>The creature or game object entry (<c>id</c>) whose spawns all join the pool.</summary>
    public uint Id { get; set; }

    public uint PoolEntry { get; set; }

    public float Chance { get; set; }

    public string Description { get; set; } = string.Empty;
}

/// <summary><c>pool_creature_template</c>.</summary>
public sealed class PoolCreatureTemplateRow : PoolEntryRowBase;

/// <summary><c>pool_gameobject_template</c>.</summary>
public sealed class PoolGameObjectTemplateRow : PoolEntryRowBase;

/// <summary>A <c>pool_pool</c> row: a child pool of a mother pool (one level of nesting in classic-db z2815).</summary>
public sealed class PoolPoolRow
{
    /// <summary>The child pool (a child has one mother: the column is the key).</summary>
    public uint PoolId { get; set; }

    public uint MotherPool { get; set; }

    public float Chance { get; set; }

    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// The pool world-schema step (<see cref="IDataModule"/>): the six cmangos pool tables classic-db z2815 carries (<c>pool_template</c>,
/// <c>pool_creature</c>, <c>pool_creature_template</c>, <c>pool_gameobject</c>, <c>pool_gameobject_template</c>, <c>pool_pool</c>). The
/// dump has no <c>game_event_pool</c>: cmangos links a pool to a game event through the event rows of its members
/// (GameEventMgr::LoadFromDB), which the event tables already hold.
/// <para>
/// <b>World version 46</b>: assigned by the wave-10 plan (43 script-engine, 44 spawn groups, 45 movement-scripts, 46 pools). The
/// placeholder that held 45 on the pools branch was removed when movement-scripts merged. The number is named once here; tests read
/// <see cref="Version"/>.
/// </para>
/// </summary>
public sealed class PoolDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 46;

    public const string TemplateTable = "pool_template";
    public const string CreatureTable = "pool_creature";
    public const string CreatureTemplateTable = "pool_creature_template";
    public const string GameObjectTable = "pool_gameobject";
    public const string GameObjectTemplateTable = "pool_gameobject_template";
    public const string PoolPoolTable = "pool_pool";

    /// <summary>Every table of the step, in creation order.</summary>
    public static readonly IReadOnlyList<string> Tables =
        [TemplateTable, CreatureTable, CreatureTemplateTable, GameObjectTable, GameObjectTemplateTable, PoolPoolTable];

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [.. Tables.Select(t => new CreateTableChange(t))];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PoolTemplateRow>(entity =>
        {
            entity.ToTable(TemplateTable);
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).ValueGeneratedNever();
            entity.Property(r => r.Description).HasMaxLength(255);
        });
        modelBuilder.Entity<PoolCreatureRow>(entity =>
        {
            entity.ToTable(CreatureTable);
            entity.HasKey(r => r.Guid);
            entity.Property(r => r.Guid).ValueGeneratedNever();
            entity.Property(r => r.Description).HasMaxLength(255);
            entity.HasIndex(r => r.PoolEntry);
        });
        modelBuilder.Entity<PoolCreatureTemplateRow>(entity =>
        {
            entity.ToTable(CreatureTemplateTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Description).HasMaxLength(255);
            entity.HasIndex(r => r.PoolEntry);
        });
        modelBuilder.Entity<PoolGameObjectRow>(entity =>
        {
            entity.ToTable(GameObjectTable);
            entity.HasKey(r => r.Guid);
            entity.Property(r => r.Guid).ValueGeneratedNever();
            entity.Property(r => r.Description).HasMaxLength(255);
            entity.HasIndex(r => r.PoolEntry);
        });
        modelBuilder.Entity<PoolGameObjectTemplateRow>(entity =>
        {
            entity.ToTable(GameObjectTemplateTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Description).HasMaxLength(255);
            entity.HasIndex(r => r.PoolEntry);
        });
        modelBuilder.Entity<PoolPoolRow>(entity =>
        {
            entity.ToTable(PoolPoolTable);
            entity.HasKey(r => r.PoolId);
            entity.Property(r => r.PoolId).ValueGeneratedNever();
            entity.Property(r => r.Description).HasMaxLength(255);
            entity.HasIndex(r => r.MotherPool);
        });
    }

    public void AddServices(IServiceCollection services)
    {
    }
}

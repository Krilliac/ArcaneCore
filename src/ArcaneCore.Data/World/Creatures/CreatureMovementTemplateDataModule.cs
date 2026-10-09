using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// <c>creature_movement_template</c>: a waypoint path per creature <i>entry</i> (cmangos-classic <c>Entry, PathId, Point, PositionX/Y/Z,
/// Orientation, WaitTime, ScriptId</c>). A spawn with no <c>creature_movement</c> rows of its own walks the entry's path 0
/// (mangos-classic MotionGenerators/WaypointManager.h:69-93 GetDefaultPath, vmangos Movement/WaypointManager.h:77-93). classic-db has
/// 15,402 rows on 544 paths of 479 entries; 319 of its 2,898 waypoint spawns have no path of their own.
/// <para>
/// <c>ScriptId</c> and <c>Comment</c> are not kept: nothing in ArcaneCore runs creature-movement scripts, and the importer reports how many nodes carried one.
/// </para>
/// </summary>
public sealed class CreatureMovementTemplateRow
{
    public uint Entry { get; set; }

    /// <summary>Path number within the entry; 0 is the default path (the others are reachable only by scripts).</summary>
    public uint PathId { get; set; }

    /// <summary>Node id, 1-based in the data; need not be contiguous.</summary>
    public uint Point { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    /// <summary>Facing on arrival; 100 means "keep the travel direction".</summary>
    public float Orientation { get; set; }

    public uint WaitTimeMs { get; set; }

    /// <summary>Creature movement DB script started on arrival.</summary>
    public uint ScriptId { get; set; }
}

/// <summary>
/// The creature movement template world-schema step (<see cref="IDataModule"/>): one new table, <c>creature_movement_template</c>.
/// <para>
/// <b>World version 23</b>: allocated as 21 (the next free number on the wave-3 base) and renumbered at wave-4 integration (NPC metadata 21, reputation templates 22). It is named once here; tests
/// read <see cref="Version"/> or <c>WorldDbContext.Schema.CurrentVersion</c>, never a literal, and the integration lead renumbers in merge
/// order (docs/integration/seams.md).
/// </para>
/// </summary>
public sealed class CreatureMovementTemplateDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 23;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange("creature_movement_template")];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CreatureMovementTemplateRow>(entity =>
        {
            entity.ToTable("creature_movement_template");
            entity.HasKey(r => new { r.Entry, r.PathId, r.Point });
        });
    }

    public void AddServices(IServiceCollection services)
    {
    }
}

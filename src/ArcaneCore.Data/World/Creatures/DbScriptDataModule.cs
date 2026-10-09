using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// One row of a cmangos DB script table other than the relays (<c>dbscripts_on_quest_start</c>, <c>_quest_end</c>, <c>_gossip</c>,
/// <c>_event</c>): the <c>dbscripts_on_relay</c> layout (mangos.sql: <c>id, delay, priority, command, datalong, datalong2, datalong3,
/// buddy_entry, search_radius, data_flags, dataint, dataint2, dataint3, dataint4, datafloat, x, y, z, o, speed, condition_id,
/// comments</c>). The tables have no key: <see cref="Ordinal"/> is the row's place in the dump among the rows of its id. The comment is not
/// kept. Each table is its own id namespace (mangos-classic ScriptMgr::LoadScripts, DBScripts/ScriptMgr.cpp).
/// </summary>
public abstract class DbScriptRow
{
    public uint Id { get; set; }

    public uint Ordinal { get; set; }

    public uint Delay { get; set; }

    public uint Priority { get; set; }

    public uint Command { get; set; }

    public uint DataLong { get; set; }

    public uint DataLong2 { get; set; }

    public uint DataLong3 { get; set; }

    public uint BuddyEntry { get; set; }

    public uint SearchRadius { get; set; }

    public uint DataFlags { get; set; }

    public int DataInt { get; set; }

    public int DataInt2 { get; set; }

    public int DataInt3 { get; set; }

    public int DataInt4 { get; set; }

    public float DataFloat { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public float O { get; set; }

    public float Speed { get; set; }

    public uint ConditionId { get; set; }
}

/// <summary><c>dbscripts_on_quest_start</c>: run when a quest whose <c>quest_template.StartScript</c> names it is taken (classic-db z2815: 629 rows).</summary>
public sealed class QuestStartScriptRow : DbScriptRow;

/// <summary><c>dbscripts_on_quest_end</c>: run when a quest whose <c>quest_template.CompleteScript</c> names it is rewarded (z2815: 1,892 rows).</summary>
public sealed class QuestEndScriptRow : DbScriptRow;

/// <summary><c>dbscripts_on_gossip</c>: run by a <c>gossip_menu.script_id</c> or <c>gossip_menu_option.action_script_id</c> (z2815: 403 rows).</summary>
public sealed class GossipScriptRow : DbScriptRow;

/// <summary><c>dbscripts_on_event</c>: run by a spell or game object event id (z2815: 453 rows).</summary>
public sealed class EventScriptRow : DbScriptRow;

/// <summary><c>dbscripts_on_creature_movement</c>: run when a scripted waypoint is reached.</summary>
public sealed class CreatureMovementScriptRow : DbScriptRow;

/// <summary>
/// One <c>script_waypoint</c> row (ScriptDev2's escort paths; mangos-classic <c>Entry, PathId, Point, PositionX/Y/Z, Orientation, WaitTime,
/// ScriptId, Comment</c>; SystemMgr::LoadScriptWaypoints, AI/ScriptDevAI/system/system.cpp:63-121). The comment is not kept.
/// classic-db z2815: 1,523 rows on 61 entries.
/// </summary>
public sealed class ScriptWaypointRow
{
    public uint Entry { get; set; }

    public uint PathId { get; set; }

    public uint Point { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public float Orientation { get; set; }

    public uint WaitTimeMs { get; set; }

    public uint ScriptId { get; set; }
}

/// <summary>
/// The quest, gossip and event DB script world-schema step (<see cref="IDataModule"/>): the four cmangos script tables
/// <c>dbscripts_on_quest_start</c>, <c>dbscripts_on_quest_end</c>, <c>dbscripts_on_gossip</c> and <c>dbscripts_on_event</c>, ScriptDev2's
/// <c>script_waypoint</c>, and the columns that name the scripts: <c>quest_template.StartScript</c> and <c>CompleteScript</c>,
/// <c>gossip_menu.script_id</c> and <c>gossip_menu_option.action_script_id</c> (docs/areas/creature-ai.md, "Quest, gossip and event scripts").
/// <para>
/// <b>World version 42</b>: assigned to the quest-scripts lane (world was 41 on main). Tests read <see cref="Version"/>, never a literal.
/// </para>
/// </summary>
public sealed class DbScriptDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 42;

    public const string QuestStartTable = "dbscripts_on_quest_start";

    public const string QuestEndTable = "dbscripts_on_quest_end";

    public const string GossipTable = "dbscripts_on_gossip";

    public const string EventTable = "dbscripts_on_event";

    public const string WaypointTable = "script_waypoint";
    public const string CreatureMovementTable = "dbscripts_on_creature_movement";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(QuestStartTable),
        new CreateTableChange(QuestEndTable),
        new CreateTableChange(GossipTable),
        new CreateTableChange(EventTable),
        new CreateTableChange(WaypointTable),
        new AddColumnChange("quest_template", nameof(QuestTemplate.StartScript)),
        new AddColumnChange("quest_template", nameof(QuestTemplate.CompleteScript)),
        new AddColumnChange("gossip_menu", "script_id"),
        new AddColumnChange("gossip_menu_option", "action_script_id"),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        Script<QuestStartScriptRow>(modelBuilder, QuestStartTable);
        Script<QuestEndScriptRow>(modelBuilder, QuestEndTable);
        Script<GossipScriptRow>(modelBuilder, GossipTable);
        Script<EventScriptRow>(modelBuilder, EventTable);
        modelBuilder.Entity<ScriptWaypointRow>(entity =>
        {
            entity.ToTable(WaypointTable);
            entity.HasKey(r => new { r.Entry, r.PathId, r.Point });
            entity.Property(r => r.Entry).ValueGeneratedNever();
        });

        // The script columns of the quest module's tables (QuestNpcWorldModule maps the rest).
        modelBuilder.Entity<GossipMenu>().Property(r => r.ScriptId).HasColumnName("script_id");
        modelBuilder.Entity<GossipMenuOption>().Property(r => r.ActionScriptId).HasColumnName("action_script_id");
    }

    public void AddServices(IServiceCollection services)
    {
    }

    /// <summary>The kernel step of a stored row (the relay step record: the four tables share the relay layout and executor).</summary>
    public static RelayScriptStep ToStep(DbScriptRow r) => new(
        r.Id, r.Delay, r.Priority, r.Command, r.DataLong, r.DataLong2, r.DataLong3, r.BuddyEntry, r.SearchRadius, r.DataFlags,
        r.DataInt, r.DataInt2, r.DataInt3, r.DataInt4, r.DataFloat, r.X, r.Y, r.Z, r.O, r.Speed, r.ConditionId, r.Ordinal);

    /// <summary>The kernel waypoint of a stored <c>script_waypoint</c> row.</summary>
    public static (uint Entry, uint PathId, CreatureWaypoint Point) ToWaypoint(ScriptWaypointRow r)
        => (r.Entry, r.PathId, new CreatureWaypoint(r.Point, r.X, r.Y, r.Z, r.Orientation, r.WaitTimeMs) { ScriptId = r.ScriptId });

    /// <summary>The script kind a row type stores.</summary>
    public static DbScriptKind KindOf(DbScriptRow row) => row switch
    {
        QuestStartScriptRow => DbScriptKind.QuestStart,
        QuestEndScriptRow => DbScriptKind.QuestEnd,
        GossipScriptRow => DbScriptKind.Gossip,
        EventScriptRow => DbScriptKind.Event,
        CreatureMovementScriptRow => DbScriptKind.CreatureMovement,
        _ => throw new ArgumentOutOfRangeException(nameof(row), row.GetType().Name, "not a DB script table row"),
    };

    /// <summary>A new, empty row of the table of <paramref name="kind"/>.</summary>
    public static DbScriptRow NewRow(DbScriptKind kind) => kind switch
    {
        DbScriptKind.QuestStart => new QuestStartScriptRow(),
        DbScriptKind.QuestEnd => new QuestEndScriptRow(),
        DbScriptKind.Gossip => new GossipScriptRow(),
        DbScriptKind.Event => new EventScriptRow(),
        DbScriptKind.CreatureMovement => new CreatureMovementScriptRow(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "the relay scripts have their own table (RelayScriptDataModule)"),
    };

    private static void Script<T>(ModelBuilder modelBuilder, string table)
        where T : DbScriptRow
        => modelBuilder.Entity<T>(entity =>
        {
            entity.ToTable(table);
            entity.HasKey(r => new { r.Id, r.Ordinal });
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Ordinal).ValueGeneratedNever();
        });
}

using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// One <c>dbscripts_on_relay</c> row (cmangos-classic mangos.sql: <c>id, delay, priority, command, datalong, datalong2, datalong3,
/// buddy_entry, search_radius, data_flags, dataint, dataint2, dataint3, dataint4, datafloat, x, y, z, o, speed, condition_id,
/// comments</c>). <see cref="Ordinal"/> is the row's place in the dump among rows of the same id (the table has no key); the comment is
/// not kept. classic-db z2815: 828 rows; EventAI's START_RELAY_SCRIPT reaches 109 of its ids.
/// </summary>
public sealed class RelayScriptRow
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

/// <summary>One relay choice: a <c>dbscript_random_templates</c> row of type 1 (cmangos RELAY_TEMPLATE): template id, relay id, chance.</summary>
public sealed class RelayScriptTemplateRow
{
    public uint Id { get; set; }

    public uint RelayId { get; set; }

    public uint Chance { get; set; }
}

/// <summary>
/// The relay DB script world-schema step (<see cref="IDataModule"/>): two new tables, <c>dbscripts_on_relay</c> and
/// <c>dbscript_relay_template</c> (the type-1 rows of cmangos <c>dbscript_random_templates</c>; the type-0 string rows stay in
/// <c>creature_ai_text_template</c>). What EventAI's START_RELAY_SCRIPT action (53) runs (docs/areas/creature-ai.md, "Relay scripts").
/// <para>
/// <b>World version 40</b>: the number the wave-2 plan reserves for the creature-ai lane. Versions must be contiguous, so this branch
/// holds 38 and 39 open with empty steps (<see cref="CreatureAiLaneSchemaGap38"/>); tests read <see cref="Version"/>, never a literal.
/// </para>
/// </summary>
public sealed class RelayScriptDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 40;

    public const string ScriptTable = "dbscripts_on_relay";

    public const string TemplateTable = "dbscript_relay_template";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(ScriptTable), new CreateTableChange(TemplateTable)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RelayScriptRow>(entity =>
        {
            entity.ToTable(ScriptTable);
            entity.HasKey(r => new { r.Id, r.Ordinal });
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Ordinal).ValueGeneratedNever();
        });
        modelBuilder.Entity<RelayScriptTemplateRow>(entity =>
        {
            entity.ToTable(TemplateTable);
            entity.HasKey(r => new { r.Id, r.RelayId });
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.RelayId).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services)
    {
    }

    /// <summary>The kernel step of a stored row.</summary>
    public static RelayScriptStep ToStep(RelayScriptRow r) => new(
        r.Id, r.Delay, r.Priority, r.Command, r.DataLong, r.DataLong2, r.DataLong3, r.BuddyEntry, r.SearchRadius, r.DataFlags,
        r.DataInt, r.DataInt2, r.DataInt3, r.DataInt4, r.DataFloat, r.X, r.Y, r.Z, r.O, r.Speed, r.ConditionId, r.Ordinal);
}

/// <summary>
/// Empty world schema steps 38 and 39: the wave-2 plan reserves them for other lanes and gives the creature-ai lane 40, but
/// <see cref="DataModules.Compose"/> requires contiguous versions, so this branch holds the gap open with steps that change nothing.
/// INTEGRATOR: the w2-ops-social branch adds <c>IReservedSchemaGap</c>/<c>ReservedSchemaGap</c> (Schema/ReservedSchemaGaps.cs), which
/// Compose drops when a real module claims the version; once it is merged, fold these classes (and every other lane's world gap
/// classes) into that one scheme. Delete each placeholder whose number a merged lane really uses (Compose reports "claimed twice" until
/// you do). For a number nobody claims, either renumber the real modules down before any live database is upgraded, or keep the empty
/// step for good and never give its number to a later module: a database upgraded through an empty step records the version as
/// applied, so a real step that later takes the number would never run there and its tables would never be created.
/// </summary>
public abstract class CreatureAiLaneSchemaGap(int version) : IDataModule
{
    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion { get; } = version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
    }

    public void AddServices(IServiceCollection services)
    {
    }
}

/// <summary>World step 38 held open for the lane that owns it (see <see cref="CreatureAiLaneSchemaGap"/>).</summary>
public sealed class CreatureAiLaneSchemaGap38() : CreatureAiLaneSchemaGap(Version)
{
    public const int Version = 38;
}

/// <summary>World step 39 held open for the lane that owns it (see <see cref="CreatureAiLaneSchemaGap"/>).</summary>
public sealed class CreatureAiLaneSchemaGap39() : CreatureAiLaneSchemaGap(Version)
{
    public const int Version = 39;
}

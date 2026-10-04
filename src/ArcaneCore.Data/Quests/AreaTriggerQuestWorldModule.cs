using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Quests;

/// <summary>
/// <c>areatrigger_involvedrelation</c> row (vmangos / classic-db: <c>id</c> = <c>areatrigger_template</c> id,
/// <c>quest</c> = the exploration quest it credits).
/// </summary>
public sealed class AreaTriggerQuestRow
{
    public uint Id { get; set; }

    public uint Quest { get; set; }
}

/// <summary>
/// World schema step of the area-trigger lane (docs/areas/area-triggers.md): the <c>areatrigger_involvedrelation</c> table that
/// credits exploration quests, and the entry-requirement columns of <c>areatrigger_teleport</c> (<c>RequiredItem</c>,
/// <c>RequiredItem2</c>, <c>RequiredQuestDone</c>, <c>RequiredCondition</c>). The columns are properties of
/// <see cref="AreaTriggerTeleportRow"/>, mapped by the map module's entity, so a database created before the step gains them here and
/// a fresh one already has them (the step is then skipped column by column). The quest store reads the relation table through
/// <see cref="Kernel.Quests.IQuestContentStore"/>, so <c>.reload quest_template</c> reloads it with the quests. No cleanup
/// registration: the world schema holds no per-character rows.
/// </summary>
public sealed class AreaTriggerQuestWorldModule : IDataModule
{
    /// <summary>The world schema version of this step (renumbered by the integrator when other world steps merge first).</summary>
    public const int Version = 29;

    public const string RelationTable = "areatrigger_involvedrelation";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(RelationTable),
        new AddColumnChange(MapDataModule.AreaTriggerTeleportTable, nameof(AreaTriggerTeleportRow.RequiredItem)),
        new AddColumnChange(MapDataModule.AreaTriggerTeleportTable, nameof(AreaTriggerTeleportRow.RequiredItem2)),
        new AddColumnChange(MapDataModule.AreaTriggerTeleportTable, nameof(AreaTriggerTeleportRow.RequiredQuestDone)),
        new AddColumnChange(MapDataModule.AreaTriggerTeleportTable, nameof(AreaTriggerTeleportRow.RequiredCondition)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AreaTriggerQuestRow>(entity =>
        {
            entity.ToTable(RelationTable);
            entity.HasKey(r => new { r.Id, r.Quest });
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.Quest).HasColumnName("quest").ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services)
    {
    }
}

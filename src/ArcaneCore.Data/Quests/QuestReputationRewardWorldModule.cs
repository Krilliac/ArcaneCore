using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Quests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Quests;

/// <summary>
/// World schema version 9 (docs/integration/quest-progression.md, quest reputation rewards): the ten
/// vmangos <c>quest_template</c> reward-reputation columns RewRepFaction1..5 and RewRepValue1..5.
/// The columns are properties of <see cref="QuestTemplate"/>, mapped by the quest module's entity, so this
/// module only adds the columns to databases created before the step; the numbered step is skipped
/// column by column when a fresh database already has them. No cleanup registration: the world
/// schema holds no per-character rows.
/// </summary>
public sealed class QuestReputationRewardWorldModule : IDataModule
{
    /// <summary>The world schema version of this step (renumbered by the integrator when other world steps merge first).</summary>
    public const int Version = 9;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("quest_template", nameof(QuestTemplate.RewRepFaction1)),
        new AddColumnChange("quest_template", nameof(QuestTemplate.RewRepFaction2)),
        new AddColumnChange("quest_template", nameof(QuestTemplate.RewRepFaction3)),
        new AddColumnChange("quest_template", nameof(QuestTemplate.RewRepFaction4)),
        new AddColumnChange("quest_template", nameof(QuestTemplate.RewRepFaction5)),
        new AddColumnChange("quest_template", nameof(QuestTemplate.RewRepValue1)),
        new AddColumnChange("quest_template", nameof(QuestTemplate.RewRepValue2)),
        new AddColumnChange("quest_template", nameof(QuestTemplate.RewRepValue3)),
        new AddColumnChange("quest_template", nameof(QuestTemplate.RewRepValue4)),
        new AddColumnChange("quest_template", nameof(QuestTemplate.RewRepValue5)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
    }

    public void AddServices(IServiceCollection services)
    {
    }
}

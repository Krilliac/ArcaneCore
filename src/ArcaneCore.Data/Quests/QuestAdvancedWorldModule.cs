using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Quests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Quests;

/// <summary>
/// World schema step of the advanced-quests lane (docs/integration/quests-advanced.md): the two vmangos
/// <c>quest_template</c> reward-mail columns <c>RewMailTemplateId</c> and <c>RewMailDelaySecs</c>
/// (ObjectMgr.cpp:5558 LoadQuests select). They are properties of <see cref="QuestTemplate"/>, mapped by the
/// quest module's entity, so this module only adds the columns to databases created before the step; a fresh
/// database already has them and the step is skipped column by column. Nothing delivers the mail yet: the
/// support gate reads the template to withhold mail quests rather than reward them without their mail.
/// No cleanup registration: the world schema holds no per-character rows.
/// </summary>
public sealed class QuestAdvancedWorldModule : IDataModule
{
    /// <summary>The world schema version of this step (renumbered by the integrator when other world steps merge first).</summary>
    public const int Version = 21;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("quest_template", nameof(QuestTemplate.RewMailTemplateId)),
        new AddColumnChange("quest_template", nameof(QuestTemplate.RewMailDelaySecs)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
    }

    public void AddServices(IServiceCollection services)
    {
    }
}

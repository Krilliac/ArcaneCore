using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// NPC service metadata from vmangos creature_template (CreatureDefines.h:242,272-275).
/// These columns let imported class, profession, weapon-skill and mount trainers use their
/// real type/class/race without an operator-maintained configuration list.
/// </summary>
public sealed class CreatureNpcMetadataDataModule : IDataModule
{
    public const int Version = 21;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.GossipMenuId)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.TrainerType)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.TrainerClass)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.TrainerRace)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.TrainerSpell)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        // CreatureDataModule maps the same row; the five new scalar properties use its conventions.
    }

    public void AddServices(IServiceCollection services)
    {
        // Existing creature data services read this metadata with each template.
    }
}

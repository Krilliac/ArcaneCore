using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>World schema 23: persist vmangos per-display template scale overrides.</summary>
public sealed class CreatureDisplayScaleDataModule : IDataModule
{
    public const int Version = 23;
    public DatabaseComponent Component => DatabaseComponent.World;
    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.DisplayScale2)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.DisplayScale3)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.DisplayScale4)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        // CreatureDataModule owns the table mapping; these properties are discovered by EF.
    }

    public void AddServices(IServiceCollection services)
    {
    }
}

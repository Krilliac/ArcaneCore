using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>World 43: retain ClassicDB's creature_template.ScriptName for named ScriptDev2 AI selection.</summary>
public sealed class CreatureScriptNameDataModule : IDataModule
{
    public const int Version = 43;
    public DatabaseComponent Component => DatabaseComponent.World;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
        [new AddColumnChange("creature_template", nameof(CreatureTemplateRow.ScriptName))];

    public void ConfigureModel(ModelBuilder modelBuilder) { }
    public void AddServices(IServiceCollection services) { }
}

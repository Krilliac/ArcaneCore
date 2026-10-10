using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// World version 51: smartai-2 lane. <c>smart_scripts.ConditionId</c>, the cmangos <c>conditions</c> row (<c>condition_entry</c>) that gates a smart
/// event; 0 means none. The column is a property of <see cref="SmartScriptDbRow"/>, configured by <see cref="SmartScriptDataModule"/>; the step only
/// adds it to a database created at world version 48-50 (docs/integration/smartai-slice2-20261010.md).
/// </summary>
public sealed class SmartScriptConditionDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 51;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange(SmartScriptDataModule.Table, nameof(SmartScriptDbRow.ConditionId)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
    }

    public void AddServices(IServiceCollection services)
    {
    }
}

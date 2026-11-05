using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// World schema 69: <c>spell_script_target.ConditionId</c>, the <c>conditions</c> row (vmangos <c>conditionId</c>, SpellMgr.cpp:2068) a listed
/// unit or game object must satisfy to be selected; 0 means none. The column is a property of <see cref="SpellScriptTargetRow"/> (its table is
/// configured by <see cref="MovementScriptDataModule"/>); the step only adds it to a database created at world version 45-68. A database
/// upgraded to 69 holds 0 in every row until <c>arcane-db refresh</c> re-reads the dump
/// (<see cref="CreatureDumpImporter.RefreshSpellScriptTargetsAsync"/>).
/// </summary>
public sealed class SpellScriptTargetConditionDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 69;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("spell_script_target", nameof(SpellScriptTargetRow.ConditionId)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
    }

    public void AddServices(IServiceCollection services)
    {
    }
}

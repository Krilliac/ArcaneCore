using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// World schema step 70: <c>dbscripts_on_go_use</c> and <c>dbscripts_on_go_template_use</c>, the game object DB scripts (mangos-classic
/// ScriptMgr::LoadScriptMap(SCRIPT_TYPE_GAMEOBJECT / SCRIPT_TYPE_GAMEOBJECT_TEMPLATE), DBScripts/ScriptMgr.cpp:1058-1078, run by
/// GameObject::Use, Entities/GameObject.cpp:1490 and :1506/:1529/:1703). The rows have the <c>dbscripts_on_relay</c> layout
/// (<see cref="DbScriptRow"/>) and run on the same map executor as the other DB script tables. <c>dbscripts_on_go_use</c> is keyed by the
/// <c>gameobject.guid</c> of the object used (<see cref="GameObjectUseScriptRow"/>), <c>dbscripts_on_go_template_use</c> by its template entry
/// (<see cref="GameObjectTemplateUseScriptRow"/>).
/// <para>
/// <b>World version 70</b>, after the spell script target condition column (69). Tests read <see cref="Version"/>, never a literal.
/// </para>
/// </summary>
public sealed class DbGameObjectScriptDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = SpellScriptTargetConditionDataModule.Version + 1;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(DbScriptDataModule.GameObjectUseTable),
        new CreateTableChange(DbScriptDataModule.GameObjectTemplateUseTable),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        Script<GameObjectUseScriptRow>(modelBuilder, DbScriptDataModule.GameObjectUseTable);
        Script<GameObjectTemplateUseScriptRow>(modelBuilder, DbScriptDataModule.GameObjectTemplateUseTable);
    }

    public void AddServices(IServiceCollection services)
    {
    }

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

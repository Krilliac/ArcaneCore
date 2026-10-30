using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// World schema step 65: <c>dbscripts_on_spell</c>, the spell DB scripts (cmangos ScriptMgr::LoadScriptMap(SCRIPT_TYPE_SPELL),
/// DBScripts/ScriptMgr.cpp:1001-1060; vmangos ScriptMgr::LoadSpellScripts, ScriptMgr.cpp:1510-1556). The rows have the
/// <c>dbscripts_on_relay</c> layout (<see cref="DbScriptRow"/>) and run on the same map executor as the other DB script tables; a spell's
/// DUMMY or SCRIPT_EFFECT effect starts them (SpellSystem.DbSpellScripts.cs).
/// <para>
/// <b>World version 65</b>, after the tuning tables (63) and the playerbot ladder (64). Tests read <see cref="Version"/>, never a
/// literal.
/// </para>
/// </summary>
public sealed class DbSpellScriptDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 65;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(DbScriptDataModule.SpellTable)];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<SpellScriptRow>(entity =>
        {
            entity.ToTable(DbScriptDataModule.SpellTable);
            entity.HasKey(r => new { r.Id, r.Ordinal });
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Ordinal).ValueGeneratedNever();
        });

    public void AddServices(IServiceCollection services)
    {
    }
}

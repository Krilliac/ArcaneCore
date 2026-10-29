using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// World schema step 62: <c>dbscripts_on_spell</c>, the spell DB scripts (cmangos ScriptMgr::LoadScriptMap(SCRIPT_TYPE_SPELL),
/// DBScripts/ScriptMgr.cpp:1001-1060; vmangos ScriptMgr::LoadSpellScripts, ScriptMgr.cpp:1510-1556). The rows have the
/// <c>dbscripts_on_relay</c> layout (<see cref="DbScriptRow"/>) and run on the same map executor as the other DB script tables; a spell's
/// DUMMY or SCRIPT_EFFECT effect starts them (SpellSystem.DbSpellScripts.cs).
/// <para>
/// <b>World version 62</b>: main was at 59 and open PRs claim 60 and 61 (#1711 npc_text_broadcast, #1701 creature immunities), so this
/// branch holds those two open with <see cref="WorldSchemaGap60"/> and <see cref="WorldSchemaGap61"/>. Tests read
/// <see cref="Version"/>, never a literal.
/// </para>
/// </summary>
public sealed class DbSpellScriptDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 62;

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

/// <summary>
/// World 60, reserved for an open PR (#1711 and #1701 both claim it). Compose drops this placeholder as soon as a real module claims 60.
/// INTEGRATOR: delete it when the version is owned in the tree, or renumber <see cref="DbSpellScriptDataModule"/> down.
/// </summary>
public sealed class WorldSchemaGap60 : ReservedSchemaGap
{
    public override DatabaseComponent Component => DatabaseComponent.World;

    public override int SchemaVersion => 60;
}

/// <summary>World 61, reserved for an open PR. INTEGRATOR: as <see cref="WorldSchemaGap60"/>.</summary>
public sealed class WorldSchemaGap61 : ReservedSchemaGap
{
    public override DatabaseComponent Component => DatabaseComponent.World;

    public override int SchemaVersion => 61;
}

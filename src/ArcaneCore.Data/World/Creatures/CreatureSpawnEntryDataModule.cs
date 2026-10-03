using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// <c>creature_spawn_entry</c>: one creature entry a spawn row can become (cmangos-classic <c>guid, entry</c>; vmangos spells them
/// <c>creature.id</c>, <c>id2</c> ... <c>id5</c>). The entry of the object is chosen from them when it loads and again at every respawn
/// (cmangos Creature::LoadFromDB / ResetEntry, vmangos CreatureData::ChooseCreatureId). classic-db has 4,863 rows for 2,280 spawns;
/// 2,234 of them have <c>creature.id = 0</c> and cannot spawn without their rows.
/// </summary>
public sealed class CreatureSpawnEntryRow
{
    public uint SpawnGuid { get; set; }

    public uint Entry { get; set; }
}

/// <summary>
/// The creature spawn entry world-schema step (<see cref="IDataModule"/>): one new table, <c>creature_spawn_entry</c>.
/// <para>
/// <b>World version 22</b>: the number after <see cref="CreatureMovementTemplateDataModule"/> (21) in this lane. It is named once here; tests
/// read <see cref="Version"/> or <c>WorldDbContext.Schema.CurrentVersion</c>, never a literal, and the integration lead renumbers in merge
/// order (docs/integration/seams.md).
/// </para>
/// </summary>
public sealed class CreatureSpawnEntryDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 22;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange("creature_spawn_entry")];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CreatureSpawnEntryRow>(entity =>
        {
            entity.ToTable("creature_spawn_entry");
            entity.HasKey(r => new { r.SpawnGuid, r.Entry });
        });
    }

    public void AddServices(IServiceCollection services)
    {
    }
}

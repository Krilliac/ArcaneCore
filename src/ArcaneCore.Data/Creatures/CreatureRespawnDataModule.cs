using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Creatures;

/// <summary>
/// The durable respawn time of one dead spawn (vmangos <c>creature_respawn</c>: guid, respawn_time, instance, map;
/// sql/characters.sql:472-480). The key is the spawn guid within a map instance (0 = the shared world copy).
/// </summary>
public sealed class CreatureRespawnRow
{
    public int InstanceId { get; set; }

    public uint SpawnGuid { get; set; }

    public uint MapId { get; set; }

    /// <summary>Unix seconds.</summary>
    public long RespawnTime { get; set; }
}

/// <summary>
/// Creature respawn times of the characters database: a restart no longer brings every dead rare and boss back to life
/// (docs/areas/creature-movement-spawns.md).
/// <para>
/// <b>Characters version 24</b>: allocated as 21 (the next free number on the wave-3 base) and renumbered at wave-4 integration (bank 21, taxi flight 22, honor 23). It is named once here; tests
/// read <see cref="Version"/> or <c>CharacterDbContext.Schema.CurrentVersion</c>, never a literal, and the integration lead renumbers in
/// merge order (docs/integration/seams.md).
/// </para>
/// <para>
/// The rows belong to spawns and instances, not to characters, so deleting a character removes nothing (<see cref="ICharacterDataCleanup"/>
/// is a deliberate no-op). Explicit lower-case column names keep PostgreSQL's identifier folding out of the picture.
/// </para>
/// </summary>
public sealed class CreatureRespawnDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The single version constant the lead renumbers.</summary>
    public const int Version = 24;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange("creature_respawn")];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CreatureRespawnRow>(entity =>
        {
            entity.ToTable("creature_respawn");
            entity.HasKey(r => new { r.InstanceId, r.SpawnGuid });
            entity.Property(r => r.InstanceId).HasColumnName("instance_id").ValueGeneratedNever();
            entity.Property(r => r.SpawnGuid).HasColumnName("spawn_guid").ValueGeneratedNever();
            entity.Property(r => r.MapId).HasColumnName("map_id");
            entity.Property(r => r.RespawnTime).HasColumnName("respawn_time");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ICreatureRespawnStore, EfCreatureRespawnStore>();

    /// <summary>Nothing to remove: the rows are keyed by spawn and instance, never by character.</summary>
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

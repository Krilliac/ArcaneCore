using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.GameObjects;

/// <summary>
/// The durable respawn time of one despawned game object spawn (vmangos <c>gameobject_respawn</c>: guid, respawn_time, instance, map,
/// written by MapPersistentState::SaveGORespawnTime, Maps/MapPersistentStateMgr.cpp:103-123). The key is the spawn guid within a map
/// instance (0 = the shared world copy).
/// </summary>
public sealed class GameObjectRespawnRow
{
    public int InstanceId { get; set; }

    public uint SpawnGuid { get; set; }

    public uint MapId { get; set; }

    /// <summary>Unix seconds.</summary>
    public long RespawnTime { get; set; }
}

/// <summary>
/// Game object respawn times of the characters database: a restart no longer brings a looted chest or a mined node back at once. The
/// counterpart of <see cref="Creatures.CreatureRespawnDataModule"/> (docs/areas/creature-movement-spawns.md).
/// <para>
/// <b>Characters version 68</b>: the next free number after realm authority leases (67). It is named once here; tests read
/// <see cref="Version"/> or <c>CharacterDbContext.Schema.CurrentVersion</c>, never a literal, and the integration lead renumbers in merge order
/// (docs/integration/seams.md).
/// </para>
/// <para>
/// The rows belong to spawns and instances, not to characters, so deleting a character removes nothing (<see cref="ICharacterDataCleanup"/>
/// is a deliberate no-op). Explicit lower-case column names keep PostgreSQL's identifier folding out of the picture.
/// </para>
/// </summary>
public sealed class GameObjectRespawnDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The single version constant the lead renumbers.</summary>
    public const int Version = 68;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange("gameobject_respawn")];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GameObjectRespawnRow>(entity =>
        {
            entity.ToTable("gameobject_respawn");
            entity.HasKey(r => new { r.InstanceId, r.SpawnGuid });
            entity.Property(r => r.InstanceId).HasColumnName("instance_id").ValueGeneratedNever();
            entity.Property(r => r.SpawnGuid).HasColumnName("spawn_guid").ValueGeneratedNever();
            entity.Property(r => r.MapId).HasColumnName("map_id");
            entity.Property(r => r.RespawnTime).HasColumnName("respawn_time");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IGameObjectRespawnStore, EfGameObjectRespawnStore>();

    /// <summary>Nothing to remove: the rows are keyed by spawn and instance, never by character.</summary>
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

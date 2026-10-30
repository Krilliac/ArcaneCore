namespace ArcaneCore.Kernel.WorldData.GameObjects;

/// <summary>
/// The durable respawn time of one despawned game object spawn (vmangos <c>gameobject_respawn</c>: <c>guid, respawn_time, instance, map</c>,
/// table <c>gameobject_respawn</c>, written by MapPersistentState::SaveGORespawnTime, Maps/MapPersistentStateMgr.cpp:103-123). <see cref="RespawnTime"/> is unix seconds. <see cref="InstanceId"/> is 0 for the shared copy of a map.
/// </summary>
public sealed record GameObjectRespawnRecord(uint MapId, uint InstanceId, uint SpawnGuid, long RespawnTime);

/// <summary>The key of a <see cref="GameObjectRespawnRecord"/>: a spawn guid within one map instance.</summary>
public readonly record struct GameObjectRespawnKey(uint InstanceId, uint SpawnGuid);

/// <summary>
/// Durable game object respawn times (the characters database). Used by one writer at a time (the world's respawn queue); callers never
/// await it from the world thread.
/// </summary>
public interface IGameObjectRespawnStore
{
    /// <summary>
    /// Every stored respawn time that is still in the future at <paramref name="nowUnixSeconds"/>. Rows whose time has passed, and rows of
    /// a dungeon instance that no longer exists, are deleted first (vmangos MapPersistentStateManager::LoadGameobjectRespawnTimes drops
    /// expired rows, Maps/MapPersistentStateMgr.cpp:1149-1156).
    /// </summary>
    Task<IReadOnlyList<GameObjectRespawnRecord>> LoadAsync(long nowUnixSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply <paramref name="upserts"/> (insert or replace by key) and <paramref name="deletes"/> in one transaction. A key in both is
    /// deleted. The replace semantics are the store's own upsert, not provider SQL (<c>REPLACE INTO</c> does not exist on PostgreSQL).
    /// </summary>
    Task SaveAsync(
        IReadOnlyCollection<GameObjectRespawnRecord> upserts, IReadOnlyCollection<GameObjectRespawnKey> deletes, CancellationToken cancellationToken = default);

    /// <summary>Remove every respawn time of a dungeon instance (it was reset or deleted).</summary>
    Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default);
}

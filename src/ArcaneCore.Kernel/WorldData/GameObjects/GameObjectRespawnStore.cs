namespace ArcaneCore.Kernel.WorldData.GameObjects;

/// <summary>
/// The durable respawn time of one despawned game object spawn (vmangos <c>gameobject_respawn</c>: <c>guid, respawn_time, instance, map</c>,
/// table <c>gameobject_respawn</c>, written by MapPersistentState::SaveGORespawnTime, Maps/MapPersistentStateMgr.cpp:103-123). <see cref="RespawnTime"/> is unix seconds. <see cref="InstanceId"/> is 0 for the shared copy of a map.
/// </summary>
public sealed record GameObjectRespawnRecord(uint MapId, uint InstanceId, uint SpawnGuid, long RespawnTime) : IRespawnRecord<GameObjectRespawnRecord>
{
    static GameObjectRespawnRecord IRespawnRecord<GameObjectRespawnRecord>.Create(uint mapId, uint instanceId, uint spawnGuid, long respawnTime)
        => new(mapId, instanceId, spawnGuid, respawnTime);
}

/// <summary>The key of a <see cref="GameObjectRespawnRecord"/>: a spawn guid within one map instance.</summary>
public readonly record struct GameObjectRespawnKey(uint InstanceId, uint SpawnGuid) : IRespawnKey<GameObjectRespawnKey>
{
    static GameObjectRespawnKey IRespawnKey<GameObjectRespawnKey>.Create(uint instanceId, uint spawnGuid) => new(instanceId, spawnGuid);
}

/// <summary>
/// Durable game object respawn times (the characters database). Used by one writer at a time (the world's respawn queue); callers never
/// await it from the world thread. The members are those of <see cref="IRespawnStore{TRecord, TKey}"/>. Load drops expired rows like vmangos MapPersistentStateManager::LoadGameobjectRespawnTimes (Maps/MapPersistentStateMgr.cpp:1149-1156).
/// </summary>
public interface IGameObjectRespawnStore : IRespawnStore<GameObjectRespawnRecord, GameObjectRespawnKey>
{
}

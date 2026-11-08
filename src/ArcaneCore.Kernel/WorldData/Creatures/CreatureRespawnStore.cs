namespace ArcaneCore.Kernel.WorldData.Creatures;

/// <summary>
/// The durable respawn time of one dead creature spawn (vmangos <c>creature_respawn</c>: <c>guid, respawn_time, instance, map</c>,
/// sql/characters.sql:472-480). <see cref="RespawnTime"/> is unix seconds. <see cref="InstanceId"/> is 0 for the shared copy of a map.
/// </summary>
public sealed record CreatureRespawnRecord(uint MapId, uint InstanceId, uint SpawnGuid, long RespawnTime) : IRespawnRecord<CreatureRespawnRecord>
{
    static CreatureRespawnRecord IRespawnRecord<CreatureRespawnRecord>.Create(uint mapId, uint instanceId, uint spawnGuid, long respawnTime)
        => new(mapId, instanceId, spawnGuid, respawnTime);
}

/// <summary>The key of a <see cref="CreatureRespawnRecord"/>: a spawn guid within one map instance.</summary>
public readonly record struct CreatureRespawnKey(uint InstanceId, uint SpawnGuid) : IRespawnKey<CreatureRespawnKey>
{
    static CreatureRespawnKey IRespawnKey<CreatureRespawnKey>.Create(uint instanceId, uint spawnGuid) => new(instanceId, spawnGuid);
}

/// <summary>
/// Durable creature respawn times (the characters database). Used by one writer at a time (the world's respawn queue); callers never
/// await it from the world thread. The members are those of <see cref="IRespawnStore{TRecord, TKey}"/>. Load drops expired rows like vmangos MapPersistentStateManager::LoadCreatureRespawnTimes (Maps/MapPersistentStateMgr.cpp:1070-1100).
/// </summary>
public interface ICreatureRespawnStore : IRespawnStore<CreatureRespawnRecord, CreatureRespawnKey>
{
}

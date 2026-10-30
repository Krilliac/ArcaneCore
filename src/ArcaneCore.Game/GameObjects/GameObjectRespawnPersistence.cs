namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// Where a map's game object system keeps the respawn times of its despawned spawns across restarts (vmangos MapPersistentState
/// <c>SaveGORespawnTime</c> / <c>GetGORespawnTime</c>, Maps/MapPersistentStateMgr.cpp:103-123). Every call comes from the world thread and
/// must not block: the world's implementation answers reads from memory and queues writes. The clock is the creature feature's
/// <see cref="Creatures.IRespawnClock"/>.
/// </summary>
public interface IGameObjectRespawnPersistence
{
    /// <summary>The stored respawn time of one spawn, without copying the map's table (a point lookup); false when the spawn has none.</summary>
    bool TryGetPending(uint mapId, uint instanceId, uint spawnGuid, out long respawnUnixSeconds);

    /// <summary>The stored respawn time (unix seconds) of every spawn of one map instance that is still waiting to respawn (<paramref name="instanceId"/> 0 = the shared map).</summary>
    IReadOnlyDictionary<uint, long> GetPending(uint mapId, uint instanceId);

    /// <summary>Remember that spawn <paramref name="spawnGuid"/> respawns at <paramref name="respawnUnixSeconds"/>.</summary>
    void Save(uint mapId, uint instanceId, uint spawnGuid, long respawnUnixSeconds);

    /// <summary>Forget the respawn time of a spawn (it respawned, or was respawned by hand).</summary>
    void Delete(uint mapId, uint instanceId, uint spawnGuid);
}

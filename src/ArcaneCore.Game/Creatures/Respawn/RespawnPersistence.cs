namespace ArcaneCore.Game.Creatures;

/// <summary>Wall-clock seconds for durable respawn times (vmangos <c>time(nullptr)</c> / <c>sWorld.GetGameTime()</c>); a seam so tests set it.</summary>
public interface IRespawnClock
{
    /// <summary>Unix time, in seconds.</summary>
    long UnixSeconds { get; }
}

/// <summary>The system clock.</summary>
public sealed class SystemRespawnClock : IRespawnClock
{
    public static readonly SystemRespawnClock Instance = new();

    public long UnixSeconds => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

/// <summary>
/// Where a map's creature system keeps the respawn times of its dead spawns across restarts (vmangos MapPersistentState
/// <c>SaveCreatureRespawnTime</c> / <c>GetCreatureRespawnTime</c>, Maps/MapPersistentStateMgr.cpp:80-101). Every call comes from the world
/// thread and must not block: the world's implementation answers reads from memory and queues writes.
/// </summary>
public interface ICreatureRespawnPersistence
{
    /// <summary>The stored respawn time (unix seconds) of every spawn of one map instance that is still waiting to respawn (<paramref name="instanceId"/> 0 = the shared map).</summary>
    IReadOnlyDictionary<uint, long> GetPending(uint mapId, uint instanceId);

    /// <summary>Remember that spawn <paramref name="spawnGuid"/> respawns at <paramref name="respawnUnixSeconds"/>.</summary>
    void Save(uint mapId, uint instanceId, uint spawnGuid, long respawnUnixSeconds);

    /// <summary>Forget the respawn time of a spawn (it respawned, or was respawned by hand).</summary>
    void Delete(uint mapId, uint instanceId, uint spawnGuid);
}

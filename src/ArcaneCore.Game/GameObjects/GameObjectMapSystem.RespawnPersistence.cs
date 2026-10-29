using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// Durable respawn times (docs/areas/creature-movement-spawns.md, game object half): the despawn of a database spawn (a looted chest, a
/// gathered node, a used object) is remembered through the <see cref="IGameObjectRespawnPersistence"/> seam, so a restart does not bring it
/// back at once. vmangos saves the time when the object despawns (GameObject::SaveRespawnTime, Objects/GameObject.cpp:1118-1122, which
/// calls MapPersistentState::SaveGORespawnTime, Maps/MapPersistentStateMgr.cpp:103-123), clears it when the object respawns
/// (GameObject::Respawn, GameObject.cpp:1171-1177), and reads it when the object loads (GameObject::LoadFromDB, GameObject.cpp:1002-1011:
/// a time still in the future leaves the object despawned, one that has passed is deleted and the object spawns).
/// Thread affinity: world thread only (shutdown: after the world stopped).
/// </summary>
public sealed partial class GameObjectMapSystem
{
    private readonly IGameObjectRespawnPersistence? _respawnPersistence;
    private readonly IRespawnClock _respawnClock;

    /// <summary>
    /// Respawn times go to the database except on a battleground map: vmangos MapPersistentState::SaveGORespawnTime returns before the
    /// database for one ("BGs/Arenas always reset at server restart/unload", Maps/MapPersistentStateMgr.cpp:106-108).
    /// </summary>
    private bool Persists => _respawnPersistence is not null && Map.Template is not { IsBattleground: true };

    /// <summary>
    /// Whether the despawned <paramref name="go"/> has a respawn time worth storing. Left out: a spawn that never respawns by timer
    /// (negative spawntimesecs; vmangos stores only m_spawnedByDefault objects, GameObject.cpp:1119), a chest whose consumed state is already
    /// kept with its instance save (<see cref="DurableKeyOf"/>), and a pool or spawn-group member (its pool or group decides what exists, and
    /// that state is not stored, so a time kept for the member would meet a different choice after a restart).
    /// </summary>
    private bool StoresRespawn(GameObject go)
        => Persists && go.Spawn is { SpawnTimeSeconds: >= 0 } spawn && go.RespawnAtMs > 0 && !go.IsSpawned
            && DurableKeyOf(go) is null && !_poolSpawns.ContainsKey(spawn.Guid) && !_groupOfSpawn.ContainsKey(spawn.Guid);

    /// <summary>The object despawned (or had its respawn time set): store when it comes back. A time that is not in the future is a delete, as in SaveGORespawnTime.</summary>
    private void SaveRespawnOnDespawn(GameObject go)
    {
        if (!StoresRespawn(go))
        {
            return;
        }

        long now = _respawnClock.UnixSeconds;
        long at = now + (Math.Max(0L, go.RespawnAtMs - _clockMs) / 1000);
        if (at > now)
        {
            _respawnPersistence!.Save(Map.MapId, Map.InstanceId, go.Spawn!.Guid, at);
        }
        else
        {
            DeletePersistedRespawn(go.Spawn!.Guid);
        }
    }

    /// <summary>The respawn row of a spawn is no longer needed (it is alive again). Queues a write only when a row is held.</summary>
    private void DeletePersistedRespawn(uint spawnGuid)
    {
        if (Persists && _respawnPersistence!.TryGetPending(Map.MapId, Map.InstanceId, spawnGuid, out _))
        {
            _respawnPersistence.Delete(Map.MapId, Map.InstanceId, spawnGuid);
        }
    }

    /// <summary>
    /// The stored respawn time of a spawn whose object is being created, as a time on <see cref="ClockMs"/>; false when the spawn has none
    /// (or it is not eligible). A time that has passed is deleted (GameObject.cpp:1005-1010).
    /// </summary>
    private bool TryRestorePersistedRespawn(GameObjectSpawn spawn, out long respawnAtMs)
    {
        respawnAtMs = 0;
        if (!Persists || spawn.SpawnTimeSeconds < 0 || _poolSpawns.ContainsKey(spawn.Guid) || _groupOfSpawn.ContainsKey(spawn.Guid)
            || !_respawnPersistence!.TryGetPending(Map.MapId, Map.InstanceId, spawn.Guid, out long unix))
        {
            return false;
        }

        long now = _respawnClock.UnixSeconds;
        if (unix > now)
        {
            respawnAtMs = _clockMs + ((unix - now) * 1000L);
            return true;
        }

        _respawnPersistence.Delete(Map.MapId, Map.InstanceId, spawn.Guid);
        return false;
    }

    /// <summary>
    /// Store the respawn time of every despawned object this system tracks (shutdown). An upsert of what the despawn already stored, so it is
    /// idempotent; a no-op without a persistence seam. World thread, or after the world stopped.
    /// </summary>
    public void SaveRespawnTimes()
    {
        if (!Persists)
        {
            return;
        }

        foreach (GameObject go in _objects.Values)
        {
            SaveRespawnOnDespawn(go);
        }
    }
}

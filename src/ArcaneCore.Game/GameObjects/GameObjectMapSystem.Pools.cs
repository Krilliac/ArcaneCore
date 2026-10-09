using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps.Pools;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Pools;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// cmangos pools of game objects (Pools/PoolManager.cpp; docs/areas/content-import.md, pools). A spawn that a pool names is never created
/// by its grid on its own (cmangos ObjectMgr::LoadGameObjects leaves pooled guids out of the grid): only the members its pool has out now
/// exist, at most <c>max_limit</c> of them. When one despawns (an ore or herb node gathered, a chest looted) its pool rolls again at once:
/// the same node may respawn in place after its respawn time, or another member of the pool comes after its own respawn time and the
/// gathered one leaves the world. That is how the nodes of a mining spot rotate.
/// </summary>
public sealed partial class GameObjectMapSystem : IPoolHost
{
    private PoolSpawnState? _pools;
    private readonly Dictionary<uint, GameObjectSpawn> _poolSpawns = [];

    /// <summary>The pools of this map, null when the content has none here.</summary>
    internal PoolSpawnState? PoolState => _pools;

    /// <summary>Whether a pooled spawn is one its pool has out now (GM, tests).</summary>
    public bool IsPoolSpawned(uint spawnGuid) => _pools?.IsSpawned(spawnGuid) == true;

    /// <summary>How many members pool <paramref name="poolId"/> has out on this map now.</summary>
    public uint PoolSpawnedCount(uint poolId) => _pools?.SpawnedCount(poolId) ?? 0;

    private void InitializePools()
    {
        if (_content.Pools.Count == 0)
        {
            return;
        }

        foreach (GameObjectSpawn spawn in _content.GetSpawns(Map.MapId))
        {
            if (_content.Pools.IsPooled(spawn.Guid))
            {
                _poolSpawns[spawn.Guid] = spawn;
            }
        }

        if (_poolSpawns.Count == 0)
        {
            return;
        }

        _pools = new PoolSpawnState(_content.Pools, Map.MapId, this, Random);
        _pools.Initialize();
    }

    /// <summary>The grid of a pooled spawn loads: it is created only when its pool has it out.</summary>
    private bool PoolRefusesAtLoad(GameObjectSpawn spawn) => _poolSpawns.ContainsKey(spawn.Guid) && _pools?.IsSpawned(spawn.Guid) != true;

    /// <summary>A pooled object despawned (cmangos GameObject::Update GO_JUST_DEACTIVATED → PoolManager::UpdatePool with it as the trigger).</summary>
    private void OnPoolMemberDespawned(GameObject go)
    {
        if (_pools is { } pools && go.Spawn is { } spawn && _poolSpawns.ContainsKey(spawn.Guid) && pools.IsSpawned(spawn.Guid))
        {
            pools.UpdatePool(_content.Pools.PoolOf(spawn.Guid), spawn.Guid);
        }
    }

    /// <summary>The event gate changed for a pooled spawn: its pool replaces it (refused) or fills up again (allowed).</summary>
    private void RefreshPoolMember(uint spawnGuid, bool allowed)
    {
        if (_pools is not { } pools)
        {
            return;
        }

        if (allowed)
        {
            pools.Refill(_content.Pools.PoolOf(spawnGuid));
        }
        else
        {
            pools.Refuse(spawnGuid);
            _respawnAt.Remove(spawnGuid);
            _unloadedLoot.Remove(spawnGuid);
        }
    }

    // --- IPoolHost ----------------------------------------------------------------------------------

    bool IPoolHost.CanSpawn(uint spawnGuid)
        => _poolSpawns.TryGetValue(spawnGuid, out GameObjectSpawn? spawn)
            && (spawn.SpawnFlags & 0x02) == 0
            && (_spawnGate?.AllowsGameObject(spawnGuid) ?? true);

    void IPoolHost.SpawnMember(uint spawnGuid, bool instantly)
    {
        if (!_poolSpawns.TryGetValue(spawnGuid, out GameObjectSpawn? spawn))
        {
            return;
        }

        if (!instantly && spawn.SpawnTimeSeconds >= 0)
        {
            // cmangos Spawn1Object: a replacement is created with a fresh respawn time (in place of the one that just despawned).
            _respawnAt[spawnGuid] = _clockMs + Math.Max(1000L, RollRespawnSeconds(spawn) * 1000L);
        }

        if (FindBySpawn(spawnGuid) is null && _grids.TryGetValue(CreatureMapSystem.ComputeGrid(spawn.X, spawn.Y), out List<GameObject>? list))
        {
            LoadSpawns(list, [spawn]);
        }
    }

    void IPoolHost.DespawnMember(uint spawnGuid)
    {
        _respawnAt.Remove(spawnGuid);
        _unloadedLoot.Remove(spawnGuid);
        if (FindBySpawn(spawnGuid) is { } go)
        {
            Remove(go);
        }
    }
}

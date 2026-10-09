using ArcaneCore.Game.Maps.Pools;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// cmangos pools of creatures (Pools/PoolManager.cpp; docs/areas/content-import.md, pools). A pooled spawn is never created by its grid on its
/// own: only the members its pool has out exist. When a pooled creature's respawn time comes (cmangos Creature::Update → PoolManager::UpdatePool
/// with it as the trigger) its pool rolls again: it may respawn in place, or another member takes its place, dead until its own respawn time,
/// and the first leaves the world. classic-db z2815 pools 1,038 creatures (rares and their spots, BRD smiths).
/// </summary>
public sealed partial class CreatureMapSystem : IPoolHost
{
    private PoolSpawnState? _pools;
    private readonly Dictionary<uint, CreatureSpawn> _poolSpawns = [];

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

        foreach (CreatureSpawn spawn in _content.GetSpawns(Map.MapId))
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

        _pools = new PoolSpawnState(_content.Pools, Map.MapId, this, _random);
        _pools.Initialize();
    }

    /// <summary>A pooled spawn's grid loads: it is created only when its pool has it out.</summary>
    private bool PoolRefusesAtLoad(CreatureSpawn spawn) => _poolSpawns.ContainsKey(spawn.Guid) && _pools?.IsSpawned(spawn.Guid) != true;

    /// <summary>
    /// A pooled creature's respawn time has come: its pool rolls with it as the trigger. False when another member was chosen and this one has
    /// left the world (it must not respawn).
    /// </summary>
    private bool PoolKeepsOnRespawn(Creature creature)
    {
        if (_pools is not { } pools || creature.Spawn is not { } spawn || !_poolSpawns.ContainsKey(spawn.Guid) || !pools.IsSpawned(spawn.Guid))
        {
            return true;
        }

        return pools.UpdatePool(_content.Pools.PoolOf(spawn.Guid), spawn.Guid) && _creatures.ContainsKey(creature.Guid);
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
        }
    }

    // --- IPoolHost ----------------------------------------------------------------------------------

    bool IPoolHost.CanSpawn(uint spawnGuid)
        => _poolSpawns.ContainsKey(spawnGuid) && !_scriptOnlySpawns.Contains(spawnGuid) && SpawnAllowed(spawnGuid);

    void IPoolHost.SpawnMember(uint spawnGuid, bool instantly)
    {
        if (!_poolSpawns.TryGetValue(spawnGuid, out CreatureSpawn? spawn))
        {
            return;
        }

        bool gridLoaded = _grids.TryGetValue(ComputeGrid(spawn.X, spawn.Y), out LoadedGrid? grid);
        if (!instantly && !gridLoaded)
        {
            // cmangos PoolGroup<Creature>::Spawn1Object: in a grid that is not loaded the replacement only gets a fresh respawn time
            // (urand(spawntimesecsmin, max)) and is dead when the grid loads. In a loaded grid it is created alive (Creature::LoadFromDB; the
            // SetRespawnTime there only matters for its next death), so the rare who replaces one that respawned is there at once.
            uint min = spawn.SpawnTimeMinSeconds;
            uint max = Math.Max(min, spawn.SpawnTimeMaxSeconds);
            long delayMs = Math.Max(1000L, (long)_random.NextInt64(min, (long)max + 1) * 1000L);
            _respawnAt[spawnGuid] = _clockMs + delayMs;
            if (Persists)
            {
                _persistence!.Save(Map.MapId, Map.InstanceId, spawnGuid, _respawnClock.UnixSeconds + (delayMs / 1000));
            }
        }

        if (grid is not null
            && FindLive(spawn, _options.Respawn.AlternateEntries ? _content.GetSpawnEntries(spawn.Guid) : []) is null)
        {
            LoadSpawns(grid, [spawn]);
        }
    }

    void IPoolHost.DespawnMember(uint spawnGuid)
    {
        _respawnAt.Remove(spawnGuid);
        if (Persists)
        {
            _persistence!.Delete(Map.MapId, Map.InstanceId, spawnGuid);
        }

        if (_poolSpawns.TryGetValue(spawnGuid, out CreatureSpawn? spawn)
            && FindLive(spawn, _options.Respawn.AlternateEntries ? _content.GetSpawnEntries(spawn.Guid) : []) is { } live)
        {
            Despawn(live);
        }
    }
}

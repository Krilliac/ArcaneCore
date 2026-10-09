using ArcaneCore.Kernel.WorldData.Pools;

namespace ArcaneCore.Game.Maps.Pools;

/// <summary>What a <see cref="PoolSpawnState"/> asks of the creature or game object system of its map.</summary>
internal interface IPoolHost
{
    /// <summary>
    /// Whether the spawn may be chosen now (cmangos <c>PoolObject::exclude</c> and <c>Map::CanSpawn</c>): it is a spawn of this map and the
    /// game-event gate allows it.
    /// </summary>
    bool CanSpawn(uint spawnGuid);

    /// <summary>
    /// cmangos Spawn1Object: the spawn is the pool's now. <paramref name="instantly"/> (a map's first spawn, an event start) brings it in at
    /// once; otherwise it replaces one that just despawned and comes after its own respawn time.
    /// </summary>
    void SpawnMember(uint spawnGuid, bool instantly);

    /// <summary>cmangos Despawn1Object: the spawn is no longer the pool's; it leaves the world (and its grid) now.</summary>
    void DespawnMember(uint spawnGuid);
}

/// <summary>
/// The pools of one spawn kind on one map: which members are spawned now (cmangos SpawnedPoolData in MapPersistentState) and the rolls that
/// choose them (Pools/PoolManager.cpp PoolGroup::RollOne, SpawnObject, DespawnObject; PoolManager::SpawnPool, DespawnPool, UpdatePool).
/// Re-implemented from the cmangos behaviour; no code is copied.
/// <list type="bullet">
/// <item>A pool keeps at most <c>max_limit</c> members out, its spawns and child pools counted together. A pool with no template row has a
/// limit of 0 and spawns nothing.</item>
/// <item>Each free place is rolled: the explicitly chanced members first (one roll of 0..100 for the whole list, the list shuffled, the first
/// member whose chance is above the roll wins), then one of the equally chanced members at random, else the first explicitly chanced member
/// that could have been taken. A member already out, or one the host refuses (event gate), is passed over.</item>
/// <item>When a member despawns (an ore node mined, a chest looted, a creature's respawn time reached) its pool rolls again with the member
/// counted out: the same one may come back in place, else another member takes its place, after its own respawn time, and the first leaves the
/// world. A member of a child pool asks the mother instead, which may switch the whole child pool.</item>
/// </list>
/// World thread only.
/// </summary>
internal sealed class PoolSpawnState
{
    private readonly PoolCatalog _catalog;
    private readonly IPoolHost _host;
    private readonly Random _random;
    private readonly HashSet<uint> _spawnedObjects = [];
    private readonly Dictionary<uint, uint> _spawnedPools = [];

    public PoolSpawnState(PoolCatalog catalog, uint mapId, IPoolHost host, Random random)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(random);
        _catalog = catalog;
        _host = host;
        _random = random;
        MapId = mapId;
    }

    public uint MapId { get; }

    public PoolCatalog Catalog => _catalog;

    /// <summary>Whether the spawn is one its pool has out now (in the world, or waiting for its respawn time).</summary>
    public bool IsSpawned(uint spawnGuid) => _spawnedObjects.Contains(spawnGuid);

    /// <summary>Whether a child pool is chosen by its mother now.</summary>
    public bool IsPoolSpawned(uint poolId) => _spawnedPools.ContainsKey(poolId);

    /// <summary>How many members (spawns and child pools) pool <paramref name="poolId"/> has out now.</summary>
    public uint SpawnedCount(uint poolId) => _spawnedPools.GetValueOrDefault(poolId);

    /// <summary>Every spawn some pool of this map has out now.</summary>
    public IReadOnlyCollection<uint> SpawnedObjects => _spawnedObjects;

    /// <summary>Whether a pool lives on this map (its spawns are here).</summary>
    public bool Holds(uint poolId) => _catalog.Find(poolId) is { MapId: { } map } && map == MapId;

    /// <summary>cmangos PoolManager::Initialize: every auto-spawned pool of this map spawns its members at once.</summary>
    public void Initialize()
    {
        foreach (PoolDefinition pool in _catalog.Pools.Where(p => p.AutoSpawn && p.MapId == MapId).OrderBy(p => p.Id))
        {
            SpawnPool(pool.Id, instantly: true);
        }
    }

    /// <summary>cmangos PoolManager::SpawnPool: the child pools, then the spawns, up to the limit.</summary>
    public void SpawnPool(uint poolId, bool instantly)
    {
        if (_catalog.Find(poolId) is not { } pool)
        {
            return;
        }

        SpawnGroup(pool, isPool: true, trigger: 0, instantly);
        SpawnGroup(pool, isPool: false, trigger: 0, instantly);
    }

    /// <summary>cmangos PoolManager::DespawnPool: every member of the pool leaves (its child pools' members too).</summary>
    public void DespawnPool(uint poolId)
    {
        if (_catalog.Find(poolId) is not { } pool)
        {
            return;
        }

        foreach (PoolMember member in pool.Spawns.ToArray())
        {
            if (_spawnedObjects.Contains(member.Id))
            {
                DespawnOne(pool, member.Id, isPool: false);
            }
        }

        foreach (PoolMember child in pool.Children.ToArray())
        {
            if (_spawnedPools.ContainsKey(child.Id))
            {
                DespawnOne(pool, child.Id, isPool: true);
            }
        }
    }

    /// <summary>
    /// cmangos PoolManager::UpdatePool: <paramref name="spawnGuid"/> of pool <paramref name="poolId"/> despawned (or reached its respawn time).
    /// A pool with a mother asks the mother, which rolls its children with this pool as the trigger; else the pool rolls its spawns with the
    /// spawn as the trigger. Returns false when the trigger was replaced (it has left the world through <see cref="IPoolHost.DespawnMember"/>).
    /// </summary>
    public bool UpdatePool(uint poolId, uint spawnGuid)
    {
        if (_catalog.Find(poolId) is not { } pool)
        {
            return true;
        }

        if (pool.Mother != 0 && _catalog.Find(pool.Mother) is { } mother)
        {
            SpawnGroup(mother, isPool: true, trigger: poolId, instantly: false);
        }
        else
        {
            SpawnGroup(pool, isPool: false, trigger: spawnGuid, instantly: false);
        }

        return _spawnedObjects.Contains(spawnGuid);
    }

    /// <summary>
    /// The event gate refuses a spawn its pool has out (cmangos GameEventUnspawn: exclude it and update the pool with it as the trigger, or, for an
    /// event's own pool, DespawnPoolInMaps): its pool rolls a replacement among the members still allowed, and the spawn leaves even when there is
    /// none.
    /// </summary>
    public void Refuse(uint spawnGuid)
    {
        uint poolId = _catalog.PoolOf(spawnGuid);
        if (poolId == 0 || !_spawnedObjects.Contains(spawnGuid))
        {
            return;
        }

        UpdatePool(poolId, spawnGuid);
        if (_spawnedObjects.Contains(spawnGuid) && _catalog.Find(poolId) is { } pool)
        {
            DespawnOne(pool, spawnGuid, isPool: false);
        }
    }

    /// <summary>
    /// The event gate allows spawns again (an event started, cmangos GameEventSpawn → SpawnPoolInMaps with instantly): every pool from the top
    /// down fills its free places at once.
    /// </summary>
    public void Refill(uint poolId)
    {
        uint top = _catalog.TopPoolOf(poolId);
        if (Holds(top) && _catalog.Find(top) is { AutoSpawn: true } topPool)
        {
            RefillTree(topPool); // a pool cmangos never spawns by itself (no template, chances that cannot pick) stays empty
        }
    }

    private void RefillTree(PoolDefinition pool)
    {
        SpawnPool(pool.Id, instantly: true);
        foreach (PoolMember child in pool.Children.ToArray())
        {
            if (_spawnedPools.ContainsKey(child.Id) && _catalog.Find(child.Id) is { } childPool)
            {
                RefillTree(childPool);
            }
        }
    }

    /// <summary>cmangos PoolGroup::SpawnObject.</summary>
    private void SpawnGroup(PoolDefinition pool, bool isPool, uint trigger, bool instantly)
    {
        IReadOnlyList<PoolMember> explicitList = isPool ? pool.ExplicitlyChancedPools : pool.ExplicitlyChanced;
        IReadOnlyList<PoolMember> equalList = isPool ? pool.EqualChancedPools : pool.EqualChanced;
        if (explicitList.Count == 0 && equalList.Count == 0)
        {
            return;
        }

        long count = (long)pool.MaxLimit - SpawnedCount(pool.Id);
        if (trigger != 0)
        {
            if (IsMemberSpawned(trigger, isPool))
            {
                count++; // the trigger is still counted: one in, one out
            }
            else
            {
                trigger = 0;
            }
        }

        uint lastDespawned = 0;
        for (long i = 0; i < count; i++)
        {
            PoolMember? chosen = RollOne(explicitList, equalList, isPool, trigger);
            if (chosen is null || chosen.Id == lastDespawned)
            {
                continue;
            }

            if (chosen.Id == trigger)
            {
                trigger = 0; // the same one again: it comes back in place (cmangos ReSpawn1Object), nothing to do here
                continue;
            }

            AddSpawn(pool.Id, chosen.Id, isPool);
            if (isPool)
            {
                SpawnPool(chosen.Id, instantly);
            }
            else
            {
                _host.SpawnMember(chosen.Id, instantly);
            }

            if (trigger != 0)
            {
                DespawnOne(pool, trigger, isPool); // one spawn, one despawn
                lastDespawned = trigger;
                trigger = 0;
            }
        }
    }

    /// <summary>cmangos PoolGroup::RollOne.</summary>
    private PoolMember? RollOne(IReadOnlyList<PoolMember> explicitList, IReadOnlyList<PoolMember> equalList, bool isPool, uint trigger)
    {
        PoolMember? explicitFound = null;
        if (explicitList.Count > 0)
        {
            float roll = (float)(_random.NextDouble() * 100.0); // rand_chance
            foreach (PoolMember member in Shuffled(explicitList))
            {
                if (!Eligible(member, isPool, trigger))
                {
                    continue;
                }

                explicitFound ??= member; // kept in case no member wins its roll
                if (roll < member.Chance)
                {
                    return member;
                }
            }
        }

        if (equalList.Count > 0)
        {
            foreach (PoolMember member in Shuffled(equalList))
            {
                if (Eligible(member, isPool, trigger))
                {
                    return member;
                }
            }
        }

        return explicitFound;
    }

    private bool Eligible(PoolMember member, bool isPool, uint trigger)
        => (isPool || _host.CanSpawn(member.Id)) && (member.Id == trigger || !IsMemberSpawned(member.Id, isPool));

    private bool IsMemberSpawned(uint id, bool isPool) => isPool ? _spawnedPools.ContainsKey(id) : _spawnedObjects.Contains(id);

    private void AddSpawn(uint poolId, uint id, bool isPool)
    {
        if (isPool)
        {
            _spawnedPools[id] = 0;
        }
        else
        {
            _spawnedObjects.Add(id);
        }

        _spawnedPools[poolId] = SpawnedCount(poolId) + 1;
    }

    /// <summary>cmangos PoolGroup::DespawnObject for one member, then SpawnedPoolData::RemoveSpawn.</summary>
    private void DespawnOne(PoolDefinition pool, uint id, bool isPool)
    {
        if (isPool)
        {
            DespawnPool(id);
            _spawnedPools.Remove(id);
        }
        else
        {
            _spawnedObjects.Remove(id);
            _host.DespawnMember(id);
        }

        if (_spawnedPools.TryGetValue(pool.Id, out uint count) && count > 0)
        {
            _spawnedPools[pool.Id] = count - 1;
        }
    }

    private PoolMember[] Shuffled(IReadOnlyList<PoolMember> list)
    {
        PoolMember[] copy = [.. list];
        for (int i = copy.Length - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        return copy;
    }
}

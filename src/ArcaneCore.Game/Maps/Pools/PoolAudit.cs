using ArcaneCore.Kernel.WorldData.Pools;

namespace ArcaneCore.Game.Maps.Pools;

/// <summary>One broken pool invariant found by <see cref="PoolAudit"/>.</summary>
public sealed record PoolAuditIssue(uint PoolId, string Problem);

/// <summary>
/// What <see cref="PoolAudit"/> found on one map for one spawn kind: how many pools it checked, how many members their state has out, how many
/// of the pooled spawns exist in the world now, and every broken invariant.
/// </summary>
public sealed record PoolAuditReport(uint MapId, int PoolsChecked, int SpawnedMembers, int LiveMembers, IReadOnlyList<PoolAuditIssue> Issues)
{
    /// <summary>A map without pools of this kind.</summary>
    public static PoolAuditReport None(uint mapId) => new(mapId, 0, 0, 0, []);

    public bool Clean => Issues.Count == 0;
}

/// <summary>
/// Checks the cmangos pool invariants (Pools/PoolManager.cpp SpawnedPoolData, PoolGroup::SpawnObject) of one map against the spawns that exist
/// in it now:
/// <list type="number">
/// <item>No pool has more members out than its <c>max_limit</c> (its spawns and child pools counted together, as cmangos counts them).</item>
/// <item>A pool's counter equals the members it really has out (spawns marked spawned plus child pools chosen).</item>
/// <item>No pool has more of its spawns in the world than its <c>max_limit</c>, and no mother more of its child pools with a spawn in the
/// world.</item>
/// <item>Every pooled spawn in the world is one its pool has out (nothing leaked past a rotation), and every spawn out belongs to a pool that
/// is itself out (a child pool not chosen by its mother has nothing out).</item>
/// </list>
/// Read-only; world thread only.
/// </summary>
internal static class PoolAudit
{
    /// <param name="state">The pools of the map.</param>
    /// <param name="pooledSpawns">Every pooled spawn guid of this kind on the map.</param>
    /// <param name="inWorld">Whether a spawn exists in the map now (alive, or dead waiting for its respawn in a loaded grid).</param>
    public static PoolAuditReport Run(PoolSpawnState state, IEnumerable<uint> pooledSpawns, Func<uint, bool> inWorld)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(pooledSpawns);
        ArgumentNullException.ThrowIfNull(inWorld);
        PoolCatalog catalog = state.Catalog;
        var issues = new List<PoolAuditIssue>();
        var livePerPool = new Dictionary<uint, int>();
        int live = 0;
        foreach (uint guid in pooledSpawns)
        {
            if (!inWorld(guid))
            {
                continue;
            }

            live++;
            uint poolId = catalog.PoolOf(guid);
            if (poolId == 0)
            {
                issues.Add(new(0, $"spawn {guid} is in the world but in no pool after the load checks (its row was dropped; cmangos never spawns it)"));
                continue;
            }

            livePerPool[poolId] = livePerPool.GetValueOrDefault(poolId) + 1;
            if (!state.IsSpawned(guid))
            {
                issues.Add(new(poolId, $"spawn {guid} is in the world but its pool does not have it out (leaked past a rotation)"));
            }
        }

        foreach (uint guid in state.SpawnedObjects)
        {
            uint poolId = catalog.PoolOf(guid);
            if (catalog.Find(poolId) is { Mother: not 0 } child && !state.IsPoolSpawned(child.Id))
            {
                issues.Add(new(poolId, $"spawn {guid} is out but its pool {poolId} is not chosen by its mother {child.Mother}"));
            }
        }

        int checkedPools = 0;
        foreach (PoolDefinition pool in catalog.Pools)
        {
            if (pool.MapId != state.MapId)
            {
                continue;
            }

            checkedPools++;
            uint counter = state.SpawnedCount(pool.Id);
            int spawnsOut = 0, childrenOut = 0, childrenLive = 0;
            foreach (PoolMember member in pool.ExplicitlyChanced)
            {
                spawnsOut += state.IsSpawned(member.Id) ? 1 : 0;
            }

            foreach (PoolMember member in pool.EqualChanced)
            {
                spawnsOut += state.IsSpawned(member.Id) ? 1 : 0;
            }

            foreach (PoolMember child in pool.Children)
            {
                childrenOut += state.IsPoolSpawned(child.Id) ? 1 : 0;
                childrenLive += LiveInTree(catalog, child.Id, livePerPool, 0) > 0 ? 1 : 0;
            }

            if (counter > pool.MaxLimit)
            {
                issues.Add(new(pool.Id, $"pool {pool.Id} '{pool.Description}' has {counter} members out, max_limit {pool.MaxLimit}"));
            }

            if (counter != spawnsOut + childrenOut)
            {
                issues.Add(new(pool.Id, $"pool {pool.Id} '{pool.Description}' counts {counter} members out but has {spawnsOut} spawn(s) and {childrenOut} child pool(s) out"));
            }

            int spawnsLive = livePerPool.GetValueOrDefault(pool.Id);
            if (spawnsLive + childrenLive > pool.MaxLimit)
            {
                issues.Add(new(pool.Id, $"pool {pool.Id} '{pool.Description}' has {spawnsLive} spawn(s) and {childrenLive} child pool(s) in the world, max_limit {pool.MaxLimit}"));
            }

            if (pool.Mother != 0 && !state.IsPoolSpawned(pool.Id) && counter != 0)
            {
                issues.Add(new(pool.Id, $"child pool {pool.Id} '{pool.Description}' is not chosen by its mother {pool.Mother} but has {counter} members out"));
            }
        }

        return new PoolAuditReport(state.MapId, checkedPools, state.SpawnedObjects.Count, live, issues);
    }

    private static int LiveInTree(PoolCatalog catalog, uint poolId, Dictionary<uint, int> livePerPool, int depth)
    {
        int total = livePerPool.GetValueOrDefault(poolId);
        if (depth < 64 && catalog.Find(poolId) is { } pool)
        {
            foreach (PoolMember child in pool.Children)
            {
                total += LiveInTree(catalog, child.Id, livePerPool, depth + 1);
            }
        }

        return total;
    }
}

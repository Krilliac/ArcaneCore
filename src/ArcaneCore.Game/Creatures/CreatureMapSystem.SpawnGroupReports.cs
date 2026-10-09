using System.Globalization;
using ArcaneCore.Game.Maps.SpawnGroups;
using ArcaneCore.Kernel.WorldData.Pools;
using ArcaneCore.Kernel.WorldData.SpawnGroups;

namespace ArcaneCore.Game.Creatures;

/// <summary>Read access to the spawn groups, formations and pools of this map for the <c>.spawngroup</c> GM commands.</summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>The creature spawn groups of this map, by id.</summary>
    public IEnumerable<uint> SpawnGroupIds => _spawnGroups.Keys.Order();

    /// <summary>The group a database spawn of this map belongs to, or 0.</summary>
    public uint SpawnGroupIdOf(uint spawnGuid) => _groupOfSpawn.TryGetValue(spawnGuid, out SpawnGroupState? state) ? state.Id : 0;

    /// <summary>Spawn group <paramref name="groupId"/> on this map and what each member is doing, or null when the map does not hold it.</summary>
    public SpawnGroupReport? DescribeSpawnGroup(uint groupId)
    {
        if (!_spawnGroups.TryGetValue(groupId, out SpawnGroupState? state))
        {
            return null;
        }

        var members = new List<SpawnGroupMemberReport>(state.Members.Count);
        foreach (SpawnGroupMember member in state.Members)
        {
            members.Add(DescribeMember(state, member));
        }

        string? formation = _formations.TryGetValue(groupId, out FormationState? f) ? DescribeFormation(f) : null;
        return new SpawnGroupReport(state.Definition, state.MaxCount, SpawnGroupConditionHolds(state.Definition, SpawnGroupCondition), members, formation);
    }

    /// <summary>The pool of a creature spawn of this map and its state, or null when it is not pooled.</summary>
    public SpawnPoolReport? DescribeSpawnPool(uint spawnGuid) => DescribePool(_content.Pools, _pools, spawnGuid);

    internal static SpawnPoolReport? DescribePool(PoolCatalog catalog, Maps.Pools.PoolSpawnState? state, uint spawnGuid)
    {
        if (!catalog.IsPooled(spawnGuid))
        {
            return null;
        }

        uint poolId = catalog.PoolOf(spawnGuid);
        PoolDefinition? pool = catalog.Find(poolId);
        PoolDefinition? mother = pool is { Mother: not 0 } ? catalog.Find(pool.Mother) : null;
        return new SpawnPoolReport(poolId, pool?.Description ?? string.Empty, pool?.MaxLimit ?? 0, state?.SpawnedCount(poolId) ?? 0,
            state?.IsSpawned(spawnGuid) == true, mother?.Id ?? 0, mother?.Description);
    }

    private SpawnGroupMemberReport DescribeMember(SpawnGroupState state, SpawnGroupMember member)
    {
        if (state.TryGetEntry(member.Guid, out uint entry))
        {
            if (!_creatures.TryGetValue(ObjectGuid.WithEntry(HighGuid.Unit, entry, member.Guid), out Creature? live))
            {
                return new(member.Guid, member.SlotId, member.Chance, entry, "chosen, grid not loaded");
            }

            string text = live.DeathState switch
            {
                CreatureDeathState.Alive => live.IsEvading ? "alive, evading" : live.Combat.IsInCombat ? "alive, in combat" : "alive",
                CreatureDeathState.Corpse => $"corpse, respawn in {Seconds(live.RespawnAtMs)}",
                _ => $"dead, respawn in {Seconds(live.RespawnAtMs)}",
            };
            return new(member.Guid, member.SlotId, member.Chance, entry, text);
        }

        return new(member.Guid, member.SlotId, member.Chance, 0,
            _respawnAt.TryGetValue(member.Guid, out long at) && at > _clockMs ? $"out, respawn in {Seconds(at)}" : "out");
    }

    private string Seconds(long atMs)
        => atMs == long.MaxValue ? "never" : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0L, atMs - _clockMs) / 1000} s");

    private static string DescribeFormation(FormationState formation)
    {
        string shape = formation.Shape switch
        {
            FormationShape.SingleFile => "single file",
            FormationShape.SideBySide => "side by side",
            FormationShape.LikeGeese => "like geese",
            FormationShape.FannedOutBehind => "fanned out behind",
            FormationShape.FannedOutInFront => "fanned out in front",
            FormationShape.CircleTheLeader => "circle the leader",
            _ => "random",
        };
        string movement = formation.MovementType switch { 1 => "random", 2 => "waypoint", 4 => "linear waypoint", _ => "idle" };
        string leader = formation.Master is Creature { Spawn: { } spawn } ? spawn.Guid.ToString(CultureInfo.InvariantCulture) : "none";
        return string.Create(CultureInfo.InvariantCulture,
            $"{shape}, spread {formation.Spread:0.##}, path {formation.PathId} ({movement}), leader {leader}{(formation.MasterDied ? " (new one after the fight)" : string.Empty)}");
    }
}

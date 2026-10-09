using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps.SpawnGroups;
using ArcaneCore.Kernel.WorldData.SpawnGroups;

namespace ArcaneCore.Game.GameObjects;

/// <summary>Read access to the spawn groups and pools of this map for the <c>.spawngroup</c> GM commands.</summary>
public sealed partial class GameObjectMapSystem
{
    /// <summary>The game object spawn groups of this map, by id.</summary>
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

        SpawnGroupMemberReport[] members = [.. state.Members.Select(m => Describe(state, m))];
        return new SpawnGroupReport(state.Definition, state.MaxCount, CreatureMapSystem.SpawnGroupConditionHolds(state.Definition, SpawnGroupCondition), members, null);
    }

    /// <summary>The pool of a game object spawn of this map and its state, or null when it is not pooled.</summary>
    public SpawnPoolReport? DescribeSpawnPool(uint spawnGuid) => CreatureMapSystem.DescribePool(_content.Pools, _pools, spawnGuid);

    private SpawnGroupMemberReport Describe(SpawnGroupState state, SpawnGroupMember member)
    {
        if (state.TryGetEntry(member.Guid, out uint entry))
        {
            if (!_objects.TryGetValue(SpawnObjectGuid(entry, member.Guid), out GameObject? live))
            {
                return new(member.Guid, member.SlotId, member.Chance, entry, "chosen, grid not loaded");
            }

            return new(member.Guid, member.SlotId, member.Chance, entry,
                live.IsSpawned ? "spawned" : $"despawned, respawn in {Seconds(live.RespawnAtMs)}");
        }

        return new(member.Guid, member.SlotId, member.Chance, 0,
            _respawnAt.TryGetValue(member.Guid, out long at) && at > _clockMs ? $"out, respawn in {Seconds(at)}" : "out");
    }

    private string Seconds(long atMs)
        => atMs is 0 or long.MaxValue ? "never" : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0L, atMs - _clockMs) / 1000} s");
}

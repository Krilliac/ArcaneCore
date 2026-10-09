using ArcaneCore.Kernel.WorldData.SpawnGroups;

namespace ArcaneCore.Game.Maps.SpawnGroups;

/// <summary>What one member of a spawn group is doing now (the <c>.spawngroup</c> GM commands).</summary>
/// <param name="Guid">The database spawn guid.</param>
/// <param name="SlotId">The formation slot (-1 none).</param>
/// <param name="Chance">The member's <c>spawn_group_spawn.Chance</c>.</param>
/// <param name="Entry">The entry it is in the world as, 0 when it is not.</param>
/// <param name="State">One phrase: "alive", "dead, respawn in 42 s", "chosen, grid not loaded", "out, respawn in 90 s", "out", ...</param>
public sealed record SpawnGroupMemberReport(uint Guid, int SlotId, uint Chance, uint Entry, string State);

/// <summary>A spawn group on one map as the GM commands show it.</summary>
/// <param name="EffectiveMaxCount">The maximum in force (a stored 0 derived as cmangos does).</param>
/// <param name="ConditionHolds">Whether the group's condition holds now (true without one).</param>
/// <param name="Formation">The formation line ("fanned out behind, spread 4, path 6883 (waypoint), leader 6883"), or null.</param>
public sealed record SpawnGroupReport(
    SpawnGroupDefinition Definition, int EffectiveMaxCount, bool ConditionHolds, IReadOnlyList<SpawnGroupMemberReport> Members, string? Formation)
{
    public int InWorld => Members.Count(m => m.Entry != 0);
}

/// <summary>A pooled spawn's pool as the GM commands show it (cmangos <c>.pool</c> / ShowNpcOrGoSpawnInformation).</summary>
public sealed record SpawnPoolReport(uint PoolId, string Description, uint MaxLimit, uint SpawnedCount, bool SpawnOut, uint MotherPool, string? MotherDescription);

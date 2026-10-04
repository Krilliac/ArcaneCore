namespace ArcaneCore.Game.Creatures;

/// <summary>Read access to respawn state for the GM commands (docs/integration/gm-objects-npc-lane.md).</summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>The number of respawn times kept for spawns whose creature left the map while dead (<see cref="PendingRespawnAt"/>).</summary>
    public int DormantRespawnCount => _respawnAt.Count;

    /// <summary>
    /// Milliseconds until a dead database spawn respawns (0 once due); null while it is alive, and null for a temporary creature
    /// (<see cref="SpawnTemporary"/>, <c>.npc add</c>, summons): the update loop respawns only creatures with a <see cref="Creature.Spawn"/>,
    /// so a dead temporary creature has no countdown even though <see cref="Creature.RespawnAtMs"/> was set at death.
    /// </summary>
    public long? RespawnRemainingMs(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        return creature.Spawn is null || creature.DeathState == CreatureDeathState.Alive ? null : Math.Max(0L, creature.RespawnAtMs - _clockMs);
    }
}

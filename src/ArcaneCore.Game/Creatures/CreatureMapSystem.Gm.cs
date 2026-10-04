namespace ArcaneCore.Game.Creatures;

/// <summary>Read access to respawn state for the GM commands (docs/integration/gm-objects-npc-lane.md).</summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>The number of respawn times kept for spawns whose creature left the map while dead (<see cref="PendingRespawnAt"/>).</summary>
    public int DormantRespawnCount => _respawnAt.Count;

    /// <summary>Milliseconds until a dead creature respawns (0 once due); null while it is alive.</summary>
    public long? RespawnRemainingMs(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        return creature.DeathState == CreatureDeathState.Alive ? null : Math.Max(0L, creature.RespawnAtMs - _clockMs);
    }
}

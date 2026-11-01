namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Removal of a database spawn that must not come back by itself (a script's RemoveFromWorld on a spawn: vmangos scourge_invasion.cpp
/// NecropolisHealthAI::SpellHitTarget, :449-457). The creature leaves the world with no respawn timer, and the "never" is kept through the
/// <see cref="ICreatureRespawnPersistence"/> seam so a restart (or a grid reload) does not bring it back.
/// Thread affinity: world thread only.
/// </summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// How far ahead the stored respawn time of a removed spawn lies (ten years): the persistence seam holds only times, so "never" is a time
    /// no server run reaches. Rows that far out are recognised by <see cref="IsRemovalRow"/> and dropped when the spawn is gated back in.
    /// </summary>
    public const long RemovedRespawnHorizonSeconds = 10L * 365 * 24 * 3600;

    /// <summary>
    /// Take <paramref name="creature"/> out of the world and keep it out: a database spawn gets "never" as its respawn delay (a death in
    /// progress included), the stored respawn time is set <see cref="RemovedRespawnHorizonSeconds"/> ahead, and the creature is despawned
    /// through <see cref="ForcedDespawn"/> after <paramref name="delayMs"/> (0: at once; use a delay when called from the creature's own
    /// death hook, which must finish first). A game event that gates the spawn out and in again brings it back (RefreshSpawns). A creature
    /// without a database spawn is simply despawned. Returns false when the creature is not in this system or was already removed this way.
    /// </summary>
    public bool RemoveWithoutRespawn(Creature creature, uint delayMs = 0)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!_creatures.ContainsKey(creature.Guid))
        {
            return false;
        }

        if (creature.Spawn is not { } spawn || creature.Summon is not null)
        {
            ForcedDespawn(creature, delayMs);
            return true;
        }

        if (creature.DeathState != CreatureDeathState.Alive && creature.RespawnAtMs == long.MaxValue)
        {
            return false; // removed before: nothing to schedule or store again
        }

        creature.RespawnDelayOverrideSeconds = Creature.RespawnNeverSeconds; // a death in progress schedules "never" from this
        if (creature.DeathState != CreatureDeathState.Alive)
        {
            creature.RespawnAtMs = long.MaxValue;
        }

        if (Persists)
        {
            _persistence!.Save(Map.MapId, Map.InstanceId, spawn.Guid, _respawnClock.UnixSeconds + RemovedRespawnHorizonSeconds);
        }

        ForcedDespawn(creature, delayMs);
        return true;
    }

    /// <summary>Whether a stored respawn time is one <see cref="RemoveWithoutRespawn"/> wrote (so far ahead no real respawn delay reaches it).</summary>
    private bool IsRemovalRow(long respawnUnixSeconds) => respawnUnixSeconds - _respawnClock.UnixSeconds > RemovedRespawnHorizonSeconds / 2;

    /// <summary>
    /// A gate decision forgets the dormant respawn of a spawn (<c>RefreshSpawns</c>): the in-memory time goes, and so does a stored
    /// <see cref="RemoveWithoutRespawn"/> row, which would otherwise keep the spawn dead after the next restart.
    /// </summary>
    private void ForgetRespawnForGate(uint spawnGuid)
    {
        _respawnAt.Remove(spawnGuid);
        if (Persists && _persistence!.TryGetPending(Map.MapId, Map.InstanceId, spawnGuid, out long stored) && IsRemovalRow(stored))
        {
            _persistence.Delete(Map.MapId, Map.InstanceId, spawnGuid);
        }
    }
}

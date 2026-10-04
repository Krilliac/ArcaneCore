namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Durable respawn times (docs/areas/creature-movement-spawns.md): the death of a database spawn is remembered through the
/// <see cref="ICreatureRespawnPersistence"/> seam, so a restart (or a grid unload) does not bring a dead rare or boss back at once.
/// vmangos saves at death (always for a world boss, for every DB creature with <c>SaveRespawnTimeImmediately</c>, the default;
/// Objects/Creature.cpp:2262-2263), otherwise when the object leaves the map (Maps/Map.cpp:1319-1322, ObjectGridLoader.cpp:346-348).
/// </summary>
public sealed partial class CreatureMapSystem
{
    private readonly ICreatureRespawnPersistence? _persistence;
    private readonly IRespawnClock _respawnClock;

    private bool Persists => _persistence is not null && _options.Respawn.Persist;

    /// <summary>
    /// Read the stored times of this map instance into the dormant-respawn table, so the creatures load dead for what is left of them
    /// (vmangos Creature::LoadFromDB: a respawn time in the future makes the creature DEAD, Creature.cpp:1972-1989). A time that has
    /// passed is deleted (Creature.cpp:1984-1989). Called before the first grid loads.
    /// </summary>
    private void LoadPersistedRespawns()
    {
        if (!Persists)
        {
            return;
        }

        long now = _respawnClock.UnixSeconds;
        foreach ((uint guid, long unix) in _persistence!.GetPending(Map.MapId, Map.InstanceId))
        {
            if (unix > now)
            {
                _respawnAt[guid] = _clockMs + ((unix - now) * 1000L);
            }
            else
            {
                _persistence.Delete(Map.MapId, Map.InstanceId, guid);
            }
        }
    }

    /// <summary>A database spawn died: save its respawn time when the options (or its rank) ask for it at death.</summary>
    private void SaveRespawnOnDeath(Creature creature)
    {
        if (!Persists || creature.Spawn is null || creature.Summon is not null)
        {
            return;
        }

        if (_options.Respawn.SaveImmediately || creature.IsWorldBoss)
        {
            _persistence!.Save(Map.MapId, Map.InstanceId, creature.Spawn.Guid, _respawnClock.UnixSeconds + (Math.Max(0, creature.RespawnAtMs - _clockMs) / 1000));
        }
    }

    /// <summary>The creature left the map while dead and the options defer saving to now (vmangos Map::Remove / ObjectGridUnloader).</summary>
    private void SaveRespawnOnRemoval(Creature creature)
    {
        if (Persists && !_options.Respawn.SaveImmediately)
        {
            SaveRespawnTime(creature);
        }
    }

    /// <summary>The respawn row of a spawn is no longer needed (it is alive again).</summary>
    private void DeletePersistedRespawn(Creature creature)
    {
        if (Persists && creature.Spawn is not null && creature.Summon is null)
        {
            _persistence!.Delete(Map.MapId, Map.InstanceId, creature.Spawn.Guid);
        }
    }

    /// <summary>
    /// vmangos Creature::SaveRespawnTime (Objects/Creature.cpp:2785-2794): when the respawn time is still in the future it is saved as it is
    /// (<c>m_respawnTime</c>, corpse or not); only when it has passed but the corpse remains is <c>now + respawn delay + the corpse time left</c> saved.
    /// </summary>
    private void SaveRespawnTime(Creature creature)
    {
        if (creature.Spawn is null || creature.Summon is not null || creature.DeathState == CreatureDeathState.Alive)
        {
            return;
        }

        long now = _respawnClock.UnixSeconds;
        long remainingMs = creature.RespawnAtMs - _clockMs;
        long at;
        if (remainingMs > 0)
        {
            at = now + (remainingMs / 1000);
        }
        else if (creature.CorpseDecayMs > 0)
        {
            at = now + creature.RespawnDelaySeconds + (creature.CorpseDecayMs / 1000);
        }
        else
        {
            return;
        }

        _persistence!.Save(Map.MapId, Map.InstanceId, creature.Spawn.Guid, at);
    }
    /// <summary>
    /// Save the respawn time of every dead creature on this map that is still held in memory: shutdown, or an unloading instance. Only with
    /// <c>Creatures:Respawn:SaveImmediately</c> off: with the default every death was saved when it happened, and vmangos saves nothing more when the
    /// object leaves the map (Maps/Map.cpp:1319-1322 "if option set then object already saved at this moment"). A no-op without a persistence seam
    /// or with <c>Creatures:Respawn:Persist</c> off. World thread, or after the world stopped.
    /// </summary>
    public void SaveRespawnTimes()
    {
        if (!Persists || _options.Respawn.SaveImmediately)
        {
            return;
        }

        foreach (Creature creature in _creatures.Values)
        {
            if (creature.DeathState != CreatureDeathState.Alive)
            {
                SaveRespawnTime(creature);
            }
        }
    }
}

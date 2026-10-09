using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Game-event spawns of the creature system (docs/areas/game-events-weather.md): a spawn listed in <c>game_event_creature</c> is not
/// created when its grid loads unless the <see cref="ISpawnGate"/> allows it, and <see cref="RefreshSpawns"/> brings the live
/// objects in line when an event starts or stops (vmangos GameEventSpawn / GameEventUnspawn, GameEventMgr.cpp:807-960).
/// </summary>
public sealed partial class CreatureMapSystem
{
    private ISpawnGate? _spawnGate;
    private Dictionary<uint, CreatureSpawn>? _spawnByGuid;

    /// <summary>
    /// The gate every grid load asks (null: every spawn is allowed, the default). Setting it re-evaluates the spawns it lists, so a
    /// gate installed after grids loaded removes what it refuses.
    /// </summary>
    public ISpawnGate? SpawnGate
    {
        get => _spawnGate;
        set
        {
            _spawnGate = value;
            if (value is not null)
            {
                RefreshSpawns(value.GatedCreatures);
            }
        }
    }

    /// <summary>The game-event gate and the instance script's variable gate (<c>InstanceData.AllowsCreatureSpawn</c>) both allow the spawn.</summary>
    private bool SpawnAllowed(uint spawnGuid)
        => (_spawnGate?.AllowsCreature(spawnGuid) ?? true)
            && (Map.FindUpdater<Instances.Scripts.InstanceData>()?.AllowsCreatureSpawn(spawnGuid) ?? true);

    /// <summary>
    /// Make the world agree with the gate for these database spawn guids: a spawn the gate now allows is created when its grid is
    /// loaded (a grid that is not loaded creates it at load), and one it refuses is destroyed for every client that sees it, its
    /// pending respawn forgotten, so a later start brings it back alive. Guids that are not spawns of this map are ignored.
    /// </summary>
    public void RefreshSpawns(IEnumerable<uint> spawnGuids)
    {
        ArgumentNullException.ThrowIfNull(spawnGuids);
        _spawnByGuid ??= _spawnsByGrid.Values.SelectMany(l => l).ToDictionary(s => s.Guid);
        foreach (uint guid in spawnGuids)
        {
            if (!_spawnByGuid.TryGetValue(guid, out CreatureSpawn? spawn))
            {
                continue;
            }

            Creature? live = FindLive(spawn, _options.Respawn.AlternateEntries ? _content.GetSpawnEntries(spawn.Guid) : []);
            bool allowed = SpawnAllowed(guid);
            if (!allowed)
            {
                _respawnAt.Remove(guid);
                if (live is not null)
                {
                    Despawn(live);
                }

                continue;
            }

            if (live is null && _grids.TryGetValue(ComputeGrid(spawn.X, spawn.Y), out LoadedGrid? grid))
            {
                _respawnAt.Remove(guid); // a stale death of an earlier run of the event
                LoadSpawns(grid, [spawn]);
            }
        }
    }
}

/// <summary>The respawn modes a battleground sets on its event creatures (vmangos BattleGround::SpawnBGCreature).</summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>vmangos RESPAWN_2MINUTES: the delay a forced event creature comes back after.</summary>
    public const uint EventRespawnSeconds = 120;

    /// <summary>
    /// vmangos SpawnBGCreature for the live creature of a spawn (BattleGround.cpp:1590-1630). RESPAWN_FORCED: later deaths come back after two
    /// minutes, and a dead one waiting for its respawn comes back in a second. RESPAWN_STOP: no later death comes back, and a dead one whose
    /// corpse is gone stays dead; a corpse keeps the respawn time it had (vmangos only moves a time already past). Returns false when the spawn
    /// has no creature in this map.
    /// </summary>
    public bool SetEventRespawnMode(uint spawnGuid, bool forced)
    {
        Creature? creature = _creatures.Values.FirstOrDefault(c => c.Spawn?.Guid == spawnGuid);
        if (creature is null)
        {
            return false;
        }

        if (forced)
        {
            creature.RespawnDelayOverrideSeconds = EventRespawnSeconds;
            if (creature.DeathState != CreatureDeathState.Alive && creature.RespawnAtMs > _clockMs)
            {
                creature.RespawnAtMs = _clockMs + 1000;
            }

            return true;
        }

        creature.RespawnDelayOverrideSeconds = Creature.RespawnNeverSeconds;
        if (creature.DeathState == CreatureDeathState.Dead)
        {
            creature.RespawnAtMs = long.MaxValue;
        }

        return true;
    }
}

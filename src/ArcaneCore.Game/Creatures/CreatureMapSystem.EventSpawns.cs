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

            ObjectGuid objectGuid = ObjectGuid.WithEntry(HighGuid.Unit, spawn.Entry, spawn.Guid);
            bool allowed = _spawnGate?.AllowsCreature(guid) ?? true;
            if (!allowed)
            {
                _respawnAt.Remove(guid);
                if (_creatures.TryGetValue(objectGuid, out Creature? live))
                {
                    Despawn(live);
                }

                continue;
            }

            if (!_creatures.ContainsKey(objectGuid) && _grids.TryGetValue(ComputeGrid(spawn.X, spawn.Y), out LoadedGrid? grid))
            {
                _respawnAt.Remove(guid); // a stale death of an earlier run of the event
                LoadSpawns(grid, [spawn]);
            }
        }
    }
}

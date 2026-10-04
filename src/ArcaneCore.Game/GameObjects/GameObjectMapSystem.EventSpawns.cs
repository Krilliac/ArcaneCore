using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// Game-event spawns of the gameobject system (docs/areas/game-events-weather.md): a spawn listed in <c>game_event_gameobject</c> is
/// not created when its grid loads unless the <see cref="ISpawnGate"/> allows it, and <see cref="RefreshSpawns"/> brings the live
/// objects in line when an event starts or stops (vmangos GameEventSpawn / GameEventUnspawn, GameEventMgr.cpp:807-960).
/// </summary>
public sealed partial class GameObjectMapSystem
{
    private ISpawnGate? _spawnGate;
    private Dictionary<uint, GameObjectSpawn>? _spawnByGuid;

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
                RefreshSpawns(value.GatedGameObjects);
            }
        }
    }

    /// <summary>
    /// Make the world agree with the gate for these database spawn guids: a spawn the gate now allows is created when its grid is
    /// loaded, and (spawn time below zero: "spawned by events and scripts only") brought into the world at once; one it refuses is
    /// taken out of the world (destroyed for every client that sees it), with its pending respawn and loot dropped. Guids that are
    /// not spawns of this map are ignored.
    /// </summary>
    public void RefreshSpawns(IEnumerable<uint> spawnGuids)
    {
        ArgumentNullException.ThrowIfNull(spawnGuids);
        _spawnByGuid ??= _spawnsByGrid.Values.SelectMany(l => l).ToDictionary(s => s.Guid);
        foreach (uint guid in spawnGuids)
        {
            if (!_spawnByGuid.TryGetValue(guid, out GameObjectSpawn? spawn))
            {
                continue;
            }

            ObjectGuid objectGuid = ObjectGuid.WithEntry(HighGuid.GameObject, spawn.Entry, spawn.Guid);
            _objects.TryGetValue(objectGuid, out GameObject? live);
            if (!(_spawnGate?.AllowsGameObject(guid) ?? true))
            {
                _respawnAt.Remove(guid);
                _unloadedLoot.Remove(guid);
                if (live is not null)
                {
                    Remove(live);
                }

                continue;
            }

            if (live is null && _grids.TryGetValue(CreatureMapSystem.ComputeGrid(spawn.X, spawn.Y), out List<GameObject>? list))
            {
                _respawnAt.Remove(guid);
                LoadSpawns(list, [spawn]);
                if (_objects.TryGetValue(objectGuid, out GameObject? added) && !added.IsSpawned && spawn.SpawnTimeSeconds < 0)
                {
                    ForceRespawn(added); // an event spawn of a negative spawntimesecs object
                }
            }
        }
    }
}

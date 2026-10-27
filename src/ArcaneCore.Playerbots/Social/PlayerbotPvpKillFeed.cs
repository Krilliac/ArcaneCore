using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// The player deaths of one world, for the bot social memory's PvP rivalry: <see cref="MapCombat.UnitKilled"/> of every map of the world (the
/// maps there at attach and every map created later, each dropped when it unloads), handed on once per death of a player with the killer
/// (null for the environment). One handler per map, so a death is heard exactly once whoever took part, and no bot has to be registered
/// anywhere first: a bot that just changed maps, a paused bot or a bot whose brain was replaced is covered all the same. Per world, not
/// process-wide: <see cref="Dispose"/> (the feature's detach) takes every handler off again, so nothing outlives the world it watches.
/// Same pattern as <see cref="ArcaneCore.Game.Death.DeathOutcomeWatcher"/>. The handler runs on the map's thread and must not throw.
/// </summary>
internal sealed class PlayerbotPvpKillFeed : IDisposable
{
    private readonly WorldRuntime _world;
    private readonly Action<Unit?, Player> _playerKilled;
    private readonly Action<Unit?, Unit> _onKilled;
    private readonly Action<Map> _onMapCreated;
    private readonly Action<Map> _onMapUnloading;
    private readonly Lock _gate = new();
    private readonly HashSet<Map> _maps = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    public PlayerbotPvpKillFeed(WorldRuntime world, Action<Unit?, Player> playerKilled)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(playerKilled);
        _world = world;
        _playerKilled = playerKilled;
        _onKilled = OnUnitKilled;
        _onMapCreated = Add;
        _onMapUnloading = Remove;
        world.MapCreated += _onMapCreated;
        world.MapUnloading += _onMapUnloading;
        foreach (Map map in world.Maps) Add(map);
    }

    /// <summary>The maps currently heard (tests).</summary>
    internal int MapCount
    {
        get
        {
            lock (_gate) return _maps.Count;
        }
    }

    /// <summary>Whether the deaths on <paramref name="map"/> are heard (tests).</summary>
    internal bool Hears(Map map)
    {
        lock (_gate) return _maps.Contains(map);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _world.MapCreated -= _onMapCreated;
            _world.MapUnloading -= _onMapUnloading;
            // Every map is detached even if one throws (a map torn down meanwhile): try per map, then clear.
            foreach (Map map in _maps)
            {
                try { map.Combat.UnitKilled -= _onKilled; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { FaultLedger.Shared.Record("playerbot.pvp-feed", ex); }
            }

            _maps.Clear();
        }
    }

    internal void Add(Map map)
    {
        lock (_gate)
        {
            if (_disposed || !_maps.Add(map)) return;
            map.Combat.UnitKilled += _onKilled;
        }
    }

    internal void Remove(Map map)
    {
        lock (_gate)
        {
            if (!_maps.Remove(map)) return;
            try { map.Combat.UnitKilled -= _onKilled; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { FaultLedger.Shared.Record("playerbot.pvp-feed", ex); } // the unload goes on
        }
    }

    private void OnUnitKilled(Unit? killer, Unit victim)
    {
        if (victim is Player player) _playerKilled(killer, player);
    }
}

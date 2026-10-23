using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Maps;

public sealed partial class Map
{
    // Consecutive failure count per updater; -1 means the fault breaker is skipping it. Empty
    // unless an updater throws and World:MaxConsecutiveUpdaterFaults is set.
    private readonly Dictionary<IMapUpdater, int> _updaterFaults = new(ReferenceEqualityComparer.Instance);

    /// <summary>The systems attached with <see cref="AddUpdater"/>, in attach order.</summary>
    public IReadOnlyList<IMapUpdater> Updaters => _updaters;

    /// <summary>Attach a per-map system, updated every tick after the timers (world thread).</summary>
    public void AddUpdater(IMapUpdater updater)
    {
        ArgumentNullException.ThrowIfNull(updater);
        EnsureWorldThread();
        EnsureNotInUpdatePhase();
        _updaters.Add(updater);
    }

    /// <summary>
    /// Detach a per-map system that <see cref="AddUpdater"/> attached (world thread, outside the update phase), forgetting its fault count.
    /// False when it was not attached.
    /// </summary>
    public bool RemoveUpdater(IMapUpdater updater)
    {
        ArgumentNullException.ThrowIfNull(updater);
        EnsureWorldThread();
        EnsureNotInUpdatePhase();
        _updaterFaults.Remove(updater);
        return _updaters.Remove(updater);
    }

    /// <summary>
    /// Give every updater skipped by the fault breaker another chance, and forget the failure
    /// counts (world thread). A code hot reload calls this after each applied edit, because the
    /// edit may be the fix.
    /// </summary>
    public void ClearUpdaterFaults()
    {
        EnsureWorldThread();
        _updaterFaults.Clear();
    }

    /// <summary>The updaters the fault breaker is skipping (world thread).</summary>
    public IReadOnlyList<IMapUpdater> IsolatedUpdaters
        => [.. _updaters.Where(u => _updaterFaults.TryGetValue(u, out int faults) && faults < 0)];

    private void CountUpdaterFault(IMapUpdater updater, Exception? lastException = null)
    {
        int limit = _world.Options.MaxConsecutiveUpdaterFaults;
        if (limit <= 0)
        {
            return;
        }

        int faults = _updaterFaults.GetValueOrDefault(updater) + 1;
        if (faults < limit)
        {
            _updaterFaults[updater] = faults;
            return;
        }

        _updaterFaults[updater] = -1;
        _logger.LogError(
            lastException,
            "map {MapId} instance {InstanceId} updater {Updater} failed {Limit} ticks in a row (last {ExceptionType}: {Message}) and is skipped until the next code edit is applied or the server restarts",
            MapId, InstanceId, updater.GetType().Name, limit, lastException?.GetType().Name ?? "unknown", lastException?.Message ?? string.Empty);
        AnomalyLedger.Shared.Report("map", UpdaterIsolatedCode, $"map {MapId} updater {updater.GetType().Name}",
            $"updater failed {limit} ticks in a row and is skipped (instance {InstanceId}, last {lastException?.GetType().Name ?? "unknown"}: {lastException?.Message})", AnomalySeverity.Warning);
    }

    /// <summary>The first attached system of type <typeparamref name="T"/>, if any (called per creature per tick: no LINQ, no enumerator box).</summary>
    public T? FindUpdater<T>()
        where T : class, IMapUpdater
    {
        for (int i = 0; i < _updaters.Count; i++)
        {
            if (_updaters[i] is T found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Anomaly code: a map updater failed the consecutive-tick limit and is isolated by the fault breaker.</summary>
    public const string UpdaterIsolatedCode = "updater-isolated";
}

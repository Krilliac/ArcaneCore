using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;

namespace ArcaneCore.World.Gm.DebugTools;

/// <summary>
/// Per-map update cost for <c>.debug maps</c>: the world loop reports every map update through <see cref="WorldRuntime.MapUpdated"/>
/// (on the world thread, also in the parallel maps phase, after the barrier), and this keeps the last, a smoothed mean (about the last
/// 50 updates), the maximum since the last <c>.debug maps reset</c> and an update count per map. One dictionary lookup per map per tick.
/// </summary>
public sealed class MapCostTracker : IWorldFeature
{
    private const double Smoothing = 0.02;
    private readonly object _gate = new();
    private readonly Dictionary<Map, MapCost> _costs = [];

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.MapUpdated += OnMapUpdated;
        world.MapUnloading += map => { lock (_gate) _costs.Remove(map); };
    }

    /// <summary>Record one map update of <paramref name="micros"/> microseconds.</summary>
    public void OnMapUpdated(Map map, long micros)
    {
        lock (_gate)
        {
            if (!_costs.TryGetValue(map, out MapCost? cost))
            {
                cost = new MapCost { MeanMicros = micros };
                _costs[map] = cost;
            }

            cost.Updates++;
            cost.LastMicros = micros;
            cost.MeanMicros += (micros - cost.MeanMicros) * Smoothing;
            cost.TotalMicros += micros;
            if (micros > cost.MaxMicros) cost.MaxMicros = micros;
        }
    }

    /// <summary>The recorded maps, most expensive (smoothed mean) first.</summary>
    public IReadOnlyList<MapCostSample> Snapshot()
    {
        lock (_gate)
        {
            return [.. _costs.Select(kv => new MapCostSample(kv.Key, kv.Value.Updates, kv.Value.LastMicros, kv.Value.MeanMicros, kv.Value.MaxMicros, kv.Value.TotalMicros))
                .OrderByDescending(s => s.MeanMicros)];
        }
    }

    /// <summary>Clear the maxima and totals (the smoothed means stay).</summary>
    public void ResetMaxima()
    {
        lock (_gate)
        {
            foreach (MapCost cost in _costs.Values)
            {
                cost.MaxMicros = 0;
                cost.TotalMicros = 0;
                cost.Updates = 0;
            }
        }
    }

    private sealed class MapCost
    {
        public long Updates;
        public long LastMicros;
        public double MeanMicros;
        public long MaxMicros;
        public long TotalMicros;
    }
}

/// <summary>One map's update cost (<see cref="MapCostTracker.Snapshot"/>).</summary>
public sealed record MapCostSample(Map Map, long Updates, long LastMicros, double MeanMicros, long MaxMicros, long TotalMicros);

using System.Diagnostics.Metrics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Ops.Metrics;

/// <summary>
/// The world daemon's instruments (docs/ops/metrics.md), after TrinityCore's world metrics
/// (<c>World::Update</c> <c>TC_METRIC_TIMER("world_update_time")</c>, <c>"map_update_time"</c> per map,
/// <c>TC_METRIC_VALUE("online_players")</c>, <c>"map_creatures"</c>/<c>"map_players"</c>, <c>"db_queue_*"</c>):
/// tick and map-update duration histograms, world-thread allocation, sessions, online players, the character save
/// queue depth and, sampled on the world thread every <c>Ops:Metrics:MapSampleIntervalSeconds</c>, each map's players,
/// creatures, objects and mean/max update time. Disabled metrics: nothing is attached and the world loop pays one null
/// check per tick and per map.
/// </summary>
public sealed class WorldMetricsFeature(IServiceProvider services, ILogger<WorldMetricsFeature> logger) : IWorldFeature, IDisposable
{
    private readonly Dictionary<Map, MapAccumulator> _maps = [];
    private Meter? _meter;
    private Histogram<double>? _tick;
    private Histogram<double>? _mapsPhase;
    private Counter<long>? _allocated;
    private Counter<long>? _ticks;
    private volatile MapSample[] _samples = [];
    private long _sampleIntervalMicros;
    private long _sinceSampleMicros;
    private bool _perMap;

    /// <summary>The latest per-map sample (world thread writes, collection reads).</summary>
    public IReadOnlyList<MapSample> Samples => _samples;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        MetricsOptions? options = services.GetService<MetricsOptions>();
        if (options is not { Enabled: true })
        {
            return;
        }

        SessionRegistry? sessions = services.GetService<SessionRegistry>();
        CharacterSaveQueue? saves = services.GetService<CharacterSaveQueue>();
        _perMap = options.PerMapMetrics;
        _sampleIntervalMicros = options.MapSampleIntervalSeconds * 1_000_000L;
        _meter = new Meter("ArcaneCore.World", "1.0");
        _tick = _meter.CreateHistogram<double>("arcanecore.world.tick.duration", "ms", "World tick body duration.");
        _mapsPhase = _meter.CreateHistogram<double>("arcanecore.world.map_update.duration", "ms", "Time spent updating every map in one tick.");
        _allocated = _meter.CreateCounter<long>("arcanecore.world.tick.allocated", "By", "Bytes the world thread allocated during ticks.");
        _ticks = _meter.CreateCounter<long>("arcanecore.world.ticks", "{tick}", "World ticks run.");
        _meter.CreateObservableGauge("arcanecore.world.sessions", () => sessions?.Count ?? 0, "{session}", "Authenticated world sessions.");
        _meter.CreateObservableGauge("arcanecore.world.players_online", () => world.OnlinePlayerCount, "{player}", "Characters in the world.");
        _meter.CreateObservableGauge("arcanecore.db.save_queue_depth", () => saves?.Pending ?? 0, "{snapshot}", "Character snapshots queued or being written.");
        _meter.CreateObservableGauge("arcanecore.world.maps_loaded", () => _samples.Length, "{map}", "Maps (and instances) loaded at the last sample.");
        if (_perMap)
        {
            _meter.CreateObservableGauge("arcanecore.map.players", () => PerMap(s => s.Players), "{player}", "Players on the map at the last sample.");
            _meter.CreateObservableGauge("arcanecore.map.creatures", () => PerMap(s => s.Creatures), "{creature}", "Creatures on the map at the last sample.");
            _meter.CreateObservableGauge("arcanecore.map.objects", () => PerMap(s => s.Objects), "{object}", "Objects on the map at the last sample.");
            _meter.CreateObservableGauge("arcanecore.map.update_time_mean", () => PerMapDouble(s => s.MeanUpdateMs), "ms", "Mean map update time over the last sample period.");
            _meter.CreateObservableGauge("arcanecore.map.update_time_max", () => PerMapDouble(s => s.MaxUpdateMs), "ms", "Longest map update over the last sample period.");
            world.MapUpdated += OnMapUpdated;
        }

        world.MapUnloading += map => _maps.Remove(map);
        world.TickCompleted += (durationMicros, bytes, phases) => OnTick(world, durationMicros, bytes, phases);
        logger.LogInformation("world metrics attached (per-map {PerMap}, sample every {Interval} s)", _perMap, options.MapSampleIntervalSeconds);
    }

    public void Dispose() => _meter?.Dispose();

    // World thread, once per tick: two histogram records and two counter adds; the map walk runs once per sample period.
    private void OnTick(WorldRuntime world, long durationMicros, long bytes, TickPhases phases)
    {
        _tick!.Record(durationMicros / 1000.0);
        _mapsPhase!.Record(phases.MapsMicros / 1000.0);
        _allocated!.Add(bytes);
        _ticks!.Add(1);
        _sinceSampleMicros += Math.Max(durationMicros, world.Options.TickIntervalMs * 1000L);
        if (_sinceSampleMicros >= _sampleIntervalMicros)
        {
            _sinceSampleMicros = 0;
            Sample(world);
        }
    }

    private void OnMapUpdated(Map map, long micros)
    {
        if (!_maps.TryGetValue(map, out MapAccumulator? acc))
        {
            acc = new MapAccumulator();
            _maps[map] = acc;
        }

        acc.Updates++;
        acc.TotalMicros += micros;
        acc.MaxMicros = Math.Max(acc.MaxMicros, micros);
    }

    private void Sample(WorldRuntime world)
    {
        var list = new List<MapSample>();
        foreach (Map map in world.Maps)
        {
            if (map.IsUnloaded)
            {
                continue;
            }

            double mean = 0, max = 0;
            if (_maps.TryGetValue(map, out MapAccumulator? acc) && acc.Updates > 0)
            {
                mean = acc.TotalMicros / 1000.0 / acc.Updates;
                max = acc.MaxMicros / 1000.0;
                acc.Updates = 0;
                acc.TotalMicros = 0;
                acc.MaxMicros = 0;
            }

            list.Add(new MapSample(map.MapId, map.InstanceId, map.PlayerCount, _perMap ? map.CountObjectsOf<Creature>() : 0, map.ObjectCount, mean, max));
        }

        _samples = [.. list];
    }

    private IEnumerable<Measurement<int>> PerMap(Func<MapSample, int> value)
    {
        foreach (MapSample s in _samples)
        {
            yield return new Measurement<int>(value(s), Tags(s));
        }
    }

    private IEnumerable<Measurement<double>> PerMapDouble(Func<MapSample, double> value)
    {
        foreach (MapSample s in _samples)
        {
            yield return new Measurement<double>(value(s), Tags(s));
        }
    }

    private static KeyValuePair<string, object?>[] Tags(MapSample s) =>
        [new("map", s.MapId), new("instance", s.InstanceId)];

    private sealed class MapAccumulator
    {
        public long Updates;
        public long TotalMicros;
        public long MaxMicros;
    }
}

/// <summary>One map's state at a metrics sample.</summary>
public readonly record struct MapSample(uint MapId, uint InstanceId, int Players, int Creatures, int Objects, double MeanUpdateMs, double MaxUpdateMs);

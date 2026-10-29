using System.Diagnostics;
using System.Diagnostics.Metrics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
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
        _meter.CreateObservableCounter("arcanecore.nav.tile_installs", () => SafeObserve(() => PathSearchCounters.ProcessTileReads.Reads, 0L), "{tile}",
            "Navigation mesh tiles installed on the thread that needed them (on-demand path-query reads and grid tile loads; the world thread makes nearly all).");
        _meter.CreateObservableCounter("arcanecore.nav.tile_install_time", () => SafeObserve(() => PathSearchCounters.ProcessTileReads.Ticks * 1000.0 / Stopwatch.Frequency, 0d), "ms",
            "Time those installations took.");
        _meter.CreateObservableCounter("arcanecore.nav.bv_trees_rejected", () => SafeObserve(() => PathSearchCounters.BvTreesRejected, 0L), "{tile}",
            "Navigation tiles read without a usable BV tree (nearest-polygon lookups on them scan every polygon).");
        _meter.CreateObservableCounter("arcanecore.creature.path.straight_fallbacks", () => SafeObserve(() => CreaturePathStats.DirectFallbacks, 0L), "{path}",
            "Authored creature routes (waypoint, point, formation, scripted move) walked along a straight line because the navmesh had no path.");
        _meter.CreateObservableCounter("arcanecore.creature.path.no_path", () => SafeObserve(() => CreaturePathStats.NoPath, 0L), "{path}",
            "Creature path searches that found no path (chase stands, flee and wander wait, home teleports).");
        _meter.CreateObservableCounter("arcanecore.creature.path.deferred", () => SafeObserve(() => CreaturePathStats.Deferred, 0L), "{path}",
            "Creature path requests put off to a later update because the per-update search budget was spent.");
        _meter.CreateObservableCounter("arcanecore.creature.path.home_teleports", () => SafeObserve(() => CreaturePathStats.HomeTeleports, 0L), "{creature}",
            "Evade returns done by teleport because no whole mesh path led home.");
        _meter.CreateObservableCounter("arcanecore.creature.drape.shared", () => SafeObserve(() => SharedDrapeStats.Hits, 0L), "{path}",
            "Creature leg ground drapes answered from loaded tiles outside the world's serial gate.");
        _meter.CreateObservableCounter("arcanecore.creature.drape.gate_fallbacks", () => SafeObserve(() => SharedDrapeStats.Fallbacks, 0L), "{path}",
            "Creature leg ground drapes that needed a tile or navmesh lookup the shared drape may not do and ran again inside the serial gate.");
        _meter.CreateObservableGauge("arcanecore.world.sessions", () => SafeObserve(() => sessions?.Count ?? 0, 0), "{session}", "Authenticated world sessions.");
        _meter.CreateObservableGauge("arcanecore.playerbots.split_brain", () => SafeObserve(() => services.GetService<Bots.ManagedBotRegistry>()?.SplitBrain == true ? 1 : 0, 0), "{state}",
            "1 while this world stopped writing playerbot rows because another world process changed them (split brain), otherwise 0.");
        _meter.CreateObservableGauge("arcanecore.world.players_online", () => SafeObserve(() => world.OnlinePlayerCount, 0), "{player}", "Characters in the world.");
        _meter.CreateObservableGauge("arcanecore.db.save_queue_depth", () => SafeObserve(() => saves?.Pending ?? 0, 0), "{snapshot}", "Character snapshots queued or being written.");
        _meter.CreateObservableGauge("arcanecore.db.save_queue_oldest_age", () => SafeObserve(() => saves is null ? 0d : saves.OldestPendingAge.TotalMilliseconds, 0d), "ms", "How long the longest-waiting character snapshot has been queued or in its write (0 when none).");
        _meter.CreateObservableGauge("arcanecore.world.maps_loaded", () => SafeObserve(() => _samples.Length, 0), "{map}", "Maps (and instances) loaded at the last sample.");
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

    private static T SafeObserve<T>(Func<T> callback, T fallback) where T : struct
    {
        try
        {
            return callback();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("metrics.observe", ex);
            return fallback;
        }
    }

    // World thread, once per tick: two histogram records and two counter adds; the map walk runs once per sample period.
    private void OnTick(WorldRuntime world, long durationMicros, long bytes, TickPhases phases)
    {
        try
        {
            if (_tick is { } tick)
            {
                tick.Record(durationMicros / 1000.0);
            }

            if (_mapsPhase is { } mapsPhase)
            {
                mapsPhase.Record(phases.MapsMicros / 1000.0);
            }

            if (_allocated is { } allocated)
            {
                allocated.Add(bytes);
            }

            if (_ticks is { } ticks)
            {
                ticks.Add(1);
            }

            _sinceSampleMicros += Math.Max(durationMicros, world.Options.TickIntervalMs * 1000L);
            if (_sinceSampleMicros >= _sampleIntervalMicros)
            {
                _sinceSampleMicros = 0;
                Sample(world);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("metrics.tick", ex);
        }
    }

    private void OnMapUpdated(Map map, long micros)
    {
        try
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("metrics.map-update", ex);
        }
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
        try
        {
            MapSample[] snapshot = _samples;
            var result = new Measurement<int>[snapshot.Length];
            for (int i = 0; i < snapshot.Length; i++)
            {
                MapSample s = snapshot[i];
                result[i] = new Measurement<int>(value(s), Tags(s));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("metrics.observe", ex);
            return [];
        }
    }

    private IEnumerable<Measurement<double>> PerMapDouble(Func<MapSample, double> value)
    {
        try
        {
            MapSample[] snapshot = _samples;
            var result = new Measurement<double>[snapshot.Length];
            for (int i = 0; i < snapshot.Length; i++)
            {
                MapSample s = snapshot[i];
                result[i] = new Measurement<double>(value(s), Tags(s));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("metrics.observe", ex);
            return [];
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

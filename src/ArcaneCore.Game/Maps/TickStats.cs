using System.Diagnostics;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Game.Maps;

/// <summary>An immutable view of <see cref="TickStats"/>. Percentiles are 0 when <see cref="Samples"/> is 0: check it first.</summary>
public sealed record TickStatsSnapshot(
    long TotalTicks,
    int Samples,
    long Overruns,
    long P50Micros,
    long P90Micros,
    long P99Micros,
    long P999Micros,
    long MaxMicros,
    double MeanMicros,
    double MeanAllocatedBytes,
    long MaxAllocatedBytes,
    long LastTickTimestamp)
{
    public int FrameSamples { get; init; }
    public double MeanFrameIntervalMicros { get; init; }
    public double? EffectiveTicksPerSecond { get; init; }
    public long FrameOverruns { get; init; }

    /// <summary>Nearest-rank percentiles of the frame interval (time between two tick starts) over the window; 0 when <see cref="FrameSamples"/> is 0.</summary>
    public long FrameP50Micros { get; init; }
    public long FrameP90Micros { get; init; }
    public long FrameP99Micros { get; init; }

    /// <summary>
    /// Full latency distribution (p50..p99.9, mean, standard deviation, min, max) of the tick body and of the frame interval, microseconds, over the last minute, the last 15 minutes and
    /// since start. Unlike the ring-based fields above these cover the whole window however many ticks it holds.
    /// </summary>
    public LatencyWindows BodyLatency { get; init; }
    public LatencyWindows FrameLatency { get; init; }

    /// <summary>The last minute of each tick phase (commands, world_tick, features, maps, autosave; autosave only counts ticks that saved), in that order; empty phases have no samples.</summary>
    public IReadOnlyList<(string Name, LatencySnapshot Last1Min)> PhaseLatency { get; init; } = [];

    /// <summary>95th percentile tick duration (nearest rank; 0 when <see cref="TickStatsSnapshot.Samples"/> is 0).</summary>
    public long P95Micros { get; init; }

    /// <summary>Mean and maximum time of each tick phase over the window: posted commands, map updates, world features.</summary>
    public TickPhaseSummary Commands { get; init; }
    public TickPhaseSummary Maps { get; init; }
    public TickPhaseSummary Features { get; init; }

    /// <summary>Per world-feature time (exponential moving average over roughly the last 50 ticks), slowest first.</summary>
    public IReadOnlyList<(string Name, double MeanMicros)> FeatureMeans { get; init; } = [];

    /// <summary>Mean bytes the world thread allocated per tick in each phase over the window (0 without samples).</summary>
    public double CommandsMeanBytes { get; init; }
    public double MapsMeanBytes { get; init; }
    public double FeaturesMeanBytes { get; init; }

    /// <summary>Per world-feature allocation (bytes per tick, smoothed like <see cref="FeatureMeans"/>), largest first.</summary>
    public IReadOnlyList<(string Name, double MeanBytes)> FeatureAllocations { get; init; } = [];
}

/// <summary>Mean and maximum of one tick phase over the recorded window.</summary>
public readonly record struct TickPhaseSummary(double MeanMicros, long MaxMicros);

/// <summary>The time one tick spent in each phase (microseconds), and the bytes the world thread allocated in each.</summary>
public readonly record struct TickPhases(long CommandsMicros, long MapsMicros, long FeaturesMicros)
{
    public long CommandsBytes { get; init; }
    public long MapsBytes { get; init; }
    public long FeaturesBytes { get; init; }

    /// <summary>Of <see cref="FeaturesMicros"/>, the world-tick handlers (game events, rest ...) and the script hooks; the rest is the per-tick feature updates.</summary>
    public long WorldTickMicros { get; init; }

    /// <summary>The periodic character-save pass that ends a tick (zero in the ticks that do not autosave) and the snapshots it queued.</summary>
    public long AutosaveMicros { get; init; }
    public int AutosaveSnapshots { get; init; }

    /// <summary>Posted world commands run this tick.</summary>
    public int CommandsRun { get; init; }

    /// <summary>True when <see cref="Gc"/> was captured around the tick (the world loop does; a direct <c>RunTick</c> call does not).</summary>
    public bool GcCaptured { get; init; }

    /// <summary>
    /// What the garbage collector did while the tick ran: the process-wide pause time and collection counts that elapsed between the
    /// start and the end of the tick body. A blocking collection suspends the world thread too, so its pause is inside the tick time.
    /// </summary>
    public GcDelta Gc { get; init; }

    /// <summary>The split as one log fragment: commands, world-tick handlers, per-tick feature updates, maps, autosave, and (when captured) the GC.</summary>
    public string Format()
    {
        string phases = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"commands {CommandsMicros / 1000} ms ({CommandsRun} run), world-tick handlers {WorldTickMicros / 1000} ms, features {Math.Max(0, FeaturesMicros - WorldTickMicros) / 1000} ms, maps {MapsMicros / 1000} ms, autosave {AutosaveMicros / 1000} ms ({AutosaveSnapshots} snapshots)");
        return GcCaptured ? phases + ", " + Gc.Format() : phases;
    }
}

/// <summary>
/// The garbage collector's counters at one instant: total pause time and the three collection counts. Reading them is a handful of
/// runtime counter loads and allocates nothing (a struct of four integers), so the world loop takes two per tick.
/// </summary>
public readonly record struct GcSnapshot(long PauseTicks, int Gen0, int Gen1, int Gen2)
{
    /// <summary>Read the counters now (<see cref="GC.GetTotalPauseDuration"/>, <see cref="GC.CollectionCount(int)"/>).</summary>
    public static GcSnapshot Read() => new(GC.GetTotalPauseDuration().Ticks, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    /// <summary>The counters that elapsed from <paramref name="before"/> to this reading.</summary>
    public GcDelta Since(in GcSnapshot before) => new(
        Math.Max(0, PauseTicks - before.PauseTicks), Math.Max(0, Gen0 - before.Gen0), Math.Max(0, Gen1 - before.Gen1), Math.Max(0, Gen2 - before.Gen2));
}

/// <summary>
/// GC activity over an interval. <see cref="Gen0"/> counts every collection (the runtime's <c>CollectionCount(0)</c> includes the
/// collections of the older generations), <see cref="Gen1"/> those of generation 1 or 2, <see cref="Gen2"/> the gen2 ones: one blocking
/// gen2 collection shows as +1 in all three.
/// </summary>
public readonly record struct GcDelta(long PauseTicks, int Gen0, int Gen1, int Gen2)
{
    /// <summary>The pause time in milliseconds.</summary>
    public double PauseMs => PauseTicks / (double)TimeSpan.TicksPerMillisecond;

    /// <summary>"gc 2345.1 ms (gen0 +1, gen1 +1, gen2 +1)".</summary>
    public string Format() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"gc {PauseMs:F1} ms (gen0 +{Gen0}, gen1 +{Gen1}, gen2 +{Gen2})");
}

/// <summary>Where the world thread is inside a tick (<see cref="WorldRuntime.CurrentPosition"/>).</summary>
public enum TickPhase
{
    /// <summary>Between ticks (waiting for the next one), or the world thread is not running.</summary>
    Idle = 0,
    Commands,
    WorldTick,
    Maps,
    Features,
    Autosave,
}

/// <summary>
/// The world thread's position, read from any thread: the phase, the handler or command running in it (a delegate, a
/// <see cref="Map"/>, or null) and the <see cref="Stopwatch.GetTimestamp"/> at which that work began.
/// The three values are read separately, so a read taken as the thread moves on may pair a phase with the next item.
/// </summary>
public readonly record struct TickPosition(TickPhase Phase, object? Work, long SinceTimestamp)
{
    /// <summary>The feature, command source or map the position names ("SyntheticSlowFeature", "map 0 instance 0").</summary>
    public string DescribeWork() => Work switch
    {
        null => "(nothing)",
        Map map => $"map {map.MapId} instance {map.InstanceId}",
        Delegate handler => TickWorkNames.Of(handler),
        _ => Work.GetType().Name,
    };
}

/// <summary>Stable, readable names for the delegates the world loop runs.</summary>
public static class TickWorkNames
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, string> Cache = new();

    /// <summary>The class a handler belongs to; a lambda's compiler-generated closure type is replaced by the class that declares it.</summary>
    public static string Of(Delegate handler)
    {
        Type? type = handler.Method.DeclaringType;
        return type is null ? handler.Method.Name : Cache.GetOrAdd(type, static t =>
        {
            Type current = t;
            while (current.DeclaringType is { } outer && current.Name.Contains('<', StringComparison.Ordinal))
            {
                current = outer;
            }

            return current.Name;
        });
    }
}

/// <summary>One tick that ran far over its interval, kept for the hang log (<see cref="TickStats.SlowTicks"/>).</summary>
public sealed record SlowTickInfo(long Sequence, long Timestamp, long DurationMicros, TickPhases Phases, IReadOnlyList<(string Name, long Micros)> TopFeatures);

/// <summary>
/// Fixed-size ring of recent world-tick durations and allocated bytes. One writer (the world
/// thread) records; any thread may take a snapshot, so monitoring never waits on the world
/// thread beyond a few instructions of a lock held only to copy the ring. Recording does not
/// allocate.
/// </summary>
public sealed partial class TickStats
{
    private readonly object _gate = new();
    private readonly long[] _durations;
    private readonly long[] _allocated;
    private readonly long[] _frameIntervals;
    private readonly long[] _commands;
    private readonly long[] _maps;
    private readonly long[] _features;
    private readonly long[] _commandsBytes;
    private readonly long[] _mapsBytes;
    private readonly long[] _featuresBytes;
    private readonly LatencyHistogram _bodyLatency = new();
    private readonly LatencyHistogram _frameLatency = new();
    private readonly LatencyHistogram _commandsLatency = new();
    private readonly LatencyHistogram _worldTickLatency = new();
    private readonly LatencyHistogram _featuresLatency = new();
    private readonly LatencyHistogram _mapsLatency = new();
    private readonly LatencyHistogram _autosaveLatency = new();
    private readonly Dictionary<string, double> _featureMeans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _featureBytes = new(StringComparer.Ordinal);
    private const double FeatureSmoothing = 0.02;

    /// <summary>Features kept per slow tick, slowest first.</summary>
    public const int TopFeatureCount = 5;

    /// <summary>Slow ticks kept; when full, the fastest of them (or one older than <see cref="SlowTickRetention"/>) is replaced.</summary>
    public const int SlowTickCapacity = 8;

    /// <summary>How long a slow tick stays eligible for the hang log.</summary>
    public static readonly TimeSpan SlowTickRetention = TimeSpan.FromSeconds(60);

    private readonly SlowTickEntry?[] _slowTicks = new SlowTickEntry?[SlowTickCapacity];

    /// <summary>The timestamp source of the slow-tick ring (<see cref="Stopwatch.GetTimestamp"/>; tests replace it to age entries).</summary>
    internal Func<long> Clock { get; set; } = Stopwatch.GetTimestamp;

    /// <summary>Seconds from <paramref name="timestamp"/> (a <see cref="SlowTickInfo.Timestamp"/>) to now on this ring's clock.</summary>
    public double AgeSeconds(long timestamp) => Stopwatch.GetElapsedTime(timestamp, Clock()).TotalSeconds;
    private long _slowSequence;
    private long _total;
    private long _overruns;
    private long _frameOverruns;
    private long _lastTimestamp;
    private long _frameToleranceMicros;

    public TickStats(int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _durations = new long[capacity];
        _allocated = new long[capacity];
        _frameIntervals = new long[capacity];
        _commands = new long[capacity];
        _maps = new long[capacity];
        _features = new long[capacity];
        _commandsBytes = new long[capacity];
        _mapsBytes = new long[capacity];
        _featuresBytes = new long[capacity];
    }

    /// <summary>
    /// How much longer than the interval a frame may be before it counts as a frame overrun (<c>World:TickLateToleranceMs</c>;
    /// 0 by default, the world loop sets it at start). Work overruns have no tolerance.
    /// </summary>
    public long FrameOverrunToleranceMicros
    {
        get => Volatile.Read(ref _frameToleranceMicros);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Volatile.Write(ref _frameToleranceMicros, value);
        }
    }

    /// <summary>
    /// Record one completed tick. An overrun is a tick strictly longer than its interval; a frame overrun is a frame longer
    /// than the interval plus <see cref="FrameOverrunToleranceMicros"/>.
    /// <paramref name="intervalMicros"/> is the configured tick length.
    /// </summary>
    public void Record(long durationMicros, long allocatedBytes, long intervalMicros, long frameIntervalMicros = 0,
        TickPhases phases = default)
    {
        long stamp = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            int slot = (int)(_total % _durations.Length);
            _durations[slot] = durationMicros;
            _allocated[slot] = allocatedBytes;
            _frameIntervals[slot] = frameIntervalMicros;
            _commands[slot] = phases.CommandsMicros;
            _maps[slot] = phases.MapsMicros;
            _features[slot] = phases.FeaturesMicros;
            _commandsBytes[slot] = phases.CommandsBytes;
            _mapsBytes[slot] = phases.MapsBytes;
            _featuresBytes[slot] = phases.FeaturesBytes;
            _total++;
            _bodyLatency.Record(durationMicros);
            if (frameIntervalMicros > 0)
            {
                _frameLatency.Record(frameIntervalMicros);
            }

            _commandsLatency.Record(phases.CommandsMicros);
            _worldTickLatency.Record(phases.WorldTickMicros);
            _featuresLatency.Record(Math.Max(0, phases.FeaturesMicros - phases.WorldTickMicros));
            _mapsLatency.Record(phases.MapsMicros);
            if (phases.AutosaveMicros > 0 || phases.AutosaveSnapshots > 0)
            {
                _autosaveLatency.Record(phases.AutosaveMicros);
            }

            if (durationMicros > intervalMicros)
            {
                _overruns++;
            }
            if (frameIntervalMicros > intervalMicros + _frameToleranceMicros)
            {
                _frameOverruns++;
            }

            _lastTimestamp = stamp;
        }
    }

    /// <summary>The tick-body duration histogram (microseconds; lock-free, any thread).</summary>
    public LatencyHistogram BodyLatency => _bodyLatency;

    /// <summary>The frame-interval histogram (microseconds between two tick starts).</summary>
    public LatencyHistogram FrameLatency => _frameLatency;

    /// <summary>The per-phase histograms (microseconds) in the order commands, world_tick, features, maps, autosave: the watchdog publishes gauges and log detail from them.</summary>
    public IReadOnlyList<KeyValuePair<string, LatencyHistogram>> PhaseLatencies => [
        new("commands", _commandsLatency),
        new("world_tick", _worldTickLatency),
        new("features", _featuresLatency),
        new("maps", _mapsLatency),
        new("autosave", _autosaveLatency),
    ];

    /// <summary>One world feature's share of a tick (world thread; smoothed, so a snapshot shows its typical cost).</summary>
    public void RecordFeature(string name, long micros, long allocatedBytes = 0)
    {
        lock (_gate)
        {
            _featureMeans[name] = _featureMeans.TryGetValue(name, out double mean)
                ? mean + (FeatureSmoothing * (micros - mean))
                : micros;
            _featureBytes[name] = _featureBytes.TryGetValue(name, out double bytes)
                ? bytes + (FeatureSmoothing * (allocatedBytes - bytes))
                : allocatedBytes;
        }
    }

    /// <summary>
    /// Remember a tick whose body ran over its interval, with its phase split and its slowest features, for the watchdog's hang log
    /// (world thread; allocation-free once the ring has filled). Of the <see cref="SlowTickCapacity"/> kept, the slowest are
    /// retained: a new tick replaces an expired one, else the fastest kept one if it is slower than that.
    /// </summary>
    public void RecordSlowTick(in TickPhases phases, ReadOnlySpan<string?> featureNames, ReadOnlySpan<long> featureMicros, int featureCount, long durationMicros)
    {
        long stamp = Clock();
        lock (_gate)
        {
            int target = -1;
            for (int i = 0; i < _slowTicks.Length; i++)
            {
                if (_slowTicks[i] is null)
                {
                    target = i;
                    break;
                }
            }

            if (target < 0)
            {
                if (_slowTicks[0] is not { } oldestEntry)
                {
                    return;
                }

                SlowTickEntry fastestEntry = oldestEntry;
                int oldest = 0, fastest = 0;
                for (int i = 1; i < _slowTicks.Length; i++)
                {
                    if (_slowTicks[i] is not { } current)
                    {
                        continue;
                    }

                    if (current.Timestamp < oldestEntry.Timestamp)
                    {
                        oldest = i;
                        oldestEntry = current;
                    }

                    if (current.DurationMicros < fastestEntry.DurationMicros)
                    {
                        fastest = i;
                        fastestEntry = current;
                    }
                }

                if (Stopwatch.GetElapsedTime(oldestEntry.Timestamp, stamp) > SlowTickRetention)
                {
                    target = oldest;
                }
                else if (durationMicros > fastestEntry.DurationMicros)
                {
                    target = fastest;
                }
                else
                {
                    return;
                }
            }

            SlowTickEntry entry = _slowTicks[target] ??= new SlowTickEntry();
            entry.Sequence = ++_slowSequence;
            entry.Timestamp = stamp;
            entry.DurationMicros = durationMicros;
            entry.Phases = phases;
            entry.Count = Math.Min(Math.Min(featureCount, TopFeatureCount), Math.Min(featureNames.Length, featureMicros.Length));
            for (int i = 0; i < entry.Count; i++)
            {
                entry.Names[i] = featureNames[i];
                entry.Micros[i] = featureMicros[i];
            }
        }
    }

    /// <summary>The slow ticks recorded within <paramref name="within"/>, slowest first (any thread).</summary>
    public IReadOnlyList<SlowTickInfo> SlowTicks(TimeSpan within)
    {
        long now = Clock();
        var result = new List<SlowTickInfo>();
        lock (_gate)
        {
            foreach (SlowTickEntry? entry in _slowTicks)
            {
                if (entry is null || Stopwatch.GetElapsedTime(entry.Timestamp, now) > within)
                {
                    continue;
                }

                var top = new (string Name, long Micros)[entry.Count];
                for (int i = 0; i < entry.Count; i++)
                {
                    top[i] = (entry.Names[i] ?? "?", entry.Micros[i]);
                }

                result.Add(new SlowTickInfo(entry.Sequence, entry.Timestamp, entry.DurationMicros, entry.Phases, top));
            }
        }

        result.Sort(static (a, b) => b.DurationMicros.CompareTo(a.DurationMicros));
        return result;
    }

    private sealed class SlowTickEntry
    {
        public long Sequence;
        public long Timestamp;
        public long DurationMicros;
        public TickPhases Phases;
        public int Count;
        public readonly string?[] Names = new string?[TopFeatureCount];
        public readonly long[] Micros = new long[TopFeatureCount];
    }

    /// <summary>Copy the ring and compute nearest-rank percentiles.</summary>
    public TickStatsSnapshot Snapshot()
    {
        long[] durations;
        long[] frames;
        long total, overruns, frameOverruns, stamp;
        double meanAlloc = 0;
        long maxAlloc = 0;
        double meanFrame = 0;
        int frameSamples = 0;
        TickPhaseSummary commands, maps, features;
        (string, double)[] featureMeans;
        (string, double)[] featureAllocations;
        double commandsBytes, mapsBytes, featuresBytes;
        lock (_gate)
        {
            int count = (int)Math.Min(_total, _durations.Length);
            commands = Summarize(_commands, count);
            maps = Summarize(_maps, count);
            features = Summarize(_features, count);
            featureMeans = _featureMeans.Select(entry => (entry.Key, entry.Value)).OrderByDescending(entry => entry.Value).ToArray();
            featureAllocations = _featureBytes.Select(entry => (entry.Key, entry.Value)).OrderByDescending(entry => entry.Value).ToArray();
            commandsBytes = Mean(_commandsBytes, count);
            mapsBytes = Mean(_mapsBytes, count);
            featuresBytes = Mean(_featuresBytes, count);
            durations = new long[count];
            Array.Copy(_durations, durations, count);
            long allocSum = 0;
            for (int i = 0; i < count; i++)
            {
                allocSum += _allocated[i];
                maxAlloc = Math.Max(maxAlloc, _allocated[i]);
            }

            meanAlloc = count == 0 ? 0 : (double)allocSum / count;
            total = _total;
            overruns = _overruns;
            frameOverruns = _frameOverruns;
            stamp = _lastTimestamp;
            long frameSum = 0;
            for (int i = 0; i < count; i++)
            {
                if (_frameIntervals[i] > 0)
                {
                    frameSum += _frameIntervals[i];
                    frameSamples++;
                }
            }
            meanFrame = frameSamples == 0 ? 0 : (double)frameSum / frameSamples;
            frames = new long[frameSamples];
            for (int i = 0, j = 0; i < count; i++)
            {
                if (_frameIntervals[i] > 0)
                {
                    frames[j++] = _frameIntervals[i];
                }
            }
        }

        if (durations.Length == 0)
        {
            return new TickStatsSnapshot(total, 0, overruns, 0, 0, 0, 0, 0, 0, 0, 0, stamp)
            {
                BodyLatency = _bodyLatency.Windows(),
                FrameLatency = _frameLatency.Windows(),
                PhaseLatency = PhaseSnapshots(),
                FrameOverruns = frameOverruns,
                FeatureMeans = featureMeans,
                FeatureAllocations = featureAllocations,
            };
        }

        Array.Sort(durations);
        Array.Sort(frames);
        return new TickStatsSnapshot(
            total,
            durations.Length,
            overruns,
            NearestRank(durations, 50),
            NearestRank(durations, 90),
            NearestRank(durations, 99),
            NearestRank(durations, 99.9),
            durations[^1],
            durations.Average(),
            meanAlloc,
            maxAlloc,
            stamp)
        {
            FrameSamples = frameSamples,
            MeanFrameIntervalMicros = meanFrame,
            EffectiveTicksPerSecond = meanFrame > 0 ? 1_000_000d / meanFrame : null,
            FrameOverruns = frameOverruns,
            FrameP50Micros = frames.Length > 0 ? NearestRank(frames, 50) : 0,
            FrameP90Micros = frames.Length > 0 ? NearestRank(frames, 90) : 0,
            FrameP99Micros = frames.Length > 0 ? NearestRank(frames, 99) : 0,
            P95Micros = NearestRank(durations, 95),
            BodyLatency = _bodyLatency.Windows(),
            FrameLatency = _frameLatency.Windows(),
            PhaseLatency = PhaseSnapshots(),
            Commands = commands,
            Maps = maps,
            Features = features,
            FeatureMeans = featureMeans,
            FeatureAllocations = featureAllocations,
            CommandsMeanBytes = commandsBytes,
            MapsMeanBytes = mapsBytes,
            FeaturesMeanBytes = featuresBytes,
        };
    }

    private (string Name, LatencySnapshot Last1Min)[] PhaseSnapshots()
    {
        TimeSpan minute = TimeSpan.FromMinutes(1);
        return
        [
            ("commands", _commandsLatency.Snapshot(minute)),
            ("world_tick", _worldTickLatency.Snapshot(minute)),
            ("features", _featuresLatency.Snapshot(minute)),
            ("maps", _mapsLatency.Snapshot(minute)),
            ("autosave", _autosaveLatency.Snapshot(minute)),
        ];
    }

    private static double Mean(long[] values, int count)
    {
        if (count == 0) return 0;
        long sum = 0;
        for (int i = 0; i < count; i++) sum += values[i];
        return (double)sum / count;
    }

    private static TickPhaseSummary Summarize(long[] values, int count)
    {
        if (count == 0) return default;
        long sum = 0, max = 0;
        for (int i = 0; i < count; i++)
        {
            sum += values[i];
            max = Math.Max(max, values[i]);
        }

        return new TickPhaseSummary((double)sum / count, max);
    }

    /// <summary>
    /// Nearest-rank percentile of ascending-sorted values: the value at 1-based rank
    /// ceil(p/100 * n). Empty input throws rather than returning a reassuring 0.
    /// </summary>
    public static long NearestRank(ReadOnlySpan<long> sortedAscending, double percentile)
    {
        if (sortedAscending.IsEmpty)
        {
            throw new ArgumentException("percentile of an empty sample is undefined", nameof(sortedAscending));
        }

        int rank = (int)Math.Ceiling(percentile / 100.0 * sortedAscending.Length - 1e-9);
        return sortedAscending[Math.Clamp(rank, 1, sortedAscending.Length) - 1];
    }
}

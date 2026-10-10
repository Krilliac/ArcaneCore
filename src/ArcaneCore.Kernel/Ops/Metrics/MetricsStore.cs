using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace ArcaneCore.Kernel.Ops.Metrics;

/// <summary>How a series is aggregated and exported.</summary>
public enum MetricKind
{
    /// <summary>A monotonic cumulative sum (Prometheus counter, OTLP monotonic sum).</summary>
    Counter,

    /// <summary>A non-monotonic cumulative sum (Prometheus gauge, OTLP non-monotonic sum).</summary>
    UpDownCounter,

    /// <summary>The last observed value.</summary>
    Gauge,

    /// <summary>Explicit-bucket histogram, cumulative.</summary>
    Histogram,
}

/// <summary>One exported series.</summary>
public sealed record SeriesSnapshot(IReadOnlyList<KeyValuePair<string, string>> Tags, double Value, long Count, double Sum, long[] BucketCounts);

/// <summary>One exported instrument and its series.</summary>
public sealed record MetricSnapshot(string MeterName, string Name, string? Unit, string? Description, MetricKind Kind, IReadOnlyList<double> Bounds, IReadOnlyList<SeriesSnapshot> Series);

/// <summary>
/// The in-process aggregation behind both exporters: a <see cref="MeterListener"/> on every meter named
/// <see cref="ArcaneMeters.Prefix"/>*, cumulative per series (TrinityCore <c>Metric</c> queues raw values for a
/// push; a pull exporter needs the running totals, so they are kept here). Recording is lock-free for the store and
/// takes one short per-series lock; observable instruments are read only in <see cref="Collect"/>, on the caller's
/// thread. Series of an observable instrument that were not reported by the last collection (an unloaded map) are
/// dropped.
/// </summary>
public sealed class MetricsStore : IDisposable
{
    /// <summary>The histogram bucket bounds (milliseconds for the duration instruments): 0.5 ms to 5 s.</summary>
    public static readonly double[] DefaultBounds = [0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000];

    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<Instrument, InstrumentState> _instruments = new();
    private readonly Lock _collectGate = new();
    private long _generation;
    private bool _started;

    /// <summary>When the store started (OTLP start time).</summary>
    public DateTimeOffset StartTime { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Attach the listener (idempotent).</summary>
    public void Start()
    {
        lock (_collectGate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            StartTime = DateTimeOffset.UtcNow;
        }

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (!instrument.Meter.Name.StartsWith(ArcaneMeters.Prefix, StringComparison.Ordinal) || KindOf(instrument) is not { } kind)
            {
                return;
            }

            InstrumentState state = _instruments.GetOrAdd(instrument, i => new InstrumentState(i, kind, this));
            listener.EnableMeasurementEvents(instrument, state);
        };
        _listener.MeasurementsCompleted = (instrument, _) => _instruments.TryRemove(instrument, out InstrumentState? _);
        _listener.SetMeasurementEventCallback<long>((_, value, tags, state) => ((InstrumentState)state!).Record(value, tags));
        _listener.SetMeasurementEventCallback<int>((_, value, tags, state) => ((InstrumentState)state!).Record(value, tags));
        _listener.SetMeasurementEventCallback<double>((_, value, tags, state) => ((InstrumentState)state!).Record(value, tags));
        _listener.SetMeasurementEventCallback<float>((_, value, tags, state) => ((InstrumentState)state!).Record(value, tags));
        _listener.Start();
    }

    /// <summary>Read the observable instruments and return every instrument's series, ordered by name.</summary>
    public IReadOnlyList<MetricSnapshot> Collect()
    {
        lock (_collectGate)
        {
            long generation = Interlocked.Increment(ref _generation);
            _listener.RecordObservableInstruments();
            var result = new List<MetricSnapshot>(_instruments.Count);
            foreach (InstrumentState state in _instruments.Values)
            {
                if (state.Snapshot(generation) is { Series.Count: > 0 } snapshot)
                {
                    result.Add(snapshot);
                }
            }

            result.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            return result;
        }
    }

    public void Dispose() => _listener.Dispose();

    internal long Generation => Volatile.Read(ref _generation);

    private static MetricKind? KindOf(Instrument instrument)
    {
        Type type = instrument.GetType();
        if (!type.IsGenericType)
        {
            return null;
        }

        Type definition = type.GetGenericTypeDefinition();
        if (definition == typeof(Counter<>) || definition == typeof(ObservableCounter<>))
        {
            return MetricKind.Counter;
        }

        if (definition == typeof(UpDownCounter<>) || definition == typeof(ObservableUpDownCounter<>))
        {
            return MetricKind.UpDownCounter;
        }

        if (definition == typeof(ObservableGauge<>) || definition == typeof(Gauge<>))
        {
            return MetricKind.Gauge;
        }

        return definition == typeof(Histogram<>) ? MetricKind.Histogram : null;
    }

    private sealed class InstrumentState(Instrument instrument, MetricKind kind, MetricsStore store)
    {
        private readonly Series _untagged = new([], kind == MetricKind.Histogram);
        private readonly ConcurrentDictionary<string, Series> _tagged = new(StringComparer.Ordinal);

        public void Record(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            Series series = tags.Length == 0 ? _untagged : Find(tags);
            lock (series)
            {
                switch (kind)
                {
                    case MetricKind.Histogram:
                        int bucket = Array.BinarySearch(DefaultBounds, value);
                        bucket = bucket >= 0 ? bucket : ~bucket; // upper-inclusive bounds (le)
                        series.Buckets![bucket]++;
                        series.Count++;
                        series.Sum += value;
                        break;
                    case MetricKind.Gauge:
                        series.Value = value;
                        break;
                    default:
                        series.Value = instrument.IsObservable ? value : series.Value + value;
                        break;
                }

                series.Seen = true;
                series.Generation = store.Generation;
            }
        }

        public MetricSnapshot? Snapshot(long generation)
        {
            var list = new List<SeriesSnapshot>();
            Add(list, _untagged, generation, removable: false);
            foreach (KeyValuePair<string, Series> pair in _tagged)
            {
                if (!Add(list, pair.Value, generation, removable: true))
                {
                    _tagged.TryRemove(pair.Key, out _);
                }
            }

            return new MetricSnapshot(instrument.Meter.Name, instrument.Name, instrument.Unit, instrument.Description, kind, DefaultBounds, list);
        }

        // False: a stale series of an observable instrument (not reported by this collection), to be removed.
        private bool Add(List<SeriesSnapshot> list, Series series, long generation, bool removable)
        {
            lock (series)
            {
                if (!series.Seen)
                {
                    return true;
                }

                if (instrument.IsObservable && series.Generation != generation)
                {
                    if (!removable)
                    {
                        series.Seen = false;
                    }

                    return false;
                }

                list.Add(new SeriesSnapshot(series.Tags, series.Value, series.Count, series.Sum, series.Buckets is null ? [] : (long[])series.Buckets.Clone()));
                return true;
            }
        }

        private Series Find(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var key = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                key.Append(tag.Key).Append('\u001f').Append(Text(tag.Value)).Append('\u001e');
            }

            string text = key.ToString();
            if (_tagged.TryGetValue(text, out Series? found))
            {
                return found;
            }

            var pairs = new KeyValuePair<string, string>[tags.Length];
            for (int i = 0; i < tags.Length; i++)
            {
                pairs[i] = new KeyValuePair<string, string>(tags[i].Key, Text(tags[i].Value));
            }

            return _tagged.GetOrAdd(text, _ => new Series(pairs, kind == MetricKind.Histogram));
        }

        private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private sealed class Series(KeyValuePair<string, string>[] tags, bool histogram)
    {
        public KeyValuePair<string, string>[] Tags { get; } = tags;

        public long[]? Buckets { get; } = histogram ? new long[DefaultBounds.Length + 1] : null;

        public double Value { get; set; }

        public long Count { get; set; }

        public double Sum { get; set; }

        public bool Seen { get; set; }

        public long Generation { get; set; }
    }
}

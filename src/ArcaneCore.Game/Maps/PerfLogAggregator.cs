using ArcaneCore.Kernel.Diagnostics;
using System.Diagnostics;
using System.Text;
using ArcaneCore.Kernel.Ops.Watchdog;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// Turns the slow-update warnings into one summary line per interval (<see cref="PerformanceLogOptions.SummaryIntervalSeconds"/>).
/// Every slow world frame and slow map update is recorded here instead of being logged: the sample goes into a fixed-bucket histogram
/// (the world's, and one per map instance) and its contributors are summed per map, with no allocation once the map has been seen.
/// Only an update over the detail threshold still asks for its own line, and at most
/// <see cref="PerformanceLogOptions.DetailLinesPerMapPerInterval"/> of those per map instance (and for the world) per interval, through
/// one <see cref="LogRateLimiter"/> per allowed line; the rest are counted into the summary.
/// Owned and used by the world thread only; not thread-safe. The summary itself allocates (it is one line per interval).
/// </summary>
internal sealed class PerfLogAggregator
{
    private const int MaxContributorSlots = 64;
    private const int MaxMapsListed = 20;
    private const int MaxDetailLines = 16;
    private const int TopContributorCount = 3;
    private const long VerdictCacheMicros = 1_000_000;

    private readonly PerformanceLogOptions _options;
    private readonly ILogger _logger;
    private readonly Func<long> _nowMicros;
    private readonly Func<long, HostLoadVerdict>? _verdictProbe;
    private readonly Func<bool>? _onOwnerThread;
    private int _ownerThreadId = -1;
    private readonly long _intervalMicros;
    private readonly int _detailCap;
    private readonly Dictionary<long, MapSlot> _maps = [];
    private readonly Stack<MapSlot> _pool = new();
    private readonly List<MapSlot> _active = [];
    private readonly Dictionary<Type, string> _labels = [];
    private readonly LatencyHistogram _world = new();
    private readonly LogRateLimiter[] _worldDetail;

    private long _nextFlush;
    private long _windowStart;
    private long _frames;
    private long _worldSuppressed;
    private long _verdictAt = long.MinValue;
    private HostLoadVerdict _verdict;
    private long _starved;
    private long _quiet;
    private long _gc;
    private long _other;
    private long _busy;
    private long _stalled;
    private long _memoryStarved;
    private long _blocked;

    /// <param name="options">The thresholds and the interval; read once (restart to change).</param>
    /// <param name="logger">Receives the summary line.</param>
    /// <param name="nowMicros">The clock the interval runs on, in microseconds (a manual one in tests).</param>
    /// <param name="verdictProbe">
    /// The host verdict for a slow window of the given length (the host-load sampler's); null: the verdicts count as unknown. It does a
    /// fresh host probe, so a result is reused for one second instead of being asked per sample.
    /// </param>
    /// <param name="onOwnerThread">
    /// True on the thread that owns the aggregator (the world runtime passes its world-thread test). Checked with <see cref="Debug.Assert(bool)"/> in
    /// every record and flush method; null: the first thread to call is the owner.
    /// </param>
    public PerfLogAggregator(PerformanceLogOptions options, ILogger logger, Func<long> nowMicros, Func<long, HostLoadVerdict>? verdictProbe = null, Func<bool>? onOwnerThread = null)
    {
        _onOwnerThread = onOwnerThread;
        _options = options;
        _logger = logger;
        _nowMicros = nowMicros;
        _verdictProbe = verdictProbe;
        _intervalMicros = Math.Max(0, options.SummaryIntervalSeconds) * 1_000_000L;
        _detailCap = Math.Clamp(options.DetailLinesPerMapPerInterval, 0, MaxDetailLines);
        _worldDetail = NewLimiters(_intervalMicros, _detailCap);
        _windowStart = nowMicros();
        _nextFlush = _intervalMicros > 0 ? _windowStart + _intervalMicros : long.MaxValue;
    }

    /// <summary>True when the summary replaces the per-event lines.</summary>
    public bool Enabled => _intervalMicros > 0;

    [Conditional("DEBUG")]
    private void AssertOwner()
    {
        if (_onOwnerThread is not null)
        {
            Debug.Assert(_onOwnerThread(), "PerfLogAggregator is not thread-safe: use it on the world thread only");
            return;
        }

        int current = Environment.CurrentManagedThreadId;
        if (_ownerThreadId == -1)
        {
            _ownerThreadId = current;
        }

        Debug.Assert(_ownerThreadId == current, "PerfLogAggregator is not thread-safe: use it on one thread only");
    }

    private static LogRateLimiter[] NewLimiters(long intervalMicros, int count)
    {
        var limiters = new LogRateLimiter[count];
        for (int i = 0; i < count; i++)
        {
            limiters[i] = new LogRateLimiter(intervalMicros);
        }

        return limiters;
    }

    /// <summary>One world frame passed (slow or not): the M of "N of M".</summary>
    public void CountFrame()
    {
        AssertOwner();
        _frames++;
    }

    /// <summary>
    /// Record a slow world frame of <paramref name="micros"/>. True: write its own line (over the detail threshold and within the
    /// per-interval cap); false: it is in the summary only.
    /// </summary>
    public bool RecordWorldFrame(long micros)
    {
        AssertOwner();
        long now = _nowMicros();
        _world.Add(micros);
        NoteVerdict(now, micros, default);
        if (!Eligible(micros, _options.SlowWorldUpdateDetail))
        {
            return false;
        }

        if (Admit(_worldDetail, now))
        {
            return true;
        }

        _worldSuppressed++;
        return false;
    }

    /// <summary>
    /// Record a slow update of one map instance: <paramref name="ownMicros"/> is its own work (gate waits excluded),
    /// <paramref name="diagnostics"/> the phase times when they were taken, <paramref name="gc"/> the collector's share of the update.
    /// True: write its own line; false: it is in the summary only.
    /// </summary>
    public bool RecordMapUpdate(uint mapId, uint instanceId, long ownMicros, MapUpdateDiagnostics? diagnostics, in GcDelta gc)
    {
        AssertOwner();
        long now = _nowMicros();
        MapSlot slot = SlotFor(mapId, instanceId);
        slot.LastActive = now;
        slot.Histogram.Add(ownMicros);
        slot.SumMicros += ownMicros;
        if (diagnostics is { Completed: true })
        {
            AddContributors(slot, diagnostics);
        }

        NoteVerdict(now, ownMicros, gc);
        if (!Eligible(ownMicros, _options.SlowMapUpdateDetail))
        {
            return false;
        }

        if (Admit(slot.Detail, now))
        {
            return true;
        }

        slot.Suppressed++;
        return false;
    }

    private static bool Eligible(long micros, int detailThresholdMs) => detailThresholdMs > 0 && micros > detailThresholdMs * 1000L;

    private static bool Admit(LogRateLimiter[] limiters, long now)
    {
        for (int i = 0; i < limiters.Length; i++)
        {
            if (limiters[i].TryAcquire(now, out _))
            {
                return true;
            }
        }

        return false;
    }

    private MapSlot SlotFor(uint mapId, uint instanceId)
    {
        long key = ((long)mapId << 32) | instanceId;
        if (_maps.TryGetValue(key, out MapSlot? slot))
        {
            return slot;
        }

        if (_pool.TryPop(out slot))
        {
            slot.Detail = slot.Detail.Length == _detailCap ? slot.Detail : NewLimiters(_intervalMicros, _detailCap);
            for (int i = 0; i < slot.Detail.Length; i++)
            {
                slot.Detail[i].Reset();
            }
        }
        else
        {
            slot = new MapSlot { Detail = NewLimiters(_intervalMicros, _detailCap) };
        }

        slot.MapId = mapId;
        slot.InstanceId = instanceId;
        _maps.Add(key, slot);
        return slot;
    }

    private void AddContributors(MapSlot slot, MapUpdateDiagnostics d)
    {
        long playerBeats = d.HeartbeatPlayersMicros;
        slot.Add(MapUpdateDiagnostics.NamePackets, d.PacketsMicros);
        slot.Add(MapUpdateDiagnostics.NamePlayerHeartbeats, playerBeats);
        slot.Add(MapUpdateDiagnostics.NameUnitHeartbeats, Math.Max(0, d.HeartbeatMicros - playerBeats));
        slot.Add(MapUpdateDiagnostics.NameVisibility, d.VisibilityMicros);
        slot.Add(MapUpdateDiagnostics.NameValues, d.ValuesMicros);
        slot.Add(MapUpdateDiagnostics.NameFlush, d.FlushMicros);
        slot.Add(MapUpdateDiagnostics.NameGrid, d.GridMicros);
        slot.Add(MapUpdateDiagnostics.NameDeferred, d.DeferredMicros);
        List<(IMapUpdater Updater, long Micros)> updaters = d.Updaters;
        for (int i = 0; i < updaters.Count; i++)
        {
            (IMapUpdater updater, long micros) = updaters[i];
            Type type = updater.GetType();
            if (!_labels.TryGetValue(type, out string? label))
            {
                label = MapUpdateDiagnostics.SubsystemOf(updater);
                _labels[type] = label;
            }

            slot.Add(label, micros);
        }
    }

    private void NoteVerdict(long now, long windowMicros, in GcDelta gc)
    {
        // The collector's share is known for free from the update's own counters.
        if (windowMicros > 0 && gc.PauseTicks / 10 >= windowMicros * HostLoadSampler.GcShareThreshold)
        {
            _gc++;
            return;
        }

        if (_verdictProbe is null)
        {
            _other++;
            return;
        }

        if (_verdictAt == long.MinValue || now - _verdictAt >= VerdictCacheMicros)
        {
            _verdictAt = now;
            try
            {
                _verdict = _verdictProbe(windowMicros);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("world.perf-verdict-probe", ex);
                _verdict = HostLoadVerdict.Unknown;
            }
        }

        switch (_verdict)
        {
            case HostLoadVerdict.HostStarved:
                _starved++;
                break;
            case HostLoadVerdict.Quiet:
                _quiet++;
                break;
            case HostLoadVerdict.GcPauses:
            case HostLoadVerdict.GcPaging:
                _gc++;
                break;
            case HostLoadVerdict.ProcessBusy:
                _busy++;
                break;
            case HostLoadVerdict.ProcessStalled:
                _stalled++;
                break;
            case HostLoadVerdict.MemoryStarved:
                _memoryStarved++;
                break;
            case HostLoadVerdict.ProcessBlocked:
                _blocked++;
                break;
            default:
                _other++;
                break;
        }
    }

    /// <summary>Write the summary when the interval has elapsed (and something was slow). Returns true when the interval ended.</summary>
    public bool FlushIfDue()
    {
        AssertOwner();
        if (_intervalMicros <= 0)
        {
            return false;
        }

        long now = _nowMicros();
        if (now < _nextFlush)
        {
            return false;
        }

        WriteSummary(now);
        return true;
    }

    /// <summary>End the interval now (the world thread is stopping): writes the summary of what has been recorded since the last one.</summary>
    public void FlushNow()
    {
        AssertOwner();
        if (_intervalMicros > 0)
        {
            WriteSummary(_nowMicros());
        }
    }

    private void WriteSummary(long now)
    {
        long elapsed = Math.Max(0, now - _windowStart);
        _windowStart = now;
        if (now >= _nextFlush)
        {
            // Anchored to the schedule, not to when the check happened (no drift); a hitch that skipped whole intervals skips their boundaries.
            _nextFlush += _intervalMicros;
            if (_nextFlush <= now)
            {
                _nextFlush += ((now - _nextFlush) / _intervalMicros + 1) * _intervalMicros;
            }
        }
        else
        {
            _nextFlush = now + _intervalMicros; // ended early (the world is stopping): a fresh window
        }

        long suppressed = _worldSuppressed;
        _active.Clear();
        foreach (MapSlot slot in _maps.Values)
        {
            if (slot.Histogram.Count > 0)
            {
                _active.Add(slot);
                suppressed += slot.Suppressed;
            }
        }

        if (_world.Count > 0 || _active.Count > 0)
        {
            _logger.LogWarning(PerformanceLogOptions.PerfEventId, "Slow update summary: {Summary}", Format(suppressed, elapsed));
        }

        ResetInterval(now);
    }

    private string Format(long suppressed, long elapsedMicros)
    {
        var sb = new StringBuilder(512);
        sb.Append("last ").Append((elapsedMicros + 500_000) / 1_000_000).Append(" s");
        if (_world.Count > 0)
        {
            sb.Append("; world frames over ").Append(_options.SlowWorldUpdate).Append(" ms: ").Append(_world.Count).Append(" of ").Append(Math.Max(_frames, _world.Count));
            AppendDistribution(sb, _world);
        }

        if (_active.Count > 0)
        {
            _active.Sort(static (a, b) => b.SumMicros != a.SumMicros ? b.SumMicros.CompareTo(a.SumMicros) : a.Key.CompareTo(b.Key));
            long updates = 0;
            foreach (MapSlot slot in _active)
            {
                updates += slot.Histogram.Count;
            }

            sb.Append("; map updates over ").Append(_options.SlowMapUpdate).Append(" ms: ").Append(updates).Append(" in ").Append(_active.Count).Append(_active.Count == 1 ? " map" : " maps");
            for (int i = 0; i < _active.Count && i < MaxMapsListed; i++)
            {
                MapSlot slot = _active[i];
                sb.Append(i == 0 ? ": " : " | ").Append("map ").Append(slot.MapId).Append(" instance ").Append(slot.InstanceId).Append(": ").Append(slot.Histogram.Count);
                AppendDistribution(sb, slot.Histogram, slot);
            }

            if (_active.Count > MaxMapsListed)
            {
                sb.Append(" | and ").Append(_active.Count - MaxMapsListed).Append(" more");
            }
        }

        sb.Append("; verdicts: host-starved ").Append(_starved).Append(", quiet ").Append(_quiet).Append(", gc ").Append(_gc);
        if (_busy > 0)
        {
            sb.Append(", process-busy ").Append(_busy);
        }

        if (_stalled > 0)
        {
            sb.Append(", process-stalled ").Append(_stalled);
        }

        if (_memoryStarved > 0)
        {
            sb.Append(", memory-starved ").Append(_memoryStarved);
        }

        if (_blocked > 0)
        {
            sb.Append(", process-blocked ").Append(_blocked);
        }

        if (_other > 0)
        {
            sb.Append(", unknown (no host sampler or no verdict) ").Append(_other);
        }

        sb.Append("; detail lines suppressed: ").Append(suppressed);
        return sb.ToString();
    }

    private static void AppendDistribution(StringBuilder sb, LatencyHistogram histogram, MapSlot? slot = null)
    {
        sb.Append(" (p50 ").Append(histogram.PercentileMs(0.50)).Append(" ms, p95 ").Append(histogram.PercentileMs(0.95)).Append(" ms, max ").Append(histogram.MaxMicros / 1000).Append(" ms");
        if (slot is not null)
        {
            slot.AppendTop(sb, TopContributorCount);
        }

        sb.Append(')');
    }

    private void ResetInterval(long now)
    {
        _world.Reset();
        _frames = 0;
        _worldSuppressed = 0;
        _starved = _quiet = _gc = _other = _busy = _stalled = _memoryStarved = _blocked = 0;
        for (int i = 0; i < _worldDetail.Length; i++)
        {
            _worldDetail[i].Reset(); // the cap is per interval, not per rolling window
        }

        _verdictAt = long.MinValue;

        // A map idle for two intervals goes back to the pool (its limiters would have admitted again anyway); the others start afresh.
        List<MapSlot> idle = _active;
        idle.Clear();
        foreach (MapSlot slot in _maps.Values)
        {
            slot.Reset();
            for (int i = 0; i < slot.Detail.Length; i++)
            {
                slot.Detail[i].Reset();
            }

            if (now - slot.LastActive >= 2 * _intervalMicros)
            {
                idle.Add(slot);
            }
        }

        foreach (MapSlot slot in idle)
        {
            _maps.Remove(slot.Key);
            _pool.Push(slot);
        }

        idle.Clear();
    }

    private sealed class MapSlot
    {
        private readonly string?[] _names = new string?[MaxContributorSlots];
        private readonly long[] _micros = new long[MaxContributorSlots];
        private int _count;

        public uint MapId;
        public uint InstanceId;
        public LatencyHistogram Histogram { get; } = new();
        public LogRateLimiter[] Detail = [];
        public long SumMicros;
        public long Suppressed;
        public long LastActive;
        public long UnattributedMicros;

        public long Key => ((long)MapId << 32) | InstanceId;

        /// <summary>Sum <paramref name="micros"/> into the contributor called <paramref name="name"/> (no allocation; a full table drops new names).</summary>
        public void Add(string name, long micros)
        {
            if (micros <= 0)
            {
                return;
            }

            for (int i = 0; i < _count; i++)
            {
                if (string.Equals(_names[i], name))
                {
                    _micros[i] += micros;
                    return;
                }
            }

            if (_count < MaxContributorSlots)
            {
                _names[_count] = name;
                _micros[_count++] = micros;
            }
            else
            {
                UnattributedMicros += micros; // a full table: counted, not lost without trace
            }
        }

        public void AppendTop(StringBuilder sb, int top)
        {
            if (UnattributedMicros > 0)
            {
                sb.Append("; (+").Append(UnattributedMicros / 1000).Append(" ms unattributed)");
            }

            if (_count == 0)
            {
                return;
            }

            sb.Append("; top ");
            long previousMicros = long.MaxValue;
            string? previousName = null;
            for (int rank = 0; rank < top; rank++)
            {
                int best = -1;
                for (int i = 0; i < _count; i++)
                {
                    bool below = _micros[i] < previousMicros
                        || (_micros[i] == previousMicros && string.CompareOrdinal(_names[i], previousName) > 0);
                    if (!below)
                    {
                        continue;
                    }

                    if (best < 0 || _micros[i] > _micros[best]
                        || (_micros[i] == _micros[best] && string.CompareOrdinal(_names[i], _names[best]) < 0))
                    {
                        best = i;
                    }
                }

                if (best < 0)
                {
                    break;
                }

                sb.Append(rank > 0 ? ", " : string.Empty).Append(_names[best]).Append(' ').Append(_micros[best] / 1000).Append(" ms");
                previousMicros = _micros[best];
                previousName = _names[best];
            }
        }

        /// <summary>Start the next interval: the samples and sums go, the limiters stay (they run on their own clock).</summary>
        public void Reset()
        {
            Histogram.Reset();
            SumMicros = 0;
            Suppressed = 0;
            UnattributedMicros = 0;
            Array.Clear(_micros, 0, _count);
            Array.Clear(_names, 0, _count);
            _count = 0;
        }
    }

    /// <summary>
    /// Fixed-bucket histogram of durations: 1 ms buckets up to 1024 ms (exact), then 64 buckets per doubling up to 2^21 ms (about 1.5 %
    /// wide), 1728 counters in all, allocated once. <see cref="PercentileMs"/> is the nearest-rank value at the bucket's lower edge.
    /// </summary>
    internal sealed class LatencyHistogram
    {
        private const int Linear = 1024;
        private const int SubBits = 6;
        private const int Sub = 1 << SubBits;
        private const int FirstExponent = 10;
        private const int Octaves = 11;
        private const long MaxMs = (1L << (FirstExponent + Octaves)) - 1;
        private readonly int[] _counts = new int[Linear + Octaves * Sub];

        public long Count { get; private set; }

        public long MaxMicros { get; private set; }

        public void Add(long micros)
        {
            long ms = micros < 0 ? 0 : Math.Min(micros / 1000, MaxMs);
            _counts[IndexOf(ms)]++;
            Count++;
            if (micros > MaxMicros)
            {
                MaxMicros = micros;
            }
        }

        public long PercentileMs(double quantile)
        {
            if (Count == 0)
            {
                return 0;
            }

            long rank = Math.Max(1, (long)Math.Ceiling(quantile * Count));
            long seen = 0;
            for (int i = 0; i < _counts.Length; i++)
            {
                seen += _counts[i];
                if (seen >= rank)
                {
                    return Math.Min(LowerEdgeMs(i), MaxMicros / 1000);
                }
            }

            return MaxMicros / 1000;
        }

        public void Reset()
        {
            if (Count > 0)
            {
                Array.Clear(_counts);
            }

            Count = 0;
            MaxMicros = 0;
        }

        private static int IndexOf(long ms)
        {
            if (ms < Linear)
            {
                return (int)ms;
            }

            int exponent = 63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)ms);
            int sub = (int)((ms >> (exponent - SubBits)) & (Sub - 1));
            return Linear + (exponent - FirstExponent) * Sub + sub;
        }

        private static long LowerEdgeMs(int index)
        {
            if (index < Linear)
            {
                return index;
            }

            int k = index - Linear;
            int exponent = k / Sub + FirstExponent;
            return (long)(Sub + k % Sub) << (exponent - SubBits);
        }
    }
}

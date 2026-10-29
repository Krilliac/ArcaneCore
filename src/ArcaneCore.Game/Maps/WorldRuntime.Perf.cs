using System.Diagnostics;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// The tick position, the slowest-feature ranking and the slow-update logging (docs/ops/watchdog.md, docs/areas/ops-perf.md).
/// </summary>
public sealed partial class WorldRuntime
{
    private PerfLogAggregator? _perfLog; // world thread: the slow-update summary (null: per-event lines, see PerfLog)
    private bool _perfLogResolved;

    // The world thread's position and the slowest features of the tick in progress (docs/ops/watchdog.md). Written by the
    // world thread only; the position is read by the watchdog thread, so it is published with volatile stores under a
    // sequence count (odd while a write is in progress) so a reader never pairs one tick's phase with another's work.
    private int _positionVersion;
    private volatile int _phase;
    private volatile object? _currentWork;
    private long _currentWorkSince;
    private readonly string?[] _topNames = new string?[TickStats.TopFeatureCount];
    private readonly long[] _topMicros = new long[TickStats.TopFeatureCount];
    private int _topCount;
    private int _commandsRun;

    /// <summary>
    /// Where the world thread is now (any thread; the watchdog's hang log): the tick phase and the feature, command or map running in it.
    /// <see cref="TickPhase.Idle"/> between ticks. Off the world thread it may wait briefly while a write is in progress.
    /// </summary>
    public TickPosition CurrentPosition
    {
        get
        {
            // Retry until one read sees the same even version before and after. SetPosition never blocks, so a failed read means the
            // writer was preempted inside a write or published again meanwhile: spin, then yield so it can finish. Bounded by time,
            // not attempts (a busy writer can win many attempts in a row): after PositionReadBudgetMs, the last read (counted).
            TickPosition read;
            long yieldingSince = 0;
            for (int attempt = 0; ; attempt++)
            {
                int before = Volatile.Read(ref _positionVersion);
                read = new((TickPhase)_phase, _currentWork, Volatile.Read(ref _currentWorkSince));
                if ((before & 1) == 0 && Volatile.Read(ref _positionVersion) == before) return read;
                if (attempt < PositionSpinAttempts) { Thread.SpinWait(8); continue; }
                if (yieldingSince == 0) yieldingSince = Stopwatch.GetTimestamp();
                else if (Stopwatch.GetElapsedTime(yieldingSince).TotalMilliseconds > PositionReadBudgetMs) break;
                Thread.Yield();
            }
            Interlocked.Increment(ref _positionReadsUnverified);
            return read;
        }
    }

    private const int PositionSpinAttempts = 64;
    private const int PositionReadBudgetMs = 50;
    private long _positionReadsUnverified;

    /// <summary>Reads of <see cref="CurrentPosition"/> that ran out of time and returned an unverified (possibly mixed) position.</summary>
    internal long PositionReadsUnverified => Interlocked.Read(ref _positionReadsUnverified);

    internal void SetPosition(TickPhase phase, object? work, long since) // world thread only (the single writer); internal for tests
    {
        int version = Interlocked.Increment(ref _positionVersion); // odd: a write in progress (full fence: the stores below stay after it)
        _currentWork = work;
        Volatile.Write(ref _currentWorkSince, since);
        _phase = (int)phase;
        Volatile.Write(ref _positionVersion, version + 1);
    }

    /// <summary>Keep the slowest <see cref="TickStats.TopFeatureCount"/> features of the tick in progress (no allocation).</summary>
    private void NoteSlowest(string name, long micros)
    {
        int index;
        if (_topCount < _topNames.Length)
        {
            index = _topCount++;
        }
        else if (micros > _topMicros[^1])
        {
            index = _topNames.Length - 1;
        }
        else
        {
            return;
        }

        while (index > 0 && _topMicros[index - 1] < micros)
        {
            _topNames[index] = _topNames[index - 1];
            _topMicros[index] = _topMicros[index - 1];
            index--;
        }

        _topNames[index] = name;
        _topMicros[index] = micros;
    }

    private void LogIfSlow(int thresholdMs, long startTimestamp, string what, Map map, MapUpdateDiagnostics? diagnostics, GcSnapshot gcBefore)
    {
        if (thresholdMs <= 0)
        {
            return;
        }

        LogIfSlowMicros(thresholdMs, (Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000 / Stopwatch.Frequency, what, map, diagnostics, gcBefore);
    }

    private void LogIfSlowMicros(int thresholdMs, long micros, string what, Map map, MapUpdateDiagnostics? diagnostics, GcSnapshot gcBefore, GcSnapshot? gcAfter = null)
    {
        if (thresholdMs <= 0)
        {
            return;
        }

        if (micros > thresholdMs * 1000L)
        {
            // With a summary interval the update is counted there and only an outlier (within the per-map cap) gets its own line.
            if (PerfLog is { } perf)
            {
                GcDelta gc = diagnostics is { Completed: true } ? (gcAfter ?? GcSource()).Since(gcBefore) : default;
                if (!perf.RecordMapUpdate(map.MapId, map.InstanceId, micros, diagnostics, gc))
                {
                    return;
                }
            }

            string host = HostLoadText(micros) + ParallelText(map);
            if (diagnostics is { Completed: true } timing)
            {
                _logger.LogWarning(PerformanceLogOptions.PerfEventId,
                    "{What}: map {MapId} instance {InstanceId} took {DurationMs} ms (simulation {SimulationMs} ms, visibility {VisibilityMs} ms, values {ValuesMs} ms, flush {FlushMs} ms, cleanup {CleanupMs} ms; players {Players}, moved {MovedObjects}, changed {ChangedObjects}, new {NewObjects}, visibility candidates {VisibilityCandidates}); top: {Top}; {Breakdown}; {Gc}{Host}",
                    what, map.MapId, map.InstanceId, micros / 1000, timing.SimulationMicros / 1000,
                    timing.VisibilityMicros / 1000, timing.ValuesMicros / 1000, timing.FlushMicros / 1000,
                    timing.CleanupMicros / 1000, timing.Players, timing.MovedObjects, timing.ChangedObjects,
                    timing.NewObjects, timing.VisibilityCandidates, timing.FormatTop(3), timing.Describe(), (gcAfter ?? GcSource()).Since(gcBefore).Format(), host);
            }
            else
            {
                _logger.LogWarning(PerformanceLogOptions.PerfEventId, "{What}: map {MapId} instance {InstanceId} took {DurationMs} ms{Host}",
                    what, map.MapId, map.InstanceId, micros / 1000, host);
            }
        }
    }

    /// <summary>
    /// The host-load sampler the slow-update lines ask "was it the machine, the collector or the code" (set by the tick watchdog feature;
    /// null: the lines carry no host text). Read on the world thread only when a line is about to be written.
    /// </summary>
    public ArcaneCore.Kernel.Ops.Watchdog.HostLoadSampler? HostLoad { get; set; }

    /// <summary>The slow-update aggregator, or null when <see cref="PerformanceLogOptions.SummaryIntervalSeconds"/> is 0 (world thread; created on first use).</summary>
    private PerfLogAggregator? PerfLog
    {
        get
        {
            if (!_perfLogResolved)
            {
                _perfLogResolved = true;
                if (Options.Perf.SummaryIntervalSeconds > 0)
                {
                    _perfLog = new PerfLogAggregator(Options.Perf, _logger, PerfClockMicros, HostVerdictOf, () => IsMainWorldThread);
                }
            }

            return _perfLog;
        }
    }

    /// <summary>The clock the summary interval runs on: the manual game clock under <see cref="UseManualClock"/>, else the world's stopwatch.</summary>
    private long PerfClockMicros() => _manualClock ? Clock.ElapsedMs * 1000 : _clock.Elapsed.Ticks / 10;

    private ArcaneCore.Kernel.Ops.Watchdog.HostLoadVerdict HostVerdictOf(long windowMicros)
        => HostLoad?.Report(windowMicros)?.Verdict ?? ArcaneCore.Kernel.Ops.Watchdog.HostLoadVerdict.Unknown;

    private string HostLoadText(long windowMicros)
    {
        if (HostLoad is not { } sampler)
        {
            return string.Empty;
        }

        try
        {
            string text = sampler.Describe(windowMicros); // a fresh probe read (one GetSystemTimes, or /proc/stat on Linux): slow-map lines are rare, and a stored sample
            // usually ends before the slow update, which would report a zero-length window as "0%"
            return text.Length == 0 ? string.Empty : "; " + text;
        }
        catch (Exception ex)
        {
            FaultLedger.Shared.Record("world.host-load", ex);
            return $"; host load unavailable ({ex.GetType().Name})";
        }
    }

    /// <summary>Benchmark seam (tests): every map update of the world loop, with its diagnostics (world thread).</summary>
    internal Action<Map, MapUpdateDiagnostics>? MapUpdateObserver { get; set; }

    /// <summary>Benchmark seam (tests): every tick of the world loop: its duration (µs), the world thread's allocation and the phases.</summary>
    internal Action<long, long, TickPhases>? TickObserver { get; set; }

    /// <summary>
    /// Raised on the world thread after every tick of the world loop with the tick body duration (microseconds), the
    /// bytes the world thread allocated during it and its phase split (the metrics feature, docs/ops/metrics.md). A
    /// handler must not block or allocate per call. Not raised by a direct <see cref="RunTick(uint)"/> call.
    /// </summary>
    public event Action<long, long, TickPhases>? TickCompleted;

    /// <summary>Raised on the world thread after each map's update with the map and its update time in microseconds (the metrics feature). Same rules as <see cref="TickCompleted"/>.</summary>
    public event Action<Map, long>? MapUpdated;

    /// <summary>Benchmark seam (tests): every timed world feature of every tick: its name, time (µs) and allocation.</summary>
    internal Action<string, long, long>? FeatureObserver { get; set; }

    /// <summary>
    /// Where the world loop reads the GC counters that it differences around every tick (test seam; default the runtime's
    /// <see cref="GcSnapshot.Read"/>). The delegate is cached, so the per-tick read allocates nothing.
    /// </summary>
    internal Func<GcSnapshot> GcSource { get; set; } = GcSnapshot.Read;

    /// <summary>The phase times of the last <see cref="RunTick"/> (world thread).</summary>
    public TickPhases LastTickPhases { get; private set; }

    /// <summary>The length in microseconds of the last <see cref="RunTick"/> body, from before its commands to its last phase (world thread).</summary>
    public long LastTickDurationMicros { get; private set; }

    /// <summary>
    /// Copy the slowest features of the last <see cref="RunTick"/> (slowest first) into the buffers and return how many were copied:
    /// <c>min(count recorded, names.Length, micros.Length)</c>. World thread only; the data is valid until the next <see cref="RunTick"/> starts its commands
    /// (readable from a <see cref="TickStarting"/> handler).
    /// </summary>
    public int CopyLastTickTopFeatures(Span<string?> names, Span<long> micros)
    {
        int count = Math.Min(_topCount, Math.Min(names.Length, micros.Length));
        _topNames.AsSpan(0, count).CopyTo(names);
        _topMicros.AsSpan(0, count).CopyTo(micros);
        return count;
    }

    private static long Micros(long stopwatchTicks) => stopwatchTicks * 1_000_000 / Stopwatch.Frequency;
}

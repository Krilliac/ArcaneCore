using System.Buffers;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Ops.Watchdog;

/// <summary>The tick-body statistics of the simulation's own recorder, when one exists. <see cref="Samples"/> == 0: not available.</summary>
public readonly record struct TickBodySummary(int Samples, long P50Micros, long P99Micros, long MaxMicros, long Overruns);

/// <summary>
/// The world-tick frame monitor. The tick thread calls <see cref="OnTick"/> once at the start of
/// every tick (it is the <c>WorldRuntime.WorldTick</c> handler); the frame time is the interval
/// between two consecutive starts, which is vmangos' "Slow world update" measure
/// (WorldRunnable.cpp:60-74): a tick that fits its budget shows as the tick interval, one that
/// does not shows as its real length. Overruns, completed hangs and the frame ring are all
/// written by the tick thread with plain stores (one writer) and read by the watchdog thread
/// with volatile loads; <see cref="OnTick"/> is a handful of stores and never blocks, allocates
/// or logs. Everything that logs runs in <see cref="Check"/> on the watchdog thread.
/// </summary>
public sealed class TickMonitor : IWatchdogMonitor, ILivenessSource
{
    private const long NoTick = long.MinValue;

    private readonly TickMonitorOptions _options;
    private readonly WatchdogClock _clock;
    private readonly ILogger _logger;
    private readonly TickRing _ring;
    private readonly Counter _frames;
    private readonly Counter _overrunCounter;
    private readonly Counter _hangCounter;
    private readonly Counter _frameP99;
    private readonly Counter _frameMax;
    // Until StartTicking resolves them, nothing counts as an overrun or a hang.
    private long _budgetMicros = long.MaxValue;
    private long _hangMicros = long.MaxValue;
    private long _intervalMicros;

    // Written by the tick thread only.
    private long _lastStartMicros = NoTick;
    private long _overruns;
    private long _hangsCompleted;

    // Watchdog thread state.
    private LogRateLimiter _overrunLimiter;
    private LogRateLimiter _hangLimiter;
    private long _overrunsReported;
    private long _hangsReported;
    private long _lastOverrunReportMicros = NoTick;
    private long _hangReportedForStart = NoTick;
    private long _nextSummaryMicros = NoTick;
    private volatile bool _started;
    private volatile bool _stopped;

    public TickMonitor(WatchdogOptions options, WatchdogClock clock, CounterRegistry counters, ILogger<TickMonitor> logger)
        : this(options.TickMonitor, clock, counters, logger)
    {
    }

    public TickMonitor(TickMonitorOptions options, WatchdogClock clock, CounterRegistry counters, ILogger logger)
    {
        _options = options;
        _clock = clock;
        _logger = logger;
        _ring = new TickRing(options.RingCapacity);
        _frames = counters.GetOrAdd("watchdog.tick.frames");
        _overrunCounter = counters.GetOrAdd("watchdog.tick.overruns");
        _hangCounter = counters.GetOrAdd("watchdog.tick.hangs");
        _frameP99 = counters.GetOrAdd("watchdog.tick.frame_p99_us", CounterKind.Gauge);
        _frameMax = counters.GetOrAdd("watchdog.tick.frame_max_us", CounterKind.Gauge);
        _overrunLimiter = new LogRateLimiter(Micros.FromSeconds(options.WarnIntervalSeconds));
        _hangLimiter = new LogRateLimiter(Micros.FromSeconds(options.WarnIntervalSeconds));
    }

    public string Name => "tick";

    /// <summary>Set by the host of the simulation to add tick-body percentiles (its own recorder) to the frame lines. Called on the watchdog thread.</summary>
    public Func<TickBodySummary>? BodyStats { get; set; }

    /// <summary>The frame ring (for diagnostics and tests).</summary>
    public TickRing Ring => _ring;

    public bool Enabled => _options.Enabled;

    /// <summary>The overrun budget in microseconds after <see cref="StartTicking"/>.</summary>
    public long BudgetMicros => _budgetMicros;

    public long HangMicros => _hangMicros;

    /// <summary>Frames longer than the budget so far.</summary>
    public long Overruns => Volatile.Read(ref _overruns);

    /// <summary>Frames longer than the hang threshold that have completed.</summary>
    public long HangsCompleted => Volatile.Read(ref _hangsCompleted);

    /// <summary>Microseconds since the last tick started, or null before the first tick.</summary>
    public long? MicrosSinceLastTick
    {
        get
        {
            long last = Volatile.Read(ref _lastStartMicros);
            return last == NoTick ? null : _clock.NowMicros - last;
        }
    }

    /// <summary>
    /// Begin consuming ticks of <paramref name="tickIntervalMs"/> milliseconds. Resolves the budget
    /// (<c>BudgetMs</c>, or twice the interval when 0) and the hang threshold. Called once, before
    /// the first <see cref="OnTick"/>.
    /// </summary>
    public void StartTicking(int tickIntervalMs)
    {
        _intervalMicros = Micros.FromMillis(Math.Max(1, tickIntervalMs));
        _budgetMicros = _options.BudgetMs > 0 ? Micros.FromMillis(_options.BudgetMs) : 2 * _intervalMicros;
        _hangMicros = _options.HangMs > 0 ? Micros.FromMillis(_options.HangMs) : long.MaxValue;
        _started = true;
    }

    /// <summary>The simulation stopped on purpose: no more ticks are expected and liveness no longer depends on them.</summary>
    public void StopTicking() => _stopped = true;

    /// <summary>
    /// Tick thread, once per tick at its start, with the watchdog clock. Two to five plain stores;
    /// no allocation, lock, interlocked operation or log.
    /// </summary>
    public void OnTick(long nowMicros)
    {
        long last = _lastStartMicros;
        if (last != NoTick)
        {
            long frame = nowMicros - last;
            _ring.Record(frame);
            if (frame > _budgetMicros)
            {
                _overruns++;
            }

            if (frame > _hangMicros)
            {
                _hangsCompleted++;
            }
        }

        Volatile.Write(ref _lastStartMicros, nowMicros);
    }

    /// <summary>Tick thread convenience: <see cref="OnTick"/> with the clock's time.</summary>
    public void OnTick() => OnTick(_clock.NowMicros);

    public void Check(long nowMicros)
    {
        if (!_options.Enabled || !_started)
        {
            return;
        }

        long frames = _ring.Count;
        _frames.Set(frames);
        long last = Volatile.Read(ref _lastStartMicros);

        // 1. A tick that is still running past the hang threshold (the ring cannot show it yet).
        if (!_stopped && last != NoTick && nowMicros - last > _hangMicros)
        {
            if (_hangReportedForStart != last)
            {
                _hangReportedForStart = last;
                _hangLimiter.Reset();
            }

            if (_hangLimiter.TryAcquire(nowMicros, out _))
            {
                _logger.LogCritical(WatchdogEvents.TickHang, "world tick has been running for {RunningMs} ms (hang threshold {HangMs} ms, interval {IntervalMs} ms)",
                    Micros.ToMillis(nowMicros - last), Micros.ToMillis(_hangMicros), Micros.ToMillis(_intervalMicros));
            }
        }

        // 2. Hangs that completed since the last check: always a critical line, never rate-limited away.
        long hangs = Volatile.Read(ref _hangsCompleted);
        if (hangs > _hangsReported)
        {
            RingStats stats = Stats(out long[] buffer);
            _logger.LogCritical(WatchdogEvents.TickHang, "world tick exceeded the hang threshold {HangMs} ms: slowest recent frame {SlowestMs} ms, {New} new hang(s), {Total} since start",
                Micros.ToMillis(_hangMicros), Micros.ToMillis(stats.Max), hangs - _hangsReported, hangs);
            ArrayPool<long>.Shared.Return(buffer);
            _hangCounter.Add(hangs - _hangsReported);
            _hangsReported = hangs;
        }

        // 3. Overruns since the last warning, one rate-limited warning with the distribution.
        long overruns = Volatile.Read(ref _overruns);
        if (overruns > _overrunsReported && _overrunLimiter.TryAcquire(nowMicros, out _))
        {
            RingStats stats = Stats(out long[] buffer);
            long sinceMs = _lastOverrunReportMicros == NoTick ? 0 : Micros.ToMillis(nowMicros - _lastOverrunReportMicros);
            _logger.LogWarning(WatchdogEvents.TickOverrun, "world tick overran its {BudgetMs} ms budget {New} time(s){Window}: frame p50 {P50Ms} ms, p99 {P99Ms} ms, slowest {MaxMs} ms over {Samples} frames{Body}",
                Micros.ToMillis(_budgetMicros), overruns - _overrunsReported, sinceMs > 0 ? $" in the last {sinceMs / 1000} s" : string.Empty,
                Micros.ToMillis(stats.P50), Micros.ToMillis(stats.P99), Micros.ToMillis(stats.Max), stats.Samples, BodyText());
            ArrayPool<long>.Shared.Return(buffer);
            _overrunCounter.Add(overruns - _overrunsReported);
            _overrunsReported = overruns;
            _lastOverrunReportMicros = nowMicros;
        }

        // 4. The periodic summary.
        long summaryInterval = Micros.FromSeconds(_options.SummaryIntervalSeconds);
        if (summaryInterval > 0)
        {
            if (_nextSummaryMicros == NoTick)
            {
                _nextSummaryMicros = nowMicros + summaryInterval;
            }
            else if (nowMicros >= _nextSummaryMicros)
            {
                _nextSummaryMicros = nowMicros + summaryInterval;
                RingStats stats = Stats(out long[] buffer);
                if (stats.Samples > 0)
                {
                    _logger.LogInformation(WatchdogEvents.TickSummary, "world tick frames: p50 {P50Ms} ms, p90 {P90Ms} ms, p99 {P99Ms} ms, slowest {MaxMs} ms over {Samples} frames; {Overruns} overrun(s) of {BudgetMs} ms, {Hangs} hang(s), {Frames} frames since start{Body}",
                        Micros.ToMillis(stats.P50), Micros.ToMillis(stats.P90), Micros.ToMillis(stats.P99), Micros.ToMillis(stats.Max), stats.Samples,
                        overruns, Micros.ToMillis(_budgetMicros), hangs, frames, BodyText());
                }

                ArrayPool<long>.Shared.Return(buffer);
            }
        }
    }

    public bool IsAlive(long nowMicros, out string? reason)
    {
        reason = null;
        if (!_options.Enabled || !_options.GatesHeartbeat || !_started || _stopped)
        {
            return true;
        }

        long last = Volatile.Read(ref _lastStartMicros);
        if (last == NoTick)
        {
            return true; // the world thread has not started ticking yet (database load, feature attach)
        }

        long since = nowMicros - last;
        if (since > _hangMicros)
        {
            reason = $"world tick has been running for {Micros.ToMillis(since)} ms (hang threshold {Micros.ToMillis(_hangMicros)} ms)";
            return false;
        }

        return true;
    }

    /// <summary>Snapshot the ring and compute its statistics (watchdog thread; the pooled buffer is returned by the caller).</summary>
    public RingStats Stats(out long[] buffer)
    {
        buffer = ArrayPool<long>.Shared.Rent(_ring.Capacity);
        int count = _ring.Snapshot(buffer.AsSpan(0, _ring.Capacity));
        RingStats stats = RingStats.Compute(buffer.AsSpan(0, count));
        _frameP99.Set(stats.P99);
        _frameMax.Set(stats.Max);
        return stats;
    }

    private string BodyText()
    {
        if (BodyStats is null)
        {
            return string.Empty;
        }

        TickBodySummary body = BodyStats();
        return body.Samples == 0
            ? string.Empty
            : $"; tick body p50 {Micros.ToMillis(body.P50Micros)} ms, p99 {Micros.ToMillis(body.P99Micros)} ms, max {Micros.ToMillis(body.MaxMicros)} ms, {body.Overruns} over the interval";
    }
}

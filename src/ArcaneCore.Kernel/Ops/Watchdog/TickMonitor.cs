using System.Buffers;
using System.Text;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Ops.Watchdog;

/// <summary>What the tick monitor may do to the process when a tick stays hung past <c>Ops:Watchdog:TickMonitor:ActionHangMs</c>; a seam so tests see the decision without stopping or aborting the test runner.</summary>
public interface ITickHangActuator
{
    /// <summary>Stop the host with <see cref="ExitCodes.Failure"/>.</summary>
    void Stop(string reason);

    /// <summary>Write a synchronous crash report and terminate the deadlocked process immediately with <see cref="ExitCodes.UnhandledException"/> so a supervisor restarts it.</summary>
    void FailFast(string reason);
}

/// <summary>The real runtime actuator for a fatal world-tick hang.</summary>
public sealed class RuntimeTickHangActuator(IHostApplicationLifetime? lifetime = null) : ITickHangActuator
{
    public void Stop(string reason)
    {
        ExitCodes.Current = ExitCodes.Failure;
        lifetime?.StopApplication();
    }

    public void FailFast(string reason)
    {
        ExitCodes.Current = ExitCodes.UnhandledException;
        try
        {
            var timeout = new TimeoutException(reason);
            string report = CrashHandler.Current?.Render(CrashKind.Unhandled, timeout)
                ?? $"FATAL TICK HANG: {reason}";
            Console.Error.WriteLine(report);
            Console.Error.Flush();
        }
        catch
        {
            // Never let a failing report block termination of a deadlocked process.
        }

        Environment.Exit(ExitCodes.UnhandledException);
    }
}

/// <summary>The tick-body statistics of the simulation's own recorder, when one exists. <see cref="Samples"/> == 0: not available.</summary>
public readonly record struct TickBodySummary(int Samples, long P50Micros, long P99Micros, long MaxMicros, long Overruns);

/// <summary>What <see cref="TickMonitor.Alerted"/> reports.</summary>
public enum TickAlertKind
{
    /// <summary>Frames over the budget (a slow tick).</summary>
    Overrun,

    /// <summary>A tick is running (or ran) past the hang threshold.</summary>
    Hang,
}

/// <summary>
/// One slow-tick report: the same facts as the log line it accompanies. <see cref="InProgress"/> is true for a hang report made while the
/// tick is still running (as opposed to the after-the-fact report of a completed hang), so a subscriber can dedup the two separately.
/// <see cref="HostVerdict"/> is the host-load sampler's verdict for the slow window (<see cref="HostLoadVerdict.Unknown"/> without a sampler) and <see cref="Hangs"/> the hangs
/// a completed-hang report stands for (one report can cover several hangs that ended between two checks; 1 for the others).
/// </summary>
public readonly record struct TickAlert(TickAlertKind Kind, string Text, string Detail, bool InProgress = false, HostLoadVerdict HostVerdict = HostLoadVerdict.Unknown, int Hangs = 1);

/// <summary>
/// What <see cref="TickMonitor.CompletedHangDetail"/> returns: the description and how many recorded hangs it stands for (consumed under the host's own lock, together with the
/// record, so the count and the record cannot disagree). <paramref name="Hangs"/> is 0 when no record was left (an earlier report already described it).
/// </summary>
public readonly record struct CompletedHangText(string Text, int Hangs);

/// <summary>
/// The world-tick frame monitor. The tick thread calls <see cref="OnTick"/> once at the start of
/// every tick (it is the <c>WorldRuntime.TickStarting</c> handler); the frame time is the interval
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

    /// <summary>How many times ActionHangMs a memory-starved hang may last before the fatal action fires regardless.</summary>
    internal const long MemoryStarvedGraceFactor = 3;

    private readonly TickMonitorOptions _options;
    private readonly WatchdogClock _clock;
    private readonly ITickHangActuator _actuator;
    private readonly ILogger _logger;
    private readonly TickRing _ring;
    private readonly Counter _frames;
    private readonly Counter _overrunCounter;
    private readonly Counter _hangCounter;
    private readonly Counter _actionCounter;
    private readonly Counter _frameP99;
    private readonly Counter _frameMax;
    private readonly CounterRegistry _counters;
    private LatencyHistogram _frameLatency = new();
    private bool _frameLatencyExternal;
    private LatencyHistogram? _bodyLatency;
    private readonly LatencyGauges _frameGauges;
    private LatencyGauges? _bodyGauges;
    private (string Name, LatencyHistogram Histogram, Counter P50, Counter P99)[] _phases = [];
    // Until StartTicking resolves them, nothing counts as an overrun or a hang.
    private long _budgetMicros = long.MaxValue;
    private long _hangMicros = long.MaxValue;
    private long _actionHangMicros = long.MaxValue;
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
    private long _actionFiredForStart = NoTick;
    private long _stackCapturedForStart = NoTick;
    private long _deferralLoggedForStart = NoTick;
    private long _nextSummaryMicros = NoTick;
    private volatile bool _started;
    private volatile bool _stopped;

    public TickMonitor(WatchdogOptions options, WatchdogClock clock, CounterRegistry counters, ILogger<TickMonitor> logger, ITickHangActuator? actuator = null)
        : this(options.TickMonitor, clock, counters, logger, actuator)
    {
    }

    public TickMonitor(TickMonitorOptions options, WatchdogClock clock, CounterRegistry counters, ILogger logger, ITickHangActuator? actuator = null)
    {
        _options = options;
        _clock = clock;
        _actuator = actuator ?? new RuntimeTickHangActuator();
        _logger = logger;
        _ring = new TickRing(options.RingCapacity);
        _frames = counters.GetOrAdd("watchdog.tick.frames");
        _overrunCounter = counters.GetOrAdd("watchdog.tick.overruns");
        _hangCounter = counters.GetOrAdd("watchdog.tick.hangs");
        _actionCounter = counters.GetOrAdd("watchdog.tick.actions");
        _frameP99 = counters.GetOrAdd("watchdog.tick.frame_p99_us", CounterKind.Gauge);
        _frameMax = counters.GetOrAdd("watchdog.tick.frame_max_us", CounterKind.Gauge);
        _counters = counters;
        _frameGauges = new LatencyGauges(counters, "watchdog.tick.frame_", includeTail: false);
        _overrunLimiter = new LogRateLimiter(Micros.FromSeconds(options.WarnIntervalSeconds));
        _hangLimiter = new LogRateLimiter(Micros.FromSeconds(options.WarnIntervalSeconds));
    }

    public string Name => "tick";

    /// <summary>Set by the host of the simulation to add tick-body percentiles (its own recorder) to the frame lines. Called on the watchdog thread.</summary>
    public Func<TickBodySummary>? BodyStats { get; set; }

    /// <summary>
    /// Set by the host of the simulation to say <em>where</em> a slow tick went, appended to the hang and overrun lines (called on the
    /// watchdog thread, only when a line is about to be logged). The argument is true while the hung tick is still running (name what
    /// it is running now) and false after the fact (break down the slowest recent tick). A throwing callback is reported in the line.
    /// </summary>
    public Func<bool, string>? HangDetail { get; set; }

    /// <summary>
    /// Set by the host of the simulation to describe the hung frame(s) that completed since the last report, used for the completed-hang line and alert instead of
    /// <see cref="HangDetail"/>(false) (which describes the slowest tick body of a whole window and so can name an earlier, unrelated tick). Called exactly once per
    /// completed-hang line, on the watchdog thread; a throwing callback is reported in the line. The host records the frame it describes in
    /// <see cref="HangClosed"/> and returns, with the text, the number of hangs it consumed: the report counts those, not the counter it read earlier, so a hang that
    /// closes between that read and this call is reported once, with its own record.
    /// </summary>
    public Func<CompletedHangText>? CompletedHangDetail { get; set; }

    /// <summary>
    /// Set by the host of the simulation to be told, on the tick thread inside <see cref="OnTick(long)"/> and <em>before</em> the hang is counted, of each frame that
    /// closed over <see cref="HangMicros"/> (the argument is its length in microseconds). The watchdog thread reports a hang only after it is counted, so whatever this
    /// stores is visible to <see cref="CompletedHangDetail"/>. It runs once per hang (a rare event) and must be quick; a throwing callback is ignored.
    /// </summary>
    public Action<long>? HangClosed { get; set; }

    /// <summary>
    /// Set by the host of the simulation to the host-load sampler, so a hang or overrun line says whether the machine, the garbage
    /// collector or the code was to blame (docs/ops/observability.md). Null: the lines carry no host-load text.
    /// </summary>
    public HostLoadSampler? HostLoad { get; set; }

    /// <summary>Set by the DI wiring (<c>Ops:Watchdog:TickMonitor:StackCaptureTool</c>) to capture the threads' stacks once per hung tick; null: no capture.</summary>
    public HangStackCapture? StackCapture { get; set; }

    /// <summary>
    /// Set by the host to be told of each hang and overrun report right after its log line (same rate limits as the log). Called on the watchdog
    /// thread; it must be quick and must not throw (a throwing callback is ignored).
    /// </summary>
    public Action<TickAlert>? Alerted { get; set; }

    /// <summary>The frame ring (for diagnostics and tests).</summary>
    public TickRing Ring => _ring;

    /// <summary>
    /// Every frame time since start and over the last minute and 15 minutes, in microseconds, with p50..p99.9, mean and standard deviation (the frame ring keeps only
    /// its newest samples). Recorded by <see cref="OnTick"/> on the monitor's own clock, unless <see cref="AttachLatency"/> gave the monitor the simulation's histogram,
    /// which is then the single source; read on any thread.
    /// </summary>
    public LatencyHistogram FrameLatency => _frameLatency;

    /// <summary>
    /// Give the monitor the simulation's own histograms: the frame interval (which then replaces the monitor's own, so a frame is recorded once), the tick body and the tick
    /// phases (name, histogram). The summary and overrun lines show their percentiles and the <c>watchdog.tick.body_*</c> and <c>watchdog.tick.phase_*</c> gauges are
    /// published. Call once, before the first <see cref="OnTick"/> and <see cref="Check"/>.
    /// </summary>
    public void AttachLatency(LatencyHistogram frame, LatencyHistogram body, IReadOnlyList<KeyValuePair<string, LatencyHistogram>> phases)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(phases);
        _frameLatency = frame;
        _frameLatencyExternal = true;
        _bodyGauges = new LatencyGauges(_counters, "watchdog.tick.body_", includeTail: true);
        _bodyLatency = body;
        _phases = [.. phases.Select(p => (p.Key, p.Value,
            _counters.GetOrAdd("watchdog.tick.phase_" + p.Key + "_p50_us", CounterKind.Gauge),
            _counters.GetOrAdd("watchdog.tick.phase_" + p.Key + "_p99_us", CounterKind.Gauge)))];
    }

    // The histogram's own clock for an attached histogram, the monitor's clock for its own.
    private LatencySnapshot FrameSnapshot(TimeSpan window, long nowMicros)
        => _frameLatencyExternal ? _frameLatency.Snapshot(window) : _frameLatency.Snapshot(window, nowMicros);

    public bool Enabled => _options.Enabled;

    /// <summary>The overrun budget in microseconds after <see cref="StartTicking"/>.</summary>
    public long BudgetMicros => _budgetMicros;

    public long HangMicros => _hangMicros;

    public long ActionHangMicros => _actionHangMicros;

    /// <summary>Frames longer than the budget so far.</summary>
    public long Overruns => Volatile.Read(ref _overruns);

    /// <summary>Frames longer than the hang threshold that have completed.</summary>
    public long HangsCompleted => Volatile.Read(ref _hangsCompleted);

    /// <summary>Fatal hang actions fired so far.</summary>
    public long ActionsFired => _actionCounter.Value;

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
        _actionHangMicros = _options.ActionHangMs > 0 && _options.Action != TickHangAction.None
            ? Micros.FromMillis(_options.ActionHangMs)
            : long.MaxValue;
        _started = true;
    }

    /// <summary>The simulation stopped on purpose: no more ticks are expected and liveness no longer depends on them.</summary>
    public void StopTicking() => _stopped = true;

    /// <summary>
    /// Tick thread, once per tick at its start, with the watchdog clock. Two to five plain stores and the frame histogram's dozen lock-free atomic operations (about 100 ns), no log.
    /// Once per histogram slice (20 s) the recording takes the histogram's short rotation lock, and the first use of a slice allocates its 9 KB bucket array; otherwise no allocation or lock.
    /// A frame over the hang threshold also runs <see cref="HangClosed"/>, which for the world host takes a short lock and allocates one small record: a rare event that costs seconds anyway.
    /// With the simulation's histogram attached (<see cref="AttachLatency"/>) the monitor records no frame itself.
    /// </summary>
    public long OnTick(long nowMicros)
    {
        long last = _lastStartMicros;
        long frame = 0;
        if (last != NoTick)
        {
            frame = nowMicros - last;
            _ring.Record(frame);
            if (!_frameLatencyExternal)
            {
                _frameLatency.Record(frame, nowMicros);
            }
            if (frame > _budgetMicros)
            {
                _overruns++;
            }

            if (frame > _hangMicros)
            {
                try
                {
                    HangClosed?.Invoke(frame);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // The tick thread must go on whatever the host's recorder does.
                }

                _hangsCompleted++;
            }
        }

        Volatile.Write(ref _lastStartMicros, nowMicros);
        return frame;
    }

    /// <summary>Tick thread convenience: <see cref="OnTick(long)"/> with the clock's time.</summary>
    public long OnTick() => OnTick(_clock.NowMicros);

    public void Check(long nowMicros)
    {
        if (!_options.Enabled || !_started)
        {
            return;
        }

        long frames = _ring.Count;
        _frames.Set(frames);
        PublishGauges(nowMicros);
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
                string running = DetailText(inProgress: true);
                string host = HostText(nowMicros - last);
                _logger.LogCritical(WatchdogEvents.TickHang, "world tick has been running for {RunningMs} ms (hang threshold {HangMs} ms, interval {IntervalMs} ms){Detail}{Host}",
                    Micros.ToMillis(nowMicros - last), Micros.ToMillis(_hangMicros), Micros.ToMillis(_intervalMicros), running, host);
                Alert(TickAlertKind.Hang, $"The world tick is hung (running {Micros.ToMillis(nowMicros - last)} ms)", $"hang threshold {Micros.ToMillis(_hangMicros)} ms{running}{host}", inProgress: true, hostVerdict: HostVerdict(nowMicros - last));
            }

            // Once per hung tick, while it is still stuck: where in the code the world thread is (out of process, off this thread).
            if (StackCapture is { } capture && _stackCapturedForStart != last)
            {
                _stackCapturedForStart = last;
                capture.TryCapture(nowMicros, $"world tick running for {Micros.ToMillis(nowMicros - last)} ms (hang threshold {Micros.ToMillis(_hangMicros)} ms)", nowMicros - last, GcPauseMicros(nowMicros - last));
            }
        }

        // 1b. Fatal hang action: when a single tick remains wedged past ActionHangMs and the host-load sampler does not blame GC pauses, machine CPU starvation or a stopped process.
        // A memory-starved verdict only defers the action: an idle process on a memory-loaded host is also what a deadlock looks like, so past MemoryStarvedGraceFactor x ActionHangMs it fires anyway.
        if (!_stopped && last != NoTick && nowMicros - last > _actionHangMicros && _actionFiredForStart != last)
        {
            HostLoadVerdict verdict = HostVerdict(nowMicros - last);
            bool memoryDeferred = verdict == HostLoadVerdict.MemoryStarved && nowMicros - last <= _actionHangMicros * MemoryStarvedGraceFactor;
            if (memoryDeferred && _deferralLoggedForStart != last)
            {
                _deferralLoggedForStart = last; // once per hung tick, not once per check
                _logger.LogWarning(WatchdogEvents.TickActionDeferred, "world tick has been hung for {RunningMs} ms (action threshold {ActionHangMs} ms): action {Action} deferred while this process is memory-starved; it fires anyway past {GraceMs} ms",
                    Micros.ToMillis(nowMicros - last), Micros.ToMillis(_actionHangMicros), _options.Action, Micros.ToMillis(_actionHangMicros * MemoryStarvedGraceFactor));
            }

            if (!memoryDeferred && verdict is not (HostLoadVerdict.HostStarved or HostLoadVerdict.GcPauses or HostLoadVerdict.GcPaging or HostLoadVerdict.ProcessStalled))
            {
                _actionFiredForStart = last;
                _actionCounter.Increment();
                string running = DetailText(inProgress: true);
                string host = HostText(nowMicros - last);
                string reason = $"world tick has been hung for {Micros.ToMillis(nowMicros - last)} ms (action threshold {Micros.ToMillis(_actionHangMicros)} ms, action {_options.Action}){running}{host}";
                _logger.LogCritical(WatchdogEvents.TickAction, "world tick has been hung for {RunningMs} ms (action threshold {ActionHangMs} ms): executing {Action}{Detail}{Host}",
                    Micros.ToMillis(nowMicros - last), Micros.ToMillis(_actionHangMicros), _options.Action, running, host);
                switch (_options.Action)
                {
                    case TickHangAction.Stop:
                        _actuator.Stop(reason);
                        break;
                    case TickHangAction.FailFast:
                        _actuator.FailFast(reason);
                        break;
                }
            }
        }

        // 2. Hangs that completed since the last check: always a critical line, never rate-limited away.
        long hangs = Volatile.Read(ref _hangsCompleted);
        if (hangs > _hangsReported)
        {
            RingStats stats = Stats(out long[] buffer);
            long newHangs = hangs - _hangsReported;
            string after = HungFrameText(ref newHangs); // the host's record count wins when it is larger: a hang closed after the counter read above
            long total = _hangsReported + newHangs;
            string host = HostText(stats.Max);
            _logger.LogCritical(WatchdogEvents.TickHang, "world tick exceeded the hang threshold {HangMs} ms: slowest recent frame {SlowestMs} ms, {New} new hang(s), {Total} since start{Detail}{Host}",
                Micros.ToMillis(_hangMicros), Micros.ToMillis(stats.Max), newHangs, total, after, host);
            Alert(TickAlertKind.Hang, $"The world tick stalled ({Micros.ToMillis(stats.Max)} ms)", $"hang threshold {Micros.ToMillis(_hangMicros)} ms, {newHangs} new hang(s), {total} since start{after}{host}", hostVerdict: HostVerdict(stats.Max), hangs: (int)Math.Min(int.MaxValue, newHangs));
            ArrayPool<long>.Shared.Return(buffer);
            _hangCounter.Add(newHangs);
            _hangsReported = total;
        }

        // 3. Overruns since the last warning, one rate-limited warning with the distribution.
        long overruns = Volatile.Read(ref _overruns);
        if (overruns > _overrunsReported && _overrunLimiter.TryAcquire(nowMicros, out _))
        {
            RingStats stats = Stats(out long[] buffer);
            long sinceMs = _lastOverrunReportMicros == NoTick ? 0 : Micros.ToMillis(nowMicros - _lastOverrunReportMicros);
            string slowest = DetailText(inProgress: false);
            string host = HostText(stats.Max);
            _logger.LogWarning(WatchdogEvents.TickOverrun, "world tick overran its {BudgetMs} ms budget {New} time(s){Window}: frame p50 {P50Ms} ms, p99 {P99Ms} ms, slowest {MaxMs} ms over {Samples} frames{Body}{Detail}{Host}; {Latency}",
                Micros.ToMillis(_budgetMicros), overruns - _overrunsReported, sinceMs > 0 ? $" in the last {sinceMs / 1000} s" : string.Empty,
                Micros.ToMillis(stats.P50), Micros.ToMillis(stats.P99), Micros.ToMillis(stats.Max), stats.Samples, BodyText(), slowest, host, LatencyText(nowMicros, full: false));
            Alert(TickAlertKind.Overrun, $"Slow world ticks: p99 {Micros.ToMillis(stats.P99)} ms", $"{overruns - _overrunsReported} frame(s) over the {Micros.ToMillis(_budgetMicros)} ms budget, slowest {Micros.ToMillis(stats.Max)} ms over {stats.Samples} frames{slowest}{host}");
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
                    _logger.LogInformation(WatchdogEvents.TickSummary, "world tick frames: p50 {P50Ms} ms, p90 {P90Ms} ms, p99 {P99Ms} ms, slowest {MaxMs} ms over {Samples} frames; {Overruns} overrun(s) of {BudgetMs} ms, {Hangs} hang(s), {Frames} frames since start{Body}; {Latency}",
                        Micros.ToMillis(stats.P50), Micros.ToMillis(stats.P90), Micros.ToMillis(stats.P99), Micros.ToMillis(stats.Max), stats.Samples,
                        overruns, Micros.ToMillis(_budgetMicros), hangs, frames, BodyText(), LatencyText(nowMicros, full: true));
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

    private void Alert(TickAlertKind kind, string text, string detail, bool inProgress = false, HostLoadVerdict hostVerdict = HostLoadVerdict.Unknown, int hangs = 1)
    {
        if (Alerted is not { } alerted)
        {
            return;
        }

        try
        {
            alerted(new TickAlert(kind, text, detail, inProgress, hostVerdict, hangs));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The watchdog thread must keep checking whatever a subscriber does.
        }
    }

    /// <summary>The detail of a completed hang: the host's <see cref="CompletedHangDetail"/> when set, else the window-wide <see cref="HangDetail"/>(false). Watchdog thread.</summary>
    private string HungFrameText(ref long newHangs)
    {
        if (CompletedHangDetail is not { } detail)
        {
            return DetailText(inProgress: false);
        }

        try
        {
            CompletedHangText result = detail();
            newHangs = Math.Max(newHangs, result.Hangs);
            return string.IsNullOrEmpty(result.Text) ? string.Empty : "; " + result.Text;
        }
        catch (Exception ex)
        {
            return $"; hang detail unavailable ({ex.GetType().Name})";
        }
    }

    private string DetailText(bool inProgress)
    {
        if (HangDetail is not { } detail)
        {
            return string.Empty;
        }

        try
        {
            string text = detail(inProgress);
            return string.IsNullOrEmpty(text) ? string.Empty : "; " + text;
        }
        catch (Exception ex)
        {
            return $"; hang detail unavailable ({ex.GetType().Name})";
        }
    }

    /// <summary>The host-load verdict of the window of a slow tick (watchdog thread), or Unknown without a sampler or a baseline.</summary>
    private HostLoadVerdict HostVerdict(long windowMicros)
    {
        try
        {
            return HostLoad?.Report(windowMicros)?.Verdict ?? HostLoadVerdict.Unknown;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return HostLoadVerdict.Unknown;
        }
    }

    /// <summary>The GC pause inside the window of a slow tick (watchdog thread): 0 without a sampler or a baseline, -1 when the probe faulted (recorded).</summary>
    private long GcPauseMicros(long windowMicros)
    {
        try
        {
            return HostLoad?.Report(windowMicros)?.GcPauseMicros ?? 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("TickMonitor.GcPauseMicros", ex, "world tick hang: the GC pause of the hung window could not be read");
            return HangStackCapture.GcPauseUnknown;
        }
    }

    /// <summary>"; host: ..." for the window of a slow tick (watchdog thread, only when a line is about to be logged), or empty.</summary>
    private string HostText(long windowMicros)
    {
        if (HostLoad is not { } sampler)
        {
            return string.Empty;
        }

        try
        {
            string text = sampler.Describe(windowMicros);
            return string.IsNullOrEmpty(text) ? string.Empty : "; " + text;
        }
        catch (Exception ex)
        {
            return $"; host load unavailable ({ex.GetType().Name})";
        }
    }

    /// <summary>Watchdog thread, every check: the percentile gauges of the newest minute of frames, tick bodies and tick phases (no allocation).</summary>
    private void PublishGauges(long nowMicros)
    {
        LatencySnapshot frame = FrameSnapshot(OneMinute, nowMicros);
        if (!frame.IsEmpty)
        {
            _frameGauges.Publish(frame);
        }

        if (_bodyLatency is { } body && _bodyGauges is { } bodyGauges && body.Snapshot(OneMinute) is { IsEmpty: false } bodySnap)
        {
            bodyGauges.Publish(bodySnap);
        }

        foreach ((string _, LatencyHistogram histogram, Counter p50, Counter p99) in _phases)
        {
            if (histogram.Snapshot(OneMinute) is { IsEmpty: false } phase)
            {
                p50.Set(phase.P50);
                p99.Set(phase.P99);
            }
        }
    }

    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FifteenMinutes = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The latency detail appended to the overrun and summary lines (watchdog thread, only when a line is logged): the frame interval's full percentile line for the last
    /// minute (and the last 15 minutes when <paramref name="full"/>), the same for the tick body when the host attached its histogram, and each phase's p50/p99 in ms.
    /// </summary>
    private string LatencyText(long nowMicros, bool full)
    {
        var text = new StringBuilder("frame latency last 1 min: ").Append(FrameSnapshot(OneMinute, nowMicros).Format());
        if (full)
        {
            text.Append("; last 15 min: ").Append(FrameSnapshot(FifteenMinutes, nowMicros).Format());
        }

        if (_bodyLatency is { } body)
        {
            text.Append("; tick body last 1 min: ").Append(body.Snapshot(OneMinute).Format());
            if (full)
            {
                text.Append("; last 15 min: ").Append(body.Snapshot(FifteenMinutes).Format());
            }
        }

        bool any = false;
        foreach ((string name, LatencyHistogram histogram, Counter _, Counter _) in _phases)
        {
            LatencySnapshot phase = histogram.Snapshot(OneMinute);
            if (phase.IsEmpty)
            {
                continue;
            }

            text.Append(any ? ", " : "; tick phases p50/p99 ms last 1 min: ").Append(name).Append(' ')
                .Append(LatencySnapshot.Millis(phase.P50)).Append('/').Append(LatencySnapshot.Millis(phase.P99));
            any = true;
        }

        return text.ToString();
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

/// <summary>The gauges of one <see cref="LatencySnapshot"/>: <c>{prefix}p50_us</c>, p75, p90, p95, p999, mean, stddev, and with <c>includeTail</c> also p99 and max (the frame ring already publishes those two).</summary>
internal sealed class LatencyGauges
{
    private readonly Counter _p50;
    private readonly Counter _p75;
    private readonly Counter _p90;
    private readonly Counter _p95;
    private readonly Counter _p999;
    private readonly Counter _mean;
    private readonly Counter _stdDev;
    private readonly Counter? _p99;
    private readonly Counter? _max;

    public LatencyGauges(CounterRegistry counters, string prefix, bool includeTail)
    {
        _p50 = counters.GetOrAdd(prefix + "p50_us", CounterKind.Gauge);
        _p75 = counters.GetOrAdd(prefix + "p75_us", CounterKind.Gauge);
        _p90 = counters.GetOrAdd(prefix + "p90_us", CounterKind.Gauge);
        _p95 = counters.GetOrAdd(prefix + "p95_us", CounterKind.Gauge);
        _p999 = counters.GetOrAdd(prefix + "p999_us", CounterKind.Gauge);
        _mean = counters.GetOrAdd(prefix + "mean_us", CounterKind.Gauge);
        _stdDev = counters.GetOrAdd(prefix + "stddev_us", CounterKind.Gauge);
        if (includeTail)
        {
            _p99 = counters.GetOrAdd(prefix + "p99_us", CounterKind.Gauge);
            _max = counters.GetOrAdd(prefix + "max_us", CounterKind.Gauge);
        }
    }

    public void Publish(in LatencySnapshot snapshot)
    {
        _p50.Set(snapshot.P50);
        _p75.Set(snapshot.P75);
        _p90.Set(snapshot.P90);
        _p95.Set(snapshot.P95);
        _p999.Set(snapshot.P999);
        _mean.Set((long)Math.Round(snapshot.Mean));
        _stdDev.Set((long)Math.Round(snapshot.StdDev));
        _p99?.Set(snapshot.P99);
        _max?.Set(snapshot.Max);
    }
}

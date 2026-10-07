using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Ops.Watchdog;

/// <summary>
/// Thread-pool starvation detector. Every <c>Ops:Watchdog:ThreadPool:ProbeIntervalSeconds</c>
/// the watchdog thread queues this object itself as a work item (no closure, no allocation: it
/// implements <see cref="IThreadPoolWorkItem"/>) and the pool thread that runs it stores the
/// queue delay. The next check judges the delay against the warning and critical thresholds,
/// and a probe that has not run at all for the critical delay is reported as well, which a
/// pool-based timer could never do because it would be starved too. The watchdog thread is a
/// dedicated thread for exactly this reason.
/// </summary>
public sealed class ThreadPoolProbe : IWatchdogMonitor, IThreadPoolWorkItem
{
    private readonly ThreadPoolProbeOptions _options;
    private readonly WatchdogClock _clock;
    private readonly ILogger _logger;
    private readonly Action<IThreadPoolWorkItem> _queue;
    private readonly Counter _probes;
    private readonly Counter _starvations;
    private readonly Counter _delayGauge;
    private readonly Counter _threadsGauge;
    private readonly Counter _pendingGauge;
    private LogRateLimiter _warnLimiter;
    private long _nextProbeMicros = long.MinValue;
    private long _queuedMicros;
    private int _outstanding; // 1 while a probe is queued and has not run
    private long _arrivedDelayMicros = -1; // written by the pool thread
    private bool _missingReported;
    private bool _evaluated = true;

    public ThreadPoolProbe(WatchdogOptions options, WatchdogClock clock, CounterRegistry counters, ILogger<ThreadPoolProbe> logger)
        : this(options.ThreadPool, clock, counters, logger, null)
    {
    }

    /// <summary><paramref name="queue"/> replaces <see cref="ThreadPool.UnsafeQueueUserWorkItem(IThreadPoolWorkItem, bool)"/> in tests (a queue that never runs the item simulates starvation).</summary>
    public ThreadPoolProbe(ThreadPoolProbeOptions options, WatchdogClock clock, CounterRegistry counters, ILogger logger, Action<IThreadPoolWorkItem>? queue)
    {
        _options = options;
        _clock = clock;
        _logger = logger;
        _queue = queue ?? QueueToPool;
        _probes = counters.GetOrAdd("watchdog.threadpool.probes");
        _starvations = counters.GetOrAdd("watchdog.threadpool.starvations");
        _delayGauge = counters.GetOrAdd("watchdog.threadpool.delay_us", CounterKind.Gauge);
        _threadsGauge = counters.GetOrAdd("watchdog.threadpool.threads", CounterKind.Gauge);
        _pendingGauge = counters.GetOrAdd("watchdog.threadpool.pending", CounterKind.Gauge);
        _warnLimiter = new LogRateLimiter(Micros.FromSeconds(options.WarnIntervalSeconds));
    }

    public string Name => "threadpool";

    /// <summary>Queue delay of the last probe that ran, in microseconds; -1 before the first.</summary>
    public long LastDelayMicros => Volatile.Read(ref _arrivedDelayMicros);

    /// <summary>Probes that exceeded the warning delay or never ran within the critical delay.</summary>
    public long Starvations => _starvations.Value;

    /// <summary>True while a probe is queued and has not run.</summary>
    public bool Outstanding => Volatile.Read(ref _outstanding) == 1;

    /// <summary>Pool thread: record the delay. One volatile load, two volatile stores.</summary>
    public void Execute()
    {
        long queued = Volatile.Read(ref _queuedMicros);
        Volatile.Write(ref _arrivedDelayMicros, _clock.NowMicros - queued);
        Volatile.Write(ref _outstanding, 0);
    }

    public void Check(long nowMicros)
    {
        if (!_options.Enabled)
        {
            return;
        }

        long warn = Micros.FromMillis(_options.WarnDelayMs);
        long critical = Micros.FromMillis(_options.CriticalDelayMs);
        if (Volatile.Read(ref _outstanding) == 1)
        {
            long waiting = nowMicros - _queuedMicros;
            if (critical > 0 && waiting > critical && !_missingReported)
            {
                _missingReported = true;
                _starvations.Increment();
                _logger.LogCritical(WatchdogEvents.ThreadPoolStarvation, "thread pool probe has not run for {WaitingMs} ms (critical delay {CriticalMs} ms): {Threads} pool threads, {Pending} pending work items, {Completed} completed",
                    Micros.ToMillis(waiting), _options.CriticalDelayMs, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, ThreadPool.CompletedWorkItemCount);
            }

            return;
        }

        if (!_evaluated)
        {
            _evaluated = true;
            long delay = Volatile.Read(ref _arrivedDelayMicros);
            _delayGauge.Set(delay);
            _threadsGauge.Set(ThreadPool.ThreadCount);
            _pendingGauge.Set(ThreadPool.PendingWorkItemCount);
            if (critical > 0 && delay > critical)
            {
                if (!_missingReported)
                {
                    _starvations.Increment();
                }

                _logger.LogCritical(WatchdogEvents.ThreadPoolStarvation, "thread pool probe waited {DelayMs} ms for a thread (critical delay {CriticalMs} ms): {Threads} pool threads, {Pending} pending work items",
                    Micros.ToMillis(delay), _options.CriticalDelayMs, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount);
            }
            else if (warn > 0 && delay > warn)
            {
                _starvations.Increment();
                if (_warnLimiter.TryAcquire(nowMicros, out long suppressed))
                {
                    _logger.LogWarning(WatchdogEvents.ThreadPoolStarvation, "thread pool probe waited {DelayMs} ms for a thread (warning delay {WarnMs} ms): {Threads} pool threads, {Pending} pending work items; {Suppressed} suppressed",
                        Micros.ToMillis(delay), _options.WarnDelayMs, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, suppressed);
                }
            }
        }

        long interval = Micros.FromSeconds(Math.Max(1, _options.ProbeIntervalSeconds));
        if (_nextProbeMicros != long.MinValue && nowMicros < _nextProbeMicros)
        {
            return;
        }

        _nextProbeMicros = nowMicros + interval;
        Probe(nowMicros);
    }

    /// <summary>Queue one probe now (the check does it on schedule; tests call it directly).</summary>
    public void Probe(long nowMicros)
    {
        _missingReported = false;
        _evaluated = false;
        _probes.Increment();
        Volatile.Write(ref _queuedMicros, nowMicros);
        Volatile.Write(ref _outstanding, 1);
        _queue(this);
    }

    private static void QueueToPool(IThreadPoolWorkItem item) => ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: false);
}

using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Ops.Watchdog;

/// <summary>
/// Runs every <see cref="IWatchdogMonitor"/> on one dedicated thread ("watchdog") every
/// <c>Ops:Watchdog:CheckIntervalMs</c>. A dedicated thread, not a timer: a thread-pool timer
/// would be starved with the pool it is meant to watch, and a world-thread hook would hang with
/// the world. A monitor that throws is logged (rate-limited per monitor) and skipped for that
/// round; the thread never dies on a monitor fault. Disabled with <c>Ops:Watchdog:Enabled=false</c>:
/// then no thread starts and nothing in the section runs.
/// </summary>
public sealed class WatchdogHost : IHostedService
{
    private readonly WatchdogOptions _options;
    private readonly IWatchdogMonitor[] _monitors;
    private readonly WatchdogClock _clock;
    private readonly ILogger _logger;
    private readonly Counter _checks;
    private readonly Counter _faults;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly LogRateLimiter[] _faultLimiters;
    private Thread? _thread;

    public WatchdogHost(WatchdogOptions options, IEnumerable<IWatchdogMonitor> monitors, WatchdogClock clock, CounterRegistry counters, ILogger<WatchdogHost> logger)
    {
        _options = options;
        _monitors = [.. monitors];
        _clock = clock;
        _logger = logger;
        _checks = counters.GetOrAdd("watchdog.checks");
        _faults = counters.GetOrAdd("watchdog.monitor_faults");
        _faultLimiters = new LogRateLimiter[_monitors.Length];
        for (int i = 0; i < _faultLimiters.Length; i++)
        {
            _faultLimiters[i] = new LogRateLimiter(Micros.FromSeconds(60));
        }
    }

    /// <summary>Rounds the thread has completed.</summary>
    public long Checks => _checks.Value;

    public bool IsRunning => _thread is { IsAlive: true };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(WatchdogEvents.Started, "watchdog disabled (Ops:Watchdog:Enabled=false): no health monitor runs");
            return Task.CompletedTask;
        }

        foreach (IWatchdogMonitor monitor in _monitors)
        {
            monitor.Start();
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "watchdog", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _logger.LogInformation(WatchdogEvents.Started, "watchdog started: {Monitors} every {IntervalMs} ms", string.Join(", ", _monitors.Select(m => m.Name)), _options.CheckIntervalMs);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_thread is null)
        {
            return Task.CompletedTask;
        }

        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(5));
        _thread = null;
        foreach (IWatchdogMonitor monitor in _monitors)
        {
            try
            {
                monitor.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogError(WatchdogEvents.MonitorFault, ex, "watchdog monitor {Monitor} failed to stop", monitor.Name);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>One round over every monitor (the thread calls it; tests call it directly).</summary>
    public void RunOnce(long nowMicros)
    {
        for (int i = 0; i < _monitors.Length; i++)
        {
            try
            {
                _monitors[i].Check(nowMicros);
            }
            catch (Exception ex)
            {
                _faults.Increment();
                if (_faultLimiters[i].TryAcquire(nowMicros, out long suppressed))
                {
                    _logger.LogError(WatchdogEvents.MonitorFault, ex, "watchdog monitor {Monitor} failed ({Suppressed} suppressed)", _monitors[i].Name, suppressed);
                }
            }
        }

        _checks.Increment();
    }

    private void Run()
    {
        int interval = Math.Clamp(_options.CheckIntervalMs, 100, 60_000);
        while (!_stop.Wait(interval))
        {
            RunOnce(_clock.NowMicros);
        }
    }
}

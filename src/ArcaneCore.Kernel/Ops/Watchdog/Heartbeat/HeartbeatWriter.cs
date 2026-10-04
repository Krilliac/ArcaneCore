using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Ops.Watchdog.Heartbeat;

/// <summary>The sink and beat interval the configuration and environment resolve to.</summary>
public sealed record HeartbeatPlan(IHeartbeatSink Sink, int IntervalSeconds, string? Note)
{
    /// <summary>
    /// Resolve <c>Ops:Watchdog:Heartbeat</c> against the environment. <c>Auto</c> picks systemd when
    /// <c>NOTIFY_SOCKET</c> is set, none otherwise. For systemd the interval defaults to half of
    /// <c>WATCHDOG_USEC</c> (at least one second) and is 0, meaning READY/STOPPING only, when systemd
    /// did not ask for a watchdog. A sink that cannot be opened (no unix sockets, unwritable file)
    /// yields <see cref="NullHeartbeatSink"/> with the reason in <see cref="Note"/>, never an exception.
    /// </summary>
    public static HeartbeatPlan Resolve(HeartbeatOptions options, Func<string, string?> environment, int processId, string contentRoot, TextWriter stdout)
    {
        bool systemd = SystemdNotifySink.TryReadEnvironment(environment, processId, out string? socket, out long watchdogMicros);
        HeartbeatMode mode = options.Mode == HeartbeatMode.Auto ? (systemd ? HeartbeatMode.Systemd : HeartbeatMode.None) : options.Mode;
        int interval = options.IntervalSeconds > 0 ? options.IntervalSeconds : 10;
        switch (mode)
        {
            case HeartbeatMode.Systemd:
                if (socket is null)
                {
                    return new HeartbeatPlan(NullHeartbeatSink.Instance, 0, "Heartbeat:Mode is Systemd but NOTIFY_SOCKET is not set; no heartbeat is sent");
                }

                if (options.IntervalSeconds <= 0)
                {
                    interval = watchdogMicros > 0 ? (int)Math.Max(1, watchdogMicros / 2 / Micros.PerSecond) : 0;
                }

                try
                {
                    return new HeartbeatPlan(new SystemdNotifySink(socket), interval, watchdogMicros > 0 ? null : "systemd did not set WATCHDOG_USEC: READY and STOPPING only, no WATCHDOG=1 beats");
                }
                catch (Exception ex) when (ex is System.Net.Sockets.SocketException or PlatformNotSupportedException or ArgumentException)
                {
                    // No unix datagram sockets here (Windows): systemd cannot be the supervisor anyway.
                    return new HeartbeatPlan(NullHeartbeatSink.Instance, 0, "systemd notify socket cannot be opened on this platform: " + ex.Message);
                }

            case HeartbeatMode.File:
                if (string.IsNullOrWhiteSpace(options.FilePath))
                {
                    return new HeartbeatPlan(NullHeartbeatSink.Instance, 0, "Heartbeat:Mode is File but FilePath is empty; no heartbeat is written");
                }

                return new HeartbeatPlan(new FileHeartbeatSink(Path.IsPathRooted(options.FilePath) ? options.FilePath : Path.Combine(contentRoot, options.FilePath), processId), interval, null);

            case HeartbeatMode.Stdout:
                return new HeartbeatPlan(new TextWriterHeartbeatSink(stdout, processId), interval, null);

            case HeartbeatMode.None:
            case HeartbeatMode.Auto:
            default:
                return new HeartbeatPlan(NullHeartbeatSink.Instance, 0, null);
        }
    }
}

/// <summary>
/// Sends the heartbeat every interval while every <see cref="ILivenessSource"/> says the process
/// is healthy. A beat is withheld (fail closed) when any source is not alive, with a rate-limited
/// warning naming the source and the reason: a supervisor watchdog then restarts a process whose
/// world thread hung although the process is otherwise responsive. READY goes out at start,
/// STOPPING at stop. Runs on the watchdog thread.
/// </summary>
public sealed class HeartbeatWriter : IWatchdogMonitor
{
    private readonly HeartbeatOptions _options;
    private readonly ILivenessSource[] _sources;
    private readonly ILogger _logger;
    private readonly Counter _sent;
    private readonly Counter _withheld;
    private readonly Counter _failed;
    private readonly Func<HeartbeatPlan> _resolve;
    private HeartbeatPlan? _plan;
    private LogRateLimiter _withheldLimiter;
    private LogRateLimiter _failureLimiter;
    private long _nextBeatMicros = long.MinValue;

    public HeartbeatWriter(WatchdogOptions options, IEnumerable<ILivenessSource> sources, CounterRegistry counters, ILogger<HeartbeatWriter> logger, IHostEnvironment? environment = null)
        : this(options.Heartbeat, sources, counters, logger, () => HeartbeatPlan.Resolve(options.Heartbeat, Environment.GetEnvironmentVariable, Environment.ProcessId, environment?.ContentRootPath ?? AppContext.BaseDirectory, Console.Out))
    {
    }

    public HeartbeatWriter(HeartbeatOptions options, IEnumerable<ILivenessSource> sources, CounterRegistry counters, ILogger logger, Func<HeartbeatPlan> resolve)
    {
        _options = options;
        _sources = [.. sources];
        _logger = logger;
        _resolve = resolve;
        _sent = counters.GetOrAdd("watchdog.heartbeat.sent");
        _withheld = counters.GetOrAdd("watchdog.heartbeat.withheld");
        _failed = counters.GetOrAdd("watchdog.heartbeat.failed");
        _withheldLimiter = new LogRateLimiter(Micros.FromSeconds(options.WarnIntervalSeconds));
        _failureLimiter = new LogRateLimiter(Micros.FromSeconds(options.WarnIntervalSeconds));
    }

    public string Name => "heartbeat";

    /// <summary>The resolved plan after <see cref="Start"/>.</summary>
    public HeartbeatPlan? Plan => _plan;

    public long Sent => _sent.Value;

    public long Withheld => _withheld.Value;

    public void Start()
    {
        _plan = _resolve();
        if (_plan.Note is not null)
        {
            _logger.LogWarning(WatchdogEvents.HeartbeatSink, "heartbeat: {Note}", _plan.Note);
        }

        if (_plan.Sink is NullHeartbeatSink)
        {
            _logger.LogInformation(WatchdogEvents.HeartbeatSink, "heartbeat: none (mode {Mode})", _options.Mode);
            return;
        }

        _logger.LogInformation(WatchdogEvents.HeartbeatSink, "heartbeat: {Sink} every {Interval} s, gated by {Sources} liveness source(s)",
            _plan.Sink.Description, _plan.IntervalSeconds, _sources.Length);
        if (!_plan.Sink.Ready("ArcaneCore started"))
        {
            _failed.Increment();
            _logger.LogWarning(WatchdogEvents.HeartbeatSink, "heartbeat READY could not be delivered to {Sink}: {Error}", _plan.Sink.Description, _plan.Sink.LastError);
        }
    }

    public void Check(long nowMicros)
    {
        if (_plan is null || _plan.IntervalSeconds <= 0 || _plan.Sink is NullHeartbeatSink)
        {
            return;
        }

        long interval = Micros.FromSeconds(_plan.IntervalSeconds);
        if (_nextBeatMicros != long.MinValue && nowMicros < _nextBeatMicros)
        {
            return;
        }

        _nextBeatMicros = nowMicros + interval;
        foreach (ILivenessSource source in _sources)
        {
            if (!source.IsAlive(nowMicros, out string? reason))
            {
                _withheld.Increment();
                if (_withheldLimiter.TryAcquire(nowMicros, out long suppressed))
                {
                    _logger.LogWarning(WatchdogEvents.HeartbeatWithheld, "heartbeat withheld: {Source} reports {Reason} ({Suppressed} suppressed)", source.Name, reason, suppressed);
                }

                return;
            }
        }

        if (_plan.Sink.Beat())
        {
            _sent.Increment();
        }
        else
        {
            _failed.Increment();
            if (_failureLimiter.TryAcquire(nowMicros, out long suppressed))
            {
                _logger.LogWarning(WatchdogEvents.HeartbeatSink, "heartbeat could not be delivered to {Sink}: {Error} ({Suppressed} suppressed)", _plan.Sink.Description, _plan.Sink.LastError, suppressed);
            }
        }
    }

    public void Stop()
    {
        if (_plan is null)
        {
            return;
        }

        if (_plan.Sink is not NullHeartbeatSink)
        {
            _plan.Sink.Stopping("ArcaneCore stopping");
        }

        _plan.Sink.Dispose();
    }
}

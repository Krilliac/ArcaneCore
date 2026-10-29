using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Ops.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.RealmList;

/// <summary>
/// Writes this world's liveness into its realm-list row, so the logon daemon can tell a running world from a stopped or hung one
/// (docs/ops/realm-liveness.md). Registered as a hosted service <i>before</i> <c>WorldHost</c>, so the row says
/// <see cref="RealmRuntimeState.Starting"/> (Offline set) from the moment the process begins starting, long before the world accepts
/// connections.
/// <list type="bullet">
/// <item><b>Starting</b>: beats every interval on process liveness (there is no world loop yet to vouch for).</item>
/// <item><b>Ready</b> = the listener is bound (<see cref="IHostApplicationLifetime.ApplicationStarted"/>) <b>and</b> the world loop has
/// started a tick (<see cref="WorldRuntime.TicksStarted"/> &gt; 0): Offline is cleared, the population set to 0 (vmangos Master.cpp:215),
/// the state becomes <see cref="RealmRuntimeState.Online"/>.</item>
/// <item><b>Online</b>: every interval a heartbeat is written <i>only if</i> <see cref="WorldRuntime.TicksStarted"/> advanced since the last
/// one; otherwise it is withheld and a rate-limited warning says so. Whether a stale heartbeat means offline is the logon daemon's decision, so a
/// loop that recovers simply beats again and the realm comes back by itself.</item>
/// <item><b>Countdowns</b> (<see cref="ServerLifecycleFeature.CountdownChanged"/>): a restart countdown shows <see cref="RealmRuntimeState.Restarting"/>,
/// a shutdown countdown <see cref="RealmRuntimeState.ShuttingDown"/>, a cancel returns to Online.</item>
/// <item><b>Clean stop</b> (<see cref="StopAsync(CancellationToken)"/>, called from the before-stop hook): the writer stops, then Offline and
/// <see cref="RealmRuntimeState.Stopped"/> are written (vmangos Master.cpp:330).</item>
/// </list>
/// Threading: the world thread only bumps a counter and, on a countdown, stores one integer; every database call runs on a thread-pool
/// thread, one at a time (<c>_gate</c>). A failed write is logged and the loop goes on. Off when <see cref="RealmStatusOptions.Id"/> is 0,
/// when no <see cref="IRealmStatusStore"/> is registered (no auth database), and in gateway worker mode (<c>Cluster:Worker:Enabled</c>), where
/// an active and a standby world share the realm and the gateway owns the client-facing address.
/// </summary>
public sealed class RealmLivenessService : IHostedService, IDisposable
{
    private enum Phase { Idle, Starting, Online, Stopped }

    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private readonly RealmStatusOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<long> _ticksStarted;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _wake;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LogGate _withheldWarning;
    private readonly CancellationTokenSource _stop = new();
    private Phase _phase;                    // guarded by _gate
    private bool _startingPending;           // guarded by _gate once the loop runs: the Starting write has not succeeded yet
    private RealmRuntimeState _written;      // guarded by _gate: the state last written
    private DateTime _lastBeatUtc;           // guarded by _gate: the last time a beat vouched for the world
    private DateTime _lastAttemptUtc;        // guarded by _gate: the last beat decision, written or withheld
    private long _lastTicks;                 // guarded by _gate: TicksStarted at the last written beat
    private int _desiredCountdown;           // written by the world thread, read by the loop: 0 none, 1 restart, 2 shutdown
    private volatile bool _listenerBound;
    private int _warnedMissing;
    private Task _loop = Task.CompletedTask;
    private CancellationTokenRegistration _startedRegistration;
    private ServerLifecycleFeature? _lifecycle;
    private Action<ShutdownMask, bool>? _countdownHandler;
    private int _stopped;

    public RealmLivenessService(IServiceProvider services, ILogger<RealmLivenessService> logger)
        : this(
            services,
            logger,
            services?.GetService<IOptions<RealmStatusOptions>>()?.Value ?? new RealmStatusOptions(),
            services?.GetService<TimeProvider>() ?? TimeProvider.System,
            WorldTicks(services))
    {
    }

    /// <summary>Test seam: the clock and the world's tick counter are supplied.</summary>
    internal RealmLivenessService(IServiceProvider services, ILogger logger, RealmStatusOptions options, TimeProvider time, Func<long> ticksStarted)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _ticksStarted = ticksStarted ?? throw new ArgumentNullException(nameof(ticksStarted));
        _interval = TimeSpan.FromSeconds(Math.Max(1, options.HeartbeatIntervalSeconds));
        _wake = _interval < TimeSpan.FromSeconds(1) ? _interval : TimeSpan.FromSeconds(1);
        long origin = time.GetTimestamp();
        _withheldWarning = new LogGate(TimeSpan.FromMinutes(1), () => (long)time.GetElapsedTime(origin).TotalMilliseconds);
    }

    /// <summary>Whether this world writes its realm-list row (decided at start).</summary>
    public bool Enabled { get; private set; }

    /// <summary>Test seam: false leaves the timer loop off, so a test drives <see cref="WakeAsync"/> itself.</summary>
    internal bool StartLoop { get; init; } = true;

    /// <summary>The queued first pass (the Starting write). <see cref="StartAsync"/> does not wait for it; tests do.</summary>
    internal Task StartWrite { get; private set; } = Task.CompletedTask;

    /// <summary>The realm-list id this service writes.</summary>
    public int RealmId => _options.Id;

    private static Func<long> WorldTicks(IServiceProvider? services)
    {
        WorldRuntime? world = services?.GetService<WorldRuntime>();
        return world is null ? static () => 0 : () => world.TicksStarted;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!EvaluateGate())
        {
            return;
        }

        Enabled = true;
        _phase = Phase.Starting;
        _startingPending = true;
        _lifecycle = _services.GetService<ServerLifecycleFeature>();
        if (_lifecycle is not null)
        {
            _countdownHandler = OnCountdownChanged;
            _lifecycle.CountdownChanged += _countdownHandler;
        }

        if (_services.GetService<IHostApplicationLifetime>() is { } lifetime)
        {
            _startedRegistration = lifetime.ApplicationStarted.Register(MarkListenerBound);
        }

        // The Starting write goes to the writer, not onto the start path: an unreachable auth database must not hold the world back
        // by the connection timeout. The realm daemon reads a missing or old Starting row as offline anyway.
        StartWrite = Task.Run(() => WakeAsync(_stop.Token), CancellationToken.None);
        if (StartLoop)
        {
            _loop = Task.Run(() => RunLoopAsync(_stop.Token), CancellationToken.None);
        }

        _logger.LogInformation("Realm {RealmId} is starting in the realm list (offline until the world accepts connections); heartbeat every {Seconds} s",
            _options.Id, _interval.TotalSeconds);
    }

    /// <summary>The world accepts connections (<see cref="IHostApplicationLifetime.ApplicationStarted"/>). Public for hosts that bind the listener themselves.</summary>
    public void MarkListenerBound() => _listenerBound = true;

    /// <summary>
    /// A countdown started, changed kind or was cancelled (world thread): record it for the loop, nothing else. The write happens within
    /// one wake interval on another thread.
    /// </summary>
    private void OnCountdownChanged(ShutdownMask mask, bool pending)
        => Volatile.Write(ref _desiredCountdown, !pending ? 0 : mask.HasFlag(ShutdownMask.Restart) ? 1 : 2);

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_wake, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await WakeAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    /// <summary>
    /// One pass of the writer: promote to Online when ready, apply a countdown change, and beat when an interval has passed. Never throws
    /// for a failed write (logged, the next pass tries again). Internal so tests drive it with a fake clock.
    /// </summary>
    internal async Task WakeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_phase is Phase.Idle or Phase.Stopped || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            long ticks = _ticksStarted();
            DateTime now = Now();
            if (_phase == Phase.Starting)
            {
                if (_startingPending)
                {
                    _lastAttemptUtc = now;
                    if (await WriteAsync("starting", store => store.SetStartingAsync(_options.Id, now, CancellationToken.None)).ConfigureAwait(false) is true)
                    {
                        _startingPending = false;
                        _lastBeatUtc = now;
                        _written = RealmRuntimeState.Starting;
                    }

                    return;
                }

                if (_listenerBound && ticks > 0)
                {
                    if (await WriteAsync("online", store => store.SetOnlineAsync(_options.Id, now, CancellationToken.None)).ConfigureAwait(false) is true)
                    {
                        _written = RealmRuntimeState.Online;
                        _phase = Phase.Online;
                        _lastBeatUtc = now;
                        _lastAttemptUtc = now;
                        _lastTicks = ticks;
                        _logger.LogInformation("Realm {RealmId} is online in the realm list", _options.Id);
                    }

                    return;
                }

                if (BeatDue(now))
                {
                    _lastAttemptUtc = now;
                    if (await WriteAsync("starting heartbeat", store => store.BeatAsync(_options.Id, now, CancellationToken.None)).ConfigureAwait(false) is true)
                    {
                        _lastBeatUtc = now; // process liveness only: there is no world loop to vouch for yet
                    }
                }

                return;
            }

            RealmRuntimeState wanted = Volatile.Read(ref _desiredCountdown) switch
            {
                1 => RealmRuntimeState.Restarting,
                2 => RealmRuntimeState.ShuttingDown,
                _ => RealmRuntimeState.Online,
            };
            if (wanted != _written
                && await WriteAsync($"state {wanted}", store => store.SetStateAsync(_options.Id, wanted, _lastBeatUtc, CancellationToken.None)).ConfigureAwait(false) is true)
            {
                // The state write carries the last beat, not now: changing the state is not proof that the loop is ticking.
                _written = wanted;
                _logger.LogInformation("Realm {RealmId} is {State} in the realm list", _options.Id, wanted);
            }

            if (!BeatDue(now))
            {
                return;
            }

            _lastAttemptUtc = now;
            if (ticks == _lastTicks)
            {
                if (_withheldWarning.TryEnter(out int suppressed))
                {
                    _logger.LogWarning(
                        "The world loop has not started a tick since the last heartbeat ({Ticks} ticks): the heartbeat of realm {RealmId} is withheld, and its realm-list row goes offline once the beat is older than Realm:Liveness:HeartbeatStaleSeconds ({Suppressed} more since the last line)",
                        ticks, _options.Id, suppressed);
                }

                return;
            }

            if (await WriteAsync("heartbeat", store => store.BeatAsync(_options.Id, now, CancellationToken.None)).ConfigureAwait(false) is true)
            {
                _lastBeatUtc = now;
                _lastTicks = ticks;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The wake interval is shorter than the beat interval, so half a wake of slack keeps a beat from slipping a whole wake late.</summary>
    private bool BeatDue(DateTime now) => now - _lastAttemptUtc + (_wake / 2) >= _interval;

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    /// <summary>Stop the writer; the clean-stop write happens in <see cref="StopAsync(CancellationToken)"/>. Idempotent.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        if (_lifecycle is not null && _countdownHandler is not null)
        {
            _lifecycle.CountdownChanged -= _countdownHandler;
        }

        _startedRegistration.Dispose();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _phase = Phase.Stopped;
            if (await WriteAsync("stopped", store => store.SetStoppedAsync(_options.Id, CancellationToken.None)).ConfigureAwait(false) is true)
            {
                _written = RealmRuntimeState.Stopped;
                _logger.LogInformation("Realm {RealmId} is offline in the realm list", _options.Id);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _stop.Dispose();
        _gate.Dispose();
    }

    private bool EvaluateGate()
    {
        if (_options.Id <= 0)
        {
            _logger.LogInformation("World:Realm:Id is 0: this world does not mark its realm online or offline in the realm list");
            return false;
        }

        if (_services.GetService<IOptions<GatewayWorkerOptions>>()?.Value is { Enabled: true })
        {
            _logger.LogInformation("Gateway worker mode: the realm-list row of realm {RealmId} is not written by this world", _options.Id);
            return false;
        }

        if (_services.GetService<IServiceScopeFactory>() is not { } scopes)
        {
            return false;
        }

        using IServiceScope probe = scopes.CreateScope();
        return probe.ServiceProvider.GetService<IRealmStatusStore>() is not null; // no auth database in this host otherwise
    }

    /// <summary>Runs one store call in its own scope; null when it failed (logged), else whether the realm row exists.</summary>
    private async Task<bool?> WriteAsync(string what, Func<IRealmStatusStore, Task<bool>> write)
    {
        try
        {
            await using AsyncServiceScope scope = _services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            bool found = await write(scope.ServiceProvider.GetRequiredService<IRealmStatusStore>()).ConfigureAwait(false);
            if (!found && Interlocked.Exchange(ref _warnedMissing, 1) == 0)
            {
                // vmangos refuses to start on a RealmID with no realmlist row (Master.cpp:159-164); here the world runs and says so.
                _logger.LogWarning("Realm {RealmId} has no row in the realm list, so its {What} state was not written; set World:Realm:Id to the realm's id", _options.Id, what);
            }

            return found;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("realm.liveness-write", ex, "Writing the realm-list {What} of realm {RealmId} failed; the next pass tries again", what, _options.Id);
            return null;
        }
    }
}

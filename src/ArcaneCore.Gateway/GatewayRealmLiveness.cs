using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.RealmList;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Gateway;

/// <summary>
/// The realm-list row writer of a clustered realm (docs/ops/realm-liveness.md section 7). In worker mode no world writes the row
/// (<c>RealmLivenessService</c> stays off there: an active and a standby world share the realm), so the gateway, which owns the client-facing
/// address and sees every worker link, writes it from the <see cref="WorkerStatusReport"/> frames the workers send:
/// <list type="bullet">
/// <item><b>Online</b>: some active (non-standby) worker is ready and its tick counter advanced within <see cref="StaleAfter"/>. A standby worker's ticks
/// and sessions never count, so a standby alone leaves the realm Offline.</item>
/// <item><b>Restarting / ShuttingDown</b>: an alive active worker announces a restart (shutdown) countdown; restart wins.</item>
/// <item><b>Starting</b>: no worker is alive yet, but an active worker is linked and has not ticked, or the gateway itself started less than
/// <see cref="StaleAfter"/> ago (the row says Starting from the first pass, before any worker reported).</item>
/// <item><b>Offline</b> (Offline flag, <see cref="RealmRuntimeState.Stopped"/>): no active worker has ticked for <see cref="StaleAfter"/>, and the
/// gateway is past its start-up grace. A worker that ticks again brings the realm Online by itself.</item>
/// <item><b>Heartbeat</b>: written every <see cref="BeatInterval"/> while the state is a running one and some active worker advanced its ticks since the
/// last beat (a hung world stops beating, as in a combined-mode world), and while Starting.</item>
/// <item><b>Population</b>: 2 x (sessions summed over the alive active workers) / player limit, written when it changes; the limit is the largest one the
/// alive workers report (they share one realm), none when every limit is 0.</item>
/// <item><b>Clean stop</b>: Offline and Stopped.</item>
/// </list>
/// Threading: <see cref="OnWorkerStatus"/> is called from each link's receive loop and only records under a short lock; every database call runs on the
/// writer's thread-pool thread, one pass at a time (<c>_gate</c>). A failed write is logged and the next pass tries again. Off when the realm id is 0
/// or no <see cref="IRealmStatusStore"/> is registered.
/// </summary>
public sealed class GatewayRealmLiveness : IHostedService, IDisposable
{
    /// <summary>A worker whose ticks did not advance for this long no longer counts (the logon daemon's default HeartbeatStaleSeconds is the same 60 s).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    /// <summary>Time between heartbeats while the realm is up (the World:Realm:HeartbeatIntervalSeconds default).</summary>
    public static readonly TimeSpan BeatInterval = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Wake = TimeSpan.FromSeconds(1);

    private enum Written { None, Starting, Online, Restarting, ShuttingDown, Offline }

    private sealed class WorkerRecord
    {
        public WorkerStatusReport Report;
        public DateTime SeenUtc;
        public DateTime AdvancedUtc;      // the last time the tick counter of an active report changed (or the worker became active with ticks)
        public bool HasReport;
    }

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _logger;
    private readonly int _realmId;
    private readonly TimeProvider _time;
    private readonly Lock _reports = new();
    private readonly Dictionary<string, WorkerRecord> _workers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private long _advanceSeq;                  // guarded by _reports: bumped whenever an active worker's ticks advanced
    private bool _running;                     // guarded by _gate: StartAsync ran and StopAsync did not
    private DateTime _startedUtc;
    private Written _written;                  // guarded by _gate
    private DateTime _lastBeatUtc;             // guarded by _gate
    private DateTime _lastAttemptUtc;          // guarded by _gate
    private long _beatSeq;                     // guarded by _gate: _advanceSeq at the last beat
    private float? _writtenPopulation;         // guarded by _gate: the population last written since the realm went up
    private Task _loop = Task.CompletedTask;
    private int _warnedMissing;
    private int _stopped;

    public GatewayRealmLiveness(IServiceScopeFactory scopes, IOptions<GatewayOptions> options, ILogger<GatewayRealmLiveness> logger)
        : this(scopes, options?.Value.RealmId ?? 0, logger, TimeProvider.System)
    {
    }

    /// <summary>Test seam: the realm id and the clock are supplied.</summary>
    internal GatewayRealmLiveness(IServiceScopeFactory scopes, int realmId, ILogger logger, TimeProvider time)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _realmId = realmId;
    }

    /// <summary>Whether this gateway writes the realm-list row (decided at start).</summary>
    public bool Enabled { get; private set; }

    /// <summary>Test seam: false leaves the timer loop off, so a test drives <see cref="WakeAsync"/> itself.</summary>
    internal bool StartLoop { get; init; } = true;

    /// <summary>
    /// A worker's liveness report (the link's receive loop; never blocks, never throws). The tick counter counts as advancing when it differs from the
    /// previous report of the same worker (a restarted worker starts from a lower number), or when a worker first reports as active with ticks.
    /// </summary>
    public void OnWorkerStatus(string nodeId, WorkerStatusReport report)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        DateTime now = Now();
        lock (_reports)
        {
            if (!_workers.TryGetValue(nodeId, out WorkerRecord? record))
            {
                record = new WorkerRecord();
                _workers[nodeId] = record;
            }

            bool wasActive = record.HasReport && !record.Report.Standby;
            if (!report.Standby && report.TicksStarted > 0 && (!wasActive || report.TicksStarted != record.Report.TicksStarted))
            {
                record.AdvancedUtc = now;
                _advanceSeq++;
            }

            record.Report = report;
            record.SeenUtc = now;
            record.HasReport = true;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_realmId <= 0)
        {
            _logger.LogInformation("Gateway:RealmId is {RealmId}: the gateway does not write the realm-list row", _realmId);
            return Task.CompletedTask;
        }

        using (IServiceScope probe = _scopes.CreateScope())
        {
            if (probe.ServiceProvider.GetService<IRealmStatusStore>() is null)
            {
                return Task.CompletedTask; // no auth database in this host
            }
        }

        Enabled = true;
        _startedUtc = Now();
        _running = true;
        if (StartLoop)
        {
            _loop = Task.Run(() => RunLoopAsync(_stop.Token), CancellationToken.None);
        }

        _logger.LogInformation("The gateway owns the realm-list row of realm {RealmId}: starting until a worker ticks, offline after {Seconds} s without an active worker's tick",
            _realmId, StaleAfter.TotalSeconds);
        return Task.CompletedTask;
    }

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Wake, _time);
        try
        {
            await WakeAsync(stoppingToken).ConfigureAwait(false);
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

    private readonly record struct Verdict(Written State, int Sessions, int PlayerLimit, long AdvanceSeq);

    /// <summary>Decide the row's state from the reports at <paramref name="now"/>.</summary>
    private Verdict Decide(DateTime now)
    {
        bool restarting = false;
        bool shuttingDown = false;
        bool anyAlive = false;
        bool starting = false;
        int sessions = 0;
        int limit = 0;
        long seq;
        lock (_reports)
        {
            seq = _advanceSeq;
            foreach (WorkerRecord record in _workers.Values)
            {
                if (!record.HasReport || record.Report.Standby)
                {
                    continue;
                }

                if (record.Report.TicksStarted > 0)
                {
                    if (now - record.AdvancedUtc >= StaleAfter)
                    {
                        continue; // ticks stopped: this worker does not vouch for the realm (a hung loop, or a link that went silent)
                    }

                    anyAlive = true;
                    restarting |= record.Report.Restarting;
                    shuttingDown |= record.Report.ShuttingDown;
                    sessions += record.Report.Sessions;
                    limit = Math.Max(limit, record.Report.PlayerLimit);
                }
                else if (now - record.SeenUtc < StaleAfter)
                {
                    starting = true; // linked and reporting, the world loop has not ticked yet
                }
            }
        }

        Written state = anyAlive
            ? (restarting ? Written.Restarting : shuttingDown ? Written.ShuttingDown : Written.Online)
            : (starting || now - _startedUtc < StaleAfter) ? Written.Starting : Written.Offline;
        return new Verdict(state, sessions, limit, seq);
    }

    private static bool IsUp(Written state) => state is Written.Online or Written.Restarting or Written.ShuttingDown;

    private static RealmRuntimeState Runtime(Written state) => state switch
    {
        Written.Restarting => RealmRuntimeState.Restarting,
        Written.ShuttingDown => RealmRuntimeState.ShuttingDown,
        _ => RealmRuntimeState.Online,
    };

    /// <summary>One pass of the writer. Never throws for a failed write (logged, the next pass tries again). Internal so tests drive it with a fake clock.</summary>
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
            if (!_running || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            DateTime now = Now();
            Verdict verdict = Decide(now);
            if (verdict.State == Written.Offline)
            {
                if (_written != Written.Offline && await WriteAsync("offline", store => store.SetStoppedAsync(_realmId, CancellationToken.None)).ConfigureAwait(false) is true)
                {
                    _written = Written.Offline;
                    _writtenPopulation = null;
                    _logger.LogWarning("Realm {RealmId} is offline in the realm list: no active worker has ticked for {Seconds} s", _realmId, StaleAfter.TotalSeconds);
                }

                return;
            }

            if (verdict.State == Written.Starting)
            {
                if (_written != Written.Starting)
                {
                    if (await WriteAsync("starting", store => store.SetStartingAsync(_realmId, now, CancellationToken.None)).ConfigureAwait(false) is true)
                    {
                        _written = Written.Starting;
                        _writtenPopulation = null;
                        _lastBeatUtc = now;
                        _lastAttemptUtc = now;
                    }

                    return;
                }

                if (BeatDue(now) && await WriteAsync("starting heartbeat", store => store.BeatAsync(_realmId, now, CancellationToken.None)).ConfigureAwait(false) is true)
                {
                    _lastBeatUtc = now; // process liveness only: no worker loop vouches for the realm yet
                    _lastAttemptUtc = now;
                }

                return;
            }

            if (!IsUp(_written))
            {
                // The Online write clears Offline and resets the population to 0; the state write that may follow carries the beat just written.
                if (await WriteAsync("online", store => store.SetOnlineAsync(_realmId, now, CancellationToken.None)).ConfigureAwait(false) is not true)
                {
                    return;
                }

                _written = Written.Online;
                _writtenPopulation = 0f;
                _lastBeatUtc = now;
                _lastAttemptUtc = now;
                _beatSeq = verdict.AdvanceSeq;
                _logger.LogInformation("Realm {RealmId} is online in the realm list", _realmId);
            }

            if (_written != verdict.State)
            {
                RealmRuntimeState runtime = Runtime(verdict.State);
                if (await WriteAsync($"state {runtime}", store => store.SetStateAsync(_realmId, runtime, _lastBeatUtc, CancellationToken.None)).ConfigureAwait(false) is true)
                {
                    _written = verdict.State; // the state write carries the last beat, not now: changing the state is not proof that a loop is ticking
                    _logger.LogInformation("Realm {RealmId} is {State} in the realm list", _realmId, runtime);
                }
            }

            if (verdict.AdvanceSeq != _beatSeq && BeatDue(now)
                && await WriteAsync("heartbeat", store => store.BeatAsync(_realmId, now, CancellationToken.None)).ConfigureAwait(false) is true)
            {
                _lastBeatUtc = now;
                _lastAttemptUtc = now;
                _beatSeq = verdict.AdvanceSeq;
            }

            if (verdict.PlayerLimit > 0)
            {
                float population = RealmStatusFeature.Population(verdict.Sessions, verdict.PlayerLimit);
                if (_writtenPopulation != population
                    && await WriteAsync("population", store => store.SetPopulationAsync(_realmId, population, CancellationToken.None)).ConfigureAwait(false) is true)
                {
                    _writtenPopulation = population;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Half a wake of slack keeps a beat from slipping a whole wake late.</summary>
    private bool BeatDue(DateTime now) => now - _lastAttemptUtc + (Wake / 2) >= BeatInterval;

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    /// <summary>Stop the writer, then write Offline and Stopped. Idempotent.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _running = false;
            if (await WriteAsync("stopped", store => store.SetStoppedAsync(_realmId, CancellationToken.None)).ConfigureAwait(false) is true)
            {
                _written = Written.Offline;
                _logger.LogInformation("Realm {RealmId} is offline in the realm list", _realmId);
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

    /// <summary>Runs one store call in its own scope; null when it failed (logged), else whether the realm row exists.</summary>
    private async Task<bool?> WriteAsync(string what, Func<IRealmStatusStore, Task<bool>> write)
    {
        try
        {
            await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
            bool found = await write(scope.ServiceProvider.GetRequiredService<IRealmStatusStore>()).ConfigureAwait(false);
            if (!found && Interlocked.Exchange(ref _warnedMissing, 1) == 0)
            {
                _logger.LogWarning("Realm {RealmId} has no row in the realm list, so its {What} state was not written; set Gateway:RealmId to the realm's id", _realmId, what);
            }

            return found;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("realm.liveness-write", ex, "Writing the realm-list {What} of realm {RealmId} failed; the next pass tries again", what, _realmId);
            return null;
        }
    }
}

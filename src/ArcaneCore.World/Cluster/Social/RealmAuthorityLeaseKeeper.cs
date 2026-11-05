using System.Data.Common;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Cluster.Gateway;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>A realm singleton that can be switched off for good when the node loses its lease (called once, from a thread-pool thread, so it must be thread-safe and must not block).</summary>
public interface IRealmSingletonHalt
{
    /// <summary>Stop the singleton's work until the process restarts.</summary>
    void Halt();
}

/// <summary>
/// Holds this node's realm-singleton leases (the social, economy and instances scopes, plus the maintenance and worldstate scopes of the realm sweeps) for a <see cref="ClusterNodeRole.MapWorker"/>: takes them at start (and the run-time scopes again later, see below),
/// renews them on a timer, and gives them back at stop. The realm singletons (AutoBroadcast, the auction house bot, and the instance reset schedule, whose durable writes are made by the Instances holder only) cannot be detached from a running
/// world, so losing a lease is fail-stop: the keeper logs it, records a fault, and asks the host to stop with exit code
/// <see cref="ExitCodes.Failure"/>. The owner id is <c>Cluster:Worker:NodeId</c>. On loss every registered <see cref="IRealmSingletonHalt"/> is halted first (each guarded), so the singletons stop ticking while the host winds down. Storage writes are checked against the fencing
/// token at commit as well: the keeper is the <see cref="IRealmLeaseFenceSource"/> of the economy, social and instance stores (<see cref="LeaseFencedWrite"/>), so a partitioned
/// former holder whose lease was taken over is refused by the database even before this stop runs.
/// <para>Renewal starts as soon as the first lease is granted (inside <see cref="AcquireAtStartAsync"/>), so a slow start cannot outlast a lease.
/// Every timer tick also judges the deadline on its own, so a renewal call that hangs in the store cannot hide an expiring lease: the node stops
/// when the next tick would fall in the last half renewal interval before the lease lapses.</para>
/// <para>Takeover: a scope another node held at start is not given up on, but only for the scopes whose work checks the lease at run time (<see cref="TakeoverScopes"/>: instances, maintenance, worldstate; the sweeps and durable
/// writes ask <see cref="Holds"/> on every tick). When that node stops or dies and its lease lapses, a surviving worker takes the scope over with a new fencing token instead of leaving it ownerless until a restart. The social and economy
/// scopes are never taken at run time: their singletons (AutoBroadcast, the auction house bot) are attached only at start, so a survivor taking the lease would hold it with no feature to run and keep the restarting node from attaching them;
/// those two leases are taken at start only, so the restarting node takes them back as it always did. A takeover round never runs in a tick where a renewal failed, and it runs under its own guard after the renewal round, so
/// store time spent on it can never delay the next renewal.</para>
/// <para>Thread affinity: <see cref="AcquireAtStartAsync"/> and <see cref="ReleaseAllAsync"/> run on the host's start and stop; the renewal timer
/// runs on the thread pool, and its ticks may overlap a still-running store call. The lease store is called synchronously off the world thread.
/// All state is guarded by one lock.</para>
/// </summary>
public sealed class RealmAuthorityLeaseKeeper : IAsyncDisposable, IRealmLeaseFenceSource
{
    /// <summary>
    /// How many times <see cref="AcquireAtStartAsync"/> asks the store for one scope before a store failure ends the start. On PostgreSQL a takeover waits (<c>FOR UPDATE</c>, bounded by the command timeout, a third of the renewal
    /// interval and at least a second) for the in-flight fenced writers of the lapsed holder's scope; a steady stream of them can keep it waiting past that timeout, and without a retry the new node would fail its start on a lock wait
    /// that the next attempt would not hit. Only a store or timeout failure is retried (<see cref="DbException"/>, <see cref="TimeoutException"/>); a refusal is an answer and is not.
    /// </summary>
    public const int StartAcquireAttempts = 4;

    /// <summary>The pause between two start attempts of <see cref="StartAcquireAttempts"/>; the whole bounded retry adds at most (attempts - 1) times this to the start, on top of the command timeouts.</summary>
    public static readonly TimeSpan StartAcquireRetryDelay = TimeSpan.FromSeconds(1);

    private static readonly string[] Scopes = [RealmAuthorityScopes.Social, RealmAuthorityScopes.Economy];
    private static readonly string[] MapWorkerScopes =
    [RealmAuthorityScopes.Social, RealmAuthorityScopes.Economy, RealmAuthorityScopes.Instances, RealmAuthorityScopes.Maintenance, RealmAuthorityScopes.WorldState];

    /// <summary>
    /// The scopes added after the realm authority lease table was released (schema 72 seeds their rows through an EnsureRowChange, not HasData). A database made by
    /// EnsureCreated has no row for them: the keeper then runs without that lease (this node is not the holder) instead of failing the start.
    /// </summary>
    private static readonly string[] OptionalScopes = [RealmAuthorityScopes.Maintenance, RealmAuthorityScopes.WorldState];

    /// <summary>The scopes that may be taken over after start: those whose work asks <see cref="Holds"/> at run time. Social and Economy are excluded, their features attach only at start (see the class summary). Only a map worker takes any.</summary>
    private string[] TakeoverScopes => _node.Role == ClusterNodeRole.MapWorker ? RuntimeScopes : [];

    private static readonly string[] RuntimeScopes = [RealmAuthorityScopes.Instances, RealmAuthorityScopes.Maintenance, RealmAuthorityScopes.WorldState];

    /// <summary>The scopes this node competes for: the Instances lease only on a <see cref="ClusterNodeRole.MapWorker"/> (the only non-combined role that attaches the instance feature), so a Gateway, Social or Economy node never holds it and orphans the schedule.</summary>
    private string[] ScopesToTake => _node.Role == ClusterNodeRole.MapWorker ? MapWorkerScopes : Scopes;

    private sealed class Held(long token, DateTimeOffset expiresAtUtc)
    {
        public long Token { get; } = token;
        public DateTimeOffset ExpiresAtUtc { get; set; } = expiresAtUtc;
    }

    private readonly IRealmAuthorityLeaseStore _store;
    private readonly ClusterNodeOptions _node;
    private readonly string _nodeId;
    private readonly ILogger _logger;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly TimeProvider _time;
    private readonly Action<int> _setExitCode;
    private readonly IRealmSingletonHalt[] _halts;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Held> _held = new(StringComparer.Ordinal);
    private readonly HashSet<string> _noRow = new(StringComparer.Ordinal); // optional scopes the database has no lease row for: never retried
    private Guid _incarnation;
    private ITimer? _timer;
    private bool _renewing;
    private bool _takingOver; // a takeover pass is running (its own guard: never part of the renewing round)
    private bool _lost;
    private bool _released;
    private bool _started; // AcquireAtStartAsync finished: from then on the renewal tick may take over scopes (never while the start is still acquiring them in order)

    public RealmAuthorityLeaseKeeper(
        IRealmAuthorityLeaseStore store,
        IOptions<ClusterNodeOptions> nodeOptions,
        IOptions<GatewayWorkerOptions> workerOptions,
        ILogger<RealmAuthorityLeaseKeeper> logger,
        IHostApplicationLifetime? lifetime = null,
        TimeProvider? time = null,
        Action<int>? setExitCode = null,
        IEnumerable<IRealmSingletonHalt>? halts = null)
    {
        _halts = halts is null ? [] : [.. halts];
        _store = store;
        _node = nodeOptions.Value;
        _nodeId = workerOptions.Value.NodeId;
        // A malformed list never reaches here (the world refuses to start on it); fall back to every map, the unrestricted behaviour.
        OwnedMaps = workerOptions.Value.TryGetOwnership(out ArcaneCore.Game.Maps.MapOwnership ownership, out _) && !ownership.OwnsEverything
            ? new OwnedMapSet(ownership.Ranges.Select(r => (r.First, r.Last)), ownership.AllowInstanceMaps)
            : OwnedMapSet.All;
        _logger = logger;
        _lifetime = lifetime;
        _time = time ?? TimeProvider.System;
        _setExitCode = setExitCode ?? (code => ExitCodes.Current = code); // the process exit code unless a test supplies its own sink
    }

    /// <summary>The clock the leases are judged against (UTC).</summary>
    public DateTimeOffset UtcNow => _time.GetUtcNow();

    /// <summary>The owner id this keeper leases under (<c>Cluster:Worker:NodeId</c>).</summary>
    public string NodeId => _nodeId;

    /// <summary>The maps this worker owns (<c>Cluster:Worker:OwnedMaps</c>); every map when the list is empty. The stores limit their per-map sweeps to it.</summary>
    public OwnedMapSet OwnedMaps { get; }

    /// <summary>The fencing token this node holds for <paramref name="scope"/>, or null when it does not hold the lease.</summary>
    public long? TokenFor(string scope)
    {
        lock (_gate)
        {
            return _held.TryGetValue(scope, out Held? held) ? held.Token : null;
        }
    }

    /// <summary>
    /// Whether this node is the live holder of <paramref name="scope"/>: it took the lease at start, has not given it back, and has not lost it (a lost lease keeps its old token in
    /// <see cref="TokenFor"/> and <see cref="FenceFor"/>, but the node is no longer the holder). The answer behind <see cref="LeaseFencedWrite.RequireHeld"/> and <see cref="LeaseFencedWrite.IsHolder"/>: realm sweeps and every
    /// write with no commit-time check of the lease row refuse on it. Stores that verify the row at commit (<see cref="LeaseFencedWrite.RequireFence"/>) ask <see cref="FenceFor"/> instead and let the commit fence decide.
    /// Cheap; safe from any thread.
    /// </summary>
    public bool Holds(string scope)
    {
        lock (_gate)
        {
            return !_lost && !_released && _held.ContainsKey(scope);
        }
    }

    /// <summary>
    /// <see cref="Holds"/> for a feature that may run with no keeper: true when <paramref name="keeper"/> is null (Combined, tests: nothing is clustered, this process is the only writer),
    /// otherwise the keeper's answer. The gate every realm sweep and singleton write of the World layer calls.
    /// </summary>
    public static bool HeldOrUnclustered(RealmAuthorityLeaseKeeper? keeper, string scope) => keeper is null || keeper.Holds(scope);

    /// <summary>The lease this node holds for <paramref name="scope"/> as a commit fence, or null when it holds none (also after a graceful release). A lease marked lost (a takeover, or a renewal round that ran too long) keeps its old token here: a store that verifies the lease row at commit then lets the commit fence decide (refused exactly when another node took the lease over), while stores with no commit check refuse on <see cref="Holds"/>.</summary>
    public RealmLeaseFence? FenceFor(string scope)
    {
        lock (_gate)
        {
            return _held.TryGetValue(scope, out Held? held) ? new RealmLeaseFence(scope, _nodeId, _incarnation, held.Token) : null;
        }
    }

    /// <summary>
    /// Try to take each scope's lease for <paramref name="incarnation"/>. A scope held by another node is skipped (that node runs the singleton). A scope still
    /// held under this node's own id by a previous incarnation (a crash that did not release) is waited out up to its expiry, then retried once.
    /// </summary>
    public async Task AcquireAtStartAsync(Guid incarnation, CancellationToken cancellationToken)
    {
        _incarnation = incarnation;
        foreach (string scope in ScopesToTake)
        {
            AuthorityLeaseResult? first = await TryAcquireOrSkipAsync(scope, cancellationToken).ConfigureAwait(false);
            if (first is null)
            {
                continue;
            }

            AuthorityLeaseResult result = first;
            if (result.Outcome == AuthorityLeaseOutcome.RejectedHeldByOther
                && result.Lease is { } holder
                && string.Equals(holder.OwnerNodeId, _nodeId, StringComparison.Ordinal))
            {
                TimeSpan wait = holder.ExpiresAtUtc - _time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    _logger.LogInformation("The {Scope} lease is held by a previous incarnation of this node; waiting {Seconds:F0} s for it to expire", scope, wait.TotalSeconds);
                }

                // A timer counts whole milliseconds off a tick clock that can be coarser than the wall clock (CLOCK_MONOTONIC_COARSE on Linux), so
                // a delay can end a millisecond or more before the lease lapses, and the store would still see the lease as live. Wait until the
                // clock itself has passed the expiry.
                while (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
                    wait = holder.ExpiresAtUtc - _time.GetUtcNow();
                }

                result = await TryAcquireWithRetryAsync(scope, cancellationToken).ConfigureAwait(false);
            }

            if (result.Outcome is AuthorityLeaseOutcome.Granted or AuthorityLeaseOutcome.Renewed && result.Lease is { } lease)
            {
                lock (_gate)
                {
                    _held[scope] = new Held(lease.FencingToken, lease.ExpiresAtUtc);
                }

                StartRenewing(); // from the first grant on: the rest of the start (a wait for another scope, the attach) may take longer than a lease lives

                _logger.LogInformation("Holding the {Scope} lease (fencing token {Token}) as {Node}", scope, lease.FencingToken, _nodeId);
            }
            else
            {
                _logger.LogInformation(
                    "Not holding the {Scope} lease: held by {Holder} (fencing token {Token}); the matching realm singleton does not run on this node unless it takes the lease over later",
                    scope, result.Lease?.OwnerNodeId ?? "another node", result.Lease?.FencingToken ?? 0);
            }
        }

        lock (_gate)
        {
            _started = true;
        }

        StartRenewing(); // also with no lease held: the same timer retries the scopes another node held (takeover)
    }

    // Null when an optional scope has no lease row (ArgumentException from the durable store, see OptionalScopes); every other failure propagates.
    private async Task<AuthorityLeaseResult?> TryAcquireOrSkipAsync(string scope, CancellationToken cancellationToken)
    {
        try
        {
            return await TryAcquireWithRetryAsync(scope, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex) when (ex.ParamName == "scope" && Array.IndexOf(OptionalScopes, scope) >= 0) // the store's UnknownScope: the scope parameter names the missing row
        {
            _logger.LogWarning(ex, "The {Scope} lease row does not exist in the characters database (schema 72 not applied); this node does not hold it, so the matching realm sweeps do not run here", scope);
            lock (_gate)
            {
                _noRow.Add(scope);
            }

            return null;
        }
    }

    // The start's acquire: a bounded number of attempts (StartAcquireAttempts) across store and timeout failures, so a lock wait that outlasts one command timeout does not fail the node's start.
    private async Task<AuthorityLeaseResult> TryAcquireWithRetryAsync(string scope, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await TryAcquireAsync(scope).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < StartAcquireAttempts && ex is DbException or TimeoutException)
            {
                _logger.LogWarning(ex, "Taking the {Scope} lease at start failed (attempt {Attempt} of {Attempts}); retrying in {Seconds:F0} s", scope, attempt, StartAcquireAttempts, StartAcquireRetryDelay.TotalSeconds);
                await Task.Delay(StartAcquireRetryDelay, _time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Task<AuthorityLeaseResult> TryAcquireAsync(string scope)
        => Task.Run(() => _store.TryAcquire(scope, _nodeId, _incarnation, _node.LeaseTtl, _time.GetUtcNow()));

    private void StartRenewing()
    {
        lock (_gate)
        {
            if (_timer is not null || _lost || _released)
            {
                return;
            }

            _timer = _time.CreateTimer(_ => RenewAll(), null, _node.LeaseRenew, _node.LeaseRenew);
        }
    }

    private bool NearDeadline(DateTimeOffset now, DateTimeOffset expiresAtUtc)
        => now + _node.LeaseRenew >= expiresAtUtc - (_node.LeaseRenew / 2);

    private void RenewAll()
    {
        KeyValuePair<string, Held>[] held;
        (string Scope, string Why)? doomed = null;
        lock (_gate)
        {
            if (_lost || _released)
            {
                return;
            }

            if (_renewing)
            {
                // The previous tick's store call is still running, so it has renewed nothing. Judge the deadline here instead of waiting for it.
                DateTimeOffset now = _time.GetUtcNow();
                foreach ((string scope, Held current) in _held)
                {
                    if (NearDeadline(now, current.ExpiresAtUtc))
                    {
                        doomed = (scope, "a renewal is still waiting on the store and the lease is about to lapse");
                        break;
                    }
                }

                if (doomed is null)
                {
                    return;
                }
            }
            else
            {
                _renewing = true;
            }

            held = [.. _held];
        }

        if (doomed is { } hung)
        {
            Lose(hung.Scope, hung.Why);
            return;
        }

        bool allRenewed = false;
        try
        {
            bool failed = false;
            foreach ((string scope, Held current) in held)
            {
                lock (_gate)
                {
                    if (_lost || _released)
                    {
                        return; // an orderly release (or a loss) began while an earlier scope was in the store: do not start a new renewal (a racing one is refused by the store)
                    }
                }

                DateTimeOffset started = _time.GetUtcNow();
                try
                {
                    AuthorityLeaseResult result = _store.TryRenew(scope, _nodeId, _incarnation, current.Token, _node.LeaseTtl, started);
                    if (result.Outcome == AuthorityLeaseOutcome.Renewed && result.Lease is { } renewed)
                    {
                        lock (_gate)
                        {
                            current.ExpiresAtUtc = renewed.ExpiresAtUtc;
                        }

                        continue;
                    }

                    Lose(scope, $"the store refused the renewal ({result.Outcome}, holder {result.Lease?.OwnerNodeId ?? "none"})");
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // The store could not be reached. Keep going while the lease is still good after the next attempt, but decide before the lease can lapse:
                    // if the next tick would land in the last half interval before expiry (or after it), this is the last chance to stop cleanly.
                    // The clock is read again: the failed call may have taken most of the interval.
                    DateTimeOffset now = _time.GetUtcNow();
                    if (NearDeadline(now, current.ExpiresAtUtc))
                    {
                        Lose(scope, $"the store failed ({ex.GetType().Name}: {ex.Message}) and the lease is about to lapse");
                        return;
                    }

                    failed = true;
                    _logger.LogWarning(ex, "Renewing the {Scope} lease failed; retrying in {Seconds} s (lease valid until {Expires:O})", scope, _node.AuthorityLeaseRenewSeconds, current.ExpiresAtUtc);
                }
            }

            allRenewed = !failed;
        }
        finally
        {
            lock (_gate)
            {
                _renewing = false;
            }
        }

        // Only after a round in which every renewal succeeded (a failing store is not asked for more), and outside the renewing guard: the next tick renews on time however long this takes.
        if (allRenewed)
        {
            lock (_gate)
            {
                if (_takingOver || _lost || _released)
                {
                    return;
                }

                _takingOver = true;
            }

            try
            {
                TakeOverFreeScopes();
            }
            finally
            {
                lock (_gate)
                {
                    _takingOver = false;
                }
            }
        }
    }

    /// <summary>
    /// Tries to acquire each scope this node does not hold. The store refuses while another node's lease is live (<see cref="AuthorityLeaseOutcome.RejectedHeldByOther"/>, nothing changes), and grants it with a higher
    /// fencing token once that lease has lapsed. A store failure is logged and retried on the next tick. Runs on the renewal tick after a round in which every held lease was renewed, under its own guard (a renewal round may overlap it, the scopes differ: a scope in this pass is not held yet).
    /// </summary>
    private void TakeOverFreeScopes()
    {
        foreach (string scope in TakeoverScopes)
        {
            lock (_gate)
            {
                if (_lost || _released || !_started)
                {
                    return;
                }

                if (_held.ContainsKey(scope) || _noRow.Contains(scope))
                {
                    continue;
                }
            }

            AuthorityLeaseResult result;
            try
            {
                result = _store.TryAcquire(scope, _nodeId, _incarnation, _node.LeaseTtl, _time.GetUtcNow());
            }
            catch (ArgumentException ex) when (ex.ParamName == "scope" && Array.IndexOf(OptionalScopes, scope) >= 0)
            {
                lock (_gate)
                {
                    _noRow.Add(scope);
                }

                _logger.LogWarning(ex, "The {Scope} lease row does not exist in the characters database; this node will not try to take it over", scope);
                continue;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Trying to take over the {Scope} lease failed; retrying in {Seconds} s", scope, _node.AuthorityLeaseRenewSeconds);
                continue;
            }

            if (result.Outcome is not (AuthorityLeaseOutcome.Granted or AuthorityLeaseOutcome.Renewed) || result.Lease is not { } lease)
            {
                continue; // another node still holds it
            }

            bool keep;
            lock (_gate)
            {
                keep = !_lost && !_released;
                if (keep)
                {
                    _held[scope] = new Held(lease.FencingToken, lease.ExpiresAtUtc);
                }
            }

            if (!keep)
            {
                // An orderly stop (or a loss) began while the store call ran: give the lease straight back instead of holding it until it expires.
                try
                {
                    _store.TryRelease(scope, _nodeId, _incarnation, lease.FencingToken);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogWarning(ex, "Giving back the {Scope} lease taken during the stop failed; it expires on its own within {Seconds} s", scope, _node.AuthorityLeaseTtlSeconds);
                }

                return;
            }

            _logger.LogWarning(
                "Took over the {Scope} lease (fencing token {Token}) as {Node}: its previous holder is gone. Sweeps and durable writes of the scope run here now",
                scope, lease.FencingToken, _nodeId);
        }
    }

    private void Lose(string scope, string why)
    {
        lock (_gate)
        {
            if (_lost || _released)
            {
                // Released: an orderly stop is already underway, so a refusal now (the lease we just gave back) is not a loss.
                return;
            }

            _lost = true;
        }

        string message = $"the {scope} realm authority lease of node {_nodeId} was lost: {why}; stopping, because the singletons it guards cannot be detached";
        _logger.LogCritical("{Message}", message);
        FaultLedger.Shared.Record("cluster.lease", new InvalidOperationException(message));
        _setExitCode(ExitCodes.Failure);
        foreach (IRealmSingletonHalt halt in _halts)
        {
            try
            {
                halt.Halt();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "Halting {Singleton} after the {Scope} lease loss failed", halt.GetType().Name, scope);
            }
        }

        _lifetime?.StopApplication();
    }

    /// <summary>Give back every held lease (the high-water token stays). A failure is logged, never thrown; a second call does nothing.</summary>
    public async Task ReleaseAllAsync()
    {
        KeyValuePair<string, Held>[] held;
        lock (_gate)
        {
            if (_released)
            {
                return;
            }

            _released = true;
            held = [.. _held];
            _held.Clear();
        }

        await StopTimerAsync().ConfigureAwait(false);
        foreach ((string scope, Held current) in held)
        {
            try
            {
                AuthorityLeaseResult result = await Task.Run(() => _store.TryRelease(scope, _nodeId, _incarnation, current.Token)).ConfigureAwait(false);
                if (result.Outcome != AuthorityLeaseOutcome.Released)
                {
                    _logger.LogWarning("The {Scope} lease was not released ({Outcome}); it expires on its own within {Seconds} s", scope, result.Outcome, _node.AuthorityLeaseTtlSeconds);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Releasing the {Scope} lease failed; it expires on its own within {Seconds} s", scope, _node.AuthorityLeaseTtlSeconds);
            }
        }
    }

    private async Task StopTimerAsync()
    {
        ITimer? timer;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
        }

        if (timer is not null)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await StopTimerAsync().ConfigureAwait(false);
}

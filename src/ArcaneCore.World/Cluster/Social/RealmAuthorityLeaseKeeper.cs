using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Cluster.Gateway;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>
/// Holds this node's realm-singleton leases (the social and economy scopes) for a <see cref="ClusterNodeRole.MapWorker"/>: takes them once at start,
/// renews them on a timer, and gives them back at stop. The realm singletons (AutoBroadcast, the auction house bot) cannot be detached from a running
/// world, so losing a lease is fail-stop: the keeper logs it, records a fault, and asks the host to stop with exit code
/// <see cref="ExitCodes.Failure"/>. The owner id is <c>Cluster:Worker:NodeId</c>. Open gap: storage writes are not checked against the fencing
/// token at commit, so a partitioned former holder is bounded by this stop, not by the database.
/// <para>Renewal starts as soon as the first lease is granted (inside <see cref="AcquireAtStartAsync"/>), so a slow start cannot outlast a lease.
/// Every timer tick also judges the deadline on its own, so a renewal call that hangs in the store cannot hide an expiring lease: the node stops
/// when the next tick would fall in the last half renewal interval before the lease lapses.</para>
/// <para>Thread affinity: <see cref="AcquireAtStartAsync"/> and <see cref="ReleaseAllAsync"/> run on the host's start and stop; the renewal timer
/// runs on the thread pool, and its ticks may overlap a still-running store call. The lease store is called synchronously off the world thread.
/// All state is guarded by one lock.</para>
/// </summary>
public sealed class RealmAuthorityLeaseKeeper : IAsyncDisposable
{
    private static readonly string[] Scopes = [RealmAuthorityScopes.Social, RealmAuthorityScopes.Economy];

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
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Held> _held = new(StringComparer.Ordinal);
    private Guid _incarnation;
    private ITimer? _timer;
    private bool _renewing;
    private bool _lost;
    private bool _released;

    public RealmAuthorityLeaseKeeper(
        IRealmAuthorityLeaseStore store,
        IOptions<ClusterNodeOptions> nodeOptions,
        IOptions<GatewayWorkerOptions> workerOptions,
        ILogger<RealmAuthorityLeaseKeeper> logger,
        IHostApplicationLifetime? lifetime = null,
        TimeProvider? time = null,
        Action<int>? setExitCode = null)
    {
        _store = store;
        _node = nodeOptions.Value;
        _nodeId = workerOptions.Value.NodeId;
        _logger = logger;
        _lifetime = lifetime;
        _time = time ?? TimeProvider.System;
        _setExitCode = setExitCode ?? (code => ExitCodes.Current = code); // the process exit code unless a test supplies its own sink
    }

    /// <summary>The clock the leases are judged against (UTC).</summary>
    public DateTimeOffset UtcNow => _time.GetUtcNow();

    /// <summary>The owner id this keeper leases under (<c>Cluster:Worker:NodeId</c>).</summary>
    public string NodeId => _nodeId;

    /// <summary>The fencing token this node holds for <paramref name="scope"/>, or null when it does not hold the lease.</summary>
    public long? TokenFor(string scope)
    {
        lock (_gate)
        {
            return _held.TryGetValue(scope, out Held? held) ? held.Token : null;
        }
    }

    /// <summary>
    /// Try to take each scope's lease for <paramref name="incarnation"/>. A scope held by another node is skipped (that node runs the singleton). A scope still
    /// held under this node's own id by a previous incarnation (a crash that did not release) is waited out up to its expiry, then retried once.
    /// </summary>
    public async Task AcquireAtStartAsync(Guid incarnation, CancellationToken cancellationToken)
    {
        _incarnation = incarnation;
        foreach (string scope in Scopes)
        {
            AuthorityLeaseResult result = await TryAcquireAsync(scope).ConfigureAwait(false);
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

                result = await TryAcquireAsync(scope).ConfigureAwait(false);
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
                    "Not holding the {Scope} lease: held by {Holder} (fencing token {Token}); the matching realm singleton does not run on this node",
                    scope, result.Lease?.OwnerNodeId ?? "another node", result.Lease?.FencingToken ?? 0);
            }
        }
    }

    private Task<AuthorityLeaseResult> TryAcquireAsync(string scope)
        => Task.Run(() => _store.TryAcquire(scope, _nodeId, _incarnation, _node.LeaseTtl, _time.GetUtcNow()));

    private void StartRenewing()
    {
        lock (_gate)
        {
            if (_timer is not null || _held.Count == 0)
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

        try
        {
            foreach ((string scope, Held current) in held)
            {
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

                    _logger.LogWarning(ex, "Renewing the {Scope} lease failed; retrying in {Seconds} s (lease valid until {Expires:O})", scope, _node.AuthorityLeaseRenewSeconds, current.ExpiresAtUtc);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _renewing = false;
            }
        }
    }

    private void Lose(string scope, string why)
    {
        lock (_gate)
        {
            if (_lost)
            {
                return;
            }

            _lost = true;
        }

        string message = $"the {scope} realm authority lease of node {_nodeId} was lost: {why}; stopping, because the singletons it guards cannot be detached";
        _logger.LogCritical("{Message}", message);
        FaultLedger.Shared.Record("cluster.lease", new InvalidOperationException(message));
        _setExitCode(ExitCodes.Failure);
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

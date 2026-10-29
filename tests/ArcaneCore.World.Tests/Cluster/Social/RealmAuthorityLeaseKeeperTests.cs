using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>The node lease keeper: takes the free realm singleton leases at start, renews them, and stops the process when one is lost.</summary>
public sealed class RealmAuthorityLeaseKeeperTests : IDisposable
{
    private const string Node = "worker-a";
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryRealmAuthorityLeaseStore _inner = new();
    private readonly StopCounter _lifetime = new();

    // The keeper sets the process exit code on a loss.
    public void Dispose() => ExitCodes.Current = ExitCodes.Success;

    private RealmAuthorityLeaseKeeper Create(IRealmAuthorityLeaseStore? store = null)
        => new(store ?? _inner,
            Options.Create(new ClusterNodeOptions { Role = ClusterNodeRole.MapWorker, AuthorityLeaseTtlSeconds = 30, AuthorityLeaseRenewSeconds = 10 }),
            Options.Create(new GatewayWorkerOptions { Enabled = true, NodeId = Node }),
            NullLogger<RealmAuthorityLeaseKeeper>.Instance,
            _lifetime,
            _time);

    private static long ClusterLeaseFaults() => FaultLedger.Shared.Snapshot().Where(f => f.Site == "cluster.lease").Sum(f => f.Count);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < end, "the condition was not met in time");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task AcquireAtStart_HoldsFreeScopes_SkipsHeldByOtherNode()
    {
        _inner.TryAcquire(RealmAuthorityScopes.Economy, "worker-b", Guid.NewGuid(), TimeSpan.FromSeconds(30), _time.GetUtcNow());
        await using RealmAuthorityLeaseKeeper keeper = Create();

        await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(1, keeper.TokenFor(RealmAuthorityScopes.Social));
        Assert.Null(keeper.TokenFor(RealmAuthorityScopes.Economy)); // worker-b runs the auction bot
        Assert.True(_inner.ValidateFencingToken(RealmAuthorityScopes.Social, Node, 1, _time.GetUtcNow()));
        Assert.Equal(Node, keeper.NodeId);
    }

    [Fact]
    public async Task AcquireAtStart_SameNodePreviousIncarnation_WaitsForExpiryThenGrants()
    {
        // A previous incarnation of this node crashed without releasing.
        _inner.TryAcquire(RealmAuthorityScopes.Social, Node, Guid.NewGuid(), TimeSpan.FromSeconds(30), _time.GetUtcNow());
        await using RealmAuthorityLeaseKeeper keeper = Create();

        Task acquire = keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);
        await WaitUntilAsync(() => _time.PendingTimers > 0); // sleeping until the old lease lapses
        Assert.False(acquire.IsCompleted);
        Assert.Null(keeper.TokenFor(RealmAuthorityScopes.Social));

        _time.Advance(TimeSpan.FromSeconds(31));
        await acquire.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, keeper.TokenFor(RealmAuthorityScopes.Social)); // strictly above the dead incarnation's token
        Assert.Equal(1, keeper.TokenFor(RealmAuthorityScopes.Economy));
    }

    [Fact]
    public async Task RenewRejected_StopsApplication_AndRecordsFault()
    {
        var store = new ControlledStore(_inner);
        await using RealmAuthorityLeaseKeeper keeper = Create(store);
        await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);
        keeper.StartRenewing();
        long faultsBefore = ClusterLeaseFaults();

        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, _lifetime.Stops); // a healthy renewal stops nothing
        Assert.Equal(2, store.Renewals);

        // Another node took the lease over: this node's renewal is refused.
        store.RejectRenewals = true;
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(1, _lifetime.Stops);
        Assert.Equal(ExitCodes.Failure, ExitCodes.Current);
        Assert.True(ClusterLeaseFaults() > faultsBefore);
    }

    [Fact]
    public async Task RenewThrowing_StopsBeforeExpiry()
    {
        var store = new ControlledStore(_inner);
        await using RealmAuthorityLeaseKeeper keeper = Create(store);
        await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);
        keeper.StartRenewing();
        DateTimeOffset expiry = _time.GetUtcNow().AddSeconds(30);
        store.ThrowOnRenew = true;

        _time.Advance(TimeSpan.FromSeconds(10)); // 20 s left: keep trying
        Assert.Equal(0, _lifetime.Stops);

        _time.Advance(TimeSpan.FromSeconds(10)); // the next attempt would land in the last half interval: decide now, before the lease lapses
        Assert.Equal(1, _lifetime.Stops);
        Assert.True(_time.GetUtcNow() < expiry);
    }

    [Fact]
    public async Task ReleaseAll_ReleasesHeldScopes_OnlyOnce()
    {
        var store = new ControlledStore(_inner);
        await using RealmAuthorityLeaseKeeper keeper = Create(store);
        await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);
        keeper.StartRenewing();

        await keeper.ReleaseAllAsync();
        await keeper.ReleaseAllAsync();

        Assert.Equal(2, store.Releases); // social and economy, once each
        Assert.Null(_inner.GetCurrent(RealmAuthorityScopes.Social, _time.GetUtcNow()));
        Assert.Equal(1, _inner.GetHighWaterToken(RealmAuthorityScopes.Social)); // the high-water token survives
        Assert.Null(keeper.TokenFor(RealmAuthorityScopes.Social));
        _time.Advance(TimeSpan.FromSeconds(30)); // the renewal timer is gone
        Assert.Equal(0, store.Renewals);
    }

    [Fact]
    public async Task ReleaseAll_NeverThrows_WhenTheStoreFails()
    {
        var store = new ControlledStore(_inner) { ThrowOnRelease = true };
        await using RealmAuthorityLeaseKeeper keeper = Create(store);
        await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);

        await keeper.ReleaseAllAsync();

        Assert.Equal(2, store.Releases);
    }

    private sealed class StopCounter : IHostApplicationLifetime
    {
        private int _stops;
        public int Stops => Volatile.Read(ref _stops);
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Interlocked.Increment(ref _stops);
    }

    /// <summary>A store that can be told to refuse or fail renewals and releases, and counts them.</summary>
    private sealed class ControlledStore(InMemoryRealmAuthorityLeaseStore inner) : IRealmAuthorityLeaseStore
    {
        private int _releases;
        private int _renewals;
        public bool RejectRenewals { get; set; }
        public bool ThrowOnRenew { get; set; }
        public bool ThrowOnRelease { get; set; }
        public int Releases => Volatile.Read(ref _releases);
        public int Renewals => Volatile.Read(ref _renewals);

        public AuthorityLeaseResult TryAcquire(string scope, string ownerNodeId, Guid ownerIncarnation, TimeSpan ttl, DateTimeOffset nowUtc)
            => inner.TryAcquire(scope, ownerNodeId, ownerIncarnation, ttl, nowUtc);

        public AuthorityLeaseResult TryRenew(string scope, string ownerNodeId, Guid ownerIncarnation, long fencingToken, TimeSpan ttl, DateTimeOffset nowUtc)
        {
            Interlocked.Increment(ref _renewals);
            if (ThrowOnRenew)
            {
                throw new InvalidOperationException("the characters database is unreachable");
            }

            return RejectRenewals
                ? new AuthorityLeaseResult(AuthorityLeaseOutcome.RejectedStaleToken, null)
                : inner.TryRenew(scope, ownerNodeId, ownerIncarnation, fencingToken, ttl, nowUtc);
        }

        public AuthorityLeaseResult TryRelease(string scope, string ownerNodeId, Guid ownerIncarnation, long fencingToken)
        {
            Interlocked.Increment(ref _releases);
            if (ThrowOnRelease)
            {
                throw new InvalidOperationException("the characters database is unreachable");
            }

            return inner.TryRelease(scope, ownerNodeId, ownerIncarnation, fencingToken);
        }

        public bool ValidateFencingToken(string scope, string ownerNodeId, long fencingToken, DateTimeOffset nowUtc)
            => inner.ValidateFencingToken(scope, ownerNodeId, fencingToken, nowUtc);

        public RealmAuthorityLease? GetCurrent(string scope, DateTimeOffset nowUtc) => inner.GetCurrent(scope, nowUtc);

        public long GetHighWaterToken(string scope) => inner.GetHighWaterToken(scope);
    }
}

/// <summary>A clock and timer source the test advances by hand: timers fire synchronously, in due order, on the thread that calls <see cref="Advance"/>.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <summary>Timers that are armed (a one-shot that has not fired, or a periodic one that is running).</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.IsArmed);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + by;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.IsArmed && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                if (next.Due > _now)
                {
                    _now = next.Due;
                }
            }

            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public bool IsArmed { get; private set; }
        public DateTimeOffset Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _period = period;
                IsArmed = dueTime != Timeout.InfiniteTimeSpan;
                Due = owner._now + (IsArmed ? dueTime : TimeSpan.Zero);
                return true;
            }
        }

        public void Fire()
        {
            lock (owner._gate)
            {
                if (_period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero)
                {
                    IsArmed = false;
                }
                else
                {
                    Due += _period;
                }
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                IsArmed = false;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

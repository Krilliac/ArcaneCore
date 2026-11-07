using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// <see cref="RealmAuthorityLeaseKeeper.Holds"/> is also false once the lease's expiry has passed, even when the renewal tick has not run to notice (a stalled process or a starved timer thread): another node may already hold the lease.
/// The clock here moves by hand and its timers never fire, so nothing renews.
/// </summary>
public sealed class RealmAuthorityLeaseKeeperExpiryTests
{
    private const string Node = "worker-a";

    /// <summary>A clock the test sets; timers are created but never fire.</summary>
    private sealed class FrozenTimerClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NeverFiringTimer();

        private sealed class NeverFiringTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_lease_is_not_held_past_its_expiry_when_no_renewal_ran_but_the_token_is_still_known()
    {
        var clock = new FrozenTimerClock(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        var store = new InMemoryRealmAuthorityLeaseStore();
        await using var keeper = new RealmAuthorityLeaseKeeper(
            store,
            Options.Create(new ClusterNodeOptions { Role = ClusterNodeRole.MapWorker, AuthorityLeaseTtlSeconds = 30, AuthorityLeaseRenewSeconds = 10 }),
            Options.Create(new GatewayWorkerOptions { Enabled = true, NodeId = Node }),
            NullLogger<RealmAuthorityLeaseKeeper>.Instance, null, clock, _ => { });
        await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.True(keeper.Holds(RealmAuthorityScopes.Social));

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.True(keeper.Holds(RealmAuthorityScopes.Social)); // still inside the lease

        clock.Advance(TimeSpan.FromSeconds(2)); // 31 s: past the 30 s lease, and no renewal tick has run
        Assert.False(keeper.Holds(RealmAuthorityScopes.Social));
        Assert.False(keeper.Holds(RealmAuthorityScopes.Economy));
        Assert.NotNull(keeper.TokenFor(RealmAuthorityScopes.Social)); // the token and the commit fence are unchanged: the store decides at commit
        Assert.NotNull(keeper.FenceFor(RealmAuthorityScopes.Social));
        Assert.False(RealmAuthorityLeaseKeeper.HeldOrUnclustered(keeper, RealmAuthorityScopes.Social));
    }
}

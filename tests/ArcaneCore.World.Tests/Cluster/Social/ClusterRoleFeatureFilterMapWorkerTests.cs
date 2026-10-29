using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Economy;
using ArcaneCore.World.ServerMail;
using ArcaneCore.World.Warden;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>A map worker runs the realm singletons only while it holds their lease (M15.2, node roles).</summary>
public sealed class ClusterRoleFeatureFilterMapWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    [Fact]
    public void MapWorker_AttachesAuctionBot_OnlyWithOwnValidEconomyToken()
    {
        var store = new InMemoryRealmAuthorityLeaseStore();
        long token = store.TryAcquire(RealmAuthorityScopes.Economy, "worker-1", Guid.NewGuid(), Ttl, Now).Lease!.FencingToken;
        var filter = new ClusterRoleFeatureFilter(store);
        Type bot = typeof(AuctionBotFeature);

        Assert.True(filter.ShouldAttachFeature(bot, ClusterNodeRole.MapWorker, "worker-1", _ => token, Now.AddSeconds(1)));
        Assert.False(filter.ShouldAttachFeature(bot, ClusterNodeRole.MapWorker, "worker-2", _ => token, Now.AddSeconds(1))); // another node's token
        Assert.False(filter.ShouldAttachFeature(bot, ClusterNodeRole.MapWorker, "worker-1", _ => null, Now.AddSeconds(1))); // holds none
        Assert.False(filter.ShouldAttachFeature(bot, ClusterNodeRole.MapWorker, "worker-1", _ => token + 1, Now.AddSeconds(1))); // not the issued token
        Assert.False(filter.ShouldAttachFeature(bot, ClusterNodeRole.MapWorker, "worker-1", _ => token, Now + Ttl + TimeSpan.FromSeconds(1))); // expired
        Assert.False(new ClusterRoleFeatureFilter().ShouldAttachFeature(bot, ClusterNodeRole.MapWorker, "worker-1", _ => token, Now)); // no store
        // The token is looked up per scope: a social token does not admit the economy singleton.
        Assert.False(filter.ShouldAttachFeature(bot, ClusterNodeRole.MapWorker, "worker-1", scope => scope == RealmAuthorityScopes.Social ? token : null, Now));
    }

    [Fact]
    public void MapWorker_AttachesAutoBroadcast_OnlyWithOwnValidSocialToken()
    {
        var store = new InMemoryRealmAuthorityLeaseStore();
        long token = store.TryAcquire(RealmAuthorityScopes.Social, "worker-1", Guid.NewGuid(), Ttl, Now).Lease!.FencingToken;
        var filter = new ClusterRoleFeatureFilter(store);
        Type broadcast = typeof(AutoBroadcastFeature);

        Assert.True(filter.ShouldAttachFeature(broadcast, ClusterNodeRole.MapWorker, "worker-1", _ => token, Now.AddSeconds(1)));
        Assert.False(filter.ShouldAttachFeature(broadcast, ClusterNodeRole.MapWorker, "worker-2", _ => token, Now.AddSeconds(1)));
        Assert.False(filter.ShouldAttachFeature(broadcast, ClusterNodeRole.MapWorker, "worker-1", _ => null, Now.AddSeconds(1)));
        Assert.False(filter.ShouldAttachFeature(broadcast, ClusterNodeRole.MapWorker, "worker-1", _ => token, Now + Ttl + TimeSpan.FromSeconds(1)));
        Assert.False(new ClusterRoleFeatureFilter().ShouldAttachFeature(broadcast, ClusterNodeRole.MapWorker, "worker-1", _ => token, Now));
    }

    [Fact]
    public void MapWorker_KeepsWarden()
    {
        var filter = new ClusterRoleFeatureFilter();

        Assert.True(filter.ShouldAttachFeature(typeof(WardenFeature), ClusterNodeRole.MapWorker, "worker-1", _ => null, Now));
        Assert.True(filter.ShouldAttachFeature(typeof(WardenFeature), ClusterNodeRole.Combined, "n", _ => null, Now));
        Assert.True(filter.ShouldAttachFeature(typeof(WardenFeature), ClusterNodeRole.Gateway, "n", _ => null, Now));
        Assert.False(filter.ShouldAttachFeature(typeof(WardenFeature), ClusterNodeRole.Social, "n", _ => null, Now));
        Assert.False(filter.ShouldAttachFeature(typeof(WardenFeature), ClusterNodeRole.Economy, "n", _ => null, Now));
    }

    [Fact]
    public void Combined_StillAttachesTheSingletons_WithoutAnyToken()
    {
        var filter = new ClusterRoleFeatureFilter();

        Assert.True(filter.ShouldAttachFeature(typeof(AuctionBotFeature), ClusterNodeRole.Combined, "n", _ => null, Now));
        Assert.True(filter.ShouldAttachFeature(typeof(AutoBroadcastFeature), ClusterNodeRole.Combined, "n", _ => null, Now));
    }

    [Fact]
    public void LeaseScopeFor_MapsSingletons()
    {
        Assert.Equal(RealmAuthorityScopes.Social, ClusterRoleFeatureFilter.LeaseScopeFor(typeof(AutoBroadcastFeature)));
        Assert.Equal(RealmAuthorityScopes.Economy, ClusterRoleFeatureFilter.LeaseScopeFor(typeof(AuctionBotFeature)));
        Assert.Null(ClusterRoleFeatureFilter.LeaseScopeFor(typeof(WardenFeature)));
        Assert.Null(ClusterRoleFeatureFilter.LeaseScopeFor(typeof(ServerMailFeature)));
    }

    [Fact]
    public void CanExecuteEconomyAuthorityTick_OnMapWorker_NeedsTheNodesOwnEconomyLease()
    {
        var store = new InMemoryRealmAuthorityLeaseStore();
        long token = store.TryAcquire(RealmAuthorityScopes.Economy, "worker-1", Guid.NewGuid(), Ttl, Now).Lease!.FencingToken;
        var filter = new ClusterRoleFeatureFilter(store);

        Assert.True(filter.CanExecuteEconomyAuthorityTick(ClusterNodeRole.MapWorker, "worker-1", token, Now.AddSeconds(1)));
        Assert.False(filter.CanExecuteEconomyAuthorityTick(ClusterNodeRole.MapWorker, "worker-2", token, Now.AddSeconds(1)));
        Assert.False(filter.CanExecuteEconomyAuthorityTick(ClusterNodeRole.MapWorker, "worker-1", token, Now + Ttl + TimeSpan.FromSeconds(1)));
        Assert.False(new ClusterRoleFeatureFilter().CanExecuteEconomyAuthorityTick(ClusterNodeRole.MapWorker, "worker-1", token, Now));
    }
}

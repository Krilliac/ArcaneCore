using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Economy;
using ArcaneCore.World.ServerMail;
using ArcaneCore.World.Warden;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// The social and economy realm singletons follow the lease at run time: attached dormant on every map worker, they start by themselves when the node takes the lease over.
/// (The auction bot's own takeover test is in AuctionBotFeatureTests.) Unproven until the integration gate runs them.
/// </summary>
public sealed class RealmSingletonTakeoverTests
{
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryRealmAuthorityLeaseStore _store = new();

    private RealmAuthorityLeaseKeeper CreateKeeper(string node)
        => new(_store,
            Options.Create(new ClusterNodeOptions { Role = ClusterNodeRole.MapWorker, AuthorityLeaseTtlSeconds = 30, AuthorityLeaseRenewSeconds = 10 }),
            Options.Create(new GatewayWorkerOptions { Enabled = true, NodeId = node }),
            NullLogger<RealmAuthorityLeaseKeeper>.Instance,
            null,
            _time,
            _ => { });

    [Fact]
    public async Task AutoBroadcast_without_the_social_lease_sends_nothing_then_broadcasts_after_the_holder_dies()
    {
        // worker-b took the social lease at start and then died: it never renews.
        _store.TryAcquire(RealmAuthorityScopes.Social, "worker-b", Guid.NewGuid(), TimeSpan.FromSeconds(30), _time.GetUtcNow());
        await using RealmAuthorityLeaseKeeper keeper = CreateKeeper("worker-a");
        await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.False(keeper.Holds(RealmAuthorityScopes.Social));

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AutoBroadcast:Enabled"] = "true",
            ["AutoBroadcast:IntervalMs"] = "1000",
            ["AutoBroadcast:Messages:0"] = "Welcome",
        }).Build();
        await using ServiceProvider services = new ServiceCollection().AddSingleton(configuration).AddSingleton(keeper).BuildServiceProvider();
        var feature = new AutoBroadcastFeature(services);

        Assert.Null(feature.Update(5_000)); // dormant: another node broadcasts, and nothing accumulates here
        Assert.Null(feature.Update(5_000));

        for (int i = 0; i < 4; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(10)); // worker-b's lease lapses at 30 s, and the keeper's renewal tick takes it over
        }

        Assert.True(keeper.Holds(RealmAuthorityScopes.Social));
        Assert.Null(feature.Update(500)); // the interval counts from the takeover, not from the dormant ticks
        Assert.Equal("Welcome", feature.Update(500));
        Assert.Null(feature.Update(500));

        feature.Halt(); // a later loss still stops it
        Assert.Null(feature.Update(5_000));
    }

    [Fact]
    public void AutoBroadcast_with_no_keeper_broadcasts_as_before()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AutoBroadcast:Enabled"] = "true",
            ["AutoBroadcast:IntervalMs"] = "1000",
            ["AutoBroadcast:Messages:0"] = "Welcome",
        }).Build();
        using ServiceProvider services = new ServiceCollection().AddSingleton(configuration).BuildServiceProvider();
        var feature = new AutoBroadcastFeature(services);

        Assert.Equal("Welcome", feature.Update(1_000));
    }

    [Fact]
    public void Only_the_realm_singletons_on_a_map_worker_attach_dormant()
    {
        Assert.True(ClusterRoleFeatureFilter.AttachesDormant(typeof(AutoBroadcastFeature), ClusterNodeRole.MapWorker));
        Assert.True(ClusterRoleFeatureFilter.AttachesDormant(typeof(AuctionBotFeature), ClusterNodeRole.MapWorker));
        Assert.False(ClusterRoleFeatureFilter.AttachesDormant(typeof(WardenFeature), ClusterNodeRole.MapWorker));
        Assert.False(ClusterRoleFeatureFilter.AttachesDormant(typeof(AutoBroadcastFeature), ClusterNodeRole.Combined));
        Assert.False(ClusterRoleFeatureFilter.AttachesDormant(typeof(AuctionBotFeature), ClusterNodeRole.Gateway));
        Assert.False(ClusterRoleFeatureFilter.AttachesDormant(typeof(AuctionBotFeature), ClusterNodeRole.Social));
    }
}

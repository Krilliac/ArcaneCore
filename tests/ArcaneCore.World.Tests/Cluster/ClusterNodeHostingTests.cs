using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Tests.Ops;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>What the production host registers for <c>Cluster:Node:Role</c>: nothing but the options for Combined, the durable lease wiring for MapWorker.</summary>
public sealed class ClusterNodeHostingTests
{
    [Fact]
    public void Combined_Default_RegistersNoFilterNoKeeperNoLeaseInterface()
    {
        using var directory = new TempDirectory("arcanecore-nodehosting-");
        using IHost host = ProductionHostKit.Build(directory.Path);
        IServiceProvider services = host.Services;

        Assert.Equal(ClusterNodeRole.Combined, services.GetRequiredService<IOptions<ClusterNodeOptions>>().Value.Role);
        Assert.Null(services.GetService<ClusterRoleFeatureFilter>());
        Assert.Null(services.GetService<RealmAuthorityLeaseKeeper>());
        Assert.Null(services.GetService<IRealmAuthorityLeaseStore>()); // so `.cluster leases` queries no table
        Assert.Null(services.GetService<EfRealmAuthorityLeaseStore>()); // Combined adds nothing but the options
    }

    [Fact]
    public void MapWorker_RegistersFilterKeeperAndDurableEfLeaseStore()
    {
        using var directory = new TempDirectory("arcanecore-nodehosting-");
        using IHost host = ProductionHostKit.Build(directory.Path, new Dictionary<string, string?>
        {
            ["Cluster:Node:Role"] = "MapWorker",
            ["Cluster:Worker:Enabled"] = "true",
            ["Cluster:Worker:LinkSecret"] = "test-link-secret-0123456789",
            ["Cluster:Fencing:Enabled"] = "true",
        });
        IServiceProvider services = host.Services;

        Assert.Equal(ClusterNodeRole.MapWorker, services.GetRequiredService<IOptions<ClusterNodeOptions>>().Value.Role);
        Assert.NotNull(services.GetService<ClusterRoleFeatureFilter>());
        Assert.NotNull(services.GetService<RealmAuthorityLeaseKeeper>());
        IRealmAuthorityLeaseStore store = services.GetRequiredService<IRealmAuthorityLeaseStore>();
        Assert.IsType<EfRealmAuthorityLeaseStore>(store);
        Assert.Same(services.GetRequiredService<EfRealmAuthorityLeaseStore>(), store);
        WorldHost world = services.GetServices<IHostedService>().OfType<WorldHost>().Single();
        Assert.NotNull(world);
    }
}

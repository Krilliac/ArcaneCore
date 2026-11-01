using ArcaneCore.World.Cluster.Tooling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Tooling;

/// <summary>
/// The node bridge lists only nodes that exist: nothing listens as social or coordinator, so the default spec leaves them out, and a launcher
/// that starts a different set of nodes gets exactly that set monitored (and shown by status).
/// </summary>
[Collection("ClusterEndpointsEnv")]
public sealed class ClusterNodeBridgeEndpointTests
{
    [Fact]
    public void DefaultSpec_HasNoSocialOrCoordinatorEntries()
    {
        string[] entries = ClusterNodeBridgeFeature.DefaultEndpointsSpec.Split(';', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
            ["worker:EK", "worker:Kalimdor", "worker:instances", "gateway", "supervisor"],
            entries.Select(e => e.Split('|')[0]).ToArray());
        Assert.DoesNotContain(entries, e => e.Contains("social", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.Contains("coordinator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConfiguredSpec_ListsExactlyTheStartedNodes()
    {
        using EndpointsEnv env = EndpointsEnv.Set(null);
        await using WorldTestHost host = WorldTestHost.Start();
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<ClusterNodeBridgeOptions>>(Options.Create(new ClusterNodeBridgeOptions
        {
            Enabled = true,
            Endpoints = "worker:EK|Worker|127.0.0.1:1|0;gateway|Gateway|127.0.0.1:2|-",
        }));
        using var bridge = new ClusterNodeBridgeFeature(services.BuildServiceProvider(), NullLogger<ClusterNodeBridgeFeature>.Instance)
        {
            StartupDelay = TimeSpan.FromMinutes(5),
        };

        bridge.Attach(host.World);
        IReadOnlyList<ConnectedClusterNodeStatus> nodes = bridge.SnapshotNodes();
        await bridge.StopAsync();

        Assert.Equal(["worker:EK", "gateway"], nodes.Select(n => n.NodeId).ToArray());
        Assert.Equal(["Worker", "Gateway"], nodes.Select(n => n.RoleLabel).ToArray());
    }
}

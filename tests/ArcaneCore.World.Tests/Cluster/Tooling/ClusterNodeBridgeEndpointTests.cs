using ArcaneCore.World.Cluster.Tooling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Tooling;

/// <summary>
/// The node bridge lists only nodes that exist: nothing listens as social or coordinator, so the default spec leaves them out, and under a
/// supervisor the bridge monitors exactly the worker and gateway children the supervisor was configured to start.
/// </summary>
[Collection("ClusterEndpointsEnv")]
public sealed class ClusterNodeBridgeEndpointTests
{
    private static IConfiguration SupervisorChildren(params (string Key, string Role, string Arguments, string Maps)[] children)
    {
        var values = new Dictionary<string, string?>();
        foreach ((string key, string role, string arguments, string maps) in children)
        {
            string prefix = $"Cluster:Supervisor:Processes:{key}:";
            values[prefix + "Name"] = key;
            values[prefix + "Role"] = role;
            values[prefix + "Arguments"] = arguments;
            if (maps.Length > 0)
            {
                values[prefix + "Maps"] = maps;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task<IReadOnlyList<ConnectedClusterNodeStatus>> SnapshotAsync(IConfiguration configuration)
    {
        using EndpointsEnv env = EndpointsEnv.Set(null);
        await using WorldTestHost host = WorldTestHost.Start();
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<ClusterNodeBridgeOptions>>(Options.Create(new ClusterNodeBridgeOptions { Enabled = true }));
        services.AddSingleton(configuration);
        using var bridge = new ClusterNodeBridgeFeature(services.BuildServiceProvider(), NullLogger<ClusterNodeBridgeFeature>.Instance)
        {
            StartupDelay = TimeSpan.FromMinutes(5),
        };

        bridge.Attach(host.World);
        IReadOnlyList<ConnectedClusterNodeStatus> nodes = bridge.SnapshotNodes();
        await bridge.StopAsync();
        return nodes;
    }

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
    public async Task SupervisorChildren_AreExactlyTheNodesTheBridgeLists()
    {
        IConfiguration configuration = SupervisorChildren(
            ("realm", "realm", "--Auth:Port=3724", ""),
            ("worker-EK", "worker", "--Cluster:Worker:Port=8095 --Cluster:Worker:BindAddress=127.0.0.1 --Cluster:Worker:NodeId=worker:EK", "0"),
            ("worker-Kal", "worker", "--Cluster:Worker:Port=8096 --Cluster:Worker:NodeId=worker:Kalimdor", "1,30-31"),
            ("gateway", "gateway", "--Gateway:Port=8086", ""));

        IReadOnlyList<ConnectedClusterNodeStatus> nodes = await SnapshotAsync(configuration);

        Assert.Equal(["worker:EK", "worker:Kalimdor"], nodes.Select(n => n.NodeId).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(8095, nodes.Single(n => n.NodeId == "worker:EK").EndPoint.Port);
        Assert.Equal(8096, nodes.Single(n => n.NodeId == "worker:Kalimdor").EndPoint.Port);
        Assert.DoesNotContain(nodes, n => n.NodeId == "gateway" || n.EndPoint.Port == 8086);
        Assert.DoesNotContain(nodes, n => n.NodeId is "supervisor" or "worker:instances" or "social" or "coordinator" or "realm");
        Assert.DoesNotContain(nodes, n => n.EndPoint.Port is >= 19081 and <= 19087);
    }

    [Fact]
    public async Task CombinedSupervisorChildren_MonitorNoNodes()
    {
        IConfiguration configuration = SupervisorChildren(
            ("realm", "realm", "--Auth:Port=3724", ""),
            ("world", "combined", "--World:Port=8085", ""));

        Assert.Empty(await SnapshotAsync(configuration));
    }

    [Fact]
    public async Task NoSupervisorChildren_FallBackToTheDefaultNodes()
    {
        IReadOnlyList<ConnectedClusterNodeStatus> nodes = await SnapshotAsync(new ConfigurationBuilder().Build());

        Assert.Equal(
            ["worker:EK", "worker:Kalimdor", "worker:instances", "gateway", "supervisor"],
            nodes.Select(n => n.NodeId).ToArray());
    }
}

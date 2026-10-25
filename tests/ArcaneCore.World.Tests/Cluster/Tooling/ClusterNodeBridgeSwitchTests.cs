using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.World.Cluster.Tooling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Tooling;

/// <summary>
/// <c>Cluster:NodeBridge:Enabled</c> (off by default): with it off the bridge opens no sockets, logs nothing, starts no task and syncs no
/// placement, and its status consumers say it is disabled; with it on it connects to the configured endpoints as before.
/// </summary>
public sealed class ClusterNodeBridgeSwitchTests
{
    private sealed class ListLogger : ILogger<ClusterNodeBridgeFeature>
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public string[] Lines => [.. _lines];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _lines.Enqueue(formatter(state, exception));
    }

    private static ClusterNodeBridgeFeature CreateFeature(ClusterNodeBridgeOptions options, ListLogger logger)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<ClusterNodeBridgeOptions>>(Options.Create(options));
        return new ClusterNodeBridgeFeature(services.BuildServiceProvider(), logger) { StartupDelay = TimeSpan.Zero };
    }

    private static string ProbeSpec(int port) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"probe|Worker|127.0.0.1:{port}|0");

    [Fact]
    public void Options_DefaultToOff_WithTheSevenLocalNodeEndpoints()
    {
        var options = new ClusterNodeBridgeOptions();

        Assert.False(options.Enabled);
        Assert.Equal(ClusterNodeBridgeFeature.DefaultEndpointsSpec, options.Endpoints);
        foreach (int port in new[] { 19081, 19082, 19083, 19084, 19085, 19086, 19087 })
        {
            Assert.Contains($"127.0.0.1:{port}|", options.Endpoints, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task StartingAWorld_WithNoConfiguration_LeavesTheBridgeOff()
    {
        await using WorldTestHost host = WorldTestHost.Start();

        var bridge = host.WorldServices.GetRequiredService<ClusterNodeBridgeFeature>();

        Assert.False(bridge.IsEnabled);
        Assert.False(bridge.IsActive);
        Assert.Empty(bridge.SnapshotNodes());
        Assert.Empty(bridge.Placement.SnapshotWorkers());
    }

    [Fact]
    public async Task Disabled_OpensNoConnection_LogsNothing_AndStartsNoTask()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using WorldTestHost host = WorldTestHost.Start();
        var logger = new ListLogger();
        using ClusterNodeBridgeFeature bridge = CreateFeature(new ClusterNodeBridgeOptions { Enabled = false, Endpoints = ProbeSpec(port) }, logger);

        bridge.Attach(host.World);
        await Task.Delay(750);
        await bridge.StopAsync();

        Assert.False(listener.Pending());
        Assert.False(bridge.IsEnabled);
        Assert.False(bridge.IsActive);
        Assert.Empty(bridge.SnapshotNodes());
        Assert.Empty(bridge.Placement.SnapshotWorkers());
        Assert.Empty(logger.Lines);
    }

    [Fact]
    public async Task Enabled_ConnectsToTheConfiguredEndpoints_AndReportsAnUnansweringNode()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using WorldTestHost host = WorldTestHost.Start();
        var logger = new ListLogger();
        using ClusterNodeBridgeFeature bridge = CreateFeature(new ClusterNodeBridgeOptions { Enabled = true, Endpoints = ProbeSpec(port) }, logger);

        bridge.Attach(host.World);
        try
        {
            Assert.True(bridge.IsEnabled);
            Assert.True(bridge.IsActive);
            ConnectedClusterNodeStatus node = Assert.Single(bridge.SnapshotNodes());
            Assert.Equal("probe", node.NodeId);
            Assert.Equal(port, node.EndPoint.Port);

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            using TcpClient accepted = await listener.AcceptTcpClientAsync(timeout.Token);
            accepted.Close();

            while (!logger.Lines.Any(l => l.Contains("is not answering", StringComparison.Ordinal)))
            {
                await Task.Delay(50, timeout.Token);
            }

            Assert.False(Assert.Single(bridge.SnapshotNodes()).IsConnected);
        }
        finally
        {
            await bridge.StopAsync();
        }
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.World.Cluster.Tooling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Tooling;

/// <summary>Sets (or clears, with null) ARCANECORE_CLUSTER_ENDPOINTS for the process and restores the previous value on dispose; users share the "ClusterEndpointsEnv" collection so they never overlap.</summary>
internal sealed class EndpointsEnv : IDisposable
{
    public const string Name = "ARCANECORE_CLUSTER_ENDPOINTS";

    private readonly string? _previous = Environment.GetEnvironmentVariable(Name);

    private EndpointsEnv(string? value) => Environment.SetEnvironmentVariable(Name, value);

    public static EndpointsEnv Set(string? value) => new(value);

    public void Dispose() => Environment.SetEnvironmentVariable(Name, _previous);
}

/// <summary>
/// <c>Cluster:NodeBridge:Enabled</c> (off by default): with it off the bridge opens no sockets, logs nothing, starts no task and syncs no
/// placement, and its status consumers say it is disabled; with it on it connects to the configured endpoints as before.
/// </summary>
[Collection("ClusterEndpointsEnv")]
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
        using EndpointsEnv env = EndpointsEnv.Set(null);
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
    public async Task Disabled_WithEndpointsEnvironmentVariableSet_WarnsOnceAndStaysOff()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        using EndpointsEnv env = EndpointsEnv.Set(ProbeSpec(1));
        var logger = new ListLogger();
        using ClusterNodeBridgeFeature bridge = CreateFeature(new ClusterNodeBridgeOptions { Enabled = false }, logger);

        bridge.Attach(host.World);

        Assert.False(bridge.IsEnabled);
        Assert.False(bridge.IsActive);
        Assert.Equal(
            "ARCANECORE_CLUSTER_ENDPOINTS is set but Cluster:NodeBridge:Enabled is false; the node bridge is off",
            Assert.Single(logger.Lines));
    }

    [Fact]
    public async Task Enabled_WithOnlyInvalidEndpoints_WarnsPerDroppedEntry_AndOnceForNone()
    {
        using EndpointsEnv env = EndpointsEnv.Set(null);
        await using WorldTestHost host = WorldTestHost.Start();
        var logger = new ListLogger();
        using ClusterNodeBridgeFeature bridge = CreateFeature(new ClusterNodeBridgeOptions { Enabled = true, Endpoints = "garbage;a|b|not-an-endpoint" }, logger);

        bridge.Attach(host.World);

        Assert.True(bridge.IsEnabled);
        Assert.False(bridge.IsActive);
        Assert.Empty(bridge.SnapshotNodes());
        Assert.Equal(2, logger.Lines.Count(l => l.Contains("dropped an invalid endpoint entry", StringComparison.Ordinal)));
        Assert.Contains(logger.Lines, l => l.Contains("garbage", StringComparison.Ordinal));
        Assert.Contains(logger.Lines, l => l.Contains("a|b|not-an-endpoint", StringComparison.Ordinal));
        Assert.Single(logger.Lines, l => l.Contains("no valid endpoints were parsed", StringComparison.Ordinal));
        Assert.Equal(3, logger.Lines.Length);
    }

    [Fact]
    public async Task StartingAWorld_WithNodeBridgeEnabledInHostConfiguration_TurnsTheBridgeOn()
    {
        using EndpointsEnv env = EndpointsEnv.Set(null);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cluster:NodeBridge:Enabled"] = "true",
            ["Cluster:NodeBridge:Endpoints"] = ProbeSpec(port),
        }).Build();

        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.Configure<ClusterNodeBridgeOptions>(configuration.GetSection(ClusterNodeBridgeOptions.SectionName)));

        var bridge = host.WorldServices.GetRequiredService<ClusterNodeBridgeFeature>();
        Assert.True(bridge.IsEnabled);
        Assert.Equal("probe", Assert.Single(bridge.SnapshotNodes()).NodeId);
    }

    [Fact]
    public async Task Enabled_ConnectsToTheConfiguredEndpoints_AndReportsAnUnansweringNode()
    {
        using EndpointsEnv env = EndpointsEnv.Set(null);
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

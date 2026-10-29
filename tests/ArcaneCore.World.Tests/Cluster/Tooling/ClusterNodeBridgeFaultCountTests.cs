using System.Net;
using System.Net.Sockets;
using ArcaneCore.World.Cluster.Tooling;
using ArcaneCore.World.Tests.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Tooling;

/// <summary>
/// Issue #1618: <c>logger.LogFault</c> already records into the fault ledger, so the monitor-iteration catch of the node bridge must not also
/// call <c>FaultLedger.Shared.Record("cluster.bridge", ...)</c>. A faulted iteration must count once under <c>cluster.bridge</c>.
/// </summary>
[Collection("ClusterEndpointsEnv")]
public sealed class ClusterNodeBridgeFaultCountTests
{
    /// <summary>
    /// Throws the fault from the "node is not answering" information line (written inside the connect catch, so it escapes into the monitor
    /// iteration's own catch), once. The Error entry that catch writes ends the wait: the ledger has the fault by then.
    /// </summary>
    private sealed class ThrowOnUnansweredNodeLogger(Exception fault) : ILogger<ClusterNodeBridgeFeature>
    {
        private readonly TaskCompletionSource _faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _thrown;

        public Task Faulted => _faulted.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error && ReferenceEquals(exception, fault)) _faulted.TrySetResult();
            else if (logLevel == LogLevel.Information && formatter(state, exception).Contains("is not answering", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _thrown, 1) == 0) throw fault;
        }
    }

    [Fact]
    public async Task AFaultedMonitorIteration_IsCountedOnce_UnderClusterBridge()
    {
        var probe = new FaultCountProbe("cluster.bridge");
        using EndpointsEnv env = EndpointsEnv.Set(null);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using WorldTestHost host = WorldTestHost.Start();
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<ClusterNodeBridgeOptions>>(Options.Create(new ClusterNodeBridgeOptions
        {
            Enabled = true,
            Endpoints = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"probe|Worker|127.0.0.1:{port}|0"),
        }));
        var logger = new ThrowOnUnansweredNodeLogger(probe.Fault());
        using ClusterNodeBridgeFeature bridge = new(services.BuildServiceProvider(), logger) { StartupDelay = TimeSpan.Zero };

        bridge.Attach(host.World);
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            using TcpClient accepted = await listener.AcceptTcpClientAsync(timeout.Token);
            accepted.Close(); // the node never answers its handshake
            await logger.Faulted.WaitAsync(timeout.Token);
        }
        finally
        {
            await bridge.StopAsync();
        }

        probe.AssertCountedOnce("the monitor iteration catch");
    }
}

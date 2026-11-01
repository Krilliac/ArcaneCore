using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// The gateway's own instruments (<see cref="GatewayMeters"/>): link state, sessions, reconnects and failed attempts, read through a
/// <see cref="MeterListener"/> exactly as the metrics store reads them. Every gateway of a test carries a unique node id, so tests running at once
/// do not see each other's series. Real loopback sockets; no world.
/// </summary>
public sealed class GatewayMetricsTests
{
    private const string Secret = "metrics-link-secret-0123456789";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task LinkStateAndSessionGauges_FollowTheLink_AndReconnectsAreCounted()
    {
        using var cts = new CancellationTokenSource(Deadline);
        string node = "metrics-" + Guid.NewGuid().ToString("N")[..8];
        using var probe = new GatewayMeterProbe();
        var acceptor = new OpenEverythingAcceptor();
        var host = new GatewaySessionLinkHost(Manifest(ClusterRole.Worker), acceptor, () => 1, Secret, new IPEndPoint(IPAddress.Loopback, 0));
        host.Start();
        GatewayServer gateway = CreateGateway(node, host.BoundEndPoint.Port);
        try
        {
            await gateway.StartAsync(cts.Token);
            await gateway.WaitForLinkAsync(cts.Token);

            Assert.Equal(1, probe.Gauge("arcanecore.gateway.link_state", node));
            Assert.Equal(0, probe.Gauge("arcanecore.gateway.sessions", node));
            Assert.Equal(0, probe.Gauge("arcanecore.gateway.connections", node));

            GatewaySessionLinkClient link = gateway.Link ?? throw new InvalidOperationException("the link is up");
            GatewaySessionOpenReply reply = await link.OpenAsync(
                new GatewaySessionOpenRequest(1, 7, "METRICS", 5875, "198.51.100.4:4000"), new GatewayLinkLoggingSink(), cts.Token);
            Assert.True(reply.Accepted);
            Assert.Equal(1, probe.Gauge("arcanecore.gateway.sessions", node));

            // Lose the worker: the gauge drops to 0 and no reconnect has happened yet.
            await host.DisposeAsync();
            await WaitUntilAsync(() => probe.Gauge("arcanecore.gateway.link_state", node) == 0, cts.Token);
            Assert.Equal(0, probe.Gauge("arcanecore.gateway.sessions", node));
            Assert.Equal(0, probe.Sum("arcanecore.gateway.link_reconnects", node));
            await WaitUntilAsync(() => probe.Sum("arcanecore.gateway.link_failures", node) >= 1, cts.Token);

            // A new worker on the same port: the link comes back and counts as one reconnect.
            host = new GatewaySessionLinkHost(Manifest(ClusterRole.Worker), acceptor, () => 1, Secret, new IPEndPoint(IPAddress.Loopback, gateway.WorkerEndPoint.Port));
            host.Start();
            await WaitUntilAsync(() => probe.Gauge("arcanecore.gateway.link_state", node) == 1, cts.Token);
            Assert.Equal(1, probe.Sum("arcanecore.gateway.link_reconnects", node));
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None);
            gateway.Dispose();
            await host.DisposeAsync();
        }

        // A stopped gateway leaves the gauges: no series of its node remains.
        Assert.Null(probe.Gauge("arcanecore.gateway.link_state", node));
    }

    [Fact]
    public async Task LinkStateGauge_IsZero_WhileTheWorkerIsUnreachable_AndFailedAttemptsAreCounted()
    {
        using var cts = new CancellationTokenSource(Deadline);
        string node = "metrics-" + Guid.NewGuid().ToString("N")[..8];
        using var probe = new GatewayMeterProbe();
        GatewayServer gateway = CreateGateway(node, FreePort());
        try
        {
            await gateway.StartAsync(cts.Token);
            Assert.Equal(0, probe.Gauge("arcanecore.gateway.link_state", node));
            await WaitUntilAsync(() => probe.Sum("arcanecore.gateway.link_failures", node) >= 1, cts.Token);
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None);
            gateway.Dispose();
        }
    }

    private static GatewayServer CreateGateway(string node, int workerPort) => new(
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new GatewayOptions
        {
            BindAddress = IPAddress.Loopback.ToString(),
            Port = 0,
            WorkerAddress = IPAddress.Loopback.ToString(),
            WorkerPort = workerPort,
            LinkSecret = Secret,
            LinkRetryDelay = TimeSpan.FromMilliseconds(50),
            NodeId = node,
        }),
        NullLoggerFactory.Instance);

    private static ClusterNodeManifest Manifest(ClusterRole role) => GatewayLinkManifest.Create(role, role.ToString(), Guid.NewGuid(), 1, "dev", 1, 1);

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(20, token);
        }
    }

    private sealed class OpenEverythingAcceptor : IGatewaySessionAcceptor
    {
        public async ValueTask<GatewaySessionAcceptResult> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return GatewaySessionAcceptResult.Accepted(new QuietSession());
        }
    }

    private sealed class QuietSession : IGatewayRemoteSession
    {
        public void Start()
        {
        }

        public void Deliver(WorldOpcode opcode, byte[] payload)
        {
        }

        public void CloseFromGateway(ClusterSessionDetachReason reason)
        {
        }
    }

    private sealed class GatewayLinkLoggingSink : IGatewaySessionSink
    {
        public void OnOpened(GatewaySessionOpenReply reply)
        {
        }

        public void OnServerPacket(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void OnClosed(ClusterSessionDetachReason reason)
        {
        }
    }
}

/// <summary>
/// A <see cref="MeterListener"/> on the gateway meter that keeps every measurement with its <c>node</c>, <c>direction</c>, <c>result</c> and
/// <c>worker</c> tags. <see cref="Gauge"/> asks the observable instruments for a fresh reading; the counters and the histogram are summed from
/// what was recorded since the probe was created. Thread-safe.
/// </summary>
internal sealed class GatewayMeterProbe : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<Reading> _readings = new();
    private readonly ConcurrentQueue<Reading> _observed = new();

    internal GatewayMeterProbe()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == GatewayMeters.Meter.Name)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
    }

    internal sealed record Reading(string Instrument, string? Node, string? Direction, string? Result, double Value);

    /// <summary>The current value of the observable gauge for <paramref name="node"/>, or null when no attached gateway reports that node.</summary>
    internal double? Gauge(string instrument, string node)
    {
        lock (_observed)
        {
            while (_observed.TryDequeue(out _))
            {
            }

            _listener.RecordObservableInstruments();
            return _observed.Where(r => r.Instrument == instrument && r.Node == node).Select(r => (double?)r.Value).LastOrDefault();
        }
    }

    /// <summary>The sum of what a counter recorded for <paramref name="node"/> (optionally one <paramref name="direction"/> or <paramref name="result"/>).</summary>
    internal double Sum(string instrument, string node, string? direction = null, string? result = null)
    {
        lock (_readings)
        {
            return _readings
                .Where(r => r.Instrument == instrument && r.Node == node && (direction is null || r.Direction == direction) && (result is null || r.Result == result))
                .Sum(r => r.Value);
        }
    }

    internal int Count(string instrument, string node)
    {
        lock (_readings)
        {
            return _readings.Count(r => r.Instrument == instrument && r.Node == node);
        }
    }

    public void Dispose() => _listener.Dispose();

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string? node = null;
        string? direction = null;
        string? result = null;
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            switch (tag.Key)
            {
                case "node":
                    node = tag.Value?.ToString();
                    break;
                case "direction":
                    direction = tag.Value?.ToString();
                    break;
                case "result":
                    result = tag.Value?.ToString();
                    break;
            }
        }

        var reading = new Reading(instrument.Name, node, direction, result, value);
        if (instrument.IsObservable)
        {
            _observed.Enqueue(reading);
        }
        else
        {
            _readings.Enqueue(reading);
        }
    }
}

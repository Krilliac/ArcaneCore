using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using ArcaneCore.Kernel.Ops.Metrics;

namespace ArcaneCore.Gateway;

/// <summary>
/// The gateway's instruments (docs/areas/gateway.md, docs/ops/metrics.md). The meter's name starts with <see cref="ArcaneMeters.Prefix"/>, so a
/// <see cref="MetricsStore"/> collects it when a host registers one; with no listener a count is a single enabled check. Packets and bytes of the
/// client sockets stay on <see cref="ArcaneMeters"/> (<c>arcanecore.net.*</c>). Every instrument carries the gateway's <c>node</c> (Gateway:NodeId),
/// so several gateways in one process (the tests) stay apart. Label sets are closed: <c>direction</c> is <c>to_worker</c> or <c>to_client</c>,
/// <c>result</c> is <c>accepted</c>, <c>refused</c> or <c>no_link</c>, <c>worker</c> is the link's worker endpoint.
/// <c>worker_node</c> is a configured worker's node id. Thread safety: the counters and the histogram are thread-safe; the gauges read the attached servers' volatile state from the collector thread.
/// </summary>
internal static class GatewayMeters
{
    private static readonly ConcurrentDictionary<GatewayServer, byte> Servers = new();

    /// <summary>The gateway meter.</summary>
    internal static Meter Meter { get; } = new("ArcaneCore.Gateway", "1.0");

    /// <summary>1 while the worker link is registered, 0 while it is down (one series per attached gateway).</summary>
    internal static ObservableGauge<int> LinkState { get; } = Meter.CreateObservableGauge(
        "arcanecore.gateway.link_state", () => Observe(server => server.ReadyLinkCount), "{link}", "Worker links that are up (with one worker: 1 while its link is up, 0 while it is down).");

    /// <summary>1 while one worker link is registered, 0 while it is down or backing off (a series per link, tagged with the worker's node id and endpoint).</summary>
    internal static ObservableGauge<int> WorkerLinkState { get; } = Meter.CreateObservableGauge(
        "arcanecore.gateway.worker_link_state", ObserveLinks, "{state}", "1 while the link to one worker is up, 0 otherwise.");

    /// <summary>Sessions open on the current worker link (0 while the link is down).</summary>
    internal static ObservableGauge<int> Sessions { get; } = Meter.CreateObservableGauge(
        "arcanecore.gateway.sessions", () => Observe(server => server.TotalSessions), "{session}", "Sessions open across the worker links.");

    /// <summary>Client connections of the gateway, authenticated or not.</summary>
    internal static ObservableGauge<int> Connections { get; } = Meter.CreateObservableGauge(
        "arcanecore.gateway.connections", () => Observe(server => server.ConnectionCount), "{connection}", "Open client connections.");

    /// <summary>Worker links registered after the first one of the process: every one follows a lost link or a retarget.</summary>
    internal static Counter<long> LinkReconnects { get; } = Meter.CreateCounter<long>(
        "arcanecore.gateway.link_reconnects", "{link}", "Worker links registered again after a lost link or a retarget.");

    /// <summary>Worker link attempts that failed (refused, handshake rejected, timed out).</summary>
    internal static Counter<long> LinkFailures { get; } = Meter.CreateCounter<long>(
        "arcanecore.gateway.link_failures", "{attempt}", "Worker link attempts that failed.");

    /// <summary>Packets and bytes forwarded across the link, by <c>direction</c>; headers are not counted (the link carries opcode and payload).</summary>
    internal static Counter<long> ForwardedPackets { get; } = Meter.CreateCounter<long>(
        "arcanecore.gateway.forwarded_packets", "{packet}", "Packets forwarded across the worker link.");

    /// <summary>Payload bytes forwarded across the link, by <c>direction</c>.</summary>
    internal static Counter<long> ForwardedBytes { get; } = Meter.CreateCounter<long>(
        "arcanecore.gateway.forwarded_bytes", "By", "Payload bytes forwarded across the worker link.");

    /// <summary>Login attempts that reached the worker step, by <c>result</c>.</summary>
    internal static Counter<long> SessionOpens { get; } = Meter.CreateCounter<long>(
        "arcanecore.gateway.session_opens", "{open}", "Session opens sent to the worker, by result.");

    /// <summary>Round trip of a session open (request queued on the link to the worker's reply), per link, in milliseconds.</summary>
    internal static Histogram<double> LinkLatency { get; } = Meter.CreateHistogram<double>(
        "arcanecore.gateway.link_latency", "ms", "Session-open round trip over the worker link.");

    /// <summary>Make <paramref name="server"/> visible to the gauges. Idempotent.</summary>
    internal static void Attach(GatewayServer server) => Servers[server] = 0;

    /// <summary>Remove <paramref name="server"/> from the gauges. Idempotent.</summary>
    internal static void Detach(GatewayServer server) => Servers.TryRemove(server, out _);

    internal static void CountLinkReconnect(string node)
    {
        if (LinkReconnects.Enabled) LinkReconnects.Add(1, Node(node));
    }

    internal static void CountLinkFailure(string node)
    {
        if (LinkFailures.Enabled) LinkFailures.Add(1, Node(node));
    }

    internal static void CountForwarded(string node, string direction, int payloadBytes)
    {
        if (ForwardedPackets.Enabled)
        {
            var nodeTag = Node(node);
            var directionTag = new KeyValuePair<string, object?>("direction", direction);
            ForwardedPackets.Add(1, nodeTag, directionTag);
            ForwardedBytes.Add(payloadBytes, nodeTag, directionTag);
        }
    }

    internal static void CountSessionOpen(string node, string result)
    {
        if (SessionOpens.Enabled) SessionOpens.Add(1, Node(node), new KeyValuePair<string, object?>("result", result));
    }

    internal static void RecordLinkLatency(string node, object worker, double milliseconds)
    {
        if (LinkLatency.Enabled)
        {
            LinkLatency.Record(milliseconds, Node(node), new KeyValuePair<string, object?>("worker", worker.ToString()));
        }
    }

    private static IEnumerable<Measurement<int>> ObserveLinks()
    {
        foreach (GatewayServer server in Servers.Keys)
        {
            foreach (GatewayWorkerLink worker in server.Workers)
            {
                yield return new Measurement<int>(
                    worker.Link is null ? 0 : 1,
                    Node(server.Options.NodeId),
                    new KeyValuePair<string, object?>("worker_node", worker.NodeId),
                    new KeyValuePair<string, object?>("worker", worker.EndPoint.ToString()));
            }
        }
    }

    private static KeyValuePair<string, object?> Node(string node) => new("node", node);

    private static IEnumerable<Measurement<int>> Observe(Func<GatewayServer, int> read)
    {
        foreach (GatewayServer server in Servers.Keys)
        {
            yield return new Measurement<int>(read(server), Node(server.Options.NodeId));
        }
    }
}

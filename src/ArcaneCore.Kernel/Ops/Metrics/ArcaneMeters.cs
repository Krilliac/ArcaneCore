using System.Diagnostics.Metrics;

namespace ArcaneCore.Kernel.Ops.Metrics;

/// <summary>
/// The process-wide meter and the instruments recorded off the world thread (network). Every meter whose name starts
/// with <see cref="Prefix"/> is collected by <see cref="MetricsStore"/> when <c>Ops:Metrics:Enabled</c> is on; with no
/// listener an instrument's <c>Add</c> is a single enabled check (docs/ops/metrics.md).
/// </summary>
public static class ArcaneMeters
{
    /// <summary>The meter-name prefix the store listens to.</summary>
    public const string Prefix = "ArcaneCore";

    /// <summary>The network meter.</summary>
    public static Meter Net { get; } = new("ArcaneCore.Net", "1.0");

    /// <summary>Client packets read (world and realm sockets).</summary>
    public static Counter<long> PacketsIn { get; } = Net.CreateCounter<long>("arcanecore.net.packets_in", "{packet}", "Client packets received.");

    /// <summary>Server packets queued for clients.</summary>
    public static Counter<long> PacketsOut { get; } = Net.CreateCounter<long>("arcanecore.net.packets_out", "{packet}", "Server packets sent.");

    /// <summary>Client bytes read, headers included.</summary>
    public static Counter<long> BytesIn { get; } = Net.CreateCounter<long>("arcanecore.net.bytes_in", "By", "Client bytes received.");

    /// <summary>Server bytes queued, headers included.</summary>
    public static Counter<long> BytesOut { get; } = Net.CreateCounter<long>("arcanecore.net.bytes_out", "By", "Server bytes sent.");

    /// <summary>Record one inbound packet of <paramref name="bytes"/> bytes.</summary>
    public static void PacketIn(int bytes)
    {
        if (PacketsIn.Enabled)
        {
            PacketsIn.Add(1);
            BytesIn.Add(bytes);
        }
    }

    /// <summary>Record one outbound packet of <paramref name="bytes"/> bytes.</summary>
    public static void PacketOut(int bytes)
    {
        if (PacketsOut.Enabled)
        {
            PacketsOut.Add(1);
            BytesOut.Add(bytes);
        }
    }
}

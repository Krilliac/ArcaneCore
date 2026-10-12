using System.Diagnostics.Metrics;
using ArcaneCore.Kernel.Ops.Metrics;

namespace ArcaneCore.Game.Updates;

/// <summary>
/// What the lean playerbot mode did not do (<c>World:Playerbots:ClientFidelity</c> = Lean): update-object blocks the map did not
/// build for a bot viewer, and received packets (with their bytes) a bot session did not copy into its ring. Process-wide; exposed as
/// the observable counter <see cref="MetricName"/> (tag <c>kind</c> = <c>update_block</c>, <c>ring_packet</c>, <c>ring_byte</c>) and on
/// <c>.server info</c>.
/// <para>Thread affinity: any thread. Each count is one <see cref="Interlocked.Add(ref long, long)"/>; no allocation.</para>
/// </summary>
public static class BotLeanStats
{
    /// <summary>The metric name.</summary>
    public const string MetricName = "arcanecore.playerbots.packets_skipped";

    private static long _updateBlocks;
    private static long _ringPackets;
    private static long _ringBytes;

    // Created with this type's first use; no listener means a recording is one enabled check inside the runtime.
    private static readonly ObservableCounter<long> Metric = ArcaneMeters.Net.CreateObservableCounter(
        MetricName, Observe, "{packet}",
        "Packets a bot viewer did not get built or kept in lean client fidelity (kind: update_block, ring_packet; ring_byte counts bytes).");

    /// <summary>Update-object blocks (create, values, out-of-range) not built for a bot viewer.</summary>
    public static long UpdateBlocks => Volatile.Read(ref _updateBlocks);

    /// <summary>Received packets a bot session did not copy into its ring.</summary>
    public static long RingPackets => Volatile.Read(ref _ringPackets);

    /// <summary>Payload bytes of <see cref="RingPackets"/>.</summary>
    public static long RingBytes => Volatile.Read(ref _ringBytes);

    /// <summary>Count one update-object block not built.</summary>
    public static void SkippedUpdateBlock()
    {
        _ = Metric;
        Interlocked.Increment(ref _updateBlocks);
    }

    /// <summary>Count one ring copy not made, of <paramref name="bytes"/> payload bytes.</summary>
    public static void SkippedRingPacket(int bytes)
    {
        _ = Metric;
        Interlocked.Increment(ref _ringPackets);
        Interlocked.Add(ref _ringBytes, bytes);
    }

    private static IEnumerable<Measurement<long>> Observe()
    {
        yield return new Measurement<long>(UpdateBlocks, new KeyValuePair<string, object?>("kind", "update_block"));
        yield return new Measurement<long>(RingPackets, new KeyValuePair<string, object?>("kind", "ring_packet"));
        yield return new Measurement<long>(RingBytes, new KeyValuePair<string, object?>("kind", "ring_byte"));
    }
}

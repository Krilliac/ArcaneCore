using System.Collections.Concurrent;
using System.Diagnostics;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Net;

/// <summary>
/// The run time of in-world packet handlers by opcode, for the periodic latency summary (top opcodes by p99) and <c>.server info</c>. One histogram per opcode, created in the
/// shared <see cref="LatencyRegistry"/> the first time that opcode is handled (bounded by the registry's name cap); a record is one concurrent-dictionary lookup, one clock
/// read and the histogram's lock-free atomic operations, from any thread, with no allocation after the opcode's first packet.
/// </summary>
internal static class PacketHandlerLatency
{
    private static readonly ConcurrentDictionary<ushort, LatencyHistogram> ByOpcode = new();

    /// <summary>Add the time since <paramref name="startTimestamp"/> (a <see cref="Stopwatch.GetTimestamp"/> reading) to <paramref name="opcode"/>'s histogram.</summary>
    internal static void Record(WorldOpcode opcode, long startTimestamp)
    {
        ushort key = (ushort)opcode;
        if (!ByOpcode.TryGetValue(key, out LatencyHistogram? histogram))
        {
            histogram = ByOpcode.GetOrAdd(key, static k => LatencyRegistry.Shared.Get(LatencyNames.PacketHandlerPrefix + WorldOpcodeNames.GetName((WorldOpcode)k)));
        }

        histogram.Record(Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000));
    }
}

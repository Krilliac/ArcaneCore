using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Net;

/// <summary>
/// The run time of in-world packet handlers by opcode, for the periodic latency summary (top opcodes by p99) and <c>.server info</c>. One histogram per opcode, created in the
/// given <see cref="LatencyRegistry"/> (compact configuration, see <see cref="LatencyRegistry"/>) the first time that opcode is handled; a record is a weak-table lookup, a
/// concurrent-dictionary lookup, one clock read and the histogram's lock-free atomic operations, from any thread, with no allocation after the opcode's first packet.
/// </summary>
internal static class PacketHandlerLatency
{
    private static readonly ConditionalWeakTable<LatencyRegistry, ConcurrentDictionary<ushort, LatencyHistogram>> Caches = new();

    /// <summary>Add the time since <paramref name="startTimestamp"/> (a <see cref="Stopwatch.GetTimestamp"/> reading) to <paramref name="opcode"/>'s histogram in <paramref name="registry"/>.</summary>
    internal static void Record(LatencyRegistry registry, WorldOpcode opcode, long startTimestamp)
    {
        ConcurrentDictionary<ushort, LatencyHistogram> byOpcode = Caches.GetValue(registry, static _ => new ConcurrentDictionary<ushort, LatencyHistogram>());
        ushort key = (ushort)opcode;
        if (!byOpcode.TryGetValue(key, out LatencyHistogram? histogram))
        {
            histogram = byOpcode.GetOrAdd(key, static (k, owner) => owner.Get(LatencyNames.PacketHandlerPrefix + WorldOpcodeNames.GetName((WorldOpcode)k)), registry);
        }

        histogram.Record(Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000));
    }
}

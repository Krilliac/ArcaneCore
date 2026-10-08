using ArcaneCore.Protocol;

namespace ArcaneCore.World.Net;

/// <summary>Optional outbound payload observer for diagnostic hosts.</summary>
internal interface IOutboundPacketObserver
{
    void Observe(WorldOpcode opcode, ReadOnlyMemory<byte> payload);
}

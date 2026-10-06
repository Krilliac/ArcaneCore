using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Tickets;

/// <summary>
/// Minimal truthful vanilla GM-ticket protocol support. The queue is permanently disabled in
/// this slice and no ticket text is parsed, retained, logged, or persisted.
/// </summary>
public sealed class TicketHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgGmticketSystemstatus, HandleSystemStatus);
        table.OnWorld(WorldOpcode.CmsgGmticketCreate, HandleCreate);
    }

    private static void HandleSystemStatus(WorldSession session, Player _, byte[] payload)
    {
        // The vanilla client packet is NullClientPacket. Existing handlers ignore malformed
        // payloads rather than manufacturing a reply for an invalid frame.
        if (payload.Length != 0)
        {
            return;
        }

        session.Send(WorldOpcode.SmsgGmticketSystemstatus, TicketPackets.BuildDisabledSystemStatus());
    }

    private static void HandleCreate(WorldSession session, Player _, byte[] payload)
    {
        // Validate the source-defined envelope before answering. The body is deliberately not
        // decoded: support is disabled and ticket text must never be collected or persisted.
        if (!TicketPackets.IsCreatePayloadLengthValid(payload.Length))
        {
            return;
        }

        session.Send(WorldOpcode.SmsgGmticketCreate, TicketPackets.BuildCreateError());
    }
}

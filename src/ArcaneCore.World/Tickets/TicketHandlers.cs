using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Tickets;

/// <summary>
/// CMSG_GMTICKET_SYSTEMSTATUS (vmangos <c>HandleGMTicketSystemStatusOpcode</c>, GMTicketHandler.cpp:133-140): the client asks whether the
/// ticket queue accepts tickets. The tickets themselves (create, get, update, delete) are the GM audit lane's
/// <see cref="Gm.Audit.GmTicketHandlers"/>, which always accepts them (rate limited per account), so the queue reports enabled.
/// </summary>
public sealed class TicketHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table) => table.OnWorld(WorldOpcode.CmsgGmticketSystemstatus, HandleSystemStatus);

    private static void HandleSystemStatus(WorldSession session, Player _, byte[] payload)
    {
        // The vanilla client packet is NullClientPacket. Existing handlers ignore malformed
        // payloads rather than manufacturing a reply for an invalid frame.
        if (payload.Length != 0)
        {
            return;
        }

        session.Send(WorldOpcode.SmsgGmticketSystemstatus, TicketPackets.BuildSystemStatus(TicketPackets.QueueEnabled));
    }
}

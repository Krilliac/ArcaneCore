using ArcaneCore.Protocol;

namespace ArcaneCore.World.Tickets;

/// <summary>Wire payloads of the vanilla GM-ticket queue status.</summary>
public static class TicketPackets
{
    /// <summary>vmangos GMTICKET_QUEUE_STATUS_DISABLED (GMTicketMgr.h:37).</summary>
    public const uint QueueDisabled = 0;

    /// <summary>vmangos GMTICKET_QUEUE_STATUS_ENABLED (GMTicketMgr.h:38).</summary>
    public const uint QueueEnabled = 1;

    /// <summary>SMSG_GMTICKET_SYSTEMSTATUS: u32 queue status.</summary>
    public static byte[] BuildSystemStatus(uint status)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(status);
        return writer.ToArray();
    }
}

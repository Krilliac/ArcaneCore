using ArcaneCore.Protocol;

namespace ArcaneCore.World.Tickets;

/// <summary>Wire payloads for the vanilla GM-ticket protocol.</summary>
public static class TicketPackets
{
    /// <summary>vmangos GMTICKET_QUEUE_STATUS_DISABLED.</summary>
    public const uint QueueDisabled = 0;

    /// <summary>vmangos GMTICKET_RESPONSE_CREATE_ERROR.</summary>
    public const uint CreateError = 3;

    /// <summary>Vanilla CMSG_GMTICKET_CREATE has category/map/position and two C strings.</summary>
    public const int CreateMinimumPayload = 19;

    /// <summary>gtker/wow_messages vanilla parser upper bound, including harassment data.</summary>
    public const int CreateMaximumPayload = 66072;

    public static bool IsCreatePayloadLengthValid(int length)
        => length is >= CreateMinimumPayload and <= CreateMaximumPayload;

    /// <summary>SMSG_GMTICKET_SYSTEMSTATUS: disabled queue status.</summary>
    public static byte[] BuildDisabledSystemStatus()
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(QueueDisabled);
        return writer.ToArray();
    }

    /// <summary>SMSG_GMTICKET_CREATE: creation failed because ticket support is unavailable.</summary>
    public static byte[] BuildCreateError()
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(CreateError);
        return writer.ToArray();
    }
}

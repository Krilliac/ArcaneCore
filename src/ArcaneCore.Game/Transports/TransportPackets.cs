using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Updates;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Transports;

/// <summary>
/// The SMSG_UPDATE_OBJECT packets that create and remove ships (vmangos <c>Map::SendInitTransports</c>,
/// <c>SendRemoveTransports</c>, <c>GenericTransport::SendCreateUpdateToMap</c> / <c>SendOutOfRangeUpdateToMap</c>): one
/// packet each, sent outside the per-tick update queue, with the has-transport byte set (vmangos
/// <c>UpdateData::Send(session, hasTransport)</c>). Layout as <see cref="UpdateData"/>: u32 block count, u8 has transport,
/// then the blocks.
/// </summary>
public static class TransportPackets
{
    /// <summary>A create block for every ship in <paramref name="ships"/>, as <paramref name="viewer"/> sees it; null when there are none.</summary>
    public static PacketWriter? BuildCreate(IEnumerable<ShipTransport> ships, Player viewer, uint serverTimeMs)
    {
        ArgumentNullException.ThrowIfNull(ships);
        ArgumentNullException.ThrowIfNull(viewer);
        var body = new PacketWriter(128);
        body.WriteUInt32(0); // block count, patched below
        body.WriteByte(1);   // has transport
        uint count = 0;
        foreach (ShipTransport ship in ships)
        {
            UpdateBlockWriter.WriteCreateBlock(body, ship, viewer, isNewObject: false, serverTimeMs);
            count++;
        }

        if (count == 0)
        {
            return null;
        }

        body.PatchUInt32(0, count);
        return body;
    }

    /// <summary>One UPDATETYPE_OUT_OF_RANGE_OBJECTS block naming every ship in <paramref name="ships"/>; null when there are none.</summary>
    public static PacketWriter? BuildOutOfRange(IEnumerable<ShipTransport> ships)
    {
        ArgumentNullException.ThrowIfNull(ships);
        ShipTransport[] list = [.. ships];
        if (list.Length == 0)
        {
            return null;
        }

        var body = new PacketWriter(16 + (list.Length * 9));
        body.WriteUInt32(1); // the out-of-range list is one block
        body.WriteByte(1);   // has transport
        body.WriteByte((byte)ObjectUpdateType.OutOfRangeObjects);
        body.WriteUInt32((uint)list.Length);
        foreach (ShipTransport ship in list)
        {
            body.WritePackedGuid(ship.Guid.Value);
        }

        return body;
    }

    /// <summary>Send a body built here to <paramref name="player"/>, compressed above the world's threshold like every update packet.</summary>
    public static void Send(Player player, PacketWriter? body, int compressionThreshold)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (body is not null)
        {
            UpdateData.SendTo(body, (opcode, payload) => player.Session.Send(opcode, payload), compressionThreshold);
        }
    }
}

using System.Text;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Teleport;

/// <summary>Why a far teleport was refused (vmangos/cmangos-classic <c>TransferAbortReason</c>, 1.12).</summary>
public enum TransferAbortReason : byte
{
    /// <summary><c>TRANSFER_ABORT_MAX_PLAYERS</c>: the instance is full.</summary>
    MaxPlayers = 0x01,

    /// <summary><c>TRANSFER_ABORT_NOT_FOUND</c>: the instance was not found.</summary>
    NotFound = 0x02,

    /// <summary><c>TRANSFER_ABORT_TOO_MANY_INSTANCES</c>.</summary>
    TooManyInstances = 0x03,

    /// <summary><c>TRANSFER_ABORT_SILENTLY</c>: no message.</summary>
    Silently = 0x04,

    /// <summary><c>TRANSFER_ABORT_ZONE_IN_COMBAT</c>.</summary>
    ZoneInCombat = 0x05,
}

/// <summary>Teleport and area-trigger packet layouts (1.12.1 build 5875).</summary>
public static class TeleportPackets
{
    /// <summary>
    /// SMSG_TRANSFER_PENDING: u32 target map (vmangos <c>WorldPackets::Misc::TransferPending</c>;
    /// a transport entry and the old map follow only when travelling on a transport, which
    /// ArcaneCore has not got yet). gtker/wow_messages agrees.
    /// </summary>
    public static byte[] BuildTransferPending(uint mapId)
    {
        var packet = new PacketWriter(4);
        packet.WriteUInt32(mapId);
        return packet.AsSpan().ToArray();
    }

    /// <summary>
    /// SMSG_NEW_WORLD: u32 map, f32 x, y, z, orientation (vmangos <c>Player::SendNewWorld</c> /
    /// <c>WorldPackets::Misc::NewWorld</c>; cmangos-classic and gtker agree).
    /// </summary>
    public static byte[] BuildNewWorld(uint mapId, float x, float y, float z, float orientation)
    {
        var packet = new PacketWriter(20);
        packet.WriteUInt32(mapId);
        packet.WriteSingle(x);
        packet.WriteSingle(y);
        packet.WriteSingle(z);
        packet.WriteSingle(orientation);
        return packet.AsSpan().ToArray();
    }

    /// <summary>
    /// SMSG_TRANSFER_ABORTED: u8 reason only — vmangos and cmangos-classic both send a single
    /// byte for 1.12. Reference discrepancy: gtker/wow_messages lists u32 map + u8 reason +
    /// u8 argument; the servers win.
    /// </summary>
    public static byte[] BuildTransferAborted(TransferAbortReason reason) => [(byte)reason];

    /// <summary>
    /// MSG_MOVE_TELEPORT_ACK (server → controller): packed GUID, u32 movement counter, then the
    /// mover's movement block with the destination position (vmangos
    /// <c>MovementPacketSender::SendTeleportToController</c> for builds after 1.9.4; gtker agrees).
    /// </summary>
    public static byte[] BuildMoveTeleportAck(ObjectGuid guid, uint movementCounter, in MovementInfo movement)
    {
        var packet = new PacketWriter(64);
        packet.WritePackedGuid(guid.Value);
        packet.WriteUInt32(movementCounter);
        movement.Write(packet);
        return packet.AsSpan().ToArray();
    }

    /// <summary>
    /// MSG_MOVE_TELEPORT (server → observers): packed GUID + movement block with the destination
    /// (vmangos <c>MovementPacketSender::SendTeleportToObservers</c>). Reference discrepancy:
    /// gtker/wow_messages only lists this message for 2.4.3+, and cmangos-classic does not send
    /// it to observers; vmangos (the primary 1.12 reference) does, so ArcaneCore follows vmangos.
    /// </summary>
    public static byte[] BuildMoveTeleport(ObjectGuid guid, in MovementInfo movement)
    {
        var packet = new PacketWriter(64);
        packet.WritePackedGuid(guid.Value);
        movement.Write(packet);
        return packet.AsSpan().ToArray();
    }

    /// <summary>
    /// SMSG_AREA_TRIGGER_MESSAGE: u32 length (string bytes + terminator), then the
    /// NUL-terminated text (vmangos <c>WorldSession::SendAreaTriggerMessage</c>).
    /// </summary>
    public static byte[] BuildAreaTriggerMessage(string text)
    {
        int length = Encoding.UTF8.GetByteCount(text) + 1;
        var packet = new PacketWriter(4 + length);
        packet.WriteUInt32((uint)length);
        packet.WriteCString(text);
        return packet.AsSpan().ToArray();
    }

    /// <summary>
    /// MSG_MOVE_TELEPORT_ACK (client → server): u64 mover GUID, u32 movement counter, u32 client
    /// time. vmangos (<c>MoveTeleportAck::ReadFromWorldPacket</c>: <c>recv_data &gt;&gt; guid</c> on an
    /// ObjectGuid) and cmangos-classic both read a full 8-byte GUID. Reference discrepancy:
    /// gtker/wow_messages lists a packed GUID; the servers win.
    /// </summary>
    public static (ulong Guid, uint MovementCounter, uint Time) ReadMoveTeleportAck(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        ulong guid = reader.ReadUInt64();
        uint counter = reader.ReadUInt32();
        uint time = reader.ReadUInt32();
        return (guid, counter, time);
    }

    /// <summary>CMSG_AREATRIGGER: u32 trigger id (vmangos <c>WorldPackets::Misc::AreaTrigger</c>; gtker agrees).</summary>
    public static uint ReadAreaTrigger(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        return reader.ReadUInt32();
    }
}

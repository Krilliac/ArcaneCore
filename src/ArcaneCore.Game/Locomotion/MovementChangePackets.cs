using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Locomotion;

/// <summary>An acknowledgement of a server-ordered change: u64 mover GUID, u32 counter, movement block, and for flag toggles u32 apply.</summary>
public readonly record struct MovementChangeAck(ulong Guid, uint Counter, MovementInfo Movement, bool Apply);

/// <summary>
/// Wire layouts of the 1.12 movement-change packets. Sources: vmangos MovementPacketSender.cpp:306-440 (packed
/// GUID + counter for builds above 1.9.4), Server/Packets/Movement.cpp:25-68 (ack layouts) and gtker
/// wow_messages smsg_move_water_walk.wowm / cmsg_move_hover_ack.wowm / cmsg_move_water_walk_ack.wowm /
/// cmsg_force_move_root_ack.wowm.
/// <para>
/// GUID-width discrepancy, unchanged from the existing root order: wow_messages lists the SMSG_FORCE_MOVE_ROOT
/// family and SMSG_SPLINE_MOVE_ROOT with a full GUID for 1.12 while vmangos, cmangos-classic and mangoszero send
/// a packed GUID. Packed is kept (what the base already sends); only a real-client capture settles it.
/// </para>
/// </summary>
public static class MovementChangePackets
{
    /// <summary>SMSG_FORCE_MOVE_ROOT / UNROOT / MOVE_WATER_WALK / LAND_WALK / SET_HOVER / UNSET_HOVER / FEATHER_FALL / NORMAL_FALL: packed GUID + u32 counter.</summary>
    public static byte[] BuildFlagChange(ulong guid, uint counter)
    {
        var writer = new PacketWriter(13);
        writer.WritePackedGuid(guid);
        writer.WriteUInt32(counter);
        return writer.ToArray();
    }

    /// <summary>MSG_MOVE_ROOT / UNROOT / WATER_WALK / HOVER / FEATHER_FALL relayed to observers: packed GUID + the mover's movement block.</summary>
    public static byte[] BuildObserverRelay(ulong guid, in MovementInfo movement)
    {
        var writer = new PacketWriter(64);
        writer.WritePackedGuid(guid);
        movement.Write(writer);
        return writer.ToArray();
    }

    /// <summary>SMSG_SPLINE_MOVE_ROOT etc. sent to everyone when the server enforces an unacknowledged change: packed GUID only.</summary>
    public static byte[] BuildEnforced(ulong guid)
    {
        var writer = new PacketWriter(9);
        writer.WritePackedGuid(guid);
        return writer.ToArray();
    }

    /// <summary>CMSG_FORCE_MOVE_(UN)ROOT_ACK: u64 GUID, u32 counter, movement block (the apply flag is the opcode).</summary>
    public static MovementChangeAck ReadRootAck(ReadOnlySpan<byte> payload, bool apply)
    {
        var reader = new PacketReader(payload);
        ulong guid = reader.ReadUInt64();
        uint counter = reader.ReadUInt32();
        MovementInfo movement = MovementInfo.Read(ref reader);
        return new MovementChangeAck(guid, counter, movement, apply);
    }

    /// <summary>CMSG_MOVE_WATER_WALK_ACK / HOVER_ACK / FEATHER_FALL_ACK: u64 GUID, u32 counter, movement block, u32 apply.</summary>
    public static MovementChangeAck ReadFlagAck(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        ulong guid = reader.ReadUInt64();
        uint counter = reader.ReadUInt32();
        MovementInfo movement = MovementInfo.Read(ref reader);
        bool apply = reader.ReadUInt32() != 0;
        return new MovementChangeAck(guid, counter, movement, apply);
    }
}

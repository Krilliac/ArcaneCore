using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Locomotion;

/// <summary>vmangos UnitMoveType, without the turn rate (no 1.12 aura changes it; Unit.h).</summary>
public enum MoveType
{
    Walk,
    Run,
    RunBack,
    Swim,
    SwimBack,
}

/// <summary>An acknowledgement of a speed change: u64 GUID, u32 counter, movement block, f32 speed.</summary>
public readonly record struct SpeedAck(ulong Guid, uint Counter, MovementInfo Movement, float Speed);

/// <summary>
/// Wire layouts of the speed-change packets (vmangos MovementPacketSender.cpp:27-184, gtker wow_messages
/// smsg_force_run_speed_change.wowm / cmsg_force_run_speed_change_ack.wowm):
/// <list type="bullet">
/// <item>SMSG_FORCE_{WALK,RUN,RUN_BACK,SWIM,SWIM_BACK}_SPEED_CHANGE to the controlling client: packed GUID, u32 counter, f32 speed.</item>
/// <item>CMSG_FORCE_*_SPEED_CHANGE_ACK: u64 GUID, u32 counter, movement block, f32 speed.</item>
/// <item>MSG_MOVE_SET_*_SPEED to the observers after the ack: packed GUID, movement block, f32 speed.</item>
/// <item>SMSG_SPLINE_SET_*_SPEED to everyone for a server-moved unit (or an enforced change): packed GUID, f32 speed.</item>
/// </list>
/// </summary>
public static class SpeedPackets
{
    public static WorldOpcode ForceOpcode(MoveType type) => type switch
    {
        MoveType.Walk => WorldOpcode.SmsgForceWalkSpeedChange,
        MoveType.Run => WorldOpcode.SmsgForceRunSpeedChange,
        MoveType.RunBack => WorldOpcode.SmsgForceRunBackSpeedChange,
        MoveType.Swim => WorldOpcode.SmsgForceSwimSpeedChange,
        MoveType.SwimBack => WorldOpcode.SmsgForceSwimBackSpeedChange,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static WorldOpcode AckOpcode(MoveType type) => type switch
    {
        MoveType.Walk => WorldOpcode.CmsgForceWalkSpeedChangeAck,
        MoveType.Run => WorldOpcode.CmsgForceRunSpeedChangeAck,
        MoveType.RunBack => WorldOpcode.CmsgForceRunBackSpeedChangeAck,
        MoveType.Swim => WorldOpcode.CmsgForceSwimSpeedChangeAck,
        MoveType.SwimBack => WorldOpcode.CmsgForceSwimBackSpeedChangeAck,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static WorldOpcode ObserverOpcode(MoveType type) => type switch
    {
        MoveType.Walk => WorldOpcode.MsgMoveSetWalkSpeed,
        MoveType.Run => WorldOpcode.MsgMoveSetRunSpeed,
        MoveType.RunBack => WorldOpcode.MsgMoveSetRunBackSpeed,
        MoveType.Swim => WorldOpcode.MsgMoveSetSwimSpeed,
        MoveType.SwimBack => WorldOpcode.MsgMoveSetSwimBackSpeed,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static WorldOpcode SplineOpcode(MoveType type) => type switch
    {
        MoveType.Walk => WorldOpcode.SmsgSplineSetWalkSpeed,
        MoveType.Run => WorldOpcode.SmsgSplineSetRunSpeed,
        MoveType.RunBack => WorldOpcode.SmsgSplineSetRunBackSpeed,
        MoveType.Swim => WorldOpcode.SmsgSplineSetSwimSpeed,
        MoveType.SwimBack => WorldOpcode.SmsgSplineSetSwimBackSpeed,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static byte[] BuildForceChange(ulong guid, uint counter, float speed)
    {
        var writer = new PacketWriter(17);
        writer.WritePackedGuid(guid);
        writer.WriteUInt32(counter);
        writer.WriteSingle(speed);
        return writer.ToArray();
    }

    public static byte[] BuildObserver(ulong guid, in MovementInfo movement, float speed)
    {
        var writer = new PacketWriter(64);
        writer.WritePackedGuid(guid);
        movement.Write(writer);
        writer.WriteSingle(speed);
        return writer.ToArray();
    }

    public static byte[] BuildSpline(ulong guid, float speed)
    {
        var writer = new PacketWriter(13);
        writer.WritePackedGuid(guid);
        writer.WriteSingle(speed);
        return writer.ToArray();
    }

    public static SpeedAck ReadAck(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        ulong guid = reader.ReadUInt64();
        uint counter = reader.ReadUInt32();
        MovementInfo movement = MovementInfo.Read(ref reader);
        float speed = reader.ReadSingle();
        return new SpeedAck(guid, counter, movement, speed);
    }
}

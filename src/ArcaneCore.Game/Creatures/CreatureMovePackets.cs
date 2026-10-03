using System.Numerics;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// SMSG_MONSTER_MOVE for build 5875, linear splines.
/// <para>
/// Layout (vmangos MoveSplineInit::Launch + PacketBuilder::WriteMonsterMove /
/// WriteLinearPath; gtker/wow_messages SMSG_MONSTER_MOVE agrees for the moving form):
/// packed GUID, start Vector3, u32 spline id, u8 move type, [facing: f32 angle | Vector3 spot |
/// u64 target], u32 spline flags (without Mask_No_Monster_Move), u32 duration ms,
/// u32 point count N (the points after the start), the destination Vector3, then N − 1 packed
/// offsets of the intermediate points.
/// </para>
/// <para>
/// Intermediate points (WriteLinearPath, vmangos Movement/spline/packet_builder.cpp:77-111,
/// mangos-classic Movement/packet_builder.cpp:98-125): each is written as <c>destination − point</c>,
/// packed into a u32 as 11 bits x, 11 bits y, 10 bits z in quarter yards (ByteBuffer::appendPackXYZ);
/// an offset under 0.25 yd on every axis is nudged on z so the client never gets a zero offset.
/// <see cref="MonsterMoveOffsetBase.Midpoint"/> keeps the former start/destination midpoint layout as a rollback switch.
/// Re-implemented from the documented layout; no reference code is copied.
/// </para>
/// <para>
/// The stop form ends after the move-type byte (MonsterMoveStop = 1) in vmangos; gtker
/// models the flags/duration/points trailer as always present. Servers win (charter), so the
/// short form is sent; see docs/areas/creatures.md.
/// </para>
/// </summary>
public static class CreatureMovePackets
{
    public static byte[] BuildMove(
        ObjectGuid guid, float startX, float startY, float startZ, uint splineId,
        float? finalOrientation, bool run, uint durationMs, float destX, float destY, float destZ)
        => BuildPath(guid, new Vector3(startX, startY, startZ), splineId,
            finalOrientation is { } angle ? SplineFacing.ToAngle(angle) : SplineFacing.None,
            run, durationMs, [new Vector3(destX, destY, destZ)]);

    /// <summary>A linear path: <paramref name="points"/> holds every point after the start (destination last, at least one).</summary>
    public static byte[] BuildPath(
        ObjectGuid guid, Vector3 start, uint splineId, SplineFacing facing, bool run, uint durationMs, IReadOnlyList<Vector3> points,
        MonsterMoveOffsetBase offsetBase = MonsterMoveOffsetBase.Destination)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            throw new ArgumentException("a spline needs at least one point after the start", nameof(points));
        }

        var w = new PacketWriter(64 + (points.Count * 4));
        w.WritePackedGuid(guid.Value);
        w.WriteSingle(start.X);
        w.WriteSingle(start.Y);
        w.WriteSingle(start.Z);
        w.WriteUInt32(splineId);

        SplineFlags flags = run ? SplineFlags.Runmode : SplineFlags.None;
        w.WriteByte((byte)facing.Type);
        switch (facing.Type)
        {
            case MonsterMoveType.FacingAngle:
                w.WriteSingle(facing.Angle);
                flags |= SplineFlags.FinalAngle;
                break;
            case MonsterMoveType.FacingSpot:
                w.WriteSingle(facing.Spot.X);
                w.WriteSingle(facing.Spot.Y);
                w.WriteSingle(facing.Spot.Z);
                flags |= SplineFlags.FinalPoint;
                break;
            case MonsterMoveType.FacingTarget:
                w.WriteUInt64(facing.Target.Value);
                flags |= SplineFlags.FinalTarget;
                break;
            case MonsterMoveType.Normal:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(facing), facing.Type, "not a moving spline facing");
        }

        w.WriteUInt32((uint)(flags & ~SplineFlags.MaskNoMonsterMove));
        w.WriteUInt32(durationMs);
        w.WriteUInt32((uint)points.Count);
        Vector3 destination = points[^1];
        w.WriteSingle(destination.X);
        w.WriteSingle(destination.Y);
        w.WriteSingle(destination.Z);
        if (points.Count > 1)
        {
            Vector3 middle = (start + destination) / 2f;
            for (int i = 0; i < points.Count - 1; i++)
            {
                Vector3 offset = offsetBase == MonsterMoveOffsetBase.Midpoint
                    ? middle - points[i]
                    : AvoidZeroOffset(destination - points[i]);
                w.WriteUInt32(PackXYZ(offset));
            }
        }

        return w.ToArray();
    }

    /// <summary>
    /// "The client freezes when it gets a zero offset": an offset under a quarter yard on every axis gets +0.51 (z below
    /// zero) or +0.26 on z, so it never packs to nothing (vmangos Movement/spline/packet_builder.cpp:99-106).
    /// </summary>
    private static Vector3 AvoidZeroOffset(Vector3 offset)
    {
        if (MathF.Abs(offset.X) < 0.25f && MathF.Abs(offset.Y) < 0.25f && MathF.Abs(offset.Z) < 0.25f)
        {
            offset.Z += offset.Z < 0 ? 0.51f : 0.26f;
        }

        return offset;
    }

    /// <summary>The stop form: packed GUID, current position, spline id, u8 MonsterMoveStop (vmangos MoveSplineInit::Launch, args.flags.done).</summary>
    public static byte[] BuildStop(ObjectGuid guid, float x, float y, float z, uint splineId)
    {
        var w = new PacketWriter(32);
        w.WritePackedGuid(guid.Value);
        w.WriteSingle(x);
        w.WriteSingle(y);
        w.WriteSingle(z);
        w.WriteUInt32(splineId);
        w.WriteByte((byte)MonsterMoveType.Stop);
        return w.ToArray();
    }

    /// <summary>11/11/10-bit two's-complement quarter-yard packing of an offset (appendPackXYZ).</summary>
    public static uint PackXYZ(Vector3 offset)
    {
        uint packed = (uint)((int)(offset.X / 0.25f) & 0x7FF);
        packed |= (uint)((int)(offset.Y / 0.25f) & 0x7FF) << 11;
        packed |= (uint)((int)(offset.Z / 0.25f) & 0x3FF) << 22;
        return packed;
    }

    /// <summary>The inverse of <see cref="PackXYZ"/> (sign-extended; tests and tools).</summary>
    public static Vector3 UnpackXYZ(uint packed)
    {
        static int SignExtend(uint value, int bits) => (int)(value << (32 - bits)) >> (32 - bits);
        return new Vector3(
            SignExtend(packed & 0x7FF, 11) * 0.25f,
            SignExtend((packed >> 11) & 0x7FF, 11) * 0.25f,
            SignExtend(packed >> 22, 10) * 0.25f);
    }
}

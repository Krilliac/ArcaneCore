using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// SMSG_MONSTER_MOVE for build 5875, linear single-segment splines.
/// <para>
/// Layout (vmangos MoveSplineInit::Launch + PacketBuilder::WriteMonsterMove /
/// WriteLinearPath; gtker/wow_messages SMSG_MONSTER_MOVE agrees for the moving form):
/// packed GUID, start Vector3, u32 spline id, u8 move type, [facing: f32 angle | Vector3 spot |
/// u64 target], u32 spline flags (without Mask_No_Monster_Move), u32 duration ms,
/// u32 point count (1), destination Vector3.
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
    {
        var w = new PacketWriter(64);
        w.WritePackedGuid(guid.Value);
        w.WriteSingle(startX);
        w.WriteSingle(startY);
        w.WriteSingle(startZ);
        w.WriteUInt32(splineId);

        SplineFlags flags = run ? SplineFlags.Runmode : SplineFlags.None;
        if (finalOrientation is { } angle)
        {
            w.WriteByte((byte)MonsterMoveType.FacingAngle);
            w.WriteSingle(angle);
            flags |= SplineFlags.FinalAngle;
        }
        else
        {
            w.WriteByte((byte)MonsterMoveType.Normal);
        }

        w.WriteUInt32((uint)(flags & ~SplineFlags.MaskNoMonsterMove));
        w.WriteUInt32(durationMs);
        w.WriteUInt32(1);
        w.WriteSingle(destX);
        w.WriteSingle(destY);
        w.WriteSingle(destZ);
        return w.ToArray();
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
}

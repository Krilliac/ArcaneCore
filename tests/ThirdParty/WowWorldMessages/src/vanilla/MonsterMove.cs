using WowSrp.Header;
using WowWorldMessages.All;

namespace WowWorldMessages.Vanilla;

using MonsterMoveTypeType = OneOf.OneOf<MonsterMove.MonsterMoveTypeFacingAngle, MonsterMove.MonsterMoveTypeFacingSpot, MonsterMove.MonsterMoveTypeFacingTarget, MonsterMove.MonsterMoveTypeNormal, MonsterMoveType>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class MonsterMove {
    public class MonsterMoveTypeFacingAngle {
        public required float Angle { get; set; }
        public required uint Duration { get; set; }
        public required Vanilla.SplineFlag SplineFlags { get; set; }
        public required List<Vector3d> Splines { get; set; }
    }
    public class MonsterMoveTypeFacingSpot {
        public required uint Duration { get; set; }
        public required Vector3d Position { get; set; }
        public required Vanilla.SplineFlag SplineFlags { get; set; }
        public required List<Vector3d> Splines { get; set; }
    }
    public class MonsterMoveTypeFacingTarget {
        public required uint Duration { get; set; }
        public required Vanilla.SplineFlag SplineFlags { get; set; }
        public required List<Vector3d> Splines { get; set; }
        public required ulong Target { get; set; }
    }
    public class MonsterMoveTypeNormal {
        public required uint Duration { get; set; }
        public required Vanilla.SplineFlag SplineFlags { get; set; }
        public required List<Vector3d> Splines { get; set; }
    }
    public required Vector3d SplinePoint { get; set; }
    public required uint SplineId { get; set; }
    public required MonsterMoveTypeType MoveType { get; set; }
    internal MonsterMoveType MoveTypeValue => MoveType.Match(
        _ => Vanilla.MonsterMoveType.FacingAngle,
        _ => Vanilla.MonsterMoveType.FacingSpot,
        _ => Vanilla.MonsterMoveType.FacingTarget,
        _ => Vanilla.MonsterMoveType.Normal,
        v => v
    );

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await SplinePoint.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(SplineId, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)MoveTypeValue, cancellationToken).ConfigureAwait(false);

        if (MoveType.Value is MonsterMove.MonsterMoveTypeFacingTarget monsterMoveTypeFacingTargettap) {
            await w.WriteULong(monsterMoveTypeFacingTargettap.Target, cancellationToken).ConfigureAwait(false);

        }
        else if (MoveType.Value is MonsterMove.MonsterMoveTypeFacingAngle monsterMoveTypeFacingAnglea) {
            await w.WriteFloat(monsterMoveTypeFacingAnglea.Angle, cancellationToken).ConfigureAwait(false);

        }
        else if (MoveType.Value is MonsterMove.MonsterMoveTypeFacingSpot monsterMoveTypeFacingSpotp) {
            await monsterMoveTypeFacingSpotp.Position.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

        }

        if (MoveType.Value is MonsterMove.MonsterMoveTypeNormal monsterMoveTypeNormalsds) {
            await w.WriteUInt((uint)monsterMoveTypeNormalsds.SplineFlags, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(monsterMoveTypeNormalsds.Duration, cancellationToken).ConfigureAwait(false);

            await ReadUtils.WriteMonsterMoveSpline(w, monsterMoveTypeNormalsds.Splines, cancellationToken).ConfigureAwait(false);

        }
        else if (MoveType.Value is MonsterMove.MonsterMoveTypeFacingSpot monsterMoveTypeFacingSpotsds) {
            await w.WriteUInt((uint)monsterMoveTypeFacingSpotsds.SplineFlags, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(monsterMoveTypeFacingSpotsds.Duration, cancellationToken).ConfigureAwait(false);

            await ReadUtils.WriteMonsterMoveSpline(w, monsterMoveTypeFacingSpotsds.Splines, cancellationToken).ConfigureAwait(false);

        }
        else if (MoveType.Value is MonsterMove.MonsterMoveTypeFacingTarget monsterMoveTypeFacingTargetsds) {
            await w.WriteUInt((uint)monsterMoveTypeFacingTargetsds.SplineFlags, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(monsterMoveTypeFacingTargetsds.Duration, cancellationToken).ConfigureAwait(false);

            await ReadUtils.WriteMonsterMoveSpline(w, monsterMoveTypeFacingTargetsds.Splines, cancellationToken).ConfigureAwait(false);

        }
        else if (MoveType.Value is MonsterMove.MonsterMoveTypeFacingAngle monsterMoveTypeFacingAnglesds) {
            await w.WriteUInt((uint)monsterMoveTypeFacingAnglesds.SplineFlags, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(monsterMoveTypeFacingAnglesds.Duration, cancellationToken).ConfigureAwait(false);

            await ReadUtils.WriteMonsterMoveSpline(w, monsterMoveTypeFacingAnglesds.Splines, cancellationToken).ConfigureAwait(false);

        }

    }

    public static async Task<MonsterMove> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        ulong moveTypeIfTarget = default;
        float moveTypeIfAngle = default;
        Vector3d moveTypeIfPosition = default;
        SplineFlag moveTypeIfSplineFlags = default;
        uint moveTypeIfDuration = default;
        List<Vector3d> moveTypeIfSplines = default;

        var splinePoint = await Vector3d.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

        var splineId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        MonsterMoveTypeType moveType = (Vanilla.MonsterMoveType)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        if (moveType.Value is Vanilla.MonsterMoveType.FacingTarget) {
            moveTypeIfTarget = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        }
        else if (moveType.Value is Vanilla.MonsterMoveType.FacingAngle) {
            moveTypeIfAngle = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        }
        else if (moveType.Value is Vanilla.MonsterMoveType.FacingSpot) {
            moveTypeIfPosition = await Vector3d.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

        }

        if (moveType.Value is Vanilla.MonsterMoveType.Normal) {
            moveTypeIfSplineFlags = (SplineFlag)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            moveTypeIfDuration = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            moveTypeIfSplines = await ReadUtils.ReadMonsterMoveSpline(r, cancellationToken).ConfigureAwait(false);

        }
        else if (moveType.Value is Vanilla.MonsterMoveType.FacingSpot) {
            moveTypeIfSplineFlags = (SplineFlag)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            moveTypeIfDuration = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            moveTypeIfSplines = await ReadUtils.ReadMonsterMoveSpline(r, cancellationToken).ConfigureAwait(false);

        }
        else if (moveType.Value is Vanilla.MonsterMoveType.FacingTarget) {
            moveTypeIfSplineFlags = (SplineFlag)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            moveTypeIfDuration = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            moveTypeIfSplines = await ReadUtils.ReadMonsterMoveSpline(r, cancellationToken).ConfigureAwait(false);

        }
        else if (moveType.Value is Vanilla.MonsterMoveType.FacingAngle) {
            moveTypeIfSplineFlags = (SplineFlag)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            moveTypeIfDuration = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            moveTypeIfSplines = await ReadUtils.ReadMonsterMoveSpline(r, cancellationToken).ConfigureAwait(false);

        }

        if (moveType.Value is Vanilla.MonsterMoveType.FacingAngle) {
            moveType = new MonsterMoveTypeFacingAngle {
                Angle = moveTypeIfAngle,
                Duration = moveTypeIfDuration,
                SplineFlags = moveTypeIfSplineFlags,
                Splines = moveTypeIfSplines,
            };
        }
        else if (moveType.Value is Vanilla.MonsterMoveType.FacingSpot) {
            moveType = new MonsterMoveTypeFacingSpot {
                Duration = moveTypeIfDuration,
                Position = moveTypeIfPosition,
                SplineFlags = moveTypeIfSplineFlags,
                Splines = moveTypeIfSplines,
            };
        }
        else if (moveType.Value is Vanilla.MonsterMoveType.FacingTarget) {
            moveType = new MonsterMoveTypeFacingTarget {
                Duration = moveTypeIfDuration,
                SplineFlags = moveTypeIfSplineFlags,
                Splines = moveTypeIfSplines,
                Target = moveTypeIfTarget,
            };
        }
        else if (moveType.Value is Vanilla.MonsterMoveType.Normal) {
            moveType = new MonsterMoveTypeNormal {
                Duration = moveTypeIfDuration,
                SplineFlags = moveTypeIfSplineFlags,
                Splines = moveTypeIfSplines,
            };
        }
        return new MonsterMove {
            SplinePoint = splinePoint,
            SplineId = splineId,
            MoveType = moveType,
        };
    }

    internal int Size() {
        var size = 0;

        // spline_point: Generator.Generated.DataTypeStruct
        size += 12;

        // spline_id: Generator.Generated.DataTypeInteger
        size += 4;

        // move_type: Generator.Generated.DataTypeEnum
        size += 1;

        if (MoveType.Value is MonsterMove.MonsterMoveTypeFacingTarget monsterMoveTypeFacingTargettap) {
            // target: Generator.Generated.DataTypeGuid
            size += 8;

        }
        else if (MoveType.Value is MonsterMove.MonsterMoveTypeFacingAngle monsterMoveTypeFacingAnglea) {
            // angle: Generator.Generated.DataTypeFloatingPoint
            size += 4;

        }
        else if (MoveType.Value is MonsterMove.MonsterMoveTypeFacingSpot monsterMoveTypeFacingSpotp) {
            // position: Generator.Generated.DataTypeStruct
            size += 12;

        }

        if (MoveType.Value is MonsterMove.MonsterMoveTypeNormal monsterMoveTypeNormalsds) {
            // spline_flags: Generator.Generated.DataTypeFlag
            size += 4;

            // duration: Generator.Generated.DataTypeInteger
            size += 4;

            // splines: Generator.Generated.DataTypeMonsterMoveSpline
            size += ReadUtils.MonsterMoveSplineLength(monsterMoveTypeNormalsds.Splines);

        }
        else if (MoveType.Value is MonsterMove.MonsterMoveTypeFacingSpot monsterMoveTypeFacingSpotsds) {
            // spline_flags: Generator.Generated.DataTypeFlag
            size += 4;

            // duration: Generator.Generated.DataTypeInteger
            size += 4;

            // splines: Generator.Generated.DataTypeMonsterMoveSpline
            size += ReadUtils.MonsterMoveSplineLength(monsterMoveTypeFacingSpotsds.Splines);

        }
        else if (MoveType.Value is MonsterMove.MonsterMoveTypeFacingTarget monsterMoveTypeFacingTargetsds) {
            // spline_flags: Generator.Generated.DataTypeFlag
            size += 4;

            // duration: Generator.Generated.DataTypeInteger
            size += 4;

            // splines: Generator.Generated.DataTypeMonsterMoveSpline
            size += ReadUtils.MonsterMoveSplineLength(monsterMoveTypeFacingTargetsds.Splines);

        }
        else if (MoveType.Value is MonsterMove.MonsterMoveTypeFacingAngle monsterMoveTypeFacingAnglesds) {
            // spline_flags: Generator.Generated.DataTypeFlag
            size += 4;

            // duration: Generator.Generated.DataTypeInteger
            size += 4;

            // splines: Generator.Generated.DataTypeMonsterMoveSpline
            size += ReadUtils.MonsterMoveSplineLength(monsterMoveTypeFacingAnglesds.Splines);

        }

        return size;
    }

}


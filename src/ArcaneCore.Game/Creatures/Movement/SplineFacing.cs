using System.Numerics;

namespace ArcaneCore.Game.Creatures;

/// <summary>How a spline ends facing (SMSG_MONSTER_MOVE move type and its payload).</summary>
public readonly record struct SplineFacing(MonsterMoveType Type, float Angle, Vector3 Spot, ObjectGuid Target)
{
    public static SplineFacing None => new(MonsterMoveType.Normal, 0, default, default);

    public static SplineFacing ToAngle(float angle) => new(MonsterMoveType.FacingAngle, angle, default, default);

    public static SplineFacing ToSpot(Vector3 spot) => new(MonsterMoveType.FacingSpot, 0, spot, default);

    public static SplineFacing ToTarget(ObjectGuid target) => new(MonsterMoveType.FacingTarget, 0, default, target);

    /// <summary>The final orientation for an angle facing, else null.</summary>
    public float? FinalAngle => Type == MonsterMoveType.FacingAngle ? Angle : null;
}

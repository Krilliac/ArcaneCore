using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Ranged;

/// <summary>
/// The movement leeway of spell range checks (vmangos WorldObject::GetLeewayBonusRange,
/// Object.cpp:1890-1912, constants ObjectDefines.h:56-57): when a player is involved (as caster,
/// or as the target of a creature's cast) and both units move laterally faster than 4.97 yd/s,
/// the maximum range grows by 2.66 yd, because the positions the server sees lag behind the
/// client by about that much. Speed is derived from the movement flags
/// (Unit::GetXZFlagBasedSpeed, Unit.cpp:7112-7137), so a unit that only jumps or turns on the spot
/// has none. Only the ability branch (<c>ability = true</c>, which Spell::CheckRange always uses) is
/// implemented; the flags-only branch is for melee auto attacks.
/// </summary>
public static class RangeLeeway
{
    /// <summary>LEEWAY_MIN_MOVE_SPEED.</summary>
    public const float MinMoveSpeed = 4.97f;

    /// <summary>LEEWAY_BONUS_RANGE.</summary>
    public const float BonusRange = 2.66f;

    private const MovementFlags LateralMask = MovementFlags.Forward | MovementFlags.Backward | MovementFlags.StrafeLeft | MovementFlags.StrafeRight;

    /// <summary>vmangos Unit::GetXZFlagBasedSpeed: 0 unless moving laterally; swim, walk, run-back or run speed otherwise.</summary>
    public static float XzFlagBasedSpeed(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        MovementFlags flags = unit.Movement.Flags;
        if ((flags & LateralMask) == 0)
        {
            return 0.0f;
        }

        bool backward = (flags & MovementFlags.Backward) != 0;
        if ((flags & MovementFlags.Swimming) != 0)
        {
            return backward ? unit.SwimBackSpeed : unit.SwimSpeed;
        }

        if ((flags & MovementFlags.WalkMode) != 0)
        {
            return unit.WalkSpeed;
        }

        return backward ? unit.RunBackSpeed : unit.RunSpeed;
    }

    /// <summary>The range bonus for <paramref name="caster"/> casting at <paramref name="target"/> (0 or <see cref="BonusRange"/>).</summary>
    public static float Bonus(Unit caster, Unit? target)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (target is null || (caster is not Player && target is not Player))
        {
            return 0.0f;
        }

        return XzFlagBasedSpeed(caster) > MinMoveSpeed && XzFlagBasedSpeed(target) > MinMoveSpeed ? BonusRange : 0.0f;
    }
}

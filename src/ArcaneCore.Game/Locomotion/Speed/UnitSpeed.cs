using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Locomotion;

/// <summary>
/// The movement speeds of a unit as vmangos computes and changes them (Unit::UpdateSpeed, Unit::SetSpeedRate,
/// Unit::SetSpeedRateReal, Unit.cpp:6959-7190). The speed auras recompute the rate from the aura ledger
/// (<see cref="UpdateSpeed"/>); a change reaches a player-controlled unit as the order/ack handshake of
/// <see cref="PendingMovementChanges"/> and takes effect on the ack, everything else changes at once.
/// <para>
/// A rate is a multiple of the base speed (<see cref="Unit.BaseRunSpeed"/> and friends); the speeds the unit stores
/// and sends are rate times base. Creatures are not recomputed here: their template rates and the wounded slowdown
/// belong to the creature speed slice, so a creature keeps the speed its template gave it
/// (docs/areas/locomotion.md).
/// </para>
/// </summary>
public static class UnitSpeed
{
    /// <summary>The base speed of a movement type (vmangos baseMoveSpeed, Unit.cpp:67-74).</summary>
    public static float BaseSpeed(MoveType type) => type switch
    {
        MoveType.Walk => Unit.BaseWalkSpeed,
        MoveType.Run => Unit.BaseRunSpeed,
        MoveType.RunBack => Unit.BaseRunBackSpeed,
        MoveType.Swim => Unit.BaseSwimSpeed,
        MoveType.SwimBack => Unit.BaseSwimBackSpeed,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>The unit's current speed in yards per second.</summary>
    public static float Get(Unit unit, MoveType type)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return type switch
        {
            MoveType.Walk => unit.WalkSpeed,
            MoveType.Run => unit.RunSpeed,
            MoveType.RunBack => unit.RunBackSpeed,
            MoveType.Swim => unit.SwimSpeed,
            MoveType.SwimBack => unit.SwimBackSpeed,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }

    /// <summary>Set the speed on the server side (vmangos SetSpeedRateReal: no packet, the create block now carries it).</summary>
    public static void SetReal(Unit unit, MoveType type, float speed)
    {
        ArgumentNullException.ThrowIfNull(unit);
        switch (type)
        {
            case MoveType.Walk: unit.WalkSpeed = speed; break;
            case MoveType.Run: unit.RunSpeed = speed; break;
            case MoveType.RunBack: unit.RunBackSpeed = speed; break;
            case MoveType.Swim: unit.SwimSpeed = speed; break;
            case MoveType.SwimBack: unit.SwimBackSpeed = speed; break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    public static MovementChangeType ChangeTypeOf(MoveType type) => type switch
    {
        MoveType.Walk => MovementChangeType.SpeedWalk,
        MoveType.Run => MovementChangeType.SpeedRun,
        MoveType.RunBack => MovementChangeType.SpeedRunBack,
        MoveType.Swim => MovementChangeType.SpeedSwim,
        MoveType.SwimBack => MovementChangeType.SpeedSwimBack,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static bool IsSpeedChange(MovementChangeType type) => type >= MovementChangeType.SpeedWalk;

    public static MoveType MoveTypeOf(MovementChangeType type) => type switch
    {
        MovementChangeType.SpeedWalk => MoveType.Walk,
        MovementChangeType.SpeedRun => MoveType.Run,
        MovementChangeType.SpeedRunBack => MoveType.RunBack,
        MovementChangeType.SpeedSwim => MoveType.Swim,
        MovementChangeType.SpeedSwimBack => MoveType.SwimBack,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>vmangos Unit::IsMounted: a mount display is set (UNIT_FIELD_MOUNTDISPLAYID; 1.12 has no mount unit flag).</summary>
    public static bool IsMounted(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit.GetUInt32(UpdateFields.UnitFieldMountdisplayid) != 0;
    }

    /// <summary>
    /// The rate the auras on the unit give for a movement type (vmangos Unit::UpdateSpeed, Unit.cpp:6959-7061 and
    /// :7086-7100), for the unit kinds that are computed here (players). Returns null for swim-back (never updated,
    /// :7005).
    /// <list type="bullet">
    /// <item>Run: not mounted, the strongest MOD_INCREASE_SPEED (31), the product of MOD_SPEED_ALWAYS (129) and the
    /// strongest MOD_SPEED_NOT_STACK (171); mounted, the same with 32, 130 and 172. The larger of the stacking and
    /// the non-stacking bonus is used, and the main modifier multiplies it: <c>bonus * (100 + main) / 100</c>.</item>
    /// <item>Swim: the strongest MOD_INCREASE_SWIM_SPEED (58). Walk and run-back have no increase.</item>
    /// <item>USE_NORMAL_MOVEMENT_SPEED (191) caps run and swim at <c>amount / base</c>.</item>
    /// <item>A player whose death state is CORPSE is multiplied by the ghost rate (:7040-7046; 1 by default).</item>
    /// <item>Run, run-back and swim are then multiplied by the strongest slow, MOD_DECREASE_SPEED (33).</item>
    /// </list>
    /// </summary>
    public static float? ComputeRate(Unit unit, MoveType type, LocomotionOptions options, bool inBattleground)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(options);
        AuraLedger auras = unit.Locomotion.Auras;
        int mainSpeedMod = 0;
        float stackBonus = 1.0f;
        float nonStackBonus = 1.0f;
        switch (type)
        {
            case MoveType.Walk:
            case MoveType.RunBack:
                break;
            case MoveType.Run:
                if (IsMounted(unit))
                {
                    mainSpeedMod = auras.MaxPositive(AuraType.ModIncreaseMountedSpeed);
                    stackBonus = auras.Multiplier(AuraType.ModMountedSpeedAlways);
                    nonStackBonus = (100.0f + auras.MaxPositive(AuraType.ModMountedSpeedNotStack)) / 100.0f;
                }
                else
                {
                    mainSpeedMod = auras.MaxPositive(AuraType.ModIncreaseSpeed);
                    stackBonus = auras.Multiplier(AuraType.ModSpeedAlways);
                    nonStackBonus = (100.0f + auras.MaxPositive(AuraType.ModSpeedNotStack)) / 100.0f;
                }

                break;
            case MoveType.Swim:
                mainSpeedMod = auras.MaxPositive(AuraType.ModIncreaseSwimSpeed);
                break;
            default:
                return null;
        }

        float bonus = nonStackBonus > stackBonus ? nonStackBonus : stackBonus;
        float speed = mainSpeedMod != 0 ? bonus * (100.0f + mainSpeedMod) / 100.0f : bonus;

        if (type is MoveType.Run or MoveType.Swim)
        {
            int normalization = auras.MaxPositive(AuraType.UseNormalMovementSpeed);
            if (normalization != 0)
            {
                float maxSpeed = normalization / BaseSpeed(type);
                if (speed > maxSpeed)
                {
                    speed = maxSpeed;
                }
            }
        }

        if (unit is Player player && player.Combat.DeathState == DeathState.Corpse)
        {
            speed *= inBattleground ? options.GhostRunSpeedBattleground : options.GhostRunSpeedWorld;
        }

        if (type is MoveType.Run or MoveType.RunBack or MoveType.Swim)
        {
            int slow = auras.MaxNegative(AuraType.ModDecreaseSpeed);
            if (slow != 0)
            {
                speed *= (100.0f + slow) / 100.0f;
            }
        }

        return speed;
    }

    /// <summary>
    /// Recompute one speed from the unit's auras and apply it (vmangos Unit::UpdateSpeed(mtype, forced = false)).
    /// Only players are computed here; other units keep their speed.
    /// </summary>
    public static void UpdateSpeed(Unit unit, MoveType type)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit is not Player)
        {
            return;
        }

        LocomotionOptions options = LocomotionEnvironment.For(unit.Map).Options;
        float? rate = ComputeRate(unit, type, options, unit.Map?.Template is { IsBattleground: true });
        if (rate is { } value)
        {
            SetRate(unit, type, value);
        }
    }

    /// <summary>
    /// Change a rate (vmangos Unit::SetSpeedRate, Unit.cpp:7152-7183). A change that leaves the speed as it is and has no
    /// pending order is dropped. A player in a map is ordered with SMSG_FORCE_*_SPEED_CHANGE (packed GUID, counter,
    /// speed) and the speed changes on its ack; a player that is not in a map cannot answer, so the speed is set at
    /// once without a packet (login restore, "explanation of (1)"); any other unit changes at once and everyone is told
    /// with SMSG_SPLINE_SET_*_SPEED.
    /// </summary>
    public static void SetRate(Unit unit, MoveType type, float rate)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (rate < 0)
        {
            rate = 0.0f;
        }

        float newSpeed = rate * BaseSpeed(type);
        MovementChangeType changeType = ChangeTypeOf(type);
        LocomotionState state = unit.Locomotion;
        if (Get(unit, type) == newSpeed && !state.Pending.HasPendingOfType(changeType))
        {
            return;
        }

        if (unit is Player player)
        {
            if (player.Map is null)
            {
                SetReal(unit, type, newSpeed);
                return;
            }

            uint counter = player.NextMovementCounter();
            state.Pending.Push(counter, changeType, apply: true, newSpeed);
            player.Session.Send(SpeedPackets.ForceOpcode(type), SpeedPackets.BuildForceChange(player.Guid.Value, counter, newSpeed));
            return;
        }

        SetReal(unit, type, newSpeed);
        CombatPackets.SendToSet(unit, SpeedPackets.SplineOpcode(type), SpeedPackets.BuildSpline(unit.Guid.Value, newSpeed));
    }
}

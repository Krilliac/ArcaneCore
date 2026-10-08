using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
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
/// and sends are rate times base. Creatures include their template rates and the wounded slowdown
/// (<see cref="ComputeRate"/>).
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

    public static bool IsSpeedChange(MovementChangeType type) => type is >= MovementChangeType.SpeedWalk and <= MovementChangeType.SpeedSwimBack;

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

        if (unit is Creature creature)
        {
            speed = ApplyCreatureFactors(creature, type, speed);
        }

        return speed;
    }

    /// <summary>vmangos CREATURE_STATIC_FLAG_2_NO_WOUNDED_SLOWDOWN (CreatureDefines.h:140): "Does not reduce run speed at low health".</summary>
    public const uint NoWoundedSlowdownFlag = 0x00000040;

    /// <summary>
    /// The creature part of Unit::UpdateSpeed (:7063-7094): run and walk are multiplied by the template's speed rate
    /// (<c>speed_run</c>, default 1.14286 = <see cref="Creature.DefaultRunSpeedRate"/>; <c>speed_walk</c>, default 1), and a
    /// run speed is multiplied by 0.7, 0.6 or 0.5 while the creature is under 16%, 11% or 6% health, unless it is a world
    /// boss or has the no-wounded-slowdown static flag (:7080-7090; creature.cpp:971-973 sets the three aura states from
    /// the health percent). Pets of players are normalised to the default run rate in vmangos; there are no pets on this base.
    /// <para>
    /// vmangos refreshes the three health aura states every creature update but only recomputes the run speed when
    /// something calls UpdateSpeed (a speed aura, fleeing at low health, returning from an assist call). The health used
    /// here is the one at that recompute; the AI lane calls <see cref="UpdateSpeed"/> where vmangos does.
    /// </para>
    /// </summary>
    private static float ApplyCreatureFactors(Creature creature, MoveType type, float speed)
    {
        switch (type)
        {
            case MoveType.Run:
                speed *= creature.Template.SpeedRun > 0 ? creature.Template.SpeedRun : Creature.DefaultRunSpeedRate;
                break;
            case MoveType.Walk:
                speed *= creature.Template.SpeedWalk > 0 ? creature.Template.SpeedWalk : 1.0f;
                break;
        }

        if (type == MoveType.Run && !creature.IsWorldBoss && (creature.Template.StaticFlags2 & NoWoundedSlowdownFlag) == 0 && creature.MaxHealth > 0)
        {
            float healthPercent = creature.Health * 100.0f / creature.MaxHealth;
            if (healthPercent < 6.0f)
            {
                speed *= 0.5f;   // SPEED_REDUCTION_HP_5
            }
            else if (healthPercent < 11.0f)
            {
                speed *= 0.6f;   // SPEED_REDUCTION_HP_10
            }
            else if (healthPercent < 16.0f)
            {
                speed *= 0.7f;   // SPEED_REDUCTION_HP_15
            }
        }

        return speed;
    }

    /// <summary>
    /// Recompute one speed from the unit's auras and apply it (vmangos Unit::UpdateSpeed(mtype, forced = false)) for players
    /// and creatures; any other unit keeps its speed.
    /// </summary>
    public static void UpdateSpeed(Unit unit, MoveType type)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit is not (Player or Creature))
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
    /// <para>
    /// The speed is the rate times the base speed times the unit's configured rate (<see cref="LocomotionState.ConfiguredSpeedRates"/>: the
    /// <c>Locomotion:Player*SpeedRate</c> options on a player, 1 on anything else), so every order, the create block and the server's own speed
    /// carry the effective value (<see cref="SpeedRates"/>).
    /// </para>
    /// </summary>
    public static void SetRate(Unit unit, MoveType type, float rate)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (rate < 0)
        {
            rate = 0.0f;
        }

        float newSpeed = rate * BaseSpeed(type) * ConfiguredRate(unit, type);
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

    /// <summary>The configured multiplier of a movement type for <paramref name="unit"/>: a player's <c>Locomotion:Player*SpeedRate</c>, else 1.</summary>
    public static float ConfiguredRate(Unit unit, MoveType type)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit is Player && LocomotionStates.TryGet(unit, out LocomotionState state) ? state.ConfiguredSpeedRates.For(type) : 1.0f;
    }

    /// <summary>
    /// Change the turn rate (vmangos Unit::SetSpeedRate(MOVE_TURN_RATE, rate), Unit.cpp:7152-7183; base 3.141594 rad/s, times a player's
    /// <c>Locomotion:PlayerTurnRate</c>). No 1.12 aura changes it, so only the configured rate does. A change that leaves the rate as it is is
    /// dropped. A player in a map is sent SMSG_FORCE_TURN_RATE_CHANGE (packed GUID, counter, rate) and the server takes the new rate at once:
    /// unlike the five speeds it is not held in the pending-change ledger, whose enforcement knows only those (deviation: vmangos applies it on the
    /// ack, MovementHandler.cpp:415-534; the ack, CMSG_FORCE_TURN_RATE_CHANGE_ACK, only relays MSG_MOVE_SET_TURN_RATE to the observers). A player
    /// not in a map has it set for its create block; any other unit changes at once and everyone is told with SMSG_SPLINE_SET_TURN_RATE.
    /// </summary>
    public static void SetTurnRate(Unit unit, float rate)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (rate < 0)
        {
            rate = 0.0f;
        }

        float turn = rate * Unit.BaseTurnRate * (unit is Player && LocomotionStates.TryGet(unit, out LocomotionState state) ? state.ConfiguredSpeedRates.Turn : 1.0f);
        if (unit.TurnRate == turn)
        {
            return;
        }

        unit.TurnRate = turn;
        if (unit is Player player)
        {
            if (player.Map is not null)
            {
                player.Session.Send(TurnRatePackets.Force, SpeedPackets.BuildForceChange(player.Guid.Value, player.NextMovementCounter(), turn));
            }

            return;
        }

        CombatPackets.SendToSet(unit, TurnRatePackets.Spline, SpeedPackets.BuildSpline(unit.Guid.Value, turn));
    }
}

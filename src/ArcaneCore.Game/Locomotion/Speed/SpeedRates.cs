using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Locomotion;

/// <summary>
/// The configured multipliers of a player's movement speeds (<see cref="LocomotionOptions.GetSpeedRates"/>): run, run back, swim, swim back and
/// walk already include <see cref="LocomotionOptions.PlayerSpeedRate"/>; <see cref="Turn"/> is the turn rate's own. All 1 is retail.
/// </summary>
public readonly record struct PlayerSpeedRates(float Walk, float Run, float RunBack, float Swim, float SwimBack, float Turn)
{
    /// <summary>Every multiplier 1: the retail speeds.</summary>
    public static PlayerSpeedRates Retail { get; } = new(1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f);

    /// <summary>The multiplier of a movement type.</summary>
    public float For(MoveType type) => type switch
    {
        MoveType.Walk => Walk,
        MoveType.Run => Run,
        MoveType.RunBack => RunBack,
        MoveType.Swim => Swim,
        MoveType.SwimBack => SwimBack,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

/// <summary>
/// The configured player speed rates (<see cref="LocomotionOptions.PlayerSpeedRate"/> and the per-type rates). The rates are copied onto the
/// player (<see cref="LocomotionState.ConfiguredSpeedRates"/>) and <see cref="UnitSpeed.SetRate"/> multiplies the base speed by them, so the
/// aura-driven speed changes, the force-speed packets, the create block and the server's own idea of the speed all carry the effective value
/// (the MaNGOS Zero fork multiplies inside Unit::UpdateSpeed's player block, UnitSpeed.cpp:162-184; this is the same place one call later,
/// which also covers swim back, a type vmangos' UpdateSpeed never recomputes, Unit.cpp:7005).
/// </summary>
public static class SpeedRates
{
    private static readonly MoveType[] Types = [MoveType.Walk, MoveType.Run, MoveType.RunBack, MoveType.Swim, MoveType.SwimBack];

    /// <summary>
    /// Give <paramref name="player"/> the rates and re-send every speed they change (a player in a map is ordered with SMSG_FORCE_*_SPEED_CHANGE
    /// and SMSG_FORCE_TURN_RATE_CHANGE; one that is not in a map yet, at login, has the speeds set at once for its create block).
    /// <paramref name="options"/> supplies the ghost rates of the recomputation.
    /// </summary>
    public static void Apply(Player player, PlayerSpeedRates rates, LocomotionOptions options)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        player.Locomotion.ConfiguredSpeedRates = rates;
        Refresh(player, options);
    }

    /// <summary>
    /// Recompute every speed of <paramref name="player"/> from its auras (vmangos Unit::UpdateSpeed for run, run back, swim and walk; swim back's
    /// rate is always 1 because UpdateSpeed never changes it) and the turn rate, and apply them with the player's configured rates.
    /// </summary>
    public static void Refresh(Player player, LocomotionOptions options)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        bool inBattleground = player.Map?.Template is { IsBattleground: true };
        foreach (MoveType type in Types)
        {
            UnitSpeed.SetRate(player, type, UnitSpeed.ComputeRate(player, type, options, inBattleground) ?? 1.0f);
        }

        UnitSpeed.SetTurnRate(player, 1.0f);
    }

    /// <summary>
    /// The options changed (<c>.reload config</c>, <c>.movement set</c>): give every online player of <paramref name="world"/> the rates of
    /// <paramref name="options"/> and re-send its speeds. Returns how many players were refreshed. World thread.
    /// </summary>
    public static int ApplyToAll(WorldRuntime world, LocomotionOptions options)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(options);
        PlayerSpeedRates rates = options.GetSpeedRates();
        int count = 0;
        foreach (Player player in world.OnlinePlayers.ToArray())
        {
            Apply(player, rates, options);
            count++;
        }

        return count;
    }
}

/// <summary>
/// Wire layouts of the turn rate packets, the same shapes as the speed ones (<see cref="SpeedPackets"/>; vmangos MovementPacketSender.cpp,
/// gtker wow_messages smsg_force_turn_rate_change.wowm / cmsg_force_turn_rate_change_ack.wowm / msg_move_set_turn_rate.wowm /
/// smsg_spline_set_turn_rate.wowm).
/// </summary>
public static class TurnRatePackets
{
    public const WorldOpcode Force = WorldOpcode.SmsgForceTurnRateChange;

    public const WorldOpcode Ack = WorldOpcode.CmsgForceTurnRateChangeAck;

    public const WorldOpcode Observer = WorldOpcode.MsgMoveSetTurnRate;

    public const WorldOpcode Spline = WorldOpcode.SmsgSplineSetTurnRate;
}

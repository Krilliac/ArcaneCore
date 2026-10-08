using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Locomotion;

/// <summary>
/// The configured player speed rates (<c>Locomotion:PlayerSpeedRate</c> and the per-type rates, non-retail when not 1; the MaNGOS Zero fork's
/// Movement.*SpeedRate). A loading character gets the rates before the world sees it, so its create block already carries the effective speeds
/// (<see cref="OnPlayerLoadedAsync"/>, after the auras are restored); a player that entered the world another way is caught up at
/// <see cref="WorldRuntime.PlayerLoggedIn"/>. <c>.reload config</c> and <c>.movement set</c> re-send everyone's speeds
/// (<see cref="SpeedRates.ApplyToAll"/>).
/// </summary>
public sealed class SpeedRateFeature(ILogger<SpeedRateFeature> logger) : IWorldFeature, ICharacterHooks
{
    private WorldRuntime? _world;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        world.PlayerLoggedIn += CatchUp;
        if (LocomotionEnvironment.RegisteredOptions(world) is { } options && options.GetSpeedRates() != PlayerSpeedRates.Retail)
        {
            PlayerSpeedRates rates = options.GetSpeedRates();
            logger.LogWarning(
                "non-retail player speed rates: walk {Walk}, run {Run}, run back {RunBack}, swim {Swim}, swim back {SwimBack}, turn {Turn}",
                rates.Walk, rates.Run, rates.RunBack, rates.Swim, rates.SwimBack, rates.Turn);
        }
    }

    /// <summary>Session task, before the world sees the player: set the effective speeds without a packet (the player has no map yet).</summary>
    public Task OnPlayerLoadedAsync(WorldSession session, CharacterRecord character, Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (Options() is { } options && (options.GetSpeedRates() != PlayerSpeedRates.Retail || player.Locomotion.ConfiguredSpeedRates != PlayerSpeedRates.Retail))
        {
            SpeedRates.Apply(player, options.GetSpeedRates(), options);
        }

        return Task.CompletedTask;
    }

    // World thread: a player whose copy differs from the options (one that did not load through the character hooks) is ordered to the
    // configured speeds.
    private void CatchUp(Player player)
    {
        if (Options() is { } options && player.Locomotion.ConfiguredSpeedRates != options.GetSpeedRates())
        {
            SpeedRates.Apply(player, options.GetSpeedRates(), options);
        }
    }

    private LocomotionOptions? Options() => _world is null ? null : LocomotionEnvironment.RegisteredOptions(_world);
}

/// <summary>
/// CMSG_FORCE_TURN_RATE_CHANGE_ACK (vmangos WorldSession::HandleForceSpeedChangeAckOpcodes, MovementHandler.cpp:415-534, the turn rate case):
/// u64 GUID, u32 counter, movement block, f32 rate, the layout of the speed acks (<see cref="SpeedPackets.ReadAck"/>). The server already took
/// the rate when it sent the order (<see cref="UnitSpeed.SetTurnRate"/>), so an ack for the player's own GUID that reports that rate (within
/// 0.01, the speed ack's tolerance) stores the movement block and is relayed to the observers as MSG_MOVE_SET_TURN_RATE; anything else is ignored.
/// </summary>
public sealed class TurnRateAckHandler : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table) => table.OnWorld(TurnRatePackets.Ack, Handle);

    private static void Handle(WorldSession session, Player player, byte[] payload)
    {
        if (session.Services.GetRequiredService<TeleportFeature>().Teleports.IsBeingTeleported(player))
        {
            return;
        }

        SpeedAck ack = SpeedPackets.ReadAck(payload);
        if (ack.Guid != player.Guid.Value || !MovementHandlers.IsAcceptable(session, ack.Movement) || MathF.Abs(ack.Speed - player.TurnRate) > 0.01f)
        {
            return;
        }

        MovementHandlers.ApplyObserved(session, player, TurnRatePackets.Ack, ack.Movement);
        player.Map?.BroadcastToObservers(player, TurnRatePackets.Observer, SpeedPackets.BuildObserver(player.Guid.Value, player.Movement, player.TurnRate));
    }
}

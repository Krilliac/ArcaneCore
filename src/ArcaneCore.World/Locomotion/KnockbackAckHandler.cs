using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Locomotion;

/// <summary>
/// CMSG_MOVE_KNOCK_BACK_ACK (vmangos HandleMoveKnockBackAck, MovementHandler.cpp:747-802): u64 GUID, u32 counter, movement block.
/// The block's jump section (direction cosine and sine, horizontal and vertical speed) must repeat the order within 0.01. A match
/// ends the fall in progress, stores the block through the movement observers and relays MSG_MOVE_KNOCK_BACK (packed GUID,
/// block, the four numbers) to the observers, not to the mover. An ack for another unit, one that matches no order or one that
/// arrives while the player is being teleported is ignored (the first two counted).
/// </summary>
public sealed class KnockbackAckHandler : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
        => table.OnWorld(WorldOpcode.CmsgMoveKnockBackAck, Handle);

    private static void Handle(WorldSession session, Player player, byte[] payload)
    {
        if (session.Services.GetRequiredService<TeleportFeature>().Teleports.IsBeingTeleported(player))
        {
            return;
        }

        KnockbackAck ack = KnockbackPackets.ReadAck(payload);
        if (ack.Guid != player.Guid.Value)
        {
            return;
        }

        if (!MovementHandlers.IsAcceptable(session, ack.Movement))
        {
            return;
        }
        if (KnockbackService.Acknowledge(player, ack.Counter, ack.Movement) is null)
        {
            return;
        }

        // vmangos clears the fall (SetFallInformation(0), :796) and then relocates without UpdateFallInformationIfNeed, so the
        // block that starts the launch does not start a fall: the fall tracking of the observers is undone after the block is stored.
        // The anticheat checks the acknowledgement's clock and starts a new baseline after the launch (docs/areas/anticheat.md).
        AntiCheat.AntiCheatFeature? antiCheat = session.Services.GetService<AntiCheat.AntiCheatFeature>();
        antiCheat?.OnAcknowledgement(session, player, ack.Movement.Time, knockback: true);
        MovementHandlers.ApplyObserved(session, player, WorldOpcode.CmsgMoveKnockBackAck, ack.Movement);
        antiCheat?.AfterMovement(player);
        player.Locomotion.ResetFall();

        // vmangos relays what the client acknowledged (the jump block of its own movement info).
        var info = new KnockbackInfo(ack.Movement.JumpCosAngle, ack.Movement.JumpSinAngle, ack.Movement.JumpXySpeed, ack.Movement.JumpZSpeed);
        player.Map?.BroadcastToObservers(player, WorldOpcode.MsgMoveKnockBack, KnockbackPackets.BuildObserver(player.Guid.Value, player.Movement, info));
    }
}

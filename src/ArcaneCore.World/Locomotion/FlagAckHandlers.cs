using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Locomotion;

/// <summary>
/// CMSG_MOVE_WATER_WALK_ACK / CMSG_MOVE_HOVER_ACK / CMSG_MOVE_FEATHER_FALL_ACK (vmangos
/// WorldSession::HandleMovementFlagChangeToggleAck, MovementHandler.cpp:536-644): u64 GUID, u32 counter, movement block,
/// u32 apply. The ack is honoured only when it matches a pending change (counter, apply flag, type); a match stores the
/// block through the movement observers, sets or clears the flag on the server side and relays MSG_MOVE_WATER_WALK /
/// HOVER / FEATHER_FALL (packed GUID + block) to the observers, not to the mover.
/// </summary>
public sealed class FlagAckHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgMoveWaterWalkAck, (session, player, payload) => Handle(session, player, payload, WorldOpcode.CmsgMoveWaterWalkAck, MovementChangeType.WaterWalk));
        table.OnWorld(WorldOpcode.CmsgMoveHoverAck, (session, player, payload) => Handle(session, player, payload, WorldOpcode.CmsgMoveHoverAck, MovementChangeType.Hover));
        table.OnWorld(WorldOpcode.CmsgMoveFeatherFallAck, (session, player, payload) => Handle(session, player, payload, WorldOpcode.CmsgMoveFeatherFallAck, MovementChangeType.FeatherFall));
    }

    private static void Handle(WorldSession session, Player player, byte[] payload, WorldOpcode opcode, MovementChangeType type)
    {
        if (session.Services.GetRequiredService<TeleportFeature>().Teleports.IsBeingTeleported(player))
        {
            return;
        }

        MovementChangeAck ack = MovementChangePackets.ReadFlagAck(payload);
        if (ack.Guid != player.Guid.Value)
        {
            return;
        }

        if (!MovementHandlers.IsAcceptable(session, ack.Movement))
        {
            return;
        }
        if (!MovementControl.Acknowledge(player, type, ack.Counter, ack.Apply))
        {
            return;
        }

        MovementHandlers.ApplyObserved(session, player, opcode, ack.Movement);
        MovementControl.ApplyReal(player, type, ack.Apply);
        MovementControl.RelayToObservers(player, type, ack.Apply);
    }
}

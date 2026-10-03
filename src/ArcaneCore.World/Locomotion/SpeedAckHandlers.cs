using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Locomotion;

/// <summary>
/// CMSG_FORCE_{WALK,RUN,RUN_BACK,SWIM,SWIM_BACK}_SPEED_CHANGE_ACK (vmangos
/// WorldSession::HandleForceSpeedChangeAckOpcodes, MovementHandler.cpp:415-534): u64 GUID, u32 counter, movement block,
/// f32 speed. The ack must match a pending speed change of that type by counter and, within 0.01, speed; the speed
/// the client reports is then the one that is applied, before the block is stored ("the speed has to be applied before
/// relocation"). The change is relayed to the observers as MSG_MOVE_SET_*_SPEED (packed GUID, movement block, speed),
/// not to the mover. An ack that matches nothing is ignored and counted.
/// </summary>
public sealed class SpeedAckHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        foreach (MoveType type in Enum.GetValues<MoveType>())
        {
            MoveType captured = type;
            table.OnWorld(SpeedPackets.AckOpcode(type), (session, player, payload) => Handle(session, player, payload, captured));
        }
    }

    private static void Handle(WorldSession session, Player player, byte[] payload, MoveType type)
    {
        if (session.Services.GetRequiredService<TeleportFeature>().Teleports.IsBeingTeleported(player))
        {
            return;
        }

        SpeedAck ack = SpeedPackets.ReadAck(payload);
        if (ack.Guid != player.Guid.Value)
        {
            return;
        }

        MovementHandlers.EnsureFinite(ack.Movement);
        if (!MovementControl.AcknowledgeSpeed(player, type, ack.Counter, ack.Speed))
        {
            return;
        }

        UnitSpeed.SetReal(player, type, ack.Speed);
        MovementHandlers.ApplyObserved(session, player, SpeedPackets.AckOpcode(type), ack.Movement);
        player.Map?.BroadcastToObservers(player, SpeedPackets.ObserverOpcode(type), SpeedPackets.BuildObserver(player.Guid.Value, player.Movement, ack.Speed));
    }
}

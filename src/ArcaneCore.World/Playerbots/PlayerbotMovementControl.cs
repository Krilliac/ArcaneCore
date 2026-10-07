using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>Acknowledges only this player's actual server-owned pending orders through ordinary handlers.</summary>
internal static class PlayerbotMovementControl
{
    internal static bool Update(WorldSession session, Player player)
    {
        if (!PlayerbotMotion.CanMove(player)) PlayerbotMotion.Reset(player);
        TeleportService? teleports = session.Services.GetService<TeleportFeature>()?.Teleports;
        switch (teleports?.StageOf(player))
        {
            case TeleportStage.Far:
                PlayerbotMotion.Reset(player);
                session.TryManagedAction(WorldOpcode.MsgMoveWorldportAck, []);
                return true;
            case TeleportStage.Near:
                PlayerbotMotion.Reset(player);
                var teleport = new PacketWriter();
                teleport.WriteUInt64(player.Guid.Value); teleport.WriteUInt32(0); teleport.WriteUInt32(session.World.NowMs);
                session.TryManagedAction(WorldOpcode.MsgMoveTeleportAck, teleport.ToArray());
                return true;
            case TeleportStage.FarScheduled or TeleportStage.Arriving:
                PlayerbotMotion.Reset(player);
                return true;
        }
        PendingMovementChange? change = player.Locomotion.Pending.Changes.FirstOrDefault(c => c.Type != MovementChangeType.KnockBack);
        if (change is null) return false;
        // The acknowledgement carries (and the handler may relay) the stored movement block: bring it up to the
        // bot's current route position first, or observers would see it jump back to its last heartbeat.
        PlayerbotMotion.Flush(session, player, session.World.NowMs);
        MovementInfo movement = player.Movement;
        movement.Time = session.World.NowMs;
        WorldOpcode opcode;
        var ack = new PacketWriter();
        ack.WriteUInt64(player.Guid.Value); ack.WriteUInt32(change.Counter);
        if (change.Type is MovementChangeType.Root or MovementChangeType.WaterWalk or MovementChangeType.Hover or MovementChangeType.FeatherFall)
        {
            MovementFlags flag = MovementChangeInfo.FlagOf(change.Type);
            movement.Flags = change.Apply ? movement.Flags | flag : movement.Flags & ~flag;
            if (change.Type == MovementChangeType.Root && change.Apply) movement.Flags &= ~MovementFlags.MaskMoving;
            movement.Write(ack);
            opcode = change.Type switch
            {
                MovementChangeType.Root => change.Apply ? WorldOpcode.CmsgForceMoveRootAck : WorldOpcode.CmsgForceMoveUnrootAck,
                MovementChangeType.WaterWalk => WorldOpcode.CmsgMoveWaterWalkAck,
                MovementChangeType.Hover => WorldOpcode.CmsgMoveHoverAck,
                _ => WorldOpcode.CmsgMoveFeatherFallAck,
            };
            if (change.Type != MovementChangeType.Root) ack.WriteUInt32(change.Apply ? 1u : 0u);
        }
        else
        {
            MoveType type = change.Type switch
            {
                MovementChangeType.SpeedWalk => MoveType.Walk,
                MovementChangeType.SpeedRun => MoveType.Run,
                MovementChangeType.SpeedRunBack => MoveType.RunBack,
                MovementChangeType.SpeedSwim => MoveType.Swim,
                MovementChangeType.SpeedSwimBack => MoveType.SwimBack,
                _ => throw new InvalidOperationException("unsupported managed movement order"),
            };
            movement.Write(ack); ack.WriteSingle(change.NewValue);
            opcode = SpeedPackets.AckOpcode(type);
        }
        if (session.TryManagedAction(opcode, ack.ToArray())) PlayerbotMotion.RefreshSpeed(player);
        return true;
    }

    /// <summary>
    /// Stop moving now: MSG_MOVE_STOP where observers already see the bot (its current route position). Never refused
    /// for lack of action budget - a client always reports its own stop. True when the bot is stopped afterwards.
    /// </summary>
    internal static bool Stop(WorldSession session, Player player) => PlayerbotMotion.Stop(session, player);
}

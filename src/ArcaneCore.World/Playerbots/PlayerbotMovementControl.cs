using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;
using System.Numerics;

namespace ArcaneCore.World.Playerbots;

/// <summary>Acknowledges only this player's actual server-owned pending orders through ordinary handlers.</summary>
internal static class PlayerbotMovementControl
{
    private sealed class StopState
    {
        internal bool Pending;
        internal MotionProjection? Projection;
    }
    private sealed record MotionProjection(Map Map, PlayerbotRoute Route, Vector3 Position, int NextPoint, float Speed, uint Time);
    private static readonly ConditionalWeakTable<WorldSession, StopState> PendingStops = [];

    internal static bool Update(WorldSession session, Player player)
    {
        if (!player.IsAlive) Invalidate(session);
        TeleportService? teleports = session.Services.GetService<TeleportFeature>()?.Teleports;
        switch (teleports?.StageOf(player))
        {
            case TeleportStage.Far:
                Invalidate(session);
                session.TryManagedAction(WorldOpcode.MsgMoveWorldportAck, []);
                return true;
            case TeleportStage.Near:
                Invalidate(session);
                var teleport = new PacketWriter();
                teleport.WriteUInt64(player.Guid.Value); teleport.WriteUInt32(0); teleport.WriteUInt32(session.World.NowMs);
                session.TryManagedAction(WorldOpcode.MsgMoveTeleportAck, teleport.ToArray());
                return true;
            case TeleportStage.FarScheduled or TeleportStage.Arriving:
                Invalidate(session);
                return true;
        }
        PendingMovementChange? change = player.Locomotion.Pending.Changes.FirstOrDefault(c => c.Type != MovementChangeType.KnockBack);
        if (change is null)
        {
            StopState state = PendingStops.GetOrCreateValue(session);
            if (!state.Pending) return false;
            bool sent = SendStop(session, player, state.Projection);
            if (sent) { state.Pending = false; state.Projection = null; }
            return true;
        }
        Invalidate(session);
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
        session.TryManagedAction(opcode, ack.ToArray());
        return true;
    }

    internal static bool Stop(WorldSession session, Player player)
    {
        StopState state = PendingStops.GetOrCreateValue(session);
        if ((player.Movement.Flags & MovementFlags.MaskMoving) == 0)
        {
            state.Pending = false;
            state.Projection = null;
            return true;
        }
        bool sent = SendStop(session, player, state.Projection);
        state.Pending = !sent;
        if (sent) state.Projection = null;
        return sent;
    }

    internal static void Track(WorldSession session, Player player, PlayerbotRoute route, Vector3 position,
        int nextPoint, float speed, uint serverTimeMs)
    {
        StopState state = PendingStops.GetOrCreateValue(session);
        state.Pending = false;
        state.Projection = player.Map is { } map && Finite(position) && float.IsFinite(speed) && speed > 0
            && nextPoint >= 1 && nextPoint <= route.Points.Count
            ? new(map, route, position, nextPoint, speed, serverTimeMs)
            : null;
    }

    internal static void Invalidate(WorldSession session)
        => PendingStops.GetOrCreateValue(session).Projection = null;

    private static bool SendStop(WorldSession session, Player player, MotionProjection? projection)
    {
        MovementInfo movement = player.Movement;
        Map? currentMap = player.Map;
        movement.X = player.X; movement.Y = player.Y; movement.Z = player.Z;
        movement.Orientation = player.Orientation;
        PlayerbotRoute? projectedRoute = null;
        int projectedNextPoint = 0;
        if (projection is { } motion && player.IsAlive && ReferenceEquals(player.Map, motion.Map)
            && (movement.Flags & (MovementFlags.Root | MovementFlags.Jumping)) == 0
            && Vector3.Distance(new(player.X, player.Y, player.Z), motion.Position) <= 0.5f
            && motion.Route.NextPoint == motion.NextPoint)
        {
            uint age = unchecked(session.World.NowMs - motion.Time);
            uint elapsed = age <= int.MaxValue ? Math.Min(age, 1000u) : 0;
            float distance = motion.Speed * elapsed / 1000f;
            if (PlayerbotNavigation.TryStepOnMap(motion.Route, motion.Position, distance, motion.Map,
                    out Vector3 projected, out int nextPoint)
                && Finite(projected))
            {
                movement.X = projected.X; movement.Y = projected.Y; movement.Z = projected.Z;
                projectedRoute = motion.Route;
                projectedNextPoint = nextPoint;
            }
        }
        // A complete/blocked/stale route still needs STOP at the safe current position.
        // Refusing projection must not leave client prediction running forever.
        if (!session.TryManagedAction(WorldOpcode.MsgMoveStop, Packet(movement, session.World.NowMs))) return false;
        bool accepted = ReferenceEquals(player.Map, currentMap)
            && (player.Movement.Flags & MovementFlags.MaskMoving) == 0
            && Vector3.Distance(new(player.X, player.Y, player.Z), new(movement.X, movement.Y, movement.Z)) <= 0.5f;
        if (accepted && projectedRoute is not null) projectedRoute.NextPoint = projectedNextPoint;
        return accepted;
    }

    private static byte[] Packet(MovementInfo movement, uint time)
    {
        movement.Flags &= ~MovementFlags.MaskMoving;
        movement.Time = time;
        var writer = new PacketWriter(); movement.Write(writer); return writer.ToArray();
    }

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

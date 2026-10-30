using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Locomotion;

/// <summary>
/// The movement-change handshake of a player-controlled unit (vmangos MovementPacketSender
/// AddMovementFlagChangeToController / SendMovementFlagChangeToObservers / SendMovementFlagChangeToAll and
/// Unit::Set{Rooted,WaterWalking,Hover,FeatherFall}, Unit.cpp:7195-7296): the server sends the order and
/// records it in the <see cref="PendingMovementChanges"/> ledger; the state itself changes only when the matching
/// ack arrives (<see cref="Acknowledge"/> accepts it and the caller then calls <see cref="ApplyReal"/>) or the ack times out (<see cref="Enforce"/>). A player that is not in a
/// map yet cannot answer, so the state is set directly and nothing is sent.
/// </summary>
public static class MovementControl
{
    /// <summary>
    /// Ask a player's client to set or clear a movement state. In the world this sends the order and records the
    /// pending change; out of the world (login restore) the flag is set directly. A request that changes nothing
    /// (the flag already is in the wanted state and no change of that type is pending) is dropped
    /// (Unit::SetWaterWalking's guard, Unit.cpp:7239).
    /// </summary>
    public static void Request(Player player, MovementChangeType type, bool apply)
    {
        ArgumentNullException.ThrowIfNull(player);
        bool has = player.Movement.HasFlag(MovementChangeInfo.FlagOf(type));
        if (has == apply && !player.Locomotion.Pending.HasPendingOfType(type))
        {
            return;
        }

        if (player.Map is null)
        {
            ApplyReal(player, type, apply);
            return;
        }

        Order(player, type, apply);
    }

    /// <summary>Send the order to the controlling client and record it as pending (counter shared with teleports).</summary>
    public static void Order(Player player, MovementChangeType type, bool apply)
    {
        ArgumentNullException.ThrowIfNull(player);
        uint counter = player.NextMovementCounter();
        player.Locomotion.Pending.Push(counter, type, apply);
        player.Session.Send(MovementChangeInfo.ControllerOpcode(type, apply), MovementChangePackets.BuildFlagChange(player.Guid.Value, counter));
    }

    /// <summary>Set or clear the flag of a change on the server side (vmangos Unit::Set*Real).</summary>
    public static void ApplyReal(Unit unit, MovementChangeType type, bool apply)
    {
        ArgumentNullException.ThrowIfNull(unit);
        MovementFlags flag = MovementChangeInfo.FlagOf(type);
        if (apply)
        {
            unit.AddMovementFlags(flag);
        }
        else
        {
            unit.RemoveMovementFlags(flag);
        }
    }

    /// <summary>
    /// The client's ack for a change: it must match a pending change by counter, apply flag and type
    /// (vmangos HandleMovementFlagChangeToggleAck / HandleMoveRootAck, MovementHandler.cpp:536-745). Returns false,
    /// and counts a wrong ack, when nothing matches; the caller then ignores the packet. On success the pending entry is
    /// removed from the pending ledger and true is returned; the caller then applies the state itself with <see cref="ApplyReal"/>
    /// (after it has stored the client's movement block), and relays the block to the observers.
    /// </summary>
    public static bool Acknowledge(Player player, MovementChangeType type, uint counter, bool apply)
    {
        ArgumentNullException.ThrowIfNull(player);
        LocomotionState state = player.Locomotion;
        if (!state.Pending.TryAcknowledge(counter, type, apply))
        {
            state.NoteWrongAck();
            return false;
        }

        return true;
    }

    /// <summary>
    /// The client's ack for a speed change (vmangos HandleForceSpeedChangeAckOpcodes): it must match a pending change of the
    /// type by counter and, within 0.01, speed. False when nothing matches; the mismatch is a counted wrong ack only when the counter is 0 or above the
    /// current counter (as <see cref="AcknowledgeTurnRate"/>; vmangos OnWrongAckData, MovementHandler.cpp:463-483), so a late ack of an order that already
    /// timed out is not counted twice. On success the
    /// caller applies <paramref name="speed"/> (the speed the client reports) with <see cref="UnitSpeed.SetReal"/>.
    /// </summary>
    public static bool AcknowledgeSpeed(Player player, MoveType type, uint counter, float speed)
    {
        ArgumentNullException.ThrowIfNull(player);
        LocomotionState state = player.Locomotion;
        if (!state.Pending.TryAcknowledgeSpeed(counter, UnitSpeed.ChangeTypeOf(type), speed))
        {
            if (counter == 0 || counter > player.MovementCounter)
            {
                state.NoteWrongAck();
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// The client's ack for a turn rate order (vmangos HandleForceSpeedChangeAckOpcodes, MOVE_TURN_RATE, MovementHandler.cpp:464-476): it must match a
    /// pending turn rate change by counter and, within 0.01, rate (a NaN never matches). False when nothing matches; the mismatch is a counted wrong ack
    /// only when the counter is 0 or above the current counter, the next one to be sent (a counter equal to it is not counted; vmangos OnWrongAckData, MovementHandler.cpp:468-469 and :481-482), so a late ack of an order that
    /// already timed out is not counted (the timeout counted it as a failed ack).
    /// </summary>
    public static bool AcknowledgeTurnRate(Player player, uint counter, float rate)
    {
        ArgumentNullException.ThrowIfNull(player);
        LocomotionState state = player.Locomotion;
        if (!state.Pending.TryAcknowledgeSpeed(counter, MovementChangeType.TurnRate, rate))
        {
            if (counter == 0 || counter > player.MovementCounter)
            {
                state.NoteWrongAck();
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// The server applies a change itself (vmangos ResolvePendingMovementChange, Unit.cpp:6719): a root also stops
    /// motion (the moving flags are cleared), and with <paramref name="sendToClient"/> everyone is told with the
    /// SMSG_SPLINE_MOVE_* packet (SendMovementFlagChangeToAll).
    /// </summary>
    public static void Enforce(Unit unit, PendingMovementChange change, bool sendToClient)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(change);
        if (change.Type == MovementChangeType.TurnRate)
        {
            // The rate was applied when the order was sent (UnitSpeed.SetTurnRate; vmangos applies it on the ack), so only the
            // announcement of SendSpeedChangeToAll is left (ResolvePendingMovementChange, Unit.cpp:6772-6774).
            if (sendToClient)
            {
                CombatPackets.SendToSet(unit, TurnRatePackets.Spline, SpeedPackets.BuildSpline(unit.Guid.Value, change.NewValue));
            }

            return;
        }

        if (change.Type == MovementChangeType.KnockBack)
        {
            // "Not resendable change", dropped, not enforced (CheckPendingMovementChanges, Unit.cpp:6670-6675);
            // the resolve switch has no knock back case either.
            return;
        }

        if (UnitSpeed.IsSpeedChange(change.Type))
        {
            // ResolvePendingMovementChange, speed cases (Unit.cpp:6767-6797): SetSpeedRateReal + SendSpeedChangeToAll.
            MoveType moveType = UnitSpeed.MoveTypeOf(change.Type);
            UnitSpeed.SetReal(unit, moveType, change.NewValue);
            if (sendToClient)
            {
                CombatPackets.SendToSet(unit, SpeedPackets.SplineOpcode(moveType), SpeedPackets.BuildSpline(unit.Guid.Value, change.NewValue));
            }

            return;
        }

        if (change is { Type: MovementChangeType.Root, Apply: true })
        {
            unit.RemoveMovementFlags(MovementFlags.MaskMoving);
        }

        ApplyReal(unit, change.Type, change.Apply);
        if (sendToClient)
        {
            CombatPackets.SendToSet(unit, MovementChangeInfo.EnforcedOpcode(change.Type, change.Apply), MovementChangePackets.BuildEnforced(unit.Guid.Value));
        }
    }

    /// <summary>
    /// Tell the observers after an ack (SendMovementFlagChangeToObservers): packed GUID + the mover's movement block
    /// as MSG_MOVE_ROOT / UNROOT / WATER_WALK / HOVER / FEATHER_FALL, not to the mover itself.
    /// </summary>
    public static void RelayToObservers(Player player, MovementChangeType type, bool apply)
    {
        ArgumentNullException.ThrowIfNull(player);
        player.Map?.BroadcastToObservers(
            player,
            MovementChangeInfo.ObserverOpcode(type, apply),
            MovementChangePackets.BuildObserverRelay(player.Guid.Value, player.Movement));
    }
}

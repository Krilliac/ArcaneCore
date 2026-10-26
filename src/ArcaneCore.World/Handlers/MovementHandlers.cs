using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// Client movement (vmangos MovementHandler.cpp HandleMovementOpcodes): store the new state,
/// flag the player for a visibility pass, and relay the movement to every client that sees it.
/// </summary>
public sealed class MovementHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        foreach (WorldOpcode opcode in MovementOpcodes.Relayable)
        {
            table.OnWorld(opcode, (session, player, payload) => HandleMovement(session, player, opcode, payload, relay: true));
        }

        // Sent when a jump hits something on the way up; applied, never relayed (vmangos).
        table.OnWorld(WorldOpcode.CmsgMoveFallReset,
            (session, player, payload) => HandleMovement(session, player, WorldOpcode.CmsgMoveFallReset, payload, relay: false));

        table.OnWorld(WorldOpcode.CmsgMoveTimeSkipped, HandleMoveTimeSkipped);
        table.OnWorld(WorldOpcode.CmsgMoveNotActiveMover, HandleMoveNotActiveMover);
    }

    /// <summary>
    /// CMSG_MOVE_TIME_SKIPPED: u64 mover GUID, u32 skipped milliseconds (vmangos, cmangos-classic
    /// and mangoszero read a full GUID). Relayed to observers as MSG_MOVE_TIME_SKIPPED with a
    /// packed GUID, as vmangos and cmangos-classic do, so they keep interpolating correctly.
    /// One that comes after boarding a ship and before any movement aboard is not relayed: the ship is sent to the
    /// player again instead ("fix an 1.12 client problem with transports", vmangos MovementHandler.cpp:1001-1010; the
    /// first movement aboard clears the mark, 1087-1089).
    /// </summary>
    private static void HandleMoveTimeSkipped(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        ulong guid = reader.ReadUInt64();
        uint lag = reader.ReadUInt32();
        if (guid == player.Guid.Value
            && Game.Transports.TransportSystem.Of(session.World) is { } transports
            && transports.TakeJustBoarded(player))
        {
            if (player.Transport is { } ship)
            {
                transports.ResendTo(ship, player);
            }

            return;
        }

        if (guid == player.Guid.Value)
        {
            session.Services.GetService<AntiCheat.AntiCheatFeature>()?.OnTimeSkipped(session, player, lag);
            player.Map?.BroadcastToObservers(player, WorldOpcode.MsgMoveTimeSkipped, MiscPackets.BuildMoveTimeSkipped(player.Guid, lag));
        }
        else if (player.GetMover() is { } mover && !ReferenceEquals(mover, player) && mover.Guid.Value == guid)
        {
            // vmangos GetMoverFromGuid: a possessed unit's skip goes to its observers too.
            mover.Map?.BroadcastToObservers(mover, WorldOpcode.MsgMoveTimeSkipped, MiscPackets.BuildMoveTimeSkipped(mover.Guid, lag), except: player);
        }
    }

    /// <summary>
    /// CMSG_MOVE_NOT_ACTIVE_MOVER (build &gt; 1.9.4: u64 old mover GUID, then the movement block; vmangos HandleMoveNotActiveMoverOpcode,
    /// MovementHandler.cpp:893-972): the client gives up the unit it moved, with its last position. The unit is relocated there and the
    /// position goes to its observers as MSG_MOVE_HEARTBEAT (still moving) or MSG_MOVE_STOP.
    /// </summary>
    private static void HandleMoveNotActiveMover(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        var oldMover = new ObjectGuid(reader.ReadUInt64());
        MovementInfo movement = MovementInfo.Read(ref reader);
        // vmangos ignores the block while the moved player (pPlayerMover), not the sender, is being teleported.
        Game.Teleport.TeleportService teleports = session.Services.GetRequiredService<TeleportFeature>().Teleports;
        if (CharmService.HandleMoveNotActiveMover(player, oldMover, teleports.IsBeingTeleported) is not { } unit || !IsAcceptable(session, movement))
        {
            return;
        }

        if (unit.Map is null)
        {
            return;
        }

        WorldOpcode relay = (movement.Flags & MovementFlags.MaskMoving) != 0 ? WorldOpcode.MsgMoveHeartbeat : WorldOpcode.MsgMoveStop;
        if (unit is Player moved)
        {
            ApplyObserved(session, moved, relay, movement);
        }
        else if (unit is Creature creature && !creature.IsMoving)
        {
            creature.ApplyMovement(movement, session.World.NowMs);

            // vmangos HandleMovementOpcodes (MovementHandler.cpp:1163): a creature mover runs HandleInterruptsOnMovement with MOVEFLAG_MASK_MOVING.
            if ((creature.Movement.Flags & MovementFlags.MaskMoving) != 0)
            {
                creature.ClearEmoteState();
            }
        }
        else
        {
            return;
        }

        var packet = new PacketWriter(payload.Length + 9);
        packet.WritePackedGuid(unit.Guid.Value);
        unit.Movement.Write(packet);
        RelayControlled(player, unit, relay, packet.AsSpan());
    }

    private static void HandleMovement(WorldSession session, Player player, WorldOpcode opcode, byte[] payload, bool relay)
    {
        if (player.Map is null)
        {
            return;
        }

        // vmangos HandleMovementOpcodes ignores movement while either teleport semaphore is set.
        if (session.Services.GetRequiredService<TeleportFeature>().Teleports.IsBeingTeleported(player))
        {
            return;
        }

        // On a taxi flight the server moves the player (FlightPathMovementGenerator); client movement is ignored.
        if ((player.UnitFlags & UnitFlags.TaxiFlight) != 0)
        {
            return;
        }

        // "Do not accept packets sent before this time" (vmangos HandleMovementOpcodes, MovementHandler.cpp:293-296): the 100 ms after the
        // server set the movement flags itself (.cheat fly / fixedz), so a packet sent before the client saw them cannot undo them.
        if (player.Locomotion.ShouldRejectMovement(session.CurrentPacketReceivedMs))
        {
            return;
        }

        // Client→server movement is the MovementInfo alone (gtker MSG_MOVE_*_Client, 1.12).
        var reader = new PacketReader(payload);
        MovementInfo movement = MovementInfo.Read(ref reader);

        // An invalid packet is dropped, not stored, relayed or punished with a kick
        // (vmangos HandleMovementOpcodes: VerifyMovementInfo, MovementHandler.cpp:315,:489,:1042-1061).
        if (!IsAcceptable(session, movement))
        {
            return;
        }

        // vmangos GetConfirmedMover (MovementHandler.cpp:298-300): a possessed or charmed player moves nothing, a player that possesses
        // another unit moves that unit once its client has switched to it (docs/areas/unit-control.md).
        Unit? mover = player.GetConfirmedMover();
        if (mover is null || mover.Map is null)
        {
            return;
        }

        if (!ReferenceEquals(mover, player))
        {
            MoveControlled(session, player, mover, opcode, movement, relay);
            return;
        }

        // The anticheat judges the block as the client sent it, before the observers correct it (docs/areas/anticheat.md).
        AntiCheat.AntiCheatFeature? antiCheat = session.Services.GetService<AntiCheat.AntiCheatFeature>();
        antiCheat?.BeforeMovement(session, player, opcode, movement);
        ApplyObserved(session, player, opcode, movement);
        antiCheat?.AfterMovement(player);
        CheckAndHealLifeStateDesync(session, player, opcode, in movement);
        // MovementHandler.cpp:1115-1131: a free move that is not a long fall is the unstuck point (SaveNoUndermapPosition: z + 3, then + 2).
        if (!session.IsInChaosProbe
            && !movement.HasFlag(MovementFlags.FallingFar) && !movement.HasFlag(MovementFlags.OnTransport)
            && (player.UnitFlags & (UnitFlags.RemoveClientControl | UnitFlags.Stunned | UnitFlags.Confused | UnitFlags.Fleeing)) == 0)
        {
            player.LastSafePosition = (player.MapId, movement.X, movement.Y, movement.Z + 5.0f, movement.Orientation);
        }
        if (!relay || session.IsInChaosProbe)
        {
            return;
        }

        RelayStoredMovement(player, opcode);
    }

    /// <summary>
    /// Detect and self-heal client/server life-state and root split-brain conditions on real clients:
    /// if a player in <see cref="DeathState.Corpse"/> or <see cref="DeathState.JustDied"/> continues sending moving packets
    /// after the root order was acknowledged or enforced, log a rate-limited warning, re-order <c>SMSG_FORCE_MOVE_ROOT</c>,
    /// and force-refresh the client's life/death update fields and visibility.
    /// </summary>
    private static void CheckAndHealLifeStateDesync(WorldSession session, Player player, WorldOpcode opcode, in MovementInfo movement)
    {
        if (session.IsPlayerbot)
        {
            return;
        }

        DeathState deathState = player.Combat.DeathState;
        if (deathState is DeathState.Corpse or DeathState.JustDied
            && (movement.Flags & MovementFlags.MaskMoving) != 0
            && !player.Locomotion.Pending.HasPendingOfType(MovementChangeType.Root))
        {
            uint nowMs = session.World.NowMs;
            if (player.Combat.LastLifeDesyncLogMs == 0 || nowMs - player.Combat.LastLifeDesyncLogMs >= 5000u)
            {
                player.Combat.LastLifeDesyncLogMs = nowMs == 0 ? 1u : nowMs;
                session.Logger.LogWarning(
                    "[{Endpoint}] client-state desync detected for '{Player}' (guid={Guid}, map={MapId}): client sent moving {Opcode} (flags=0x{Flags:X}) while in {DeathState} (health={Health}, isRooted={IsRooted}, deathTimer={DeathTimer}ms); re-ordering root and forcing life-state fields",
                    session.RemoteEndpoint, player.Name, player.Guid.Low, player.MapId, WorldOpcodeNames.GetName(opcode), (uint)movement.Flags,
                    deathState, player.Health, player.IsRooted, player.Combat.DeathTimer);
                player.SetRooted(true);
                MovementControl.Order(player, MovementChangeType.Root, true);
                player.ForceFieldUpdate(UpdateFields.UnitFieldHealth);
                player.ForceFieldUpdate(UpdateFields.UnitFieldBytes1);
                player.ForceFieldUpdate(UpdateFields.PlayerFlags);
                player.ForceFieldUpdate(UpdateFields.PlayerFieldBytes);
                player.NeedsVisibilityUpdate = true;
            }
        }
        else if (deathState == DeathState.Alive && (player.Health == 0 || (player.Flags & PlayerFlags.Ghost) != 0))
        {
            uint nowMs = session.World.NowMs;
            if (player.Combat.LastLifeDesyncLogMs == 0 || nowMs - player.Combat.LastLifeDesyncLogMs >= 5000u)
            {
                player.Combat.LastLifeDesyncLogMs = nowMs == 0 ? 1u : nowMs;
                session.Logger.LogWarning(
                    "[{Endpoint}] life-state invariant violation for '{Player}' (guid={Guid}, map={MapId}): DeathState is Alive but health={Health}, flags=0x{PlayerFlags:X}",
                    session.RemoteEndpoint, player.Name, player.Guid.Low, player.MapId, player.Health, (uint)player.Flags);
            }
        }
    }

    /// <summary>
    /// A server-managed bot's own movement without the client path (<c>World:Playerbots:MovementPackets</c> = false): the same
    /// admission as <see cref="HandleMovement"/> (not while teleported or on a taxi, a valid block, the bot moves itself), the same
    /// relocation with the locomotion observers, and the same relay to the bot's observers, but no opcode dispatch and no
    /// encode/decode of a client packet. False when the movement was not applied.
    /// </summary>
    internal static bool MoveManagedBot(WorldSession session, Player player, WorldOpcode opcode, MovementInfo movement)
    {
        movement = AsDecoded(movement);
        if (player.Map is null
            || !MovementOpcodes.IsRelayable(opcode)
            || session.Services.GetRequiredService<TeleportFeature>().Teleports.IsBeingTeleported(player)
            || (player.UnitFlags & UnitFlags.TaxiFlight) != 0
            || !IsAcceptable(session, movement)
            || !ReferenceEquals(player.GetConfirmedMover(), player))
        {
            return false;
        }

        ApplyObserved(session, player, opcode, movement);
        RelayStoredMovement(player, opcode);
        return true;
    }

    /// <summary>
    /// The block as <see cref="MovementInfo.Read"/> would return it after <see cref="MovementInfo.Write"/>: the fields its flags do not
    /// announce are zero, so the server-applied path stores exactly what the packet path stores.
    /// </summary>
    private static MovementInfo AsDecoded(MovementInfo movement)
    {
        if (!movement.HasFlag(MovementFlags.OnTransport))
        {
            movement.TransportGuid = 0;
            movement.TransportX = movement.TransportY = movement.TransportZ = movement.TransportOrientation = 0;
        }

        if (!movement.HasFlag(MovementFlags.Swimming)) movement.Pitch = 0;
        if (!movement.HasFlag(MovementFlags.Jumping)) movement.JumpZSpeed = movement.JumpCosAngle = movement.JumpSinAngle = movement.JumpXySpeed = 0;
        if (!movement.HasFlag(MovementFlags.SplineElevation)) movement.SplineElevation = 0;
        return movement;
    }

    /// <summary>
    /// Relay a player's stored movement to every observer: packed mover GUID + the movement block carrying the server receive time
    /// (vmangos/cmangos-classic MovementInfo::Write sends stime).
    /// </summary>
    internal static void RelayStoredMovement(Player player, WorldOpcode opcode)
    {
        if (player.Map is not { } map)
        {
            return;
        }

        var packet = new PacketWriter(64);
        packet.WritePackedGuid(player.Guid.Value);
        player.Movement.Write(packet);
        map.BroadcastToObservers(player, opcode, packet.AsSpan());
    }

    /// <summary>
    /// vmangos HandleMoverRelocation (MovementHandler.cpp:1062-1162) for a unit the player possesses: a possessed player gets the block
    /// through its own locomotion observers; a creature that the server is not moving along a spline is relocated
    /// (Map::CreatureRelocation). The block is relayed to everyone who sees the unit except the controller, the unit's own client
    /// included (vmangos SendMovementMessageToSet(data, true, _player)).
    /// </summary>
    private static void MoveControlled(WorldSession session, Player controller, Unit mover, WorldOpcode opcode, MovementInfo movement, bool relay)
    {
        if (mover.Map is null)
        {
            return;
        }

        if (mover is Player moved)
        {
            ApplyObserved(session, moved, opcode, movement);
        }
        else if (mover is Creature creature)
        {
            // currently being moved by server
            if (creature.IsMoving)
            {
                return;
            }

            creature.ApplyMovement(movement, session.World.NowMs);

            // vmangos HandleMovementOpcodes (MovementHandler.cpp:1163): a creature mover runs HandleInterruptsOnMovement with MOVEFLAG_MASK_MOVING.
            if ((creature.Movement.Flags & MovementFlags.MaskMoving) != 0)
            {
                creature.ClearEmoteState();
            }
        }

        if (!relay)
        {
            return;
        }

        var packet = new PacketWriter(64);
        packet.WritePackedGuid(mover.Guid.Value);
        mover.Movement.Write(packet);
        RelayControlled(controller, mover, opcode, packet.AsSpan());
    }

    private static void RelayControlled(Player controller, Unit mover, WorldOpcode opcode, ReadOnlySpan<byte> packet)
    {
        mover.Map?.BroadcastToObservers(mover, opcode, packet, except: controller);
        if (mover is Player moved && !ReferenceEquals(moved, controller))
        {
            moved.Session.Send(opcode, packet);
        }
    }

    /// <summary>
    /// Store a client's movement block with the locomotion observers around it (vmangos HandleMovementOpcodes
    /// :333-344 and HandleMoverRelocation :1062-1170): the Before observers see the previously stored block and
    /// may correct the incoming one, the After observers see the stored result. Shared with the movement-change
    /// acks (HandleMoveRootAck runs the same relocation).
    /// </summary>
    internal static void ApplyObserved(WorldSession session, Player player, WorldOpcode opcode, MovementInfo movement)
    {
        bool previousSuppress = player.Locomotion.SuppressAnomalies;
        if (session.IsInChaosProbe)
        {
            player.Locomotion.SuppressAnomalies = true;
        }

        try
        {
            MovementInfo previous = player.Movement;
            var context = new MovementObserverContext(player, session.World, opcode);
            MovementObservers.Before(context, in previous, ref movement, session.Logger);
            Invariant.Check(
                float.IsFinite(movement.X) && float.IsFinite(movement.Y) && float.IsFinite(movement.Z) && float.IsFinite(movement.Orientation),
                $"MovementHandlers.ApplyObserved non-finite coordinates after observers for player {player.Guid.Low}");
            player.ApplyClientMovement(movement, session.World.NowMs);
            MovementObservers.After(context, in previous, session.Logger);

            // vmangos Player::SetPosition (Player.cpp:5949-5958) -> Unit::HandleInterruptsOnMovement (Unit.cpp:10131-10140), reached from
            // HandleMovementOpcodes -> HandleMoverRelocation (MovementHandler.cpp:1113): a changed position, or MOVEFLAG_MASK_XZ on the
            // block (forward, backward, strafe; not pitch, jump or turning), ends a text-emote state (/dance). Players and managed bots
            // both land here.
            MovementInfo stored = player.Movement;
            bool positionChanged = stored.X != previous.X || stored.Y != previous.Y || stored.Z != previous.Z;
            if (positionChanged || (stored.Flags & MovementFlags.MaskXz) != 0)
            {
                player.ClearEmoteState();
            }
        }
        finally
        {
            player.Locomotion.SuppressAnomalies = previousSuppress;
        }
    }

    /// <summary>
    /// A malformed movement block is dropped, not kicked (vmangos VerifyMovementInfo, MovementHandler.cpp:1042-1061);
    /// shared with the movement-change acks.
    /// </summary>
    internal static bool IsAcceptable(WorldSession session, in MovementInfo movement)
    {
        bool chaosProbe = session.IsInChaosProbe;
        if (!float.IsFinite(movement.X) || !float.IsFinite(movement.Y) || !float.IsFinite(movement.Z) || !float.IsFinite(movement.Orientation))
        {
            if (chaosProbe)
            {
                session.Logger.LogDebug("[{Endpoint}] chaos-probe movement packet rejected: non-finite world coordinates or orientation", session.RemoteEndpoint);
            }
            else
            {
                session.Logger.LogWarning("[{Endpoint}] movement packet rejected: non-finite world coordinates or orientation", session.RemoteEndpoint);
                ReportNonFiniteMovement(session, $"Non-finite world coordinates or orientation in movement packet: pos=({movement.X}, {movement.Y}, {movement.Z}, o={movement.Orientation}).");
            }
            return false;
        }

        if (movement.HasFlag(MovementFlags.OnTransport)
            && (!float.IsFinite(movement.TransportX) || !float.IsFinite(movement.TransportY)
                || !float.IsFinite(movement.TransportZ) || !float.IsFinite(movement.TransportOrientation)))
        {
            if (chaosProbe)
            {
                session.Logger.LogDebug("[{Endpoint}] chaos-probe movement packet rejected: non-finite transport coordinates or orientation", session.RemoteEndpoint);
            }
            else
            {
                session.Logger.LogWarning("[{Endpoint}] movement packet rejected: non-finite transport coordinates or orientation", session.RemoteEndpoint);
                ReportNonFiniteMovement(session, $"Non-finite transport coordinates or orientation in movement packet: transport=0x{movement.TransportGuid:X} offset=({movement.TransportX}, {movement.TransportY}, {movement.TransportZ}, o={movement.TransportOrientation}).");
            }
            return false;
        }

        if (session.StrictMovementFiniteness
            && (!float.IsFinite(movement.Pitch) || !float.IsFinite(movement.JumpZSpeed)
                || !float.IsFinite(movement.JumpCosAngle) || !float.IsFinite(movement.JumpSinAngle)
                || !float.IsFinite(movement.JumpXySpeed) || !float.IsFinite(movement.SplineElevation)))
        {
            if (chaosProbe)
            {
                session.Logger.LogDebug("[{Endpoint}] chaos-probe movement packet rejected: non-finite pitch/jump/spline field under strict finiteness", session.RemoteEndpoint);
            }
            else
            {
                session.Logger.LogWarning("[{Endpoint}] movement packet rejected: non-finite pitch/jump/spline field under strict finiteness", session.RemoteEndpoint);
                ReportNonFiniteMovement(session, $"Non-finite pitch/jump/spline field in movement packet: pitch={movement.Pitch}, jumpZ={movement.JumpZSpeed}, jumpCos={movement.JumpCosAngle}, jumpSin={movement.JumpSinAngle}, jumpXY={movement.JumpXySpeed}, splineElev={movement.SplineElevation}.");
            }
            return false;
        }

        return MovementValidator.IsValid(movement, session.StrictMovementFiniteness);
    }

    private static void ReportNonFiniteMovement(WorldSession session, string detail)
    {
        // Every caller of IsAcceptable runs inside a player's handler, so the session always has its player here.
        if (session.Player is not { } player || player.Locomotion.SuppressAnomalies)
        {
            return;
        }

        player.Locomotion.LastNonFiniteReportServerMs = session.World.NowMs;
        AnomalyLedger.Shared.Report(
            "move",
            "non-finite",
            $"player {player.Name} (guid {player.Guid.Low})",
            detail,
            AnomalySeverity.Error);
    }
}

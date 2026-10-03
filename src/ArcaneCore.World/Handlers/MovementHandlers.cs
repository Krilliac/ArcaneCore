using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

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
    }

    /// <summary>
    /// CMSG_MOVE_TIME_SKIPPED: u64 mover GUID, u32 skipped milliseconds (vmangos, cmangos-classic
    /// and mangoszero read a full GUID). Relayed to observers as MSG_MOVE_TIME_SKIPPED with a
    /// packed GUID, as vmangos and cmangos-classic do, so they keep interpolating correctly.
    /// </summary>
    private static void HandleMoveTimeSkipped(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        ulong guid = reader.ReadUInt64();
        uint lag = reader.ReadUInt32();
        if (guid == player.Guid.Value)
        {
            player.Map?.BroadcastToObservers(player, WorldOpcode.MsgMoveTimeSkipped, MiscPackets.BuildMoveTimeSkipped(player.Guid, lag));
        }
    }

    private static void HandleMovement(WorldSession session, Player player, WorldOpcode opcode, byte[] payload, bool relay)
    {
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

        // Client→server movement is the MovementInfo alone (gtker MSG_MOVE_*_Client, 1.12).
        var reader = new PacketReader(payload);
        MovementInfo movement = MovementInfo.Read(ref reader);

        EnsureFinite(movement);
        ApplyObserved(session, player, opcode, movement);
        if (!relay)
        {
            return;
        }

        // Relay: packed mover GUID + the movement block carrying the server receive time
        // (vmangos/cmangos-classic MovementInfo::Write sends stime).
        var packet = new PacketWriter(payload.Length + 9);
        packet.WritePackedGuid(player.Guid.Value);
        player.Movement.Write(packet);
        player.Map?.BroadcastToObservers(player, opcode, packet.AsSpan());
    }

    /// <summary>
    /// Store a client's movement block with the locomotion observers around it (vmangos HandleMovementOpcodes
    /// :333-344 and HandleMoverRelocation :1062-1170): the Before observers see the previously stored block and
    /// may correct the incoming one, the After observers see the stored result. Shared with the movement-change
    /// acks (HandleMoveRootAck runs the same relocation).
    /// </summary>
    internal static void ApplyObserved(WorldSession session, Player player, WorldOpcode opcode, MovementInfo movement)
    {
        MovementInfo previous = player.Movement;
        var context = new MovementObserverContext(player, session.World, opcode);
        MovementObservers.Before(context, in previous, ref movement, session.Logger);
        player.ApplyClientMovement(movement, session.World.NowMs);
        MovementObservers.After(context, in previous, session.Logger);
    }

    /// <summary>A non-finite position is a malformed packet (the session disconnects the client).</summary>
    internal static void EnsureFinite(in MovementInfo movement)
    {
        if (!float.IsFinite(movement.X) || !float.IsFinite(movement.Y) || !float.IsFinite(movement.Z) || !float.IsFinite(movement.Orientation))
        {
            throw new ArgumentOutOfRangeException(nameof(movement), "non-finite position");
        }
    }
}

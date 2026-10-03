using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
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

        table.OnWorld(WorldOpcode.CmsgForceMoveRootAck, (session, player, payload) => HandleRootAck(session, player, payload, rooted: true));
        table.OnWorld(WorldOpcode.CmsgForceMoveUnrootAck, (session, player, payload) => HandleRootAck(session, player, payload, rooted: false));
        table.OnWorld(WorldOpcode.CmsgMoveTimeSkipped, HandleMoveTimeSkipped);
    }

    /// <summary>
    /// CMSG_FORCE_MOVE_(UN)ROOT_ACK: u64 mover GUID, u32 movement counter, MovementInfo
    /// (vmangos Movement::MoveRootAck; gtker agrees). The client confirms a root the server
    /// ordered; its movement block is applied and relayed to observers as MSG_MOVE_(UN)ROOT —
    /// packed GUID + movement block (vmangos HandleMoveRootAck →
    /// MovementPacketSender::SendMovementFlagChangeToObservers). An ack for another unit, or
    /// one that does not match the current root state (stale), is ignored.
    /// </summary>
    private static void HandleRootAck(WorldSession session, Player player, byte[] payload, bool rooted)
    {
        if (session.Services.GetRequiredService<TeleportFeature>().Teleports.IsBeingTeleported(player))
        {
            return;
        }

        var reader = new PacketReader(payload);
        ulong guid = reader.ReadUInt64();
        _ = reader.ReadUInt32(); // movement counter
        MovementInfo movement = MovementInfo.Read(ref reader);
        if (guid != player.Guid.Value || player.IsRooted != rooted)
        {
            return;
        }

        if (!MovementValidator.IsValid(movement))
        {
            return; // dropped like vmangos VerifyMovementInfo failures (MovementHandler.cpp:596,:690)
        }

        player.ApplyClientMovement(movement, session.World.NowMs);
        var packet = new PacketWriter(payload.Length + 9);
        packet.WritePackedGuid(player.Guid.Value);
        player.Movement.Write(packet);
        player.Map?.BroadcastToObservers(player, rooted ? WorldOpcode.MsgMoveRoot : WorldOpcode.MsgMoveUnroot, packet.AsSpan());
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

        // An invalid packet is dropped, not stored, relayed or punished with a kick
        // (vmangos HandleMovementOpcodes: VerifyMovementInfo, MovementHandler.cpp:315,:489,:1042-1061).
        if (!MovementValidator.IsValid(movement))
        {
            return;
        }

        player.ApplyClientMovement(movement, session.World.NowMs);
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
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Locomotion;

/// <summary>
/// CMSG_FORCE_MOVE_ROOT_ACK / CMSG_FORCE_MOVE_UNROOT_ACK (vmangos WorldSession::HandleMoveRootAck,
/// MovementHandler.cpp:646-745). The ack is honoured only when it matches a root order the server sent (counter and
/// apply flag, see <see cref="MovementControl.Acknowledge"/>); an ack for another unit, a replay or a made-up
/// counter is ignored and counted. A matching ack stores the client's movement block (through the movement
/// observers), sets the server's Root flag and relays MSG_MOVE_(UN)ROOT — packed GUID + movement block — to the
/// observers (SendMovementFlagChangeToObservers).
/// <para>
/// vmangos kicks a client that acknowledges a root without carrying the Root flag in its block (a 1.14-client
/// workaround block, :719-731); ArcaneCore applies the flag from the server order instead and does not kick
/// (kicking is the anticheat's business).
/// </para>
/// </summary>
public sealed class RootAckHandler : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgForceMoveRootAck, (session, player, payload) => Handle(session, player, payload, rooted: true));
        table.OnWorld(WorldOpcode.CmsgForceMoveUnrootAck, (session, player, payload) => Handle(session, player, payload, rooted: false));
    }

    private static void Handle(WorldSession session, Player player, byte[] payload, bool rooted)
    {
        if (session.Services.GetRequiredService<TeleportFeature>().Teleports.IsBeingTeleported(player))
        {
            return;
        }

        MovementChangeAck ack = MovementChangePackets.ReadRootAck(payload, rooted);
        if (ack.Guid != player.Guid.Value)
        {
            return;
        }

        if (!MovementHandlers.IsAcceptable(session, ack.Movement))
        {
            return;
        }
        if (!MovementControl.Acknowledge(player, MovementChangeType.Root, ack.Counter, rooted))
        {
            return;
        }

        MovementHandlers.ApplyObserved(session, player, rooted ? WorldOpcode.CmsgForceMoveRootAck : WorldOpcode.CmsgForceMoveUnrootAck, ack.Movement);
        MovementControl.ApplyReal(player, MovementChangeType.Root, rooted);
        MovementControl.RelayToObservers(player, MovementChangeType.Root, rooted);
    }
}

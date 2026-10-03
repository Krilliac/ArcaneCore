using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Combat.Duel;

/// <summary>
/// CMSG_DUEL_ACCEPTED and CMSG_DUEL_CANCELLED (vmangos WorldSession::HandleDuelAcceptedOpcode / HandleDuelCancelledOpcode, Handlers/DuelHandler.cpp:30-71;
/// gtker cmsg_duel_accepted.wowm / cmsg_duel_cancelled.wowm: one u64 flag guid). Both references read the guid and ignore it. A payload shorter
/// than eight bytes is dropped; a daemon without a duel feature ignores the packets. The rules live in <see cref="DuelService"/>. Discovered
/// through <see cref="IOpcodeHandlerGroup"/>; world thread.
/// </summary>
public sealed class DuelHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgDuelAccepted, HandleDuelAccepted);
        table.OnWorld(WorldOpcode.CmsgDuelCancelled, HandleDuelCancelled);
    }

    private static void HandleDuelAccepted(WorldSession session, Player player, byte[] payload)
    {
        if (DuelPackets.ParseArbiterGuid(payload) is null)
        {
            return;
        }

        session.Services.GetService<DuelFeature>()?.Service?.Accept(player);
    }

    private static void HandleDuelCancelled(WorldSession session, Player player, byte[] payload)
    {
        if (DuelPackets.ParseArbiterGuid(payload) is null)
        {
            return;
        }

        session.Services.GetService<DuelFeature>()?.Service?.Cancel(player);
    }
}

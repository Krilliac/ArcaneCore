using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// The battleground queue poll while battleground queues are unsupported (the meeting stone poll moved to
/// <c>GameObjects.MeetingStoneHandlers</c> with the meeting stone queue). The request is empty in vanilla 1.12.1.
/// Runs through the normal world-thread seam.
/// Facts verified against vmangos/core 4b3d241cffe245a1f68da11380bce96c23db48c0:
/// Handlers/BattleGroundHandler.cpp, LFG/LFGHandler.cpp, LFG/LFGQueue.cpp,
/// LFG/LFGDefines.h and Server/Packets/Misc.cpp.
/// Cross-checked with gtker/wow_messages 70abb9deff0bb63440d8aeb4386b820653e8a176
/// battleground/cmsg_battlefield_status.wowm and meetingstone/*.wowm.
/// Replace these inactive results when authoritative queue state is implemented.
/// </summary>
public sealed class InactiveQueueHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgBattlefieldStatus, BattlefieldStatus);
    }

    private static void BattlefieldStatus(WorldSession session, Player player, byte[] payload)
    {
        RequireEmptyBody(payload);
        // The vanilla handler only answers occupied queues (vmangos HandleBattlefieldStatusOpcode): the battleground feature reports them.
        session.Services.GetService<Battlegrounds.BattlegroundFeature>()?.SendStatusReports(player);
    }

    private static void RequireEmptyBody(byte[] payload)
    {
        // ArcaneCore policy rejects surplus bytes, like its strict quest adapters.
        // Upstream NullClientPacket reads no body fields but does not enforce this length.
        if (payload.Length != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "queue status request requires an empty body");
        }
    }
}

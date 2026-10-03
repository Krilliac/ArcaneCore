using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reputation;

/// <summary>
/// The client's reputation-pane messages (vmangos CharacterHandler.cpp HandleSetFactionAtWarOpcode,
/// HandleSetFactionInactiveOpcode, HandleSetWatchedFactionOpcode). Malformed payloads are ignored.
/// </summary>
public sealed class ReputationHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgSetFactionAtwar, HandleSetAtWar);
        table.OnWorld(WorldOpcode.CmsgSetFactionInactive, HandleSetInactive);
        table.OnWorld(WorldOpcode.CmsgSetWatchedFaction, HandleSetWatched);
    }

    /// <summary>CMSG_SET_FACTION_ATWAR: u32 list slot, u8 flag; ignored in combat (vmangos).</summary>
    private static void HandleSetAtWar(WorldSession session, Player player, byte[] payload)
    {
        if (ReputationPackets.TryReadSetAtWar(payload, out int listId, out bool atWar) && !player.Combat.IsInCombat)
        {
            Service(session)?.SetAtWar(player, listId, atWar);
        }
    }

    /// <summary>CMSG_SET_FACTION_INACTIVE: u32 list slot, u8 bool.</summary>
    private static void HandleSetInactive(WorldSession session, Player player, byte[] payload)
    {
        if (ReputationPackets.TryReadSetInactive(payload, out int listId, out bool inactive))
        {
            Service(session)?.SetInactive(player, listId, inactive);
        }
    }

    /// <summary>CMSG_SET_WATCHED_FACTION: i32 list slot (-1 none).</summary>
    private static void HandleSetWatched(WorldSession session, Player player, byte[] payload)
    {
        if (ReputationPackets.TryReadSetWatched(payload, out int listId))
        {
            Service(session)?.SetWatchedFaction(player, listId);
        }
    }

    private static ReputationService? Service(WorldSession session) => session.Services.GetService<ReputationFeature>()?.Service;
}

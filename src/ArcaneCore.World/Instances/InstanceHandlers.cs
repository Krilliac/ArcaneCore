using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Instances;

/// <summary>CMSG_RESET_INSTANCES and CMSG_REQUEST_RAID_INFO (vmangos MiscHandler.cpp; both empty packets).</summary>
public sealed class InstanceHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgResetInstances, HandleResetInstances);
        table.OnWorld(WorldOpcode.CmsgRequestRaidInfo, HandleRequestRaidInfo);
    }

    // vmangos WorldSession::HandleResetInstancesOpcode: the group leader resets the group's
    // instances, an ungrouped player its own.
    private static void HandleResetInstances(WorldSession session, Player player, byte[] payload)
        => session.Services.GetService<InstanceFeature>()?.Instances.HandleResetInstances(player);

    // vmangos WorldSession::HandleRequestRaidInfoOpcode → Player::SendRaidInfo.
    private static void HandleRequestRaidInfo(WorldSession session, Player player, byte[] payload)
    {
        if (session.Services.GetService<InstanceFeature>() is { } feature)
        {
            feature.Instances.SendRaidInfo(player);
        }
        else
        {
            session.Send(WorldOpcode.SmsgRaidInstanceInfo, MiscPackets.BuildNoRaidInstances());
        }
    }
}

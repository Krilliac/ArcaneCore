using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Instances;

public sealed partial class InstanceManager
{
    /// <summary>
    /// SMSG_UPDATE_INSTANCE_OWNERSHIP (does the player have a permanent bind) and one
    /// SMSG_UPDATE_LAST_INSTANCE per permanent bind (vmangos <c>Player::SendSavedInstances</c>,
    /// Player.cpp:16002-16031, called by <c>SendNewWorld</c> on every far teleport,
    /// Player.cpp:2113-2120). Layouts: wow_messages raid/smsg_update_instance_ownership.wowm and
    /// smsg_update_last_instance.wowm. Deviation: sent when the player has entered its new map
    /// (after the worldport ack) instead of together with SMSG_NEW_WORLD, because the teleport
    /// service is not owned by this subsystem.
    /// </summary>
    internal void SendSavedInstances(Player player)
    {
        uint[] permanentMaps = [.. GetPlayerBinds(player.Guid).Where(b => b.Permanent).Select(b => b.Save.MapId).Order()];
        player.Session.Send(WorldOpcode.SmsgUpdateInstanceOwnership, InstancePackets.BuildUpdateInstanceOwnership(permanentMaps.Length > 0));
        foreach (uint mapId in permanentMaps)
        {
            player.Session.Send(WorldOpcode.SmsgUpdateLastInstance, InstancePackets.BuildUpdateLastInstance(mapId));
        }
    }
}

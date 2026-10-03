using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Teleport;

/// <summary>
/// Seam for CMSG_AREATRIGGER consumers other than teleports (vmangos HandleAreaTriggerOpcode
/// quest relation, tavern and scripts). Called on the world thread after the trigger exists and
/// the player was verified inside it, before any teleport. A world feature implementing it is
/// registered automatically (WorldFeatures.SeamInterfaces).
/// </summary>
public interface IAreaTriggerListener
{
    void OnAreaTrigger(Player player, uint triggerId);
}

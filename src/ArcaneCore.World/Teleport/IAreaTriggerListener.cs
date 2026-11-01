using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Teleport;

/// <summary>
/// Seam for CMSG_AREATRIGGER consumers other than teleports (vmangos HandleAreaTriggerOpcode
/// quest relation, tavern and scripts). Called on the world thread after the trigger exists and
/// the player was verified inside it, before any teleport. Every registered world feature that
/// implements it is called (TeleportHandlers filters the discovered IWorldFeatures).
/// </summary>
public interface IAreaTriggerListener
{
    void OnAreaTrigger(Player player, uint triggerId);
}

/// <summary>
/// A CMSG_AREATRIGGER consumer that can end the handler (AzerothCore HandleAreaTriggerOpcode returns once a trigger script ran, before quest
/// exploration, tavern and teleport). TeleportHandlers asks every implementer first, after the volume check; when one returns true the listeners
/// and the teleport are skipped. Called on the world thread.
/// </summary>
public interface IAreaTriggerPreemptor
{
    /// <summary>True when the trigger was handled completely here and the rest of the handler must not run.</summary>
    bool Preempts(Player player, uint triggerId);
}

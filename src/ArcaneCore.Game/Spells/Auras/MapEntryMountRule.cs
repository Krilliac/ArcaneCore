using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The mount allow check made when a player enters a map: a player on a map where mounts are not allowed
/// (<see cref="MountCastCheck.IsMountAllowed"/>) loses every Mounted aura and the mount display
/// (vmangos <c>WorldSession::HandleMoveWorldportAckOpcode</c>, MovementHandler.cpp:189-191:
/// <c>if (!mEntry-&gt;IsMountAllowed()) RemoveSpellsCausingAura(SPELL_AURA_MOUNTED)</c>). A map the world does not know
/// (no template) allows mounts, as <c>RaidDummyScripts</c> and the cast check treat it. Thread affinity: world thread.
/// </summary>
public static class MapEntryMountRule
{
    /// <summary>
    /// Dismount <paramref name="player"/> when its current map forbids mounts: remove every Mounted aura (vmangos
    /// <c>RemoveSpellsCausingAura(SPELL_AURA_MOUNTED)</c>); the aura's remove handler clears the mount display. A display-only mount
    /// with no Mounted aura (a taxi flight) is left alone, as vmangos leaves it. Idempotent and a no-op off-map or on an allowed map.
    /// Call it once per far arrival, after the map add (vmangos HandleMoveWorldportAckOpcode); it is not run on near teleports or at
    /// login (vmangos has no login map-mount check).
    /// </summary>
    /// <returns>True when a Mounted aura was removed.</returns>
    public static bool Enforce(SpellSystem system, Player player)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(player);
        if (player.Map?.Template is not { } template || MountCastCheck.IsMountAllowed(template))
        {
            return false;
        }

        bool hasMountedAura = system.GetAuras(player).Any(h => h.HasAura(AuraType.Mounted));
        if (hasMountedAura)
        {
            system.RemoveAurasByType(player, AuraType.Mounted);
        }

        return hasMountedAura;
    }
}

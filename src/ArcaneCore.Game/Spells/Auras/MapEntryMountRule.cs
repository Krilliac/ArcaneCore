using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Teleport;

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
    /// Dismount <paramref name="player"/> when its current map forbids mounts. Idempotent and a no-op off-map, on an
    /// allowed map, or when the player is neither mounted nor carrying a Mounted aura.
    /// </summary>
    /// <returns>True when a dismount was performed.</returns>
    public static bool Enforce(SpellSystem system, Player player)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(player);
        if (player.Map?.Template is not { } template || MountCastCheck.IsMountAllowed(template))
        {
            return false;
        }

        bool wasMounted = MountService.IsMounted(player);
        MountService.Unmount(system, player);
        system.RemoveAurasByType(player, AuraType.Mounted);
        return wasMounted;
    }

    /// <summary>
    /// Run <see cref="Enforce"/> for every player that completes a far teleport (the point after the map add in vmangos'
    /// worldport ack). <paramref name="spells"/> is read at each arrival because the spell system may be wired after the
    /// teleport service; null skips the check.
    /// </summary>
    public static void Attach(TeleportService teleports, Func<SpellSystem?> spells)
    {
        ArgumentNullException.ThrowIfNull(teleports);
        ArgumentNullException.ThrowIfNull(spells);
        teleports.TeleportCompleted += player =>
        {
            if (spells() is { } system)
            {
                Enforce(system, player);
            }
        };
    }
}

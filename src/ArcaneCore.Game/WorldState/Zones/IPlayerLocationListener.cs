using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.WorldState.Zones;

/// <summary>
/// Told when a player's zone or area changes (vmangos <c>Player::UpdateZone</c> /
/// <c>UpdateArea</c>, Player.cpp:6560-6675). The consumers are other lanes: capital-city and
/// tavern rest, weather and world states, PvP-enforced areas, zone-limited items, local
/// channels, spell_area auras, group/guild zone refresh. Register with
/// <see cref="WorldStateHooks.AddLocationListener"/>. World thread.
/// </summary>
public interface IPlayerLocationListener
{
    /// <summary>
    /// Lower runs first (ties: registration order). The core's own listener that sends
    /// SMSG_INIT_WORLD_STATES uses <see cref="int.MinValue"/> so weather follows it, as in vmangos.
    /// </summary>
    int Order => 0;

    /// <summary>
    /// The player entered a different zone (also after login / a far teleport, when
    /// <paramref name="oldZone"/> is 0). <paramref name="zoneEntry"/> carries the zone's
    /// <c>Flags</c> (<see cref="AreaFlags"/>) and <c>Team</c> (<see cref="AreaTeams"/>); null when no
    /// area data is loaded and the zone came from the client / stored value. The matching
    /// <see cref="OnAreaChanged"/> follows.
    /// </summary>
    void OnZoneChanged(Player player, uint oldZone, uint newZone, uint newArea, AreaTemplate? zoneEntry)
    {
    }

    /// <summary>The player entered a different area (also raised for a zone change, as vmangos UpdateZone calls UpdateArea).</summary>
    void OnAreaChanged(Player player, uint oldArea, uint newArea)
    {
    }
}

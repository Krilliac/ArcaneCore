using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Graveyards;

/// <summary>
/// Graveyard links a script adds at run time (vmangos <c>ObjectMgr::AddGraveYardLink</c> with <c>persist = false</c>, used by the
/// Eastern Plaguelands Crown Guard tower, OutdoorPvPEP.cpp:699-713). <see cref="GraveyardRepopService"/> treats each returned safe
/// location as one more candidate of the spirit's zone and keeps the closest on the spirit's map.
/// </summary>
public interface IGraveyardLinkSource
{
    /// <summary>The extra safe-location ids linked for <paramref name="player"/> in <paramref name="zoneId"/> / <paramref name="areaId"/>.</summary>
    IEnumerable<uint> ExtraLinks(Player player, uint zoneId, uint areaId);
}

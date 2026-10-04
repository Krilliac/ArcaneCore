using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.Graveyards;

/// <summary>
/// The choice of the graveyard of a released spirit: vmangos <c>ObjectMgr::GetClosestGraveYard</c> and
/// <c>GetClosestGraveYardForArea</c> (ObjectMgr.cpp:7512-7645), as a pure function of the catalog, the map registry and the
/// position. Area links are tried first, then the zone's (only when the area is not the zone itself).
/// </summary>
public static class GraveyardSelector
{
    /// <summary>
    /// <see cref="FindClosest"/> with the mangos-classic default graveyards as a last resort (GraveyardManager.cpp:170-174):
    /// the deviation behind <c>World:Death:GraveyardFallbackToDefaults</c>.
    /// </summary>
    public static WorldSafeLoc? FindClosestOrDefault(
        GraveyardCatalog catalog, MapRegistry maps, uint mapId, float x, float y, float z, uint zoneId, uint areaId, uint team)
        => FindClosest(catalog, maps, mapId, x, y, z, zoneId, areaId, team)
            ?? catalog.Find(team == GraveyardCatalog.TeamHorde ? GraveyardCatalog.DefaultHordeGraveyard : GraveyardCatalog.DefaultAllianceGraveyard);

    /// <summary>
    /// The graveyard for a ghost at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) on <paramref name="mapId"/>
    /// in <paramref name="areaId"/> of <paramref name="zoneId"/>, or null when nothing is linked for this team (the ghost then
    /// stays where it is, Player.cpp:5008). <paramref name="team"/> is 469 (Alliance), 67 (Horde) or 0 (any team, the GM
    /// <c>.neargrave</c> without an argument).
    /// </summary>
    public static WorldSafeLoc? FindClosest(
        GraveyardCatalog catalog, MapRegistry maps, uint mapId, float x, float y, float z, uint zoneId, uint areaId, uint team)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(maps);
        if (ForArea(catalog, maps, areaId, mapId, x, y, z, team) is { } byArea)
        {
            return byArea;
        }

        return areaId == zoneId ? null : ForArea(catalog, maps, zoneId, mapId, x, y, z, team);
    }

    private static WorldSafeLoc? ForArea(GraveyardCatalog catalog, MapRegistry maps, uint areaOrZone, uint mapId, float x, float y, float z, uint team)
    {
        IReadOnlyList<GraveyardEntry> links = catalog.LinksOf(areaOrZone);
        if (links.Count == 0)
        {
            return null;
        }

        MapTemplate? map = maps.Find(mapId);
        WorldSafeLoc? near = null;
        float nearDistance = 0f;
        WorldSafeLoc? entrance = null;
        float entranceDistance = 0f;
        WorldSafeLoc? far = null;
        foreach (GraveyardEntry entry in links)
        {
            WorldSafeLoc loc = entry.Location;

            // Skip the enemy faction's graveyard; team 0 on either side matches everything.
            if (entry.Team != 0 && team != 0 && entry.Team != team)
            {
                continue;
            }

            if (mapId != loc.MapId)
            {
                // A graveyard on another map: only the one on the map of the dungeon's entrance can be measured; any other
                // is the last resort, and the last one seen wins (vmangos overwrites entryFar).
                if (map is null || map.GhostEntranceMap < 0 || (uint)map.GhostEntranceMap != loc.MapId
                    || (map.GhostEntranceX == 0f && map.GhostEntranceY == 0f))
                {
                    far = loc;
                    continue;
                }

                float dx = loc.X - map.GhostEntranceX;
                float dy = loc.Y - map.GhostEntranceY;
                float distance = (dx * dx) + (dy * dy); // 2D, squared
                if (entrance is null || distance < entranceDistance)
                {
                    entrance = loc;
                    entranceDistance = distance;
                }
            }
            else
            {
                float dx = loc.X - x;
                float dy = loc.Y - y;
                float dz = loc.Z - z;
                float distance = (dx * dx) + (dy * dy) + (dz * dz); // 3D, squared
                if (near is null || distance < nearDistance)
                {
                    near = loc;
                    nearDistance = distance;
                }
            }
        }

        return near ?? entrance ?? far;
    }
}

using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.Graveyards;

/// <summary>One graveyard link as the selector reads it: the safe location and the team it serves (0 = both).</summary>
public readonly record struct GraveyardEntry(WorldSafeLoc Location, uint Team);

/// <summary>
/// The graveyards of a world (vmangos <c>m_GraveYardMap</c> and <c>sWorldSafeLocsStore</c>): the safe locations by id and, per
/// zone or area id, the links in load order. Immutable once built, so a reload swaps the whole catalog
/// (<see cref="WorldGraveyards.Replace"/>); safe to read from any thread.
/// </summary>
public sealed class GraveyardCatalog
{
    /// <summary><c>game_graveyard_zone.faction</c> for the Alliance (vmangos <c>ALLIANCE</c>).</summary>
    public const uint TeamAlliance = 469;

    /// <summary><c>game_graveyard_zone.faction</c> for the Horde (vmangos <c>HORDE</c>).</summary>
    public const uint TeamHorde = 67;

    /// <summary>mangos-classic default graveyard of the Alliance (GraveyardManager.cpp:146).</summary>
    public const uint DefaultAllianceGraveyard = 4;

    /// <summary>mangos-classic default graveyard of the Horde (GraveyardManager.cpp:147).</summary>
    public const uint DefaultHordeGraveyard = 10;

    private readonly Dictionary<uint, WorldSafeLoc> _locs;
    private readonly Dictionary<uint, List<GraveyardEntry>> _links;

    private GraveyardCatalog(Dictionary<uint, WorldSafeLoc> locs, Dictionary<uint, List<GraveyardEntry>> links, int linkCount)
    {
        _locs = locs;
        _links = links;
        LinkCount = linkCount;
    }

    /// <summary>A catalog without graveyards: no released spirit is moved.</summary>
    public static GraveyardCatalog Empty { get; } = new([], [], 0);

    public int SafeLocCount => _locs.Count;

    public int LinkCount { get; }

    /// <summary>
    /// Build a catalog with the load rules of vmangos <c>ObjectMgr::LoadGraveyardZones</c> (ObjectMgr.cpp:7453-7510): a link
    /// to a safe location that does not exist is skipped, so is a zone or area that is not in the area table (checked only
    /// when <paramref name="areaExists"/> is given: a world without area data cannot tell), a team other than 0, 67 or 469,
    /// and a second link for the same graveyard and zone (the first stays). Every skip is described in
    /// <paramref name="diagnostics"/>.
    /// </summary>
    public static GraveyardCatalog Build(GraveyardContent content, Func<uint, bool>? areaExists, out IReadOnlyList<string> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(content);
        var messages = new List<string>();
        var locs = new Dictionary<uint, WorldSafeLoc>();
        foreach (WorldSafeLoc loc in content.SafeLocs)
        {
            locs[loc.Id] = loc;
        }

        var links = new Dictionary<uint, List<GraveyardEntry>>();
        int count = 0;
        foreach (GraveyardLink link in content.Links)
        {
            if (!locs.TryGetValue(link.SafeLocId, out WorldSafeLoc? loc))
            {
                messages.Add($"game_graveyard_zone has a record for the not existing graveyard {link.SafeLocId}, skipped");
            }
            else if (areaExists is not null && !areaExists(link.ZoneId))
            {
                messages.Add($"game_graveyard_zone has a record for the not existing zone id {link.ZoneId}, skipped");
            }
            else if (link.Team is not (0 or TeamHorde or TeamAlliance))
            {
                messages.Add($"game_graveyard_zone has a record for the non player faction {link.Team}, skipped");
            }
            else
            {
                if (!links.TryGetValue(link.ZoneId, out List<GraveyardEntry>? list))
                {
                    links[link.ZoneId] = list = [];
                }

                if (list.Any(e => e.Location.Id == link.SafeLocId))
                {
                    messages.Add($"game_graveyard_zone has a duplicate record for graveyard {link.SafeLocId} and zone {link.ZoneId}, skipped");
                }
                else
                {
                    list.Add(new GraveyardEntry(loc, link.Team));
                    count++;
                }
            }
        }

        diagnostics = messages;
        return new GraveyardCatalog(locs, links, count);
    }

    /// <summary>The links of a zone or area, in load order (vmangos <c>m_GraveYardMap.equal_range</c>).</summary>
    public IReadOnlyList<GraveyardEntry> LinksOf(uint zoneOrArea)
        => _links.TryGetValue(zoneOrArea, out List<GraveyardEntry>? list) ? list : [];

    /// <summary>A safe location by id (vmangos <c>sWorldSafeLocsStore.LookupEntry</c>), or null.</summary>
    public WorldSafeLoc? Find(uint id) => _locs.GetValueOrDefault(id);
}

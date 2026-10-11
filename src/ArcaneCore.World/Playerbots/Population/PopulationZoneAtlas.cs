using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// One zone a bot may be placed in. The level range is the range of its sub-areas' <c>AreaLevel</c> (the 1.12.1 AreaTable.dbc zone row
/// carries 0 except for capitals; the level sits on the sub-areas: Elwynn Forest 5-10, Westfall 9-18, Durotar 5-10, The Barrens 10-33,
/// Duskwood 10-30, Stranglethorn Vale 30-50, parsed from the 5875 DBC with the vmangos layout, DBCfmt.h:25, Map.h:107-118).
/// </summary>
internal sealed record PopulationZone(
    uint ZoneId,
    uint MapId,
    string Name,
    int MinLevel,
    int MaxLevel,
    uint AreaTeam,
    IReadOnlyList<WorldSafeLoc> AllianceLocations,
    IReadOnlyList<WorldSafeLoc> HordeLocations)
{
    /// <summary>
    /// The <c>AreaLevel</c> of the sub-area each location stands in, by location id, as the terrain reports it. A location the terrain
    /// does not know (no map data) or that stands in an unlevelled area is absent and counts as the zone's lowest level.
    /// </summary>
    public IReadOnlyDictionary<uint, int> LocationLevels { get; init; } = new Dictionary<uint, int>();

    public IReadOnlyList<WorldSafeLoc> LocationsFor(Team team) => team == Team.Alliance ? AllianceLocations : HordeLocations;

    /// <summary>
    /// The lowest level a bot landing at <paramref name="location"/> is meant to have (before the slack): the level of the sub-area the
    /// graveyard stands in, never below the zone's lowest. Duskwood's lowest sub-area is The Darkened Bank (10), but its graveyards stand
    /// in Darkshire (25) and Raven Hill (24/26), where a level 9 bot dies to level 23-25 skeletons.
    /// </summary>
    public int FloorOf(WorldSafeLoc location) => Math.Max(MinLevel, LocationLevels.GetValueOrDefault(location.Id));

    /// <summary>The lowest <see cref="FloorOf"/> of the team's locations; the zone's lowest level when it has none for the team.</summary>
    public int FloorFor(Team team)
    {
        IReadOnlyList<WorldSafeLoc> locations = LocationsFor(team);
        return locations.Count == 0 ? MinLevel : locations.Min(FloorOf);
    }

    /// <summary>Whether the zone is open to the team: no team (0), or its own (2 Alliance, 4 Horde). Any other area team value is never offered.</summary>
    public bool OpenTo(Team team) => AreaTeam == AreaTeams.None
        || (AreaTeam == AreaTeams.Ally && team == Team.Alliance)
        || (AreaTeam == AreaTeams.Horde && team == Team.Horde);
}

/// <summary>A graveyard link the atlas left out: the terrain puts location <c>LocationId</c> in <c>TerrainZoneId</c>, not in <c>ZoneId</c>.</summary>
internal sealed record PopulationDroppedLocation(uint ZoneId, uint LocationId, uint TerrainZoneId);

/// <summary>A zone, one of its graveyards, and the levels a bot placed there may have.</summary>
internal sealed record PopulationPlacement(PopulationZone Zone, WorldSafeLoc Location, int LowLevel, int HighLevel);

/// <summary>
/// The zones bots can be placed in, built from the area table and the graveyard catalog (both immutable; a content reload replaces them
/// and the feature rebuilds the atlas). A zone qualifies when it is a zone (no parent), lies on an allowed map, is not excluded, is not a
/// capital or city (<see cref="AreaFlags"/>) and has at least one sub-area with a level. Its safe locations are the graveyards linked
/// (<c>game_graveyard_zone</c>) to the zone or any of its sub-areas on the zone's map and that the terrain does not put in another zone
/// (see <see cref="Build"/>), split by team: link team 0 serves both,
/// 469 the Alliance, 67 the Horde (vmangos <c>ObjectMgr::LoadGraveyardZones</c>).
/// <para>Thread affinity: immutable after <see cref="Build"/>; safe to read from any thread.</para>
/// </summary>
internal sealed class PopulationZoneAtlas
{
    private readonly PopulationZone[] _zones;
    private readonly Dictionary<uint, PopulationZone> _byId;

    private PopulationZoneAtlas(PopulationZone[] zones, IReadOnlyList<PopulationDroppedLocation> dropped)
    {
        _zones = zones;
        _byId = zones.ToDictionary(z => z.ZoneId);
        Dropped = dropped;
    }

    public static PopulationZoneAtlas Empty { get; } = new([], []);

    /// <summary>The graveyard links <see cref="Build"/> left out because the terrain puts the graveyard in another zone (diagnostics).</summary>
    public IReadOnlyList<PopulationDroppedLocation> Dropped { get; }

    /// <summary>The (zone, location) pairs the atlas offers, both teams together (diagnostics).</summary>
    public int LocationCount => _zones.Sum(z => z.AllianceLocations.Concat(z.HordeLocations).Select(l => l.Id).Distinct().Count());

    public IReadOnlyList<PopulationZone> Zones => _zones;

    public PopulationZone? Find(uint zoneId) => _byId.GetValueOrDefault(zoneId);

    /// <summary>The zones that can take a bot of the team at some level (a location for the team, open to it).</summary>
    public int CountFor(Team team) => _zones.Count(z => z.OpenTo(team) && z.LocationsFor(team).Count > 0);

    /// <param name="locate">
    /// Where a safe location really stands (<c>Map.GetZoneAndAreaId</c> of its position; (0, 0) when the terrain does not know); null
    /// knows nothing. A linked graveyard whose terrain zone is known and is not the zone is left out: <c>game_graveyard_zone</c> links a
    /// graveyard to the zones whose ghosts it serves, not to the zone it stands in. 829 "Alterac Valley, Alliance Exit" stands in Alterac
    /// Mountains beside four level 61 Stormpike Battleguards and serves Western Plaguelands; Duskwood's Horde graveyard is Lakeshire in
    /// Redridge. A bot "placed" there lands in another zone, often far above or below its level. A location the terrain puts in the zone
    /// also gets the level of the sub-area it stands in (<see cref="PopulationZone.LocationLevels"/>).
    /// </param>
    public static PopulationZoneAtlas Build(IReadOnlyList<AreaTemplate> areas, GraveyardCatalog graveyards, IReadOnlyCollection<uint> allowedMaps,
        ISet<uint> excluded, Func<WorldSafeLoc, (uint ZoneId, uint AreaId)>? locate = null)
    {
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(graveyards);
        ArgumentNullException.ThrowIfNull(allowedMaps);
        ArgumentNullException.ThrowIfNull(excluded);
        ILookup<uint, AreaTemplate> subAreas = areas.Where(a => !a.IsZone).ToLookup(a => a.ZoneId);
        var byEntry = new Dictionary<uint, AreaTemplate>();
        foreach (AreaTemplate area in areas) byEntry.TryAdd(area.Entry, area);
        var where = new Dictionary<uint, (uint ZoneId, uint AreaId)>();
        var dropped = new List<PopulationDroppedLocation>();
        var zones = new List<PopulationZone>();
        foreach (AreaTemplate zone in areas)
        {
            if (!zone.IsZone || !allowedMaps.Contains(zone.MapId) || excluded.Contains(zone.Entry)) continue;
            if ((zone.Flags & (uint)(AreaFlags.Capital | AreaFlags.City)) != 0) continue;
            if (zone.Team is not (AreaTeams.None or AreaTeams.Ally or AreaTeams.Horde)) continue;
            AreaTemplate[] children = [.. subAreas[zone.Entry]];
            int[] levels = [.. children.Select(a => a.AreaLevel).Where(level => level > 0)];
            if (levels.Length == 0) continue;

            var alliance = new List<WorldSafeLoc>();
            var horde = new List<WorldSafeLoc>();
            var locationLevels = new Dictionary<uint, int>();
            foreach (uint linked in children.Select(a => a.Entry).Prepend(zone.Entry))
            {
                foreach (GraveyardEntry entry in graveyards.LinksOf(linked))
                {
                    WorldSafeLoc? location = graveyards.Find(entry.Location.Id);
                    if (location is null || location.MapId != zone.MapId) continue;
                    if (locate is not null)
                    {
                        if (!where.TryGetValue(location.Id, out (uint ZoneId, uint AreaId) at)) where[location.Id] = at = locate(location);
                        if (at.ZoneId != 0 && at.ZoneId != zone.Entry)
                        {
                            if (!dropped.Any(d => d.ZoneId == zone.Entry && d.LocationId == location.Id))
                                dropped.Add(new PopulationDroppedLocation(zone.Entry, location.Id, at.ZoneId));
                            continue;
                        }

                        if (at.AreaId != 0 && byEntry.TryGetValue(at.AreaId, out AreaTemplate? standsIn) && standsIn.AreaLevel > 0)
                            locationLevels[location.Id] = standsIn.AreaLevel;
                    }

                    if (entry.Team is 0 or GraveyardCatalog.TeamAlliance && alliance.All(l => l.Id != location.Id)) alliance.Add(location);
                    if (entry.Team is 0 or GraveyardCatalog.TeamHorde && horde.All(l => l.Id != location.Id)) horde.Add(location);
                }
            }

            zones.Add(new PopulationZone(zone.Entry, zone.MapId, zone.Name, levels.Min(), levels.Max(), zone.Team, alliance, horde)
            {
                LocationLevels = locationLevels,
            });
        }

        return new PopulationZoneAtlas([.. zones.OrderBy(z => z.ZoneId)], dropped);
    }

    /// <summary>
    /// Whether a bot of <paramref name="team"/> and <paramref name="level"/> may be placed in <paramref name="zone"/>: the zone is open to
    /// the team, the level is at most the zone's highest, and one of the team's locations takes it (its <see cref="PopulationZone.FloorOf"/>
    /// minus <paramref name="slack"/> is at most the level). The slack is an ArcaneCore choice, not a game rule: it lets a level 1 bot
    /// start in a starter zone whose lowest sub-area is level 4 or 5.
    /// </summary>
    public static bool Fits(PopulationZone zone, int level, Team team, int slack)
        => level <= zone.MaxLevel && zone.OpenTo(team) && zone.LocationsFor(team).Any(l => zone.FloorOf(l) - slack <= level);

    /// <summary>
    /// Whether a bot of <paramref name="level"/> is below every place in <paramref name="zone"/> it could have been put (the zone's
    /// <see cref="PopulationZone.FloorFor"/> the team, minus <paramref name="slack"/>). Such a bot could not have been placed there.
    /// </summary>
    public static bool UnderLevelled(PopulationZone zone, int level, Team team, int slack) => level < zone.FloorFor(team) - slack;

    private static bool Takes(PopulationZone zone, WorldSafeLoc location, int minLevel, int maxLevel, int slack)
        => Math.Max(minLevel, zone.FloorOf(location) - slack) <= Math.Min(maxLevel, zone.MaxLevel);

    /// <summary>The zones <see cref="Fits"/> for the team at some level of <paramref name="minLevel"/>..<paramref name="maxLevel"/>.</summary>
    public IEnumerable<PopulationZone> FittingZones(int minLevel, int maxLevel, Team team, int slack)
        => _zones.Where(z => z.OpenTo(team) && z.LocationsFor(team).Any(l => Takes(z, l, minLevel, maxLevel, slack)));

    /// <summary>
    /// A placement for a bot that may have any level of <paramref name="minLevel"/>..<paramref name="maxLevel"/>: the fitting zone with the
    /// fewest bots assigned (ties broken by <paramref name="random"/>), one of its graveyards for the team, and the levels of the range the
    /// zone takes. Null when no zone fits.
    /// </summary>
    public PopulationPlacement? Pick(int minLevel, int maxLevel, Team team, int slack, IReadOnlyDictionary<uint, int> assigned, Random random)
    {
        ArgumentNullException.ThrowIfNull(assigned);
        ArgumentNullException.ThrowIfNull(random);
        PopulationZone[] fitting = [.. FittingZones(minLevel, maxLevel, team, slack)];
        if (fitting.Length == 0) return null;
        int fewest = fitting.Min(z => assigned.GetValueOrDefault(z.ZoneId));
        PopulationZone[] least = [.. fitting.Where(z => assigned.GetValueOrDefault(z.ZoneId) == fewest)];
        PopulationZone zone = least[random.Next(least.Length)];
        WorldSafeLoc[] locations = [.. zone.LocationsFor(team).Where(l => Takes(zone, l, minLevel, maxLevel, slack))];
        WorldSafeLoc location = locations[random.Next(locations.Length)];
        return new PopulationPlacement(zone, location, Math.Max(minLevel, Math.Max(1, zone.FloorOf(location) - slack)), Math.Min(maxLevel, zone.MaxLevel));
    }
}

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
    public IReadOnlyList<WorldSafeLoc> LocationsFor(Team team) => team == Team.Alliance ? AllianceLocations : HordeLocations;

    /// <summary>Whether the zone is open to the team: no team (0), or its own (2 Alliance, 4 Horde). Any other area team value is never offered.</summary>
    public bool OpenTo(Team team) => AreaTeam == AreaTeams.None
        || (AreaTeam == AreaTeams.Ally && team == Team.Alliance)
        || (AreaTeam == AreaTeams.Horde && team == Team.Horde);
}

/// <summary>A zone, one of its graveyards, and the levels a bot placed there may have.</summary>
internal sealed record PopulationPlacement(PopulationZone Zone, WorldSafeLoc Location, int LowLevel, int HighLevel);

/// <summary>
/// The zones bots can be placed in, built from the area table and the graveyard catalog (both immutable; a content reload replaces them
/// and the feature rebuilds the atlas). A zone qualifies when it is a zone (no parent), lies on an allowed map, is not excluded, is not a
/// capital or city (<see cref="AreaFlags"/>) and has at least one sub-area with a level. Its safe locations are the graveyards linked
/// (<c>game_graveyard_zone</c>) to the zone or any of its sub-areas on the zone's map, split by team: link team 0 serves both,
/// 469 the Alliance, 67 the Horde (vmangos <c>ObjectMgr::LoadGraveyardZones</c>).
/// <para>Thread affinity: immutable after <see cref="Build"/>; safe to read from any thread.</para>
/// </summary>
internal sealed class PopulationZoneAtlas
{
    private readonly PopulationZone[] _zones;
    private readonly Dictionary<uint, PopulationZone> _byId;

    private PopulationZoneAtlas(PopulationZone[] zones)
    {
        _zones = zones;
        _byId = zones.ToDictionary(z => z.ZoneId);
    }

    public static PopulationZoneAtlas Empty { get; } = new([]);

    public IReadOnlyList<PopulationZone> Zones => _zones;

    public PopulationZone? Find(uint zoneId) => _byId.GetValueOrDefault(zoneId);

    /// <summary>The zones that can take a bot of the team at some level (a location for the team, open to it).</summary>
    public int CountFor(Team team) => _zones.Count(z => z.OpenTo(team) && z.LocationsFor(team).Count > 0);

    public static PopulationZoneAtlas Build(IReadOnlyList<AreaTemplate> areas, GraveyardCatalog graveyards, IReadOnlyCollection<uint> allowedMaps,
        ISet<uint> excluded)
    {
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(graveyards);
        ArgumentNullException.ThrowIfNull(allowedMaps);
        ArgumentNullException.ThrowIfNull(excluded);
        ILookup<uint, AreaTemplate> subAreas = areas.Where(a => !a.IsZone).ToLookup(a => a.ZoneId);
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
            foreach (uint linked in children.Select(a => a.Entry).Prepend(zone.Entry))
            {
                foreach (GraveyardEntry entry in graveyards.LinksOf(linked))
                {
                    WorldSafeLoc? location = graveyards.Find(entry.Location.Id);
                    if (location is null || location.MapId != zone.MapId) continue;
                    if (entry.Team is 0 or GraveyardCatalog.TeamAlliance && alliance.All(l => l.Id != location.Id)) alliance.Add(location);
                    if (entry.Team is 0 or GraveyardCatalog.TeamHorde && horde.All(l => l.Id != location.Id)) horde.Add(location);
                }
            }

            zones.Add(new PopulationZone(zone.Entry, zone.MapId, zone.Name, levels.Min(), levels.Max(), zone.Team, alliance, horde));
        }

        return new PopulationZoneAtlas([.. zones.OrderBy(z => z.ZoneId)]);
    }

    /// <summary>
    /// Whether a bot of <paramref name="team"/> and <paramref name="level"/> may be placed in <paramref name="zone"/>: the zone's lowest
    /// level minus <paramref name="slack"/> is at most the level, the level is at most the zone's highest, the zone is open to the team and
    /// has a location for it. The slack is an ArcaneCore choice, not a game rule: it lets a level 1 bot start in a starter zone whose
    /// lowest sub-area is level 4 or 5.
    /// </summary>
    public static bool Fits(PopulationZone zone, int level, Team team, int slack)
        => zone.MinLevel - slack <= level && level <= zone.MaxLevel && zone.OpenTo(team) && zone.LocationsFor(team).Count > 0;

    /// <summary>The zones <see cref="Fits"/> for the team at some level of <paramref name="minLevel"/>..<paramref name="maxLevel"/>.</summary>
    public IEnumerable<PopulationZone> FittingZones(int minLevel, int maxLevel, Team team, int slack)
        => _zones.Where(z => z.OpenTo(team) && z.LocationsFor(team).Count > 0
            && Math.Max(minLevel, z.MinLevel - slack) <= Math.Min(maxLevel, z.MaxLevel));

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
        IReadOnlyList<WorldSafeLoc> locations = zone.LocationsFor(team);
        return new PopulationPlacement(zone, locations[random.Next(locations.Count)],
            Math.Max(minLevel, Math.Max(1, zone.MinLevel - slack)), Math.Min(maxLevel, zone.MaxLevel));
    }
}

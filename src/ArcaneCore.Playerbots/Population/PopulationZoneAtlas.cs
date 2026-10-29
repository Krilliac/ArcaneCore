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
    /// What the creature survey found near each location, by location and team: the level most of the creatures hostile to the team
    /// within <c>CreatureSurveyRadius</c> are at least (the lower median of their template <c>MinLevel</c>). A location without a survey, or
    /// with too few hostile creatures near it (<see cref="PopulationCreatureSurvey.MinHostile"/>), is absent and does not constrain.
    /// </summary>
    public IReadOnlyDictionary<(uint LocationId, Team Team), int> CreatureLevels { get; init; } = new Dictionary<(uint, Team), int>();

    /// <summary>
    /// The level of the sub-area <paramref name="location"/> stands in, never below the zone's lowest (before the slack). Duskwood's lowest
    /// sub-area is The Darkened Bank (10), but its graveyards stand in Darkshire (25) and Raven Hill (24/26), where a level 9 bot dies to
    /// level 23-25 skeletons.
    /// </summary>
    public int FloorOf(WorldSafeLoc location) => Math.Max(MinLevel, LocationLevels.GetValueOrDefault(location.Id));

    /// <summary>
    /// The lowest level a bot of <paramref name="team"/> landing at <paramref name="location"/> may have under <paramref name="rule"/>: the
    /// location's <see cref="FloorOf"/> minus the slack the rule gives that floor, raised to the survey's creature level minus the rule's
    /// creature margin (the bot is not placed where most hostile creatures are more than that margin above it), and at least 1.
    /// </summary>
    public int LowestLevelAt(WorldSafeLoc location, Team team, PopulationLevelRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        int floor = FloorOf(location);
        int lowest = floor - rule.SlackAt(floor);
        if (rule.CreatureMargin is { } margin && CreatureLevels.TryGetValue((location.Id, team), out int creatures))
            lowest = Math.Max(lowest, creatures - margin);
        return Math.Max(1, lowest);
    }

    /// <summary>The lowest <see cref="LowestLevelAt"/> of the team's locations; the zone's lowest level (minus its slack) when it has none for the team.</summary>
    public int LowestLevelFor(Team team, PopulationLevelRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        IReadOnlyList<WorldSafeLoc> locations = LocationsFor(team);
        return locations.Count == 0 ? Math.Max(1, MinLevel - rule.SlackAt(MinLevel)) : locations.Min(l => LowestLevelAt(l, team, rule));
    }

    /// <summary>Whether the zone is open to the team: no team (0), or its own (2 Alliance, 4 Horde). Any other area team value is never offered.</summary>
    public bool OpenTo(Team team) => AreaTeam == AreaTeams.None
        || (AreaTeam == AreaTeams.Ally && team == Team.Alliance)
        || (AreaTeam == AreaTeams.Horde && team == Team.Horde);
}

/// <summary>A graveyard link the atlas left out: the terrain puts location <c>LocationId</c> in <c>TerrainZoneId</c>, not in <c>ZoneId</c>.</summary>
internal sealed record PopulationDroppedLocation(uint ZoneId, uint LocationId, uint TerrainZoneId);

/// <summary>
/// How far below a placement's floor a bot may be (an ArcaneCore choice, not a game rule). <see cref="Slack"/> levels for a floor up to
/// <see cref="FullSlackMaxFloor"/>, so a level 1 bot starts in a starter zone whose graveyards stand in level 4-7 areas; above it the slack
/// shrinks by one level for every two floor levels, to 0 from <c>FullSlackMaxFloor + 7</c> (19 by default). The levels the zones of the
/// 40s and 50s are built for are not much below their graveyard areas: a level 38 bot at Aerie Peak (41) or the Dire Maul graveyard (44,
/// level 43-46 bears and howlers) dies in a loop. With <see cref="CreatureMargin"/> set, a location whose creature survey level
/// (<see cref="PopulationZone.CreatureLevels"/>) is more than that margin above the bot does not take it either; null ignores the survey.
/// </summary>
internal sealed record PopulationLevelRule(int Slack, int FullSlackMaxFloor, int? CreatureMargin)
{
    /// <summary>The same slack at every floor and no creature check (the rule before the taper; tests).</summary>
    public static PopulationLevelRule Flat(int slack) => new(slack, PlayerbotPopulationOptions.MaxLevel, null);

    /// <summary>The rule the options configure: <c>ZoneLevelSlack</c>, <c>ZoneLevelSlackMaxFloor</c> and <c>CreatureLevelMargin</c>.</summary>
    public static PopulationLevelRule From(PlayerbotPopulationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(options.ZoneLevelSlack, options.ZoneLevelSlackMaxFloor, options.CreatureLevelMargin);
    }

    /// <summary>The slack a placement whose floor is <paramref name="floor"/> gets (0..<see cref="Slack"/>).</summary>
    public int SlackAt(int floor) => floor <= FullSlackMaxFloor ? Slack : Math.Max(0, Slack - (floor - FullSlackMaxFloor + 1) / 2);
}

/// <summary>A zone, one of its graveyards, and the levels a bot placed there may have.</summary>
internal sealed record PopulationPlacement(PopulationZone Zone, WorldSafeLoc Location, int LowLevel, int HighLevel);

/// <summary>A safe location and exact level chosen before a character is created.</summary>
internal sealed record PopulationSafeSelection(PopulationPlacement Placement, int Level);

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

    private PopulationZoneAtlas(PopulationZone[] zones, IReadOnlyList<PopulationDroppedLocation> dropped, int spawnsPerBot = 0, bool frozen = false)
    {
        _zones = zones;
        _byId = zones.ToDictionary(z => z.ZoneId);
        Dropped = dropped;
        _spawnsPerBot = spawnsPerBot;
        _frozen = frozen;
    }

    // The shared Empty atlas takes no capacity or divisor (it would leak between features).
    private readonly bool _frozen;
    private volatile int _spawnsPerBot;

    /// <summary>
    /// The zone capacity divisor (<c>SpawnsPerBot</c>; 0 = no cap): a zone takes no bot once its assigned bots reach the fitting surface
    /// spawns of <see cref="Capacity"/> divided by this.
    /// </summary>
    public int SpawnsPerBot
    {
        get => _spawnsPerBot;
        set
        {
            if (!_frozen) _spawnsPerBot = value;
        }
    }

    private volatile PopulationSpawnCapacity? _capacity;

    /// <summary>
    /// The surface spawn survey (<see cref="PopulationCreatureSurvey.CountSurface"/>); null until it is in, and while it is null no zone is
    /// capped. Set by the world thread when the background survey posts its result; the zones themselves never change.
    /// </summary>
    public PopulationSpawnCapacity? Capacity
    {
        get => _capacity;
        set
        {
            if (!_frozen) _capacity = value;
        }
    }

    /// <summary>The most bots <paramref name="zone"/> takes of <paramref name="team"/> in the level range; null when it is not capped (cap off or survey not in).</summary>
    public int? CapOf(PopulationZone zone, Team team, int minLevel, int maxLevel)
    {
        ArgumentNullException.ThrowIfNull(zone);
        int perBot = _spawnsPerBot;
        if (perBot <= 0 || _capacity is not { } capacity) return null;
        return capacity.Fitting(zone.ZoneId, team, minLevel, maxLevel) / perBot;
    }

    /// <summary>
    /// The bots the zones that fit the band can hold together (a zone counts the larger of its caps for the teams); null when no zone is
    /// capped. A value below the band's want means the band cannot be filled.
    /// </summary>
    public int? BandCapacity(PopulationBand band, IReadOnlyList<Team> teams, PopulationLevelRule rule)
    {
        ArgumentNullException.ThrowIfNull(band);
        ArgumentNullException.ThrowIfNull(teams);
        if (_spawnsPerBot <= 0 || _capacity is null) return null;
        var perZone = new Dictionary<uint, int>();
        foreach (Team team in teams)
            foreach (PopulationZone zone in FittingZones(band.MinLevel, band.MaxLevel, team, rule))
                perZone[zone.ZoneId] = Math.Max(perZone.GetValueOrDefault(zone.ZoneId), CapOf(zone, team, band.MinLevel, band.MaxLevel) ?? 0);
        return perZone.Values.Sum();
    }

    /// <summary>Diagnostic lines for the survey log: the capped bots of each band, and each zone whose cap is under 20 at its own level range.</summary>
    public IEnumerable<string> CapacityLines(PlayerbotPopulationOptions pop)
    {
        ArgumentNullException.ThrowIfNull(pop);
        PopulationLevelRule rule = PopulationLevelRule.From(pop);
        Team[] teams = pop.Factions switch { PopulationFactions.Alliance => [Team.Alliance], PopulationFactions.Horde => [Team.Horde], _ => [Team.Alliance, Team.Horde] };
        foreach (PopulationBand band in pop.Bands.Where(b => b.Weight > 0))
            if (BandCapacity(band, teams, rule) is { } cap)
                yield return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"levels {band.MinLevel}-{band.MaxLevel} hold {cap} bots");
        foreach (PopulationZone zone in _zones)
        {
            int cap = teams.Where(zone.OpenTo).Select(t => CapOf(zone, t, zone.MinLevel, zone.MaxLevel) ?? 0).DefaultIfEmpty(0).Max();
            if (cap < 20 && teams.Any(zone.OpenTo))
                yield return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"zone {zone.ZoneId} {zone.Name} cap {cap}");
        }
    }

    /// <summary>
    /// Whether <paramref name="zone"/> may take one more bot of <paramref name="team"/> in the level range given <paramref name="assigned"/> bots:
    /// always when the cap is off or the survey is not in, otherwise only while the assigned bots are fewer than
    /// (fitting surface spawns) / <see cref="SpawnsPerBot"/>.
    /// </summary>
    public bool HasRoom(PopulationZone zone, Team team, int minLevel, int maxLevel, IReadOnlyDictionary<uint, int> assigned)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(assigned);
        int perBot = _spawnsPerBot;
        if (perBot <= 0 || _capacity is not { } capacity) return true;
        return assigned.GetValueOrDefault(zone.ZoneId) < capacity.Fitting(zone.ZoneId, team, minLevel, maxLevel) / perBot;
    }

    public static PopulationZoneAtlas Empty { get; } = new([], [], 0, frozen: true);

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
    /// <param name="creatureLevel">
    /// The creature survey level of a location for a team (<see cref="PopulationCreatureSurvey"/>; 0 when nothing constrains it); null
    /// surveys nothing. Kept on the zone as <see cref="PopulationZone.CreatureLevels"/>.
    /// </param>
    /// <param name="spawnsPerBot">The zone capacity divisor (<see cref="SpawnsPerBot"/>); 0 puts no cap on a zone.</param>
    /// <param name="capacity">The surface spawn survey (<see cref="Capacity"/>); null while it is not in.</param>
    public static PopulationZoneAtlas Build(IReadOnlyList<AreaTemplate> areas, GraveyardCatalog graveyards, IReadOnlyCollection<uint> allowedMaps,
        ISet<uint> excluded, Func<WorldSafeLoc, (uint ZoneId, uint AreaId)>? locate = null, Func<WorldSafeLoc, Team, int>? creatureLevel = null,
        int spawnsPerBot = 0, PopulationSpawnCapacity? capacity = null)
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

            var creatureLevels = new Dictionary<(uint, Team), int>();
            if (creatureLevel is not null)
            {
                foreach ((Team team, List<WorldSafeLoc> locations) in new[] { (Team.Alliance, alliance), (Team.Horde, horde) })
                    foreach (WorldSafeLoc location in locations)
                        if (creatureLevel(location, team) is var level and > 0) creatureLevels[(location.Id, team)] = level;
            }

            zones.Add(new PopulationZone(zone.Entry, zone.MapId, zone.Name, levels.Min(), levels.Max(), zone.Team, alliance, horde)
            {
                LocationLevels = locationLevels,
                CreatureLevels = creatureLevels,
            });
        }

        return new PopulationZoneAtlas([.. zones.OrderBy(z => z.ZoneId)], dropped, spawnsPerBot) { Capacity = capacity };
    }

    /// <summary>
    /// Whether a bot of <paramref name="team"/> and <paramref name="level"/> may be placed in <paramref name="zone"/>: the zone is open to
    /// the team, the level is at most the zone's highest, and one of the team's locations takes it (its
    /// <see cref="PopulationZone.LowestLevelAt"/> under <paramref name="rule"/> is at most the level).
    /// </summary>
    public static bool Fits(PopulationZone zone, int level, Team team, PopulationLevelRule rule)
        => level <= zone.MaxLevel && zone.OpenTo(team) && zone.LocationsFor(team).Any(l => zone.LowestLevelAt(l, team, rule) <= level);

    /// <summary>
    /// Whether a bot of <paramref name="level"/> is below every place in <paramref name="zone"/> it could have been put
    /// (<see cref="PopulationZone.LowestLevelFor"/> the team under the same <paramref name="rule"/> placement uses). Such a bot could not
    /// have been placed there, and a bot placed anywhere is never under-levelled where it was placed, so relocation does not ping-pong.
    /// </summary>
    public static bool UnderLevelled(PopulationZone zone, int level, Team team, PopulationLevelRule rule) => level < zone.LowestLevelFor(team, rule);

    private static bool Takes(PopulationZone zone, WorldSafeLoc location, Team team, int minLevel, int maxLevel, PopulationLevelRule rule)
        => Math.Max(minLevel, zone.LowestLevelAt(location, team, rule)) <= Math.Min(maxLevel, zone.MaxLevel);

    /// <summary>The zones <see cref="Fits"/> for the team at some level of <paramref name="minLevel"/>..<paramref name="maxLevel"/>.</summary>
    public IEnumerable<PopulationZone> FittingZones(int minLevel, int maxLevel, Team team, PopulationLevelRule rule)
        => _zones.Where(z => z.OpenTo(team) && z.LocationsFor(team).Any(l => Takes(z, l, team, minLevel, maxLevel, rule)));

    /// <summary>
    /// The (band, team) pairs no zone takes at any level of the band under <paramref name="rule"/>, for the bands with a weight above 0
    /// and the given teams. Empty when every band has a zone for every team; the feature logs any other result.
    /// </summary>
    public IReadOnlyList<(PopulationBand Band, Team Team)> UncoveredBands(IReadOnlyList<PopulationBand> bands, IReadOnlyList<Team> teams, PopulationLevelRule rule)
    {
        ArgumentNullException.ThrowIfNull(bands);
        ArgumentNullException.ThrowIfNull(teams);
        return [.. bands.Where(b => b.Weight > 0).SelectMany(b => teams.Select(t => (Band: b, Team: t)))
            .Where(p => !FittingZones(p.Band.MinLevel, p.Band.MaxLevel, p.Team, rule).Any())];
    }

    /// <summary>
    /// A placement for a bot that may have any level of <paramref name="minLevel"/>..<paramref name="maxLevel"/>: the fitting zone with the
    /// fewest bots assigned (ties broken by <paramref name="random"/>), one of its graveyards for the team, and the levels of the range the
    /// zone takes. Null when no zone fits.
    /// </summary>
    public PopulationPlacement? Pick(int minLevel, int maxLevel, Team team, PopulationLevelRule rule, IReadOnlyDictionary<uint, int> assigned, Random random)
    {
        ArgumentNullException.ThrowIfNull(assigned);
        ArgumentNullException.ThrowIfNull(random);
        PopulationZone[] fitting = [.. FittingZones(minLevel, maxLevel, team, rule).Where(z => HasRoom(z, team, minLevel, maxLevel, assigned))];
        if (fitting.Length == 0) return null;
        int fewest = fitting.Min(z => assigned.GetValueOrDefault(z.ZoneId));
        PopulationZone[] least = [.. fitting.Where(z => assigned.GetValueOrDefault(z.ZoneId) == fewest)];
        PopulationZone zone = least[random.Next(least.Length)];
        WorldSafeLoc[] locations = [.. zone.LocationsFor(team).Where(l => Takes(zone, l, team, minLevel, maxLevel, rule))];
        WorldSafeLoc location = locations[random.Next(locations.Length)];
        return new PopulationPlacement(zone, location, Math.Max(minLevel, zone.LowestLevelAt(location, team, rule)), Math.Min(maxLevel, zone.MaxLevel));
    }

    /// <summary>
    /// Try the least-assigned fitting zones first, including every eligible level and team-linked graveyard before giving up.
    /// <paramref name="safe"/> reads terrain and creature content on the world thread; a refused zone is skipped in this
    /// selection, even when it stays least-assigned after the refusal. A zone at its capacity (<see cref="HasRoom"/>) and a zone
    /// <paramref name="vetoed"/> for this bot (it escaped a dead end there) are not offered.
    /// </summary>
    internal PopulationSafeSelection? PickSafe(int minLevel, int maxLevel, Team team, PopulationLevelRule rule,
        IReadOnlyDictionary<uint, int> assigned, Random random, Func<WorldSafeLoc, int, bool> safe, Func<uint, bool>? vetoed = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(assigned);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(safe);
        var ordered = FittingZones(minLevel, maxLevel, team, rule)
            .Where(zone => HasRoom(zone, team, minLevel, maxLevel, assigned) && vetoed?.Invoke(zone.ZoneId) != true)
            .Select(zone => (Zone: zone, Tie: random.Next()))
            .OrderBy(pair => assigned.GetValueOrDefault(pair.Zone.ZoneId)).ThenBy(pair => pair.Tie);
        foreach (var (zone, _) in ordered)
        {
            IReadOnlyList<WorldSafeLoc> locations = zone.LocationsFor(team);
            int firstLocation = random.Next(locations.Count);
            for (int place = 0; place < locations.Count; place++)
            {
                WorldSafeLoc location = locations[(firstLocation + place) % locations.Count];
                if (!Takes(zone, location, team, minLevel, maxLevel, rule)) continue;
                // The levels this location takes under the rule (its floor and creature survey), not the zone's.
                int low = Math.Max(minLevel, zone.LowestLevelAt(location, team, rule));
                int high = Math.Min(maxLevel, zone.MaxLevel);
                int levels = high - low + 1;
                int firstLevel = random.Next(levels);
                for (int step = 0; step < levels; step++)
                {
                    int level = low + ((firstLevel + step) % levels);
                    if (safe(location, level))
                        return new PopulationSafeSelection(new PopulationPlacement(zone, location, low, high), level);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A placement for a bot in one of the best valued zones for it (see <see cref="Rank"/>): a weighted draw among the top
    /// <c>TopZones</c> by total value, scaled down as the zone fills towards <c>ZoneSoftCap</c>, then a safe location of the zone the bot's
    /// level takes. A zone with no safe location is dropped and the draw repeats, first over the rest of the top zones, then over the rest
    /// of the ranking. <paramref name="ranked"/> receives the ranking when given. Null when no valued zone is safe.
    /// </summary>
    internal PopulationSafeSelection? PickValued(PopulationBotProfile bot, PopulationLevelRule rule, PopulationZoneValue value,
        PopulationValueWeights w, IReadOnlyDictionary<uint, int> assigned, Random random, Func<WorldSafeLoc, int, bool> safe,
        Func<uint, bool>? vetoed = null, Func<uint, int>? trouble = null, IList<ZoneScore>? ranked = null)
        => throw new NotImplementedException();

    /// <summary>
    /// The zones the bot may be sent to for their value, best first: eligible (open to the team, with a location for it, not vetoed, with
    /// room, under the soft cap, and fitting the bot's level, or for a level-capped bot reaching it within <c>CapReach</c>), valued and
    /// with a total above 0.
    /// </summary>
    internal IReadOnlyList<ZoneScore> Rank(PopulationBotProfile bot, PopulationLevelRule rule, PopulationZoneValue value, PopulationValueWeights w,
        IReadOnlyDictionary<uint, int> assigned, Func<uint, bool>? vetoed = null, Func<uint, int>? trouble = null)
        => throw new NotImplementedException();
}

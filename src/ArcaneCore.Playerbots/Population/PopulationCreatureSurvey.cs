using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// The creature level near a placement location: what a bot landing at a graveyard meets first. The area level alone misses it: the
/// Feralas Dire Maul graveyard stands in Dire Maul (44) beside level 43-46 Longtooth Howlers, Grizzled Ironfur Bears and Rogue Vale
/// Screechers, and Morgan's Plot in Deadwind Pass (an unlevelled sub-area, so the zone's 50) beside level 58-60 undead.
/// <para>
/// The level of a location for a team is the lower median of the template <c>MinLevel</c> of the database creature spawns within the
/// radius that are hostile to the team: a bot of level L meets mostly creatures above L + margin exactly when that median is above
/// L + margin. Hostile is the FactionTemplate.dbc relation of the creature's template to the team's player template (1 Human for the
/// Alliance, 2 Orc for the Horde; vmangos <c>FactionTemplateEntry::IsHostileTo</c>), without reputation: a placement heuristic, not the
/// combat rule. Only creatures within <see cref="MaxHeightDifference"/> yards above or below the graveyard count: the 2D circle around
/// the Stonard graveyard (z 22) takes in 23 level 46-47 Nethergarde Miners and 3 others at z -13 to +7 in the valley below it, which
/// would close the Horde's level 35-45 hub. Civilians, creatures that never aggro on sight, invisible (trigger) creatures and
/// not-selectable ones do not count, nor do spawns listed under a positive game event (the AQ war effort silithids at Camp Mojache
/// and Gadgetzan are absent outside their event). Fewer than <see cref="MinHostile"/> hostile creatures give no level.
/// </para>
/// <para>Thread affinity: pure; the feature runs it on the background graveyard lookup, over immutable spawn lists.</para>
/// </summary>
internal static class PopulationCreatureSurvey
{
    /// <summary>The fewest hostile creatures near a location that give it a level (a lone patrol or rare is not the place).</summary>
    public const int MinHostile = 3;

    /// <summary>The most yards a creature spawn may stand above or below the graveyard and still count.</summary>
    public const float MaxHeightDifference = 15f;

    private const CreatureBehaviourFlags Ignored = CreatureBehaviourFlags.Civilian | CreatureBehaviourFlags.NoAggro | CreatureBehaviourFlags.Invisible;

    /// <summary>The player faction template the survey tests hostility against: 1 (Human) for the Alliance, 2 (Orc) for the Horde.</summary>
    public static uint PlayerTemplateOf(Team team) => team == Team.Alliance ? 1u : 2u;

    /// <summary>
    /// Whether the spawn is a creature a bot of the <paramref name="player"/> faction template would fight (the survey's counting rule:
    /// not gated by a positive game event, a known template, not civilian, no-aggro, invisible or not selectable, hostile to the player
    /// template); <paramref name="level"/> is the template <c>MinLevel</c>.
    /// </summary>
    internal static bool TryHostileLevel(CreatureSpawn spawn, FactionTemplateRecord player, Func<uint, CreatureTemplate?> templateOf,
        FactionTemplateCatalog factions, IReadOnlySet<uint> gated, out int level)
    {
        level = 0;
        if (gated.Contains(spawn.Guid) || templateOf(spawn.Entry) is not { } template) return false;
        if ((template.Behaviour & Ignored) != 0 || (template.UnitFlags & (uint)UnitFlags.NotSelectable) != 0) return false;
        if (!IsHostileTo(template, player, factions)) return false;
        level = template.MinLevel;
        return true;
    }

    /// <summary>Whether the creature template's faction is hostile to the player faction template (false when its faction is unknown).</summary>
    internal static bool IsHostileTo(CreatureTemplate template, FactionTemplateRecord player, FactionTemplateCatalog factions)
        => factions.Find(template.Faction) is { } faction && faction.IsHostileTo(player);

    /// <summary>
    /// Zone capacity: the level of every creature spawn a bot of each team would fight on the surface, by zone and team. A spawn counts
    /// for a team under the same rule as <see cref="LevelNear"/>, when <paramref name="zoneOf"/> gives it a zone (0: unknown, or under
    /// the terrain: the caller decides that off the world thread from the terrain tile) and <paramref name="outdoors"/> says it is not
    /// indoors (an indoor WMO group, which needs the map's collision and so the world thread). The spawns that pass the cheap rule are
    /// ordered by map tile and handed to <paramref name="outdoors"/> in batches that lie within one tile and hold at most
    /// <see cref="SurfaceBatch"/> spawns, so the caller can hop to the world thread once per batch, loading at most one tile per hop.
    /// <para>Thread affinity: any thread; <paramref name="outdoors"/> decides where its own work runs. Throws <see cref="OperationCanceledException"/> when cancelled.</para>
    /// </summary>
    public static PopulationSpawnCapacity CountSurface(IEnumerable<CreatureSpawn> spawns, Func<CreatureSpawn, uint> zoneOf,
        Func<IReadOnlyList<CreatureSpawn>, IReadOnlyList<bool>> outdoors, Func<uint, CreatureTemplate?> templateOf,
        FactionTemplateCatalog factions, IReadOnlySet<uint> gated, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spawns);
        ArgumentNullException.ThrowIfNull(zoneOf);
        ArgumentNullException.ThrowIfNull(outdoors);
        ArgumentNullException.ThrowIfNull(templateOf);
        ArgumentNullException.ThrowIfNull(factions);
        ArgumentNullException.ThrowIfNull(gated);
        FactionTemplateRecord? alliance = factions.Find(PlayerTemplateOf(Team.Alliance));
        FactionTemplateRecord? horde = factions.Find(PlayerTemplateOf(Team.Horde));
        var candidates = new List<(CreatureSpawn Spawn, uint Zone, int Alliance, int Horde, int TileX, int TileY)>();
        int seen = 0;
        foreach (CreatureSpawn spawn in spawns)
        {
            if ((++seen & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            int forAlliance = 0, forHorde = 0;
            bool ally = alliance is not null && TryHostileLevel(spawn, alliance, templateOf, factions, gated, out forAlliance);
            bool orc = horde is not null && TryHostileLevel(spawn, horde, templateOf, factions, gated, out forHorde);
            if (!ally && !orc) continue;
            uint zone = zoneOf(spawn);
            if (zone == 0) continue;
            (int X, int Y) tile = TerrainTile.TileOf(spawn.X, spawn.Y) ?? (-1, -1);
            candidates.Add((spawn, zone, ally ? forAlliance : -1, orc ? forHorde : -1, tile.X, tile.Y));
        }

        candidates.Sort(static (a, b) =>
        {
            int c = a.Spawn.MapId.CompareTo(b.Spawn.MapId);
            if (c == 0) c = a.TileX.CompareTo(b.TileX);
            if (c == 0) c = a.TileY.CompareTo(b.TileY);
            return c == 0 ? a.Spawn.Guid.CompareTo(b.Spawn.Guid) : c;
        });
        var levels = new Dictionary<(uint ZoneId, Team Team), List<int>>();
        for (int start = 0; start < candidates.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = candidates[start];
            int count = 1;
            while (count < SurfaceBatch && start + count < candidates.Count
                && candidates[start + count] is var next && next.Spawn.MapId == first.Spawn.MapId && next.TileX == first.TileX && next.TileY == first.TileY)
                count++;
            var batch = new CreatureSpawn[count];
            for (int i = 0; i < count; i++) batch[i] = candidates[start + i].Spawn;
            IReadOnlyList<bool> surface = outdoors(batch);
            if (surface.Count != count) throw new InvalidOperationException("the surface test answered for a different number of spawns");
            for (int i = 0; i < count; i++)
            {
                if (!surface[i]) continue;
                var c = candidates[start + i];
                if (c.Alliance >= 0) Add(levels, (c.Zone, Team.Alliance), c.Alliance);
                if (c.Horde >= 0) Add(levels, (c.Zone, Team.Horde), c.Horde);
            }

            start += count;
        }

        return new PopulationSpawnCapacity(levels.ToDictionary(p => p.Key, p => { int[] sorted = [.. p.Value]; Array.Sort(sorted); return sorted; }));

        static void Add(Dictionary<(uint, Team), List<int>> into, (uint, Team) key, int level)
        {
            if (!into.TryGetValue(key, out List<int>? list)) into[key] = list = [];
            list.Add(level);
        }
    }

    /// <summary>How far under the ground a spawn must stand to be underground (the rule of <c>PlayerbotNavigation.IsOutdoorsOnSurface</c>).</summary>
    public const float UndergroundYards = 8f;

    /// <summary>The most spawns one <c>outdoors</c> call of <see cref="CountSurface"/> is asked about.</summary>
    public const int SurfaceBatch = 128;

    /// <summary>
    /// The lower median <c>MinLevel</c> of the hostile creatures within <paramref name="radius"/> yards (2D) of <paramref name="location"/>
    /// and <see cref="MaxHeightDifference"/> yards of its height,
    /// or 0 when fewer than <see cref="MinHostile"/> are there, the radius is not positive or the team's player template is unknown.
    /// </summary>
    public static int LevelNear(WorldSafeLoc location, Team team, float radius, IReadOnlyList<CreatureSpawn> spawnsOnMap,
        Func<uint, CreatureTemplate?> templateOf, FactionTemplateCatalog factions, IReadOnlySet<uint> gated)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(spawnsOnMap);
        ArgumentNullException.ThrowIfNull(templateOf);
        ArgumentNullException.ThrowIfNull(factions);
        ArgumentNullException.ThrowIfNull(gated);
        if (radius <= 0 || factions.Find(PlayerTemplateOf(team)) is not { } player) return 0;
        float r2 = radius * radius;
        var levels = new List<int>();
        foreach (CreatureSpawn spawn in spawnsOnMap)
        {
            float dx = spawn.X - location.X, dy = spawn.Y - location.Y;
            if (spawn.MapId != location.MapId || dx * dx + dy * dy > r2 || MathF.Abs(spawn.Z - location.Z) > MaxHeightDifference) continue;
            if (TryHostileLevel(spawn, player, templateOf, factions, gated, out int level)) levels.Add(level);
        }

        if (levels.Count < MinHostile) return 0;
        levels.Sort();
        return levels[(levels.Count - 1) / 2];
    }
}

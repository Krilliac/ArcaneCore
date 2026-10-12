using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots;

/// <summary>One bounded, collision-backed route owned by a server-managed player.</summary>
internal sealed class PlayerbotRoute
{
    /// <summary>A completed quest's ender route may cross only individually manageable pack hazards.</summary>
    internal bool CompletedQuestReturn { get; set; }
    internal PlayerbotRoute(IReadOnlyList<Vector3> points, float distance, bool navigated = false)
    {
        Points = points;
        Distance = distance;
        Navigated = navigated;
    }

    /// <summary>
    /// A route joined from legs of different origins (a hazard detour: a mesh leg, then legs that may have been stepped over the
    /// terrain): <paramref name="legNavigated"/>[i] says whether the leg from point i to point i + 1 came from the navigation mesh.
    /// </summary>
    internal PlayerbotRoute(IReadOnlyList<Vector3> points, float distance, IReadOnlyList<bool> legNavigated)
    {
        if (legNavigated.Count != Math.Max(0, points.Count - 1))
            throw new ArgumentException("one flag per leg", nameof(legNavigated));
        Points = points;
        Distance = distance;
        _legs = legNavigated;
        Navigated = legNavigated.Count > 0 && legNavigated.All(leg => leg);
    }

    private readonly IReadOnlyList<bool>? _legs;

    internal IReadOnlyList<Vector3> Points { get; }
    internal float Distance { get; }

    /// <summary>
    /// The corners came from the map's navigation mesh: every leg between them is walkable by construction (vmangos
    /// <c>PathFinder</c> moves units along such corners without further checks). The motion still snaps each position to the
    /// floor, but it does not test a straight line of sight between two positions, which across a corner cuts through the very
    /// obstacle the mesh goes around (a pillar, a counter, a doorway's edge). Routes stepped over the terrain without a mesh
    /// (<see cref="PlayerbotNavigation.TryTerrainRoute"/>) carry no such proof and keep the line-of-sight check.
    /// </summary>
    internal bool Navigated { get; }

    /// <summary>
    /// Whether every leg walked from the one ending at point <paramref name="fromPoint"/> through the one ending at point
    /// <paramref name="toPoint"/> came from the navigation mesh (<see cref="Navigated"/> for a route of one origin). A position on a
    /// stepped leg keeps the floor and line-of-sight checks even when an earlier leg of the same route was the mesh's.
    /// </summary>
    internal bool LegsNavigated(int fromPoint, int toPoint)
    {
        if (_legs is null) return Navigated;
        int first = Math.Clamp(fromPoint, 1, Points.Count - 1), last = Math.Clamp(toPoint, 1, Points.Count - 1);
        for (int point = first; point <= last; point++)
            if (!_legs[point - 1]) return false;
        return true;
    }

    internal int NextPoint { get; set; } = 1;
    internal bool Complete => NextPoint >= Points.Count;
}

internal static class PlayerbotNavigation
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, PlayerbotRisk> Guards = [];

    /// <summary>
    /// The risk whose hazards (<see cref="PlayerbotHazards"/>) every route of this bot must keep out of (null: none). The brain sets
    /// it each think; a party bot follows its master wherever he goes and has none.
    /// </summary>
    internal static void Guard(Player player, PlayerbotRisk? risk)
    {
        if (risk is null) Guards.Remove(player);
        else Guards.AddOrUpdate(player, risk);
    }

    /// <summary>The detour offsets tried past a hazard on the way, on either side (yards beyond its radius).</summary>
    private static readonly float[] HazardDetourYards = [5f, 15f];

    internal static bool IsUsablePath(PathResult path, int maxPoints, float maxDistance)
        => path.HasPath && (path.Type & (PathType.NotUsingPath | PathType.DestForced | PathType.FlyPath)) == 0
            && path.Points.Count <= maxPoints
            && path.Points.All(Finite) && float.IsFinite(path.Length)
            && path.Length <= maxDistance;

    /// <param name="trial">A plan made to weigh something (the pull estimate's approach, a detour's waypoint), not to walk a goal: a
    /// refusal of it is no evidence that the bot is boxed in (<see cref="PlayerbotRisk.Refused"/> is not told).</param>
    internal static bool TryPlan(Player player, Vector3 destination, PlayerbotOptions options,
        out PlayerbotRoute? route, bool trial = false)
        => Plan(player, destination, destination, options, partial: false, trial, out route, out _);

    /// <summary>
    /// A route to <paramref name="destination"/> with no hazard check at all: only the escape from a ring of strong creatures
    /// (<see cref="PlayerbotRisk.Refused"/>), which checks the route against every other hazard itself.
    /// </summary>
    internal static bool TryPlanUnguarded(Player player, Vector3 destination, PlayerbotOptions options, out PlayerbotRoute? route)
        => PlanDirect(player, destination, destination, options, partial: false, out route, out _) && route is not null;

    /// <summary>
    /// <see cref="TryPlan"/> with an explicit search budget (up to <see cref="MaxSearchNodes"/>) whatever the straight distance: for a
    /// goal near in a straight line but far by the mesh (a ledge above the bot, reached by a long way round). One query; the route
    /// bounds (<see cref="PlayerbotOptions.MaxRouteYards"/>, <see cref="PlayerbotOptions.MaxPathPoints"/>) still hold.
    /// </summary>
    internal static bool TryPlanFar(Player player, Vector3 destination, PlayerbotOptions options, int searchNodes, out PlayerbotRoute? route)
        => Plan(player, destination, destination, options, partial: false, trial: false, out route, out _, Math.Clamp(searchNodes, 1, MaxSearchNodes));

    /// <summary>Closer than this to its goal, a partial route ends near enough to count (<see cref="TryPlanToward"/>).</summary>
    internal const float PartialProgressYards = 2f;

    /// <summary>
    /// A route towards <paramref name="goal"/>, which may lie beyond one route. Beyond <see cref="PlayerbotOptions.MaxPathPoints"/> yards
    /// the map's region graph leads (<see cref="RegionLeg"/>); without it, the goal itself when it is within one route's
    /// reach (<see cref="PlayerbotOptions.MaxRouteYards"/>, and <see cref="PlayerbotOptions.MaxPathPoints"/> yards for the terrain
    /// stepper), else the point that far along the straight line to it. When the navigation mesh does not connect that point, the
    /// route goes to the walkable point nearest to it (vmangos <c>PathFinder</c> with <c>PATHFIND_INCOMPLETE</c>), taken when it ends
    /// at least <see cref="PartialProgressYards"/> closer to the goal than the bot stands. One path query, as <see cref="TryPlan"/>.
    /// <para>
    /// The straight-line point on its own is often not walkable in the mountains, and the exact search then fails although the
    /// way is open: on the real Dun Morogh terrain the point 126 yards towards Mirthblade's body (359 yards away) has no exact
    /// path, while the mesh reaches a point 4 yards from it. The ghost was judged stalled after 10 seconds and took the spirit
    /// healer, and a living bot with a far quest giver stood still for good.
    /// </para>
    /// </summary>
    internal static bool TryPlanToward(Player player, Vector3 goal, PlayerbotOptions options, out PlayerbotRoute? route,
        bool completedQuestReturn = false)
    {
        route = null;
        Vector3 origin = new(player.X, player.Y, player.Z);
        if (!Finite(goal) || !Finite(origin)) return false;
        float distance = Vector3.Distance(origin, goal);
        // The way to the goal itself first: the search reads the navigation tiles it crosses (PathOptions.LoadTiles), so a road
        // that leaves the loaded tiles is followed. The straight-line steps below are a greedy fallback that stops wherever the
        // mesh ends nearest a point on the line: on the edge of the Red Cloud Mesa above Bloodhoof Village (live stress test
        // 2026-10-08, quest 1656, 99 stalls), whose way down runs south through a tile no grid held.
        // Within the first step the loop below plans to the goal itself, so the goal-first query would only repeat it.
        float chunk = MathF.Min(options.MaxRouteYards * 0.9f, Math.Max(1, options.MaxPathPoints - 2));
        // Beyond the first step the map's region graph knows the way, round mountains and out of dead ends: the leg goes to a portal
        // of that way (RegionLeg). Only when the graph does not decide (none, still being built, both ends in one region) do the
        // goal-first query and the straight-line steps below plan.
        if (distance > chunk && RegionLeg(player, goal, options, out route, completedQuestReturn) is { } planned) return planned;
        // When the mesh had a corridor to judge (no path, a blacklisted end), the shorter straight-line points below would only repeat
        // the refusal: one plan attempt costs one query, as before. Only an unloaded tile or no mesh (a refused straight line) goes on.
        if (distance > chunk && distance <= options.MaxRouteYards)
        {
            if (Plan(player, goal, goal, options, partial: true, trial: false, out route, out bool goalStraightRefused,
                completedQuestReturn: completedQuestReturn)) return true;
            if (!goalStraightRefused) return false;
        }
        // A point that far may lie on a navigation-mesh tile not loaded yet (tiles load with the map's grids, around the players):
        // the mesh then answers a straight line (vmangos PathFinder's HaveTiles shortcut), which the terrain stepper refuses across
        // any hill, and the bot stood at the tile's edge for good (Dawnrover on quest 35, 107 yards short of the Elwynn tile
        // boundary south of Goldshire, the 126-yard point beyond it). Only then is a shorter step tried: it stays on the loaded mesh,
        // walking it loads the next tile, and the next plan crosses. Any other refusal (no path, a blacklisted end) costs one query.
        for (; ; chunk /= 2)
        {
            Vector3 destination = distance > chunk ? origin + ((goal - origin) * (chunk / distance)) : goal;
            if (Plan(player, destination, goal, options, partial: true, trial: false, out route, out bool straightRefused,
                completedQuestReturn: completedQuestReturn)) return true;
            if (!straightRefused || distance <= chunk / 2 || chunk / 2 < MinTowardChunkYards) return false;
        }
    }

    /// <summary>The shortest step <see cref="TryPlanToward"/> shortens its point to before it gives up.</summary>
    internal const float MinTowardChunkYards = 24f;

    /// <summary>
    /// How far along the region graph's way one leg of <see cref="RegionLeg"/> goes at most (straight lines between portals), and at most
    /// half of <see cref="PlayerbotOptions.MaxRouteYards"/>: the path query's search grows with the distance (<see cref="SearchNodes"/>),
    /// and the way it finds is longer than the straight lines.
    /// </summary>
    internal const float RegionLegYards = 1000f;

    /// <summary>The most portals one region search settles (<see cref="NavRegionGraph.FindRoute"/>).</summary>
    internal const int RegionSearchNodes = 20_000;

    /// <summary>
    /// A leg of the way to a far <paramref name="goal"/> along the map's region graph (<see cref="NavRegionGraph"/>: which parts of the
    /// whole navigation mesh join which, built once per map off the world thread): the region search finds the way across the map coarsely,
    /// and the leg is the path query to its farthest portal within <see cref="RegionLegYards"/> (the goal itself when that near), or to
    /// its first portal. Without a way (the goal's region is not joined to the bot's) the search's way leads to the portal nearest the
    /// goal, as a partial path does. Null when the graph does not decide: none yet, no region under the bot or the goal, both in one
    /// region, or no portal nearer the goal than the bot; the straight-line planning goes on then.
    /// <para>
    /// Before, a goal beyond one route was approached only by points on the straight line to it. On the real terrain no route at all was
    /// planned from any stall point of issue #191 (quest 5881, Stonetalon to Grish Longrunner at the Great Lift, 5,200 to 7,900 yards)
    /// or of issue #194 (quest 1418, the Swamp of Sorrows to Kargath, 3,400 yards): the line's next point lay off the mesh, in rock. A
    /// partial path to the goal itself reaches Grish in four or five legs, but not Kargath: it ends at the point of the swamp nearest to
    /// it (-9356.8, -3872.5), from which no partial path closes on it, while the way leads west and north round the mountains (by
    /// Deadwind Pass, Redridge, the Burning Steppes and Blackrock Mountain: twelve legs, 9,400 yards).
    /// </para>
    /// </summary>
    private static bool? RegionLeg(Player player, Vector3 goal, PlayerbotOptions options, out PlayerbotRoute? route,
        bool completedQuestReturn)
    {
        route = null;
        if (player.Map is not { } map || map.Collision.Pathfinder is not NavMeshPathfinder nav
            || nav.RegionGraph(map.MapId, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes) is not { } graph)
            return null;
        Vector3 start = PlayerbotMotion.CurrentPosition(player);
        var locate = new PathOptions { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true };
        int from = nav.RegionAt(graph, start, locate), to = nav.RegionAt(graph, goal, locate);
        if (from < 0 || to < 0) return null;
        NavRegionRouteStatus status = graph.FindRoute(from, start, to, goal, RegionSearchNodes, out List<Vector3> portals);
        if (portals.Count == 0) return null;
        if (status == NavRegionRouteStatus.Found) portals.Add(goal);

        int pick = 0;
        float leg = MathF.Min(RegionLegYards, options.MaxRouteYards * 0.5f), along = Vector3.Distance(start, portals[0]);
        for (int i = 1; i < portals.Count; i++)
        {
            along += Vector3.Distance(portals[i - 1], portals[i]);
            if (along > leg) break;
            pick = i;
        }

        // The portal is a point on the walkable mesh joined to the bot's: the query reaches it, or closes on it partially. When neither
        // query is a route (a blacklisted portal, a hazard in the way) the graph does not decide: the goal-first query and the
        // straight-line steps still get their turn.
        if (Plan(player, portals[pick], portals[pick], options, partial: true, trial: false, out route, out _,
            completedQuestReturn: completedQuestReturn)) return true;
        if (pick > 0 && Plan(player, portals[0], portals[0], options, partial: true, trial: false, out route, out _,
            completedQuestReturn: completedQuestReturn)) return true;
        route = null;
        return null;
    }

    /// <summary>
    /// One path query from where the bot is now to <paramref name="destination"/>; with <paramref name="partial"/> a route that
    /// stops short of it on the mesh is accepted when it closes on <paramref name="goal"/>.
    /// </summary>
    /// <param name="straightRefused">The mesh had no corridor to offer, only a straight line (an unloaded tile, or no mesh), and the terrain
    /// stepper refused that line.</param>
    private static bool Plan(Player player, Vector3 destination, Vector3 goal, PlayerbotOptions options, bool partial, bool trial,
        out PlayerbotRoute? route, out bool straightRefused, int? searchNodes = null, bool completedQuestReturn = false)
    {
        if (!PlanDirect(player, destination, goal, options, partial, out route, out straightRefused, searchNodes) || route is null) return false;
        route.CompletedQuestReturn = completedQuestReturn;
        if (!Guards.TryGetValue(player, out PlayerbotRisk? risk)
            || risk.Blocking(player, route.Points, completedReturn: completedQuestReturn) is not { } hazard) return true;

        // The way passes through a hazard: walk round it (by a corner before it and one past it, on either side), or not at all.
        PlayerbotRoute direct = route;
        route = null;
        Vector3 start = direct.Points[0], end = direct.Points[^1];
        Vector3 flat = new(end.X - start.X, end.Y - start.Y, 0);
        if (flat.Length() < 1f || player.Map is not { } map) return false; // no way to judge: not evidence of a ring

        Vector3 along = Vector3.Normalize(flat);
        Vector3 side = new(-along.Y, along.X, 0);
        // Other ways after the direct one was refused: held to the think's search budget (PlayerbotThinkProbe.Fallback).
        using PlayerbotThinkProbe.FallbackScope fallback = PlayerbotThinkProbe.Fallback();
        foreach (float extra in HazardDetourYards)
            foreach (float sign in (float[])[1f, -1f])
            {
                // Round the hazard by its side: a corner before it and a corner past it, each its radius plus the margin off.
                float off = hazard.Radius + extra;
                Vector3 before = hazard.At - (along * off) + (side * sign * off);
                Vector3 past = hazard.At + (along * off) + (side * sign * off);
                if (!PlanDirect(player, before, before, options, partial: false, out PlayerbotRoute? first, out _) || first is null) continue;
                if (Leg(map, first.Points[^1], past, options) is not { } middle
                    || Leg(map, middle.Points[^1], end, options) is not { } last) continue;
                List<Vector3> points = [.. first.Points, .. middle.Points.Skip(1), .. last.Points.Skip(1)];
                float length = 0;
                for (int i = 1; i < points.Count; i++) length += Vector3.Distance(points[i - 1], points[i]);
                if (length > options.MaxRouteYards || points.Count > options.MaxPathPoints * 3) continue;
                // Each leg keeps its own proof: the first may be the mesh's while a later one was stepped over the terrain, and the
                // motion must not skip the floor and line-of-sight checks on that one (PlayerbotMotion.Validate).
                bool[] legs = [.. Enumerable.Repeat(first.Navigated, first.Points.Count - 1),
                    .. Enumerable.Repeat(middle.Navigated, middle.Points.Count - 1), .. Enumerable.Repeat(last.Navigated, last.Points.Count - 1)];
                var detour = new PlayerbotRoute(points, length, legs);
                if (risk.Blocking(player, detour.Points, completedReturn: completedQuestReturn) is not null) continue;
                detour.CompletedQuestReturn = completedQuestReturn;
                route = detour;
                return true;
            }

        // A goal's walk refused: evidence for the ring escape (PlayerbotRisk.Refused), which judges whether the bot is boxed in.
        if (!trial) risk.Refused(player, hazard);
        return false;
    }

    /// <summary>A further leg from <paramref name="from"/> (the navigation mesh's, else a stepped terrain line), or null.</summary>
    private static PlayerbotRoute? Leg(Map map, Vector3 from, Vector3 to, PlayerbotOptions options)
    {
        PathResult path = PlayerbotThinkProbe.Find(map.Collision, from, to, new PathOptions { MaxPoints = Math.Max(2, options.MaxPathPoints),
            Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, MaxSearchNodes = PathOptions.DefaultMaxSearchNodes, LoadTiles = true });
        if ((path.Type & PathType.NotUsingPath) != 0)
            return TryTerrainRoute(from, to, options, (x, y, z) => map.Collision.GetHeight(x, y, z),
                (a, b) => map.Collision.IsInLineOfSight(a.X, a.Y, a.Z + 2, b.X, b.Y, b.Z + 2), out PlayerbotRoute? terrain) ? terrain : null;
        return IsUsablePath(path, options.MaxPathPoints, options.MaxRouteYards) && (path.Type & PathType.Incomplete) == 0
            ? new PlayerbotRoute(path.Points, path.Length, navigated: true) : null;
    }

    /// <summary>The route without the hazard check (<see cref="Plan"/>).</summary>
    private static bool PlanDirect(Player player, Vector3 destination, Vector3 goal, PlayerbotOptions options, bool partial,
        out PlayerbotRoute? route, out bool straightRefused, int? searchNodes = null)
    {
        straightRefused = false;
        route = null;
        if (player.Map is not { } map || !Finite(destination)
            || !Finite(new Vector3(player.X, player.Y, player.Z)))
            return false;

        // AllowedMaps limits open-world travel; the dungeon or raid instance the bot is in is always walkable (PlayerbotMapPolicy).
        if (!PlayerbotMapPolicy.MayMoveOn(player, options))
            return false;

        // A destination given up after a loop is refused for a while, so the goal picks another one.
        if (PlayerbotMotion.IsBlacklisted(player, destination))
            return false;

        // A moving bot plans from where it is now, not from its last heartbeat.
        Vector3 start = PlayerbotMotion.CurrentPosition(player);
        // The search budget is vmangos' own (navMeshQuery->init(navMesh, 2048), MoveMap.cpp:350) for a near destination, more for a far
        // one (SearchNodes). The old 512 polygons ran out on the real Dun Morogh mesh well inside the route bound: 200 yards from
        // Coldridge Valley to the Rockjaw Raiders (quest 179) answered no path with 512 and an 18-corner path with 2048.
        PathResult path = Query(map, start, destination, options, partial, steep: false, out bool capped, searchNodes);
        // The no-mmap answer carries no walkability proof. Validate each short terrain step
        // before using that route; absent heights, steep terrain and known model obstructions refuse it.
        if ((path.Type & PathType.NotUsingPath) != 0)
        {
            straightRefused = !TryTerrainRoute(start, destination, options,
                (x, y, z) => map.Collision.GetHeight(x, y, z),
                (a, b) => map.Collision.IsInLineOfSight(a.X, a.Y, a.Z + 2, b.X, b.Y, b.Z + 2), out route);
            return !straightRefused;
        }

        if (Usable(player, path, start, destination, goal, partial, options, out PlayerbotRoute? usable))
        {
            // The corners are the navigation mesh's, which already proves each leg walkable; they are not re-tested against the
            // collision data. A line of sight at their own height hits the floor model itself inside any building (the Deathknell
            // crypt, the Goldshire inn), and the floor probe finds nothing at some spots the mesh covers (on the crypt's stairs, at
            // 1645.4, 1665.9, 132.6, where Graveweaver stopped): either refused every route from or through such a place, and a bot
            // that started or stood there never moved again. The motion snaps to a floor where it finds one (PlayerbotMotion).
            route = usable;
            return true;
        }

        // The way up indoors is a fallback too, held to the think's search budget (PlayerbotThinkProbe.Fallback).
        using (PlayerbotThinkProbe.Fallback())
        {
            if (TryClimbIndoors(map, start, destination, options, partial ? path : null, out route)) return true;
        }

        // No path at all from walkable ground: the destination is the one off the mesh, not the bot. A partial path is a patch of
        // walkable ground among steep slopes, or a fragment of mesh, only when it ends close by and the search ran out of corridor,
        // not out of nodes; any other refusal stays one.
        // A search that hit its node cap (PathSearchCounters) is no sign of a patch or a fragment: those are a few polygons, searched
        // through long before any cap, while a capped search towards a far goal ends on the visited polygon nearest it, which may lie
        // right beside the bot when the way starts off in another direction. Taken for a fragment, it ran the steep escape and the
        // starts beside the bot, up to 34 more searches of up to 32 768 nodes: on the 200-bot terrain benchmark (2026-10-11) the bots
        // in Coldridge Valley planning to a quest destination in Stormwind (1 800 yards) thought for up to 557 ms, 41 searches each.
        if ((path.Type & PathType.NoPath) != 0
                ? OnWalkableMesh(map, start)
                : !partial || (path.Type & PathType.Incomplete) == 0 || Vector2.Distance(Flat(path.End), Flat(start)) > SteepPatchYards || capped)
            return false;
        // The ways off a patch, a fragment or steep ground are fallbacks, held to the think's search budget (PlayerbotThinkProbe.Fallback).
        using PlayerbotThinkProbe.FallbackScope fallback = PlayerbotThinkProbe.Fallback();
        if (TryLeaveSteepGround(player, map, start, destination, goal, options, partial, out route)) return true;
        // A fragment answers a partial corridor that ends where it began; a no-path answer (the bot off the mesh) is not one.
        if ((path.Type & PathType.NoPath) != 0) return false;

        // The polygon under the bot is a fragment the mesh does not join to its surroundings (the nearest polygon is chosen as
        // Detour does, and a sliver on a rock or a root can win over the ground beside it): every query from it ends where it
        // began. Start a step away instead, on ground the bot can step to (live stress test 2026-10-08: bots in Shadowglen,
        // Dolanaar and north of Razor Hill stood on such spots for hours, every way out refused).
        route = FromBeside(player, map, start, destination, options, partial, goal);
        return route is not null;
    }

    /// <summary>
    /// A partial route ending this close to the bot (flat yards) may mean it stands on a patch of walkable ground among steep slopes
    /// (<see cref="TryLeaveSteepGround"/>) or on a fragment of mesh (<see cref="FromBeside"/>: the ones bots stood on in the live
    /// stress test reached 3 to 11 yards); one ending farther away is an ordinary refusal and costs no further query.
    /// </summary>
    internal const float SteepPatchYards = 15f;

    /// <summary>
    /// One query of the navigation mesh, with steep slopes excluded as for every bot route, or allowed (<paramref name="steep"/>);
    /// navigation tiles are read as needed (<see cref="PathOptions.LoadTiles"/>).
    /// </summary>
    private static PathResult Query(Map map, Vector3 from, Vector3 to, PlayerbotOptions options, bool partial, bool steep,
        int? searchNodes = null)
        => Query(map, from, to, options, partial, steep, out _, searchNodes);

    /// <summary><see cref="Query"/>, and whether its search hit the node cap (<see cref="PathSearchCounters.CappedSearches"/>).</summary>
    private static PathResult Query(Map map, Vector3 from, Vector3 to, PlayerbotOptions options, bool partial, bool steep, out bool capped,
        int? searchNodes = null)
    {
        long before = PathSearchCounters.CappedSearches;
        PathResult path = QueryOnce(map, from, to, options, partial, steep, searchNodes);
        capped = PathSearchCounters.CappedSearches != before;
        return path;
    }

    private static PathResult QueryOnce(Map map, Vector3 from, Vector3 to, PlayerbotOptions options, bool partial, bool steep, int? searchNodes)
        => PlayerbotThinkProbe.Find(map.Collision, from, to, new PathOptions { MaxPoints = Math.Max(2, options.MaxPathPoints), Mover = PathMover.Player,
            ExcludeFlags = steep ? NavTerrain.Empty : NavTerrain.SteepSlopes, AllowPartial = partial,
            MaxSearchNodes = searchNodes ?? SearchNodes(from, to), LoadTiles = true });

    /// <summary>Whether a mesh answer from <paramref name="from"/> is a route the bot takes (<see cref="Bounded"/>), and the route.</summary>
    private static bool Usable(Player player, PathResult path, Vector3 from, Vector3 destination, Vector3 goal, bool partial,
        PlayerbotOptions options, out PlayerbotRoute? route)
    {
        route = null;
        if (!IsUsablePath(path, options.MaxPathPoints, float.PositiveInfinity))
            return false;
        // A partial answer (the corridor did not reach the destination) must at least close on the goal, and must not end at a
        // place given up after a loop. An end projected from above or below the destination stays at its spot and is exact.
        if (partial && (path.Type & PathType.Incomplete) != 0 && !AtDestination(path.End, destination)
            && (Vector2.Distance(Flat(path.End), Flat(goal)) > Vector2.Distance(Flat(from), Flat(goal)) - PartialProgressYards
                || PlayerbotMotion.IsBlacklisted(player, path.End)))
            return false;

        route = Bounded(path.Points, options, complete: Reaches(path) || EndsAt(path, destination));
        return route is not null;
    }

    /// <summary>
    /// A partial answer whose end is the destination's own spot, projected onto the mesh from above or below (within a yard, flat): the
    /// way reaches the destination as surely as a complete one, and a long one is cut at the route bound like one (<see cref="Bounded"/>).
    /// An area trigger's centre floats above its floor: the Stormwind tram entrance (2173) is 4.8 yards above it, so the mesh answered
    /// its 2,314-yard way from Sentinel Hill as incomplete, the bound refused it whole, and 67 Westfall bots stood still for a minute
    /// and gave their tram trip up (live-w22-r1, 2026-10-11 08:05).
    /// <para>
    /// Only within <see cref="ProjectedEndYards"/> vertically: <see cref="PathType.Incomplete"/> is also the answer of a search that stopped
    /// short, ending on the visited polygon nearest the goal, which may lie straight under a destination on a level the bot cannot reach
    /// (a tower top, a cliff top, a bridge, an upper floor; the end is searched up to 200 yards vertically). Such an end is no arrival.
    /// Storeys closer than that (some buildings' floors) can still pass; the stall watch gives those goals up.
    /// </para>
    /// </summary>
    internal static bool EndsAt(PathResult path, Vector3 destination)
        => path.HasPath && (path.Type & PathType.Incomplete) != 0 && (path.Type & (PathType.NotUsingPath | PathType.NoPath)) == 0
            && AtDestination(path.End, destination);

    /// <summary>
    /// How far above or below the destination a mesh answer's end may lie and still be the destination's spot: a complete path already
    /// accepts an end found up to 5 yards off vertically (NavMeshPathfinder NearExtents Y = 5), and the Stormwind tram entrance's centre
    /// floats 4.75 yards above its floor.
    /// </summary>
    internal const float ProjectedEndYards = 6f;

    /// <summary>Whether <paramref name="end"/> is <paramref name="destination"/>'s spot: within a yard flat and <see cref="ProjectedEndYards"/> vertically.</summary>
    private static bool AtDestination(Vector3 end, Vector3 destination)
        => Vector2.Distance(Flat(end), Flat(destination)) <= 1f && MathF.Abs(end.Z - destination.Z) <= ProjectedEndYards;

    /// <summary>
    /// The route along <paramref name="points"/>, or null when a point is not finite or it is longer than
    /// <see cref="PlayerbotOptions.MaxRouteYards"/>. A way that reaches its destination is cut there instead: its first part is
    /// progress along the real way, and the bot plans the rest from where it stops.
    /// </summary>
    private static PlayerbotRoute? Bounded(IReadOnlyList<Vector3> points, PlayerbotOptions options, bool complete)
    {
        var kept = new List<Vector3>(points.Count);
        float distance = 0;
        for (int index = 0; index < points.Count; index++)
        {
            Vector3 point = points[index];
            if (!Finite(point)) return null;
            if (index > 0)
            {
                float leg = Vector3.Distance(points[index - 1], point);
                if (!float.IsFinite(leg)) return null;
                if (distance + leg > options.MaxRouteYards)
                {
                    if (!complete || kept.Count < 2) return null;
                    break;
                }

                distance += leg;
            }

            kept.Add(point);
        }

        return kept.Count < 2 ? null : new PlayerbotRoute(kept.ToArray(), distance, navigated: true);
    }

    /// <summary>Whether a mesh answer reaches its destination (a whole way, or the first part of one cut at the point bound).</summary>
    private static bool Reaches(PathResult path)
        => path.HasPath && (path.Type & (PathType.Incomplete | PathType.NotUsingPath | PathType.NoPath)) == 0;

    /// <summary>How many directions <see cref="IsOnMeshIsland"/> asks the mesh round the bot about.</summary>
    internal const int IslandDirections = 12;

    /// <summary>How far away the points lie that <see cref="IsOnMeshIsland"/> asks the mesh to approach.</summary>
    internal const float IslandProbeYards = 120f;

    /// <summary>
    /// An island's ground ends this close to the bot (flat yards) in every direction <see cref="IsOnMeshIsland"/> asks about; ground that
    /// leads farther in any of them is no island.
    /// </summary>
    internal const float IslandYards = 55f;

    /// <summary>
    /// The point budget of each of <see cref="IsOnMeshIsland"/>'s queries, fixed (not the operator's MaxPathPoints): 120 yards of winding
    /// ground fit in it, and a path cut at it (<see cref="PathType.Short"/>) is never taken for an island's edge.
    /// </summary>
    internal const int IslandPathPoints = 256;

    /// <summary>The search budget of each of <see cref="IsOnMeshIsland"/>'s queries: an island's few polygons run out long before it.</summary>
    internal const int IslandSearchNodes = 8192;

    /// <summary>
    /// Whether the bot stands on a navigation-mesh island: walkable polygons the mesh joins to nothing round them, so that no route from
    /// there leads anywhere else. Partial queries from the bot towards <see cref="IslandDirections"/> points
    /// <see cref="IslandProbeYards"/> away all end within <see cref="IslandYards"/> of it; the first that ends farther (the mesh goes
    /// on, or the path was cut at its point bound), and any answer without the mesh (no navigation data, or no polygon under the bot: other
    /// recoveries' cases), says no. So an island beside water or a void, where a probe point has no polygon near it (a NoPath answer), is
    /// never detected: its bots still stall, and are given up goal by goal as before. At most
    /// <see cref="IslandDirections"/> queries, all through <see cref="PlayerbotThinkProbe.Find"/> as fallbacks (counted, and held to the
    /// think's node budget: a query the budget cut short or refused proves nothing and answers no). The brain asks once a stall gives
    /// a goal up and once when exploring stays confined (PlayerbotBrain.EscapeIsland, EscapeConfinedExplore), and does nothing else
    /// in that think.
    /// <para>
    /// Live 2026-10-11 (bot QA #180, live-w23-r1, main 85023136): the ground inside the round wall of the Molsen Farm in Westfall
    /// (x -10305..-10274, y 1355..1385, the wall 1.7 to 3 yards high) is such an island, closed by the collision data too (no floor
    /// within a step and in sight leads over the wall). Wfbotac and Wfbotaj, once inside, stalled there whatever goal they took
    /// (Wiley the Black for quest 65, Bingles Blastenheimer for quest 2039, the Harvest Watchers beside the farm), restart after restart:
    /// every route ended at the island's edge nearest the goal, and the next one was refused.
    /// The lower Stormwind ledge of #207 extends almost 50 yards from the bot, but none of its 36 routes toward points 60, 120 and
    /// 200 yards away leaves it. Probing 120 yards and requiring every end within 55 yards includes this ledge while retaining
    /// a substantial gap between the probe and the edge; ordinary connected ground fails the first long-reaching probe.
    /// </para>
    /// </summary>
    internal static bool IsOnMeshIsland(Player player, PlayerbotOptions options)
    {
        if (player.Map is not { } map) return false;
        Vector3 start = PlayerbotMotion.CurrentPosition(player);
        if (!Finite(start)) return false;
        using PlayerbotThinkProbe.FallbackScope fallback = PlayerbotThinkProbe.Fallback();
        for (int direction = 0; direction < IslandDirections; direction++)
        {
            float angle = direction * 2f * MathF.PI / IslandDirections;
            Vector3 probe = start + (new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * IslandProbeYards);
            int cutBefore = PlayerbotThinkProbe.Active?.Clamped ?? 0;
            PathResult path = PlayerbotThinkProbe.Find(map.Collision, start, probe, new PathOptions { MaxPoints = IslandPathPoints,
                Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, AllowPartial = true, MaxSearchNodes = IslandSearchNodes,
                LoadTiles = true });
            // A search the think's node budget cut short ends early on ground that may go on: no proof of an island.
            if ((PlayerbotThinkProbe.Active?.Clamped ?? 0) != cutBefore) return false;
            // A path cut at the point bound (Short) ends early on ground that goes on: winding connected ground, never an island.
            if (!path.HasPath || (path.Type & (PathType.NotUsingPath | PathType.NoPath | PathType.Short)) != 0 || !Finite(path.End)
                || Vector2.Distance(Flat(path.End), Flat(start)) > IslandYards)
                return false;
        }

        return true;
    }

    /// <summary>How far from the bot the starts beside it lie (<see cref="FromBeside"/>), eight directions each.</summary>
    private static readonly float[] BesideYards = [2.5f, 5f];

    /// <summary>
    /// A route that steps from <paramref name="start"/> to ground beside it (a stepped terrain leg: floor within a step, in sight)
    /// and follows the mesh from there to <paramref name="destination"/>, or null. At most sixteen queries.
    /// </summary>
    private static PlayerbotRoute? FromBeside(Player player, Map map, Vector3 start, Vector3 destination, PlayerbotOptions options, bool partial, Vector3 goal)
    {
        foreach (float yards in BesideYards)
        {
            for (int direction = 0; direction < 8; direction++)
            {
                float angle = MathF.Atan2(destination.Y - start.Y, destination.X - start.X) + (direction * MathF.PI / 4f);
                float x = start.X + (MathF.Cos(angle) * yards), y = start.Y + (MathF.Sin(angle) * yards);
                float floor = map.Collision.GetHeight(x, y, start.Z + 2f);
                if (InvalidHeight(floor) || MathF.Abs(floor - start.Z) > 2.5f) continue;
                Vector3 beside = new(x, y, floor + 0.05f);
                if (!map.Collision.IsInLineOfSight(start.X, start.Y, start.Z + 2, beside.X, beside.Y, beside.Z + 2)) continue;
                PathResult path = Query(map, beside, destination, options, partial, steep: false, out bool capped);
                // The same arrival rule as Usable: an end at the destination's own spot (EndsAt) reaches it.
                bool reaches = Reaches(path) || EndsAt(path, destination);
                if (!path.HasPath || (path.Type & (PathType.NotUsingPath | PathType.NoPath)) != 0
                    || (!reaches && (Vector3.Distance(path.End, beside) <= SteepPatchYards
                        || Vector2.Distance(Flat(path.End), Flat(goal)) > Vector2.Distance(Flat(start), Flat(goal)) - PartialProgressYards)))
                {
                    // Refused after running out of nodes short of the destination: the other starts lie a few yards away and search the
                    // same ground, so they end the same way (each up to 32 768 nodes). A search that reached it despite the cap is no such
                    // sign (refused for its length, say), and the next start is tried.
                    if (capped && (path.Type & (PathType.Incomplete | PathType.NoPath)) != 0) return null;
                    continue;
                }

                if (Bounded([start, .. path.Points], options, reaches) is not { } joined || joined.Points.Count > options.MaxPathPoints + 1) continue;
                bool[] legs = [false, .. Enumerable.Repeat(true, joined.Points.Count - 2)];
                return new PlayerbotRoute(joined.Points, joined.Distance, legs);
            }
        }

        return null;
    }

    private static Vector2 Flat(Vector3 value) => new(value.X, value.Y);

    /// <summary>How far above where the bot stands a way off steep ground may lead (<see cref="TryLeaveSteepGround"/>).</summary>
    internal const float SteepEscapeClimbYards = 1.5f;

    /// <summary>The search budget of <see cref="RequiresSteepAscent"/>'s two queries (a diagnosis, not a route: the climb shows within the first corners).</summary>
    internal const int DiagnosisSearchNodes = 8192;

    /// <summary>A leg of a path climbs steep ground when it rises at least this much per flat yard (45 degrees).</summary>
    internal const float SteepLegSlope = 0.9f;

    /// <summary>The height gained on steep legs within the first corners of the steep-allowed path that makes a way unwalkable.</summary>
    internal const float SteepAscentYards = 15f;

    /// <summary>
    /// Whether the ordinary mesh cannot leave the bot's spot toward <paramref name="goal"/> because the way there climbs steep ground:
    /// the ordinary query ends at the bot's feet, and the path with steep polygons allowed first gains at least
    /// <see cref="SteepAscentYards"/> of height along legs steeper than <see cref="SteepLegSlope"/> (the slope of each leg, not the
    /// height of the highest corner: an ordinary climb to an uphill turn-in gains more than <see cref="SteepEscapeClimbYards"/> too).
    /// A local topology diagnosis, not proof the goal is unreachable from elsewhere. Two queries of
    /// <see cref="DiagnosisSearchNodes"/> nodes at most; the caller runs it once per quest and spot (<see cref="PlayerbotWorldDestinations"/>).
    /// </summary>
    internal static bool RequiresSteepAscent(Player player, Vector3 goal, PlayerbotOptions options, out float ascent)
    {
        ascent = 0;
        if (player.Map is not { } map || !Finite(goal)) return false;
        Vector3 start = PlayerbotMotion.CurrentPosition(player);
        PathResult ordinary = Query(map, start, goal, options, partial: true, steep: false, DiagnosisSearchNodes);
        if ((ordinary.Type & PathType.NotUsingPath) != 0
            || (ordinary.Type & PathType.NoPath) == 0 &&
                ((ordinary.Type & PathType.Incomplete) == 0 || Vector2.Distance(Flat(ordinary.End), Flat(start)) > SteepPatchYards))
            return false;

        PathResult steep = Query(map, start, goal, options, partial: true, steep: true, DiagnosisSearchNodes);
        if (!steep.HasPath || (steep.Type & (PathType.NotUsingPath | PathType.NoPath | PathType.DestForced)) != 0) return false;
        float climb = 0;
        for (int index = 1; index < steep.Points.Count && index <= SteepEscapeCorners; index++)
        {
            Vector3 from = steep.Points[index - 1], to = steep.Points[index];
            if (!Finite(from) || !Finite(to)) return false;
            float rise = to.Z - from.Z;
            if (rise > 0 && rise >= Vector2.Distance(Flat(from), Flat(to)) * SteepLegSlope) climb += rise;
        }

        if (climb < SteepAscentYards) return false;
        ascent = climb;
        return true;
    }

    /// <summary>A connected ordinary route still crosses a hazard the completed-return rule keeps in force.</summary>
    internal static bool HasBlockingReturnHazard(Player player, Vector3 goal, PlayerbotOptions options,
        out PlayerbotHazard hazard)
    {
        hazard = default;
        if (!Guards.TryGetValue(player, out PlayerbotRisk? risk) || risk.Hazards.Count == 0
            || !PlanDirect(player, goal, goal, options, partial: false, out PlayerbotRoute? route, out _,
                DiagnosisSearchNodes) || route is null
            || risk.Blocking(player, route.Points, completedReturn: true) is not { } blocked)
            return false;
        hazard = blocked;
        return true;
    }

    /// <summary>The corners of a way off steep ground tried as the place where the ordinary routes begin again.</summary>
    private const int SteepEscapeCorners = 16;

    /// <summary>
    /// Every bot route excludes the navigation mesh's steep polygons (<see cref="NavTerrain.SteepSlopes"/>). A bot standing on steep
    /// ground, or on a patch of walkable polygons among them, then gets no route anywhere: the mesh finds no walkable polygon within
    /// three yards of it (no path), or only the patch's own edge (a partial path that never closes). The way off is the mesh's own
    /// path with the steep polygons allowed, cut at its first corner on walkable ground from which the ordinary query is a route,
    /// and only while no corner leads more than <see cref="SteepEscapeClimbYards"/> above the bot: a player slides down a slope it
    /// cannot walk up. A destination off the walkable mesh from walkable ground gets no route, as before.
    /// <para>
    /// Live 2026-10-09: Ironwander (a warrior, level 7) stood on the mountainside north of Coldridge Valley at -5605.6, 155.8, 453.3,
    /// seven yards above the slope's floor, for over two hours. Every route from there answered no path, so every goal failed before
    /// it began: the Ice Claw Bear it last chose (entry 1196) was marked unreachable, the exploration refused, and the stall watch's
    /// give-up had nothing to give up. With the steep polygons allowed the mesh leads down to the bear's meadow in 129 yards, past
    /// a walkable patch at -5573, 185.6, 441.4 whose own edge ends every ordinary route.
    /// </para>
    /// </summary>
    private static bool TryLeaveSteepGround(Player player, Map map, Vector3 start, Vector3 destination, Vector3 goal,
        PlayerbotOptions options, bool partial, out PlayerbotRoute? route)
    {
        route = null;
        PathResult steep = Query(map, start, destination, options, partial: true, steep: true);
        if (!steep.HasPath || (steep.Type & (PathType.NotUsingPath | PathType.DestForced | PathType.FlyPath)) != 0) return false;

        float distance = 0;
        for (int index = 1; index < steep.Points.Count && index <= SteepEscapeCorners; index++)
        {
            Vector3 point = steep.Points[index];
            if (!Finite(point) || point.Z > start.Z + SteepEscapeClimbYards) return false;
            distance += Vector3.Distance(steep.Points[index - 1], point);
            if (!float.IsFinite(distance) || distance > options.MaxRouteYards) return false;
            if (!OnWalkableMesh(map, point)) continue;
            if (Vector3.Distance(point, destination) > 1f)
            {
                PathResult onward = Query(map, point, destination, options, partial, steep: false, out bool capped);
                if (!Usable(player, onward, point, destination, goal, partial, options, out _))
                {
                    // Ran out of nodes short of the destination: the next corner, a few yards on, searches the same ground (see FromBeside).
                    if (capped && (onward.Type & (PathType.Incomplete | PathType.NoPath)) != 0) return false;
                    continue;
                }
            }
            route = new PlayerbotRoute(steep.Points.Take(index + 1).ToArray(), distance, navigated: true);
            return true;
        }

        return false;
    }

    /// <summary>How far (flat) from an indoor destination the walkable mesh must end for <see cref="TryClimbIndoors"/>.</summary>
    internal const float ClimbReachYards = 20f;

    /// <summary>How much longer than the walkable mesh's partial way the way up <see cref="TryClimbIndoors"/> takes may be.</summary>
    internal const float ClimbExtraYards = 60f;

    /// <summary>
    /// The way to a destination inside a building over its stairs, where the mesh flags the stairs as steep slopes. The generator marks
    /// polygons steeper than a player walks up (<see cref="NavTerrain.SteepSlopes"/>) whatever they lie on, and a flight of stairs in a
    /// model is such a ramp, while a player climbs it step by step. Taken only when the destination is indoors (inside a model's group,
    /// <see cref="MapCollision.IsOutdoors"/>), the walkable mesh ends within <see cref="ClimbReachYards"/> of it (flat, the stairs
    /// above or below), and the way with steep polygons reaches it at most <see cref="ClimbExtraYards"/> longer: a slope of the terrain
    /// outdoors stays refused (<see cref="TryLeaveSteepGround"/>). One or two queries more, only for an indoor destination the walkable
    /// mesh does not reach.
    /// <para>
    /// Neeka Bloodscar, the ender of quest 1418, stands at the top of the Kargath tower (-6672.3, -2240.5, 278.6); the walkable mesh
    /// leads up its lower stairs and ends 13 yards below her, where the upper flight is flagged steep. Every bot that reached Kargath
    /// stood there (issue #194).
    /// </para>
    /// <para>
    /// Not in a dungeon, raid or battleground (<see cref="MapTemplate.IsDungeon"/>, <see cref="MapTemplate.IsBattleground"/>): there
    /// nearly every place is indoors, so the indoor test would not tell stairs from slopes, and no stall there is known to need it.
    /// </para>
    /// </summary>
    private static bool TryClimbIndoors(Map map, Vector3 start, Vector3 destination, PlayerbotOptions options, PathResult? walk,
        out PlayerbotRoute? route)
    {
        route = null;
        if (map.Template is { IsDungeon: true } or { IsBattleground: true }) return false;
        if (map.Collision.IsOutdoors(destination.X, destination.Y, destination.Z + 1f)) return false;
        PathResult reach = walk ?? Query(map, start, destination, options, partial: true, steep: false);
        if (!reach.HasPath || (reach.Type & (PathType.NotUsingPath | PathType.NoPath)) != 0 || Reaches(reach) || EndsAt(reach, destination)
            || Vector2.Distance(Flat(reach.End), Flat(destination)) > ClimbReachYards)
            return false;
        PathResult climb = Query(map, start, destination, options, partial: false, steep: true);
        if (!IsUsablePath(climb, options.MaxPathPoints, reach.Length + ClimbExtraYards) || !(Reaches(climb) || EndsAt(climb, destination)))
            return false;
        route = Bounded(climb.Points, options, complete: true);
        return route is not null;
    }

    /// <summary>
    /// Whether the mesh finds walkable (not steep) ground at <paramref name="point"/>: the path from it to itself is not refused (Detour
    /// locates no polygon there otherwise). One more query on a refused plan; a plan that succeeds never makes it.
    /// </summary>
    private static bool OnWalkableMesh(Map map, Vector3 point)
        => (PlayerbotThinkProbe.Find(map.Collision, point, point, new PathOptions { MaxPoints = 4, Mover = PathMover.Player,
            ExcludeFlags = NavTerrain.SteepSlopes, AllowPartial = true, MaxSearchNodes = 64 }).Type & PathType.NoPath) == 0;

    /// <summary>
    /// The search budget of one query: vmangos' 2048 nodes (<see cref="PathOptions.DefaultMaxSearchNodes"/>) within
    /// <see cref="NearYards"/>, else 48 nodes a yard of straight distance, at most <see cref="MaxSearchNodes"/>. The way down from the
    /// Red Cloud Mesa to Bloodhoof Village (405 yards apart, a 1,589-yard road) needs more than 8,192 nodes, the way from Stormwind to
    /// Northshire Abbey (630 yards apart) more than 8,192 and at most 32,768.
    /// </summary>
    internal static int SearchNodes(Vector3 start, Vector3 destination)
    {
        float distance = Vector3.Distance(start, destination);
        return distance <= NearYards || !float.IsFinite(distance) ? PathOptions.DefaultMaxSearchNodes
            : (int)Math.Clamp(distance * 48f, PathOptions.DefaultMaxSearchNodes, MaxSearchNodes);
    }

    /// <summary>Within this straight distance a query keeps vmangos' search budget (<see cref="SearchNodes"/>).</summary>
    internal const float NearYards = 100f;

    /// <summary>The largest search budget of one bot route query (<see cref="SearchNodes"/>).</summary>
    internal const int MaxSearchNodes = 32_768;

    /// <summary>
    /// Follow <paramref name="route"/> this think (<see cref="PlayerbotMotion.Follow"/>): start, switch or keep moving.
    /// The motion itself advances every world tick, so the distance does not depend on <paramref name="elapsedMs"/>;
    /// <paramref name="serverTimeMs"/> is the current server time. False when the route is finished or cannot be
    /// followed (the caller drops it).
    /// </summary>
    internal static bool TryAdvance(WorldSession session, PlayerbotRoute route, PlayerbotOptions options,
        uint elapsedMs, uint serverTimeMs)
    {
        if (session.Player is not { } player)
            return false;
        if (player.Map is not { } map || options.MoveSpeed <= 0 || elapsedMs == 0)
            return false;
        if (route.Complete)
        {
            PlayerbotMovementControl.Stop(session, player);
            return false;
        }
        if (player.StandState != StandState.Stand)
            return session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(0u));
        if (!PlayerbotMapPolicy.MayMoveOn(player, options)) return false;
        // A hazard that turned up since the route was planned (a creature that kills outright came into sight): give the route up
        // here, before walking into it; the goal plans again, round it or elsewhere.
        if (Guards.TryGetValue(player, out PlayerbotRisk? risk))
        {
            List<Vector3> rest = [PlayerbotMotion.CurrentPosition(player), .. route.Points.Skip(route.NextPoint)];
            if (risk.Blocking(player, rest, strong: !risk.IsEscape(route),
                completedReturn: route.CompletedQuestReturn) is not null)
            {
                PlayerbotMovementControl.Stop(session, player);
                return false;
            }
        }

        return PlayerbotMotion.Follow(session, player, route, options, serverTimeMs);
    }

    /// <summary>Propose a step without changing the route; the caller commits after normal admission.</summary>
    internal static bool TryStep(PlayerbotRoute route, Vector3 start, float budget,
        Func<Vector3, Vector3?> validate, out Vector3 next, out int nextPoint)
    {
        next = start;
        nextPoint = route.NextPoint;
        if (!Finite(start) || !float.IsFinite(budget) || budget <= 0 || route.Complete
            || nextPoint < 1) return false;
        float remainingBudget = budget;
        int scanned = 0;
        while (nextPoint < route.Points.Count && remainingBudget > 0.001f && ++scanned <= 64)
        {
            Vector3 destination = route.Points[nextPoint];
            float remaining = Vector3.Distance(next, destination);
            if (!Finite(destination) || !float.IsFinite(remaining)) break;
            if (remaining <= 0.05f) { nextPoint++; continue; }
            bool reachesPoint = remaining <= remainingBudget;
            Vector3 candidate = reachesPoint ? destination
                : next + Vector3.Normalize(destination - next) * remainingBudget;
            Vector3? checkedPoint = validate(candidate);
            if (checkedPoint is not { } accepted || !Finite(accepted)) break;
            float spent = Vector3.Distance(next, accepted);
            if (!float.IsFinite(spent) || spent > remainingBudget + 0.05f) break;
            next = accepted;
            remainingBudget = MathF.Max(0, remainingBudget - spent);
            if (reachesPoint && Vector3.Distance(accepted, destination) <= 0.1f) nextPoint++;
            else break;
        }
        return Vector3.Distance(start, next) > 0.05f;
    }

    internal static bool TryStepOnMap(PlayerbotRoute route, Vector3 start, float budget, Map map,
        out Vector3 next, out int nextPoint)
        => TryStep(route, start, budget, candidate =>
        {
            float floor = map.Collision.GetHeight(candidate.X, candidate.Y, candidate.Z);
            if (!Finite(candidate) || InvalidHeight(floor) || MathF.Abs(floor - candidate.Z) > 2f
                || !map.Collision.IsInLineOfSight(start.X, start.Y, start.Z + 2,
                    candidate.X, candidate.Y, floor + 2)) return null;
            return new Vector3(candidate.X, candidate.Y, floor + 0.05f);
        }, out next, out nextPoint);

    internal static bool TryTerrainRoute(Vector3 start, Vector3 destination, PlayerbotOptions options,
        Func<float, float, float, float> getHeight, Func<Vector3, Vector3, bool> clear,
        out PlayerbotRoute? route)
    {
        route = null;
        if (!Finite(start) || !Finite(destination)) return false;
        float horizontal = Vector2.Distance(new Vector2(start.X, start.Y), new Vector2(destination.X, destination.Y));
        if (!float.IsFinite(horizontal) || horizontal < 0.05f || horizontal > options.MaxRouteYards) return false;
        int steps = (int)MathF.Ceiling(horizontal); // at most one yard between floor probes
        if (steps + 1 > options.MaxPathPoints) return false;
        var points = new List<Vector3>(steps + 1) { start };
        float length = 0;
        for (int i = 1; i <= steps; i++)
        {
            Vector3 previous = points[^1];
            Vector3 next = Vector3.Lerp(start, destination, (float)i / steps);
            float floor = getHeight(next.X, next.Y, previous.Z);
            if (InvalidHeight(floor) || MathF.Abs(floor - previous.Z) > 1f) return false;
            next.Z = floor + 0.05f;
            if (!clear(previous, next)) return false;
            length += Vector3.Distance(previous, next);
            if (!float.IsFinite(length) || length > options.MaxRouteYards) return false;
            points.Add(next);
        }
        route = new PlayerbotRoute(points, length);
        return true;
    }

    internal static byte[] GuidPayload(ulong guid)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid);
        return writer.ToArray();
    }

    internal static byte[] UseItemPayload(byte bag, byte slot)
    {
        var writer = new PacketWriter(16);
        writer.WriteByte(bag);
        writer.WriteByte(slot);
        writer.WriteByte(0);
        ArcaneCore.Game.Spells.SpellCastTargets.ForSelf().Write(writer);
        return writer.ToArray();
    }

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool InvalidHeight(float height)
        => !float.IsFinite(height) || height == TerrainTile.InvalidHeight
            || height == TerrainTile.InvalidHeightValue;
}

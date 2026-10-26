using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The walkable test the planner asks (<see cref="TravelFacts.Walkable"/>): whether two places of one map lie on one connected piece of the
/// navigation mesh (<see cref="NavRegionGraph"/> components). A place the mesh cannot place (its tile is not read yet, the graph is still
/// building) is undecided and taken as joined. Ship ports are placed by a scan of the floor around them (<see cref="PortComponents"/>) that
/// never reads a tile on the calling thread (tiles are read on the pool, <see cref="PathOptions.NonBlockingTiles"/>) and gives up at the first
/// tile still being read: the port stays undecided (joined, as before) and is scanned again at a later plan. A decided port is kept for the
/// life of the navigation data, in a table of its own that is never cleared. A port is a keyframe of the ship (z 0 in the harbour water, an
/// airship hull above its tower), not a floor, and a far port whose tile was not read used to count as joined to everything (the top of
/// Teldrassil "walked" to Auberdine). Thread affinity: world thread.
/// </summary>
internal sealed partial class PlayerbotTravel
{
    private static readonly PathOptions WalkingLocate = new()
    {
        Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true, NonBlockingTiles = true,
    };

    private static readonly PathOptions DryWalkingLocate = new()
    {
        IncludeFlags = NavTerrain.Ground, ExcludeFlags = NavTerrain.SteepSlopes | NavTerrain.Water, LoadTiles = true, NonBlockingTiles = true,
    };

    /// <summary>Like <see cref="DryWalkingLocate"/>: tiles are read on the pool, never here (a scan stops at the first one still being read).</summary>
    internal static readonly PathOptions DryPortLocate = new()
    {
        IncludeFlags = NavTerrain.Ground, ExcludeFlags = NavTerrain.SteepSlopes | NavTerrain.Water, LoadTiles = true, NonBlockingTiles = true,
    };

    /// <summary>The flat rings (yards) around a port searched for large-component floor: out to the relaxed dock reach (<see cref="PlayerbotShip.RelaxedReachYards"/>).</summary>
    private static readonly float[] PortRingsYards = [0f, 4f, 8f, 12f, 16f, 20f, 25f, 30f, 35f, 40f, PlayerbotShip.RelaxedReachYards];

    /// <summary>The heights, relative to the port's, floor is searched at (a boat's keyframe lies in the water, a zeppelin's high above its platform).</summary>
    private static readonly float[] PortHeightOffsets = [0f, -8f, -16f, -22f, 6f];

    private const int PortDirections = 16;

    /// <summary>Most ports scanned in one build of facts (a scan is up to 805 mesh lookups; the rest wait for a later plan).</summary>
    internal const int MaxPortScansPerPlan = 3;

    private static readonly int[] Undecided = [];

    /// <summary>
    /// The components of each decided port, per navigation data (empty: nothing to place it on). Its own table, shared by all bots and never
    /// cleared: there are as many entries as there are ship ports.
    /// </summary>
    private static readonly ConditionalWeakTable<NavMeshPathfinder, Dictionary<(uint Map, Vector3 Position), int[]>> KnownPorts = [];

    /// <summary>Water between the two places that no walk crosses (Teldrassil on map 1), whatever the mesh has loaded.</summary>
    private static bool Separated(TravelPlace a, TravelPlace b)
        => Population.PopulationTownProgression.SeparatedByWater(a.MapId, a.Position.X, a.Position.Y, b.MapId, b.Position.X, b.Position.Y);

    /// <summary>The components a decided port was placed on (empty: no floor), or null while it is undecided (tests).</summary>
    internal static int[]? DecidedPort(Player player, TravelPlace port)
        => player.Map?.Collision.Pathfinder is NavMeshPathfinder nav && KnownPorts.TryGetValue(nav, out Dictionary<(uint Map, Vector3 Position), int[]>? kept)
            && kept.TryGetValue((port.MapId, port.Position), out int[]? components) ? components : null;

    /// <summary>The walkable test of <see cref="BuildFacts(WorldSession, Player, PlayerbotOptions, IReadOnlyList{ShipRoute}, TravelExclusions?)"/>.</summary>
    internal static Func<TravelPlace, TravelPlace, bool> WalkableFor(Player player, IReadOnlyList<ShipRoute> ships)
    {
        if (player.Map?.Collision.Pathfinder is not NavMeshPathfinder nav) return static (a, b) => a.MapId == b.MapId && !Separated(a, b);
        Dictionary<(uint Map, Vector3 Position), int[]> kept = KnownPorts.GetValue(nav, static _ => []);
        int scans = 0;
        HashSet<TravelPlace> ports = [];
        foreach (ShipRoute ship in ships)
        {
            ports.Add(ship.PortA);
            ports.Add(ship.PortB);
        }

        var components = new Dictionary<TravelPlace, int[]>();
        int[] Locate(TravelPlace place)
        {
            if (components.TryGetValue(place, out int[]? known)) return known;
            int[] found = Undecided;
            if (!place.Position.IsFinite()) { }
            else if (ports.Contains(place))
            {
                if (kept.TryGetValue((place.MapId, place.Position), out int[]? remembered))
                    found = remembered;
                else if (scans < MaxPortScansPerPlan && nav.RegionGraph(place.MapId, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes) is { } graph)
                {
                    scans++;
                    if (PortComponents(nav, graph, place) is { } scanned)
                    {
                        found = scanned;
                        kept[(place.MapId, place.Position)] = scanned;
                    }
                }
            }
            else if (nav.RegionGraph(place.MapId, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes) is { } graph)
            {
                int region = nav.RegionAt(graph, place.Position, DryWalkingLocate);
                if (region >= 0 && graph.ComponentPolygons(region) > PlayerbotNavigation.FragmentPolygons)
                    found = [graph.ComponentOf(region)];
            }

            components[place] = found;
            return found;
        }

        return (a, b) =>
        {
            if (a.MapId != b.MapId || Separated(a, b)) return false;
            int[] ca = Locate(a), cb = Locate(b);
            if (ca.Length == 0 || cb.Length == 0) return true;
            foreach (int x in ca)
            {
                foreach (int y in cb)
                {
                    if (x == y) return true;
                }
            }

            return false;
        };
    }

    /// <summary>
    /// The large components (more than <see cref="PlayerbotNavigation.FragmentPolygons"/> polygons) the dry floor within
    /// <see cref="PlayerbotShip.RelaxedReachYards"/> of the port stands on: a bot at the port's dock may stand on any of them (a pier the mesh
    /// splits from the mainland is a component of its own, and the mainland within reach one more). Empty: no floor. Null: a tile around the
    /// port is still being read (<see cref="PathSearchCounters.TilesPending"/>), so nothing is decided yet. Reads no tile on this thread.
    /// </summary>
    internal static int[]? PortComponents(NavMeshPathfinder nav, NavRegionGraph graph, TravelPlace port)
    {
        long pendingMark = PathSearchCounters.TilesPending;
        List<int> found = [];
        foreach (float radius in PortRingsYards)
        {
            int directions = radius == 0f ? 1 : PortDirections;
            for (int i = 0; i < directions; i++)
            {
                float angle = i * MathF.Tau / PortDirections;
                float x = port.Position.X + (MathF.Cos(angle) * radius);
                float y = port.Position.Y + (MathF.Sin(angle) * radius);
                foreach (float dz in PortHeightOffsets)
                {
                    int region = nav.RegionAt(graph, new Vector3(x, y, port.Position.Z + dz), DryPortLocate);
                    if (PathSearchCounters.TilesPending != pendingMark) return null;
                    if (region < 0 || graph.ComponentPolygons(region) <= PlayerbotNavigation.FragmentPolygons) continue;
                    int component = graph.ComponentOf(region);
                    if (!found.Contains(component)) found.Add(component);
                    break;
                }
            }
        }

        return [.. found];
    }
}

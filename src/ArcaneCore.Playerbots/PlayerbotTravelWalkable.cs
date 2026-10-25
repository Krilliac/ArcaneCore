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
/// building) is undecided and taken as joined. Ship ports are placed with blocking tile reads, once per navigation data and port
/// (<see cref="PortComponents"/>): a port is a keyframe of the ship (z 0 in the harbour water, an airship hull above its tower), not a floor,
/// and a far port whose tile was not read used to count as joined to everything (the top of Teldrassil "walked" to Auberdine).
/// Thread affinity: world thread.
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

    /// <summary>Like <see cref="DryWalkingLocate"/> but the tile under the point is read here, on the caller's thread (once per port).</summary>
    private static readonly PathOptions DryPortLocate = new()
    {
        IncludeFlags = NavTerrain.Ground, ExcludeFlags = NavTerrain.SteepSlopes | NavTerrain.Water, LoadTiles = true, NonBlockingTiles = false,
    };

    /// <summary>The flat rings (yards) around a port searched for large-component floor: out to the relaxed dock reach (<see cref="PlayerbotShip.RelaxedReachYards"/>).</summary>
    private static readonly float[] PortRingsYards = [0f, 4f, 8f, 12f, 16f, 20f, 25f, 30f, 35f, 40f, PlayerbotShip.RelaxedReachYards];

    /// <summary>The heights, relative to the port's, floor is searched at (a boat's keyframe lies in the water, a zeppelin's high above its platform).</summary>
    private static readonly float[] PortHeightOffsets = [0f, -8f, -16f, -22f, 6f];

    private const int PortDirections = 16;

    /// <summary>Most places kept per navigation data; past it the table starts over (start and goal places are arbitrary points).</summary>
    private const int MaxKeptPlaces = 4096;

    private static readonly int[] Undecided = [];

    /// <summary>
    /// What was decided about places, per navigation data: the components of ports (empty: scanned, no floor to place them on), and the single
    /// component of other places. Only decided answers are kept: an undecided one (tile not read, graph still building) is asked again.
    /// </summary>
    private static readonly ConditionalWeakTable<NavMeshPathfinder, Dictionary<(uint Map, Vector3 Position), int[]>> KnownPlaces = [];

    /// <summary>Water between the two places that no walk crosses (Teldrassil on map 1), whatever the mesh has loaded.</summary>
    private static bool Separated(TravelPlace a, TravelPlace b)
        => Population.PopulationTownProgression.SeparatedByWater(a.MapId, a.Position.X, a.Position.Y, b.MapId, b.Position.X, b.Position.Y);

    /// <summary>The walkable test of <see cref="BuildFacts(WorldSession, Player, PlayerbotOptions, IReadOnlyList{ShipRoute}, TravelExclusions?)"/>.</summary>
    internal static Func<TravelPlace, TravelPlace, bool> WalkableFor(Player player, IReadOnlyList<ShipRoute> ships)
    {
        if (player.Map?.Collision.Pathfinder is not NavMeshPathfinder nav) return static (a, b) => a.MapId == b.MapId && !Separated(a, b);
        Dictionary<(uint Map, Vector3 Position), int[]> kept = KnownPlaces.GetValue(nav, static _ => []);
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
            if (kept.TryGetValue((place.MapId, place.Position), out int[]? remembered))
            {
                found = remembered;
            }
            else if (place.Position.IsFinite() && nav.RegionGraph(place.MapId, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes) is { } graph)
            {
                bool decided = false;
                if (ports.Contains(place))
                {
                    found = PortComponents(nav, graph, place);
                    decided = true; // scanned with the tiles read: no floor within reach is an answer too (kept as an empty set)
                }
                else
                {
                    int region = nav.RegionAt(graph, place.Position, DryWalkingLocate);
                    if (region < 0 && player.Map is { } map && map.MapId == place.MapId)
                    {
                        Vector3 resolved = PlayerbotShip.ResolveDockTarget(map, place.Position, new Vector3(player.X, player.Y, player.Z));
                        if (resolved != place.Position)
                            region = nav.RegionAt(graph, resolved, WalkingLocate);
                    }

                    if (region >= 0 && graph.ComponentPolygons(region) > PlayerbotNavigation.FragmentPolygons)
                    {
                        found = [graph.ComponentOf(region)];
                        decided = true;
                    }
                }

                if (decided)
                {
                    if (kept.Count >= MaxKeptPlaces) kept.Clear();
                    kept[(place.MapId, place.Position)] = found;
                }
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
    /// <see cref="PlayerbotShip.RelaxedReachYards"/> of the port stands on, tiles read here: a bot at the port's dock may stand on any of them (a
    /// pier the mesh splits from the mainland is a component of its own, and the mainland within reach one more). Empty: no floor.
    /// </summary>
    internal static int[] PortComponents(NavMeshPathfinder nav, NavRegionGraph graph, TravelPlace port)
    {
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

using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Npc;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>A theory that needs the live server's terrain extraction (<see cref="RealTerrainBotFactAttribute"/>); Skipped without it.</summary>
internal sealed class RealTerrainBotTheoryAttribute : TheoryAttribute
{
    public RealTerrainBotTheoryAttribute()
    {
        string? root = Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(Path.Combine(root, "mmaps")))
            Skip = $"{RealTerrainBotFactAttribute.Variable} is not set to a terrain data root with mmaps/: the real-terrain long routes did NOT run.";
    }
}

/// <summary>
/// Long routes on the real terrain: the bot QA's stall points of issues #191 (quest 5881, Stonetalon to Grish Longrunner at the Great
/// Lift) and #194 (quest 1418, the Swamp of Sorrows to Neeka Bloodscar in Kargath), each planned leg by leg to the quest ender. A leg is
/// one <see cref="PlayerbotNavigation.TryPlanToward"/> as the brain makes it; the bot is then placed at the route's end (walking each
/// leg is the motion's business, covered by the other real-terrain tests), so the whole way of up to 9,400 yards is checked in seconds.
/// </summary>
public sealed partial class PlayerbotRealTerrainNavigationTests
{
    // Grish Longrunner (creature 12576, spawn 21295) and Neeka Bloodscar (creature 5394, spawn 6874).
    private static readonly Vector3 GrishLongrunner = new(-4602.11f, -1835.37f, 86.1941f);
    private static readonly Vector3 NeekaBloodscar = new(-6672.25f, -2240.52f, 278.577f);

    /// <summary>
    /// The most wall-clock time one plan may take. The tick-perf lane measured bot-think spikes of 0.7 to 1.9 seconds from long path
    /// searches; the legs measured 5 to 100 ms on a machine loaded by a live server, so this bound catches that class of spike, not noise.
    /// </summary>
    private const double PlanBudgetMs = 500;

    public static TheoryData<string, uint, float, float, float> IssueStallPoints => new()
    {
        { "#191 183.9,181.2", 1, 183.9f, 181.2f, 51.8f },
        { "#191 796.0,900.0", 1, 796.0f, 900.0f, 168.2f },
        { "#191 2079.2,987.7", 1, 2079.2f, 987.7f, 220.7f },
        { "#191 2705.1,1243.8", 1, 2705.1f, 1243.8f, 298.2f },
        { "#191 2424.1,1338.6", 1, 2424.1f, 1338.6f, 274.8f },
        { "#191 620.3,441.3", 1, 620.3f, 441.3f, 90.1f },
        { "#194 -9979.2,-3137.6", 0, -9979.2f, -3137.6f, 113.9f },
        { "#194 -9973.3,-3152.3", 0, -9973.3f, -3152.3f, 109.1f },
        { "#194 -9988.4,-3070.9", 0, -9988.4f, -3070.9f, 54.1f },
    };

    /// <summary>
    /// From each stall point every plan is a route that makes progress, and the legs end within interaction range of the ender (in three
    /// dimensions: Neeka stands on the Kargath tower's top, 34 yards above its floor), each plan within <see cref="PlanBudgetMs"/>.
    /// Before, no route was planned from any of these points: the next point on the straight line lay off the mesh.
    /// </summary>
    [RealTerrainBotTheory]
    [MemberData(nameof(IssueStallPoints))]
    public async Task FromAnIssueStallPoint_TheBotPlansLegByLeg_AllTheWayToTheQuestEnder(string stall, uint map, float x, float y, float z)
    {
        Vector3 ender = map == 1 ? GrishLongrunner : NeekaBloodscar;
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(new Vector3(x, y, z), map);
        await RegionGraphAsync(terrain, map);

        var options = new PlayerbotOptions();
        var times = new List<double>();
        float walked = 0, left = float.PositiveInfinity;
        for (int leg = 0; leg < 25 && left > QuestNpcServices.InteractionDistance; leg++)
        {
            (PlayerbotRoute? route, Vector3 at) = await terrain.Host.OnWorldAsync(() =>
            {
                var watch = Stopwatch.StartNew();
                bool planned = PlayerbotNavigation.TryPlanToward(terrain.Player, ender, options, out PlayerbotRoute? next);
                times.Add(watch.Elapsed.TotalMilliseconds);
                return (planned ? next : null, new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z));
            });
            Assert.True(route is not null, $"{stall}: leg {leg}: no route from {at}, {Vector3.Distance(at, ender):F0} yards from the ender");
            Vector3 end = route!.Points[^1];
            output.WriteLine($"leg {leg}: {at} -> {end}: {route.Distance:F0} yards, {route.Points.Count} corners, {times[^1]:F1} ms");
            Assert.True(route.Distance <= options.MaxRouteYards, $"leg {leg}: a {route.Distance:F0}-yard route");
            Assert.True(Vector3.Distance(at, end) >= 1f, $"leg {leg}: no progress from {at}");
            walked += route.Distance;
            left = Vector3.Distance(end, ender);
            await terrain.PlaceAsync(end with { Z = end.Z + 0.5f }, map);
        }

        output.WriteLine($"{stall}: {times.Count} legs, {walked:F0} yards; plan ms max {times.Max():F1}, mean {times.Average():F1}");
        Assert.True(left <= QuestNpcServices.InteractionDistance, $"{stall}: the legs end {left:F1} yards from the ender");
        Assert.True(times.Max() <= PlanBudgetMs, $"{stall}: a plan took {times.Max():F0} ms");
    }

    /// <summary>
    /// The bot walks (the real motion, game time) from Kargath's floor up the tower's stairs to Neeka Bloodscar, the upper flight of which
    /// the mesh flags steep. Before, every route stopped 13 yards below her and the bots that reached Kargath stood there.
    /// </summary>
    [RealTerrainBotFact]
    public async Task FromKargathsFloor_TheBotWalksUpTheTowerStairs_ToNeekaBloodscar()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(new Vector3(-6675.7f, -2173.8f, 244.2f));
        await terrain.WalkAsync(NeekaBloodscar, 60_000, output);
        float left = await terrain.Host.OnWorldAsync(() => Vector3.Distance(new(terrain.Player.X, terrain.Player.Y, terrain.Player.Z), NeekaBloodscar));
        Assert.True(left <= QuestNpcServices.InteractionDistance, $"stopped {left:F1} yards from Neeka Bloodscar");
    }

    /// <summary>
    /// The region graph is read on the thread pool: the first plan that wants it gets none at once (the plan goes on without it) and the
    /// world thread does not wait the seconds a continent's tiles take to read.
    /// </summary>
    [RealTerrainBotFact]
    public async Task TheRegionGraph_IsBuiltOffTheWorldThread()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(new Vector3(796.0f, 900.0f, 168.2f), 1);
        (bool none, double ms) = await terrain.Host.OnWorldAsync(() =>
        {
            var watch = Stopwatch.StartNew();
            var nav = (NavMeshPathfinder)terrain.Player.Map!.Collision.Pathfinder;
            bool absent = nav.RegionGraph(1, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes) is null;
            return (absent, watch.Elapsed.TotalMilliseconds);
        });
        Assert.True(none, "the graph was there before it could have been built");
        Assert.True(ms < 50, $"the first call took {ms:F1} ms on the world thread");
        var stopwatch = Stopwatch.StartNew();
        NavRegionGraph graph = await RegionGraphAsync(terrain, 1);
        output.WriteLine($"built in about {stopwatch.ElapsedMilliseconds} ms: {graph.TileCount} tiles, {graph.RegionCount} regions, {graph.LinkCount} links");
        Assert.True(graph.TileCount > 600, $"only {graph.TileCount} Kalimdor tiles read");
    }

    /// <summary>The map's region graph for the bots' filter, waiting for its build.</summary>
    private static async Task<NavRegionGraph> RegionGraphAsync(Terrain terrain, uint map)
    {
        Task<NavRegionGraph?> build = await terrain.Host.OnWorldAsync(() =>
            ((NavMeshPathfinder)terrain.Player.Map!.Collision.Pathfinder).RegionGraphTask(map, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!);
        return await build ?? throw new InvalidOperationException($"no region graph for map {map}");
    }
}

using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Battlegrounds;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// Arathi Basin (map 529) on the real navigation meshes: the two live faults of the battleground bots' walking.
/// <list type="bullet">
/// <item>Bots 2-4 yards from a banner were refused its route (217 "bot-bg/objective-unreachable" a day; Blacksmith 977.0, 1046.6, -44.8 and
/// Stables 1166.8, 1200.1, -56.7): <see cref="EveryBanner_PlannedExactlyFromAnySpotBesideIt_IsRouted"/> plans each banner the way
/// <c>PlayerbotBattlegroundAI.WalkTo</c> does for one (<see cref="PlayerbotNavigation.PlanExact"/>, no chase).</item>
/// <item>A follower / flag carrier planned as partial ("mesh ended short") from 982.3, 1210.4, -45.1 towards about 1155, 1194, -56: the
/// mesh queries (<see cref="MeshAnswer_ToTheStablesBanner_FromTheLiveStall_IsNotCappedAndEndsWithinInteractRange"/>, <see cref="MeshReaches_TheAllianceStart_FromTheLiveStall"/>) say
/// whether the mesh or its node budget is at fault, and <see cref="FollowPlans_FromTheLiveStall_ReachTheirGoals"/> whether the toward / chase
/// planner choice is.</item>
/// </list>
/// Banner positions: classic-db z2815 <c>gameobject</c> rows 5290025-5290029 as recorded in <c>ArathiBasinTestContent.NeutralBanners</c>
/// (tests/ArcaneCore.World.Tests/Playerbots/Scenarios/ArathiBasinWorldScenarioTests.cs); the Alliance start is WorldSafeLoc 890
/// (1286.05, 1282.53, -15.66) of the same file (classic-db z2815).
/// <para>
/// Test-side setup: the plain real-terrain world knows only the two continents, and a teleport to a map outside its registry is refused.
/// Map 529 is therefore registered as a common (non-battleground) map, so the battleground entry rules (a bound match) do not apply;
/// and the bots' options list 529 in <see cref="PlayerbotOptions.AllowedMaps"/>, which is what a match participant is granted
/// (<c>PlayerbotMapPolicy.Allows</c>: a participant walks a battleground map off the list). Everything else is the default bot options.
/// </para>
/// </summary>
public sealed class PlayerbotArathiBasinBannerRouteRealTerrainTests : RealTerrainNavigationTestBase
{
    public PlayerbotArathiBasinBannerRouteRealTerrainTests(ITestOutputHelper output) : base(output) { }

    private const uint ArathiBasin = 529;

    /// <summary>The five neutral banners, in node order (ArathiBasin.NodeStables .. NodeGoldMine).</summary>
    private static readonly (string Name, Vector3 At)[] Banners =
    [
        ("Stables", new Vector3(1166.79f, 1200.13f, -56.7086f)),
        ("Blacksmith", new Vector3(977.016f, 1046.62f, -44.8092f)),
        ("Farm", new Vector3(806.182f, 874.272f, -55.9937f)),
        ("Lumber Mill", new Vector3(856.142f, 1148.9f, 11.1847f)),
        ("Gold Mine", new Vector3(1146.92f, 848.178f, -110.917f)),
    ];

    /// <summary>Where the follower stood live ("mesh ended short"), and the Alliance start (WorldSafeLoc 890).</summary>
    private static readonly Vector3 LiveStall = new(982.3f, 1210.4f, -45.1f);
    private static readonly Vector3 StablesBanner = Banners[0].At;
    private static readonly Vector3 AllianceStart = new(1286.05f, 1282.53f, -15.66f);

    private static readonly float[] SideRadii = [2f, 3f, 4f];

    private static readonly (string Name, float X, float Y)[] Compass =
    [
        ("north", 0f, 1f), ("east", 1f, 0f), ("south", 0f, -1f), ("west", -1f, 0f),
        ("north-east", 0.7071f, 0.7071f), ("south-east", 0.7071f, -0.7071f), ("south-west", -0.7071f, -0.7071f), ("north-west", -0.7071f, 0.7071f),
    ];

    private const int MaxAsks = 1500;

    private static void AddArathiBasinMap(IServiceCollection services) => services.AddSingleton<IMapDataStore>(new Maps());

    private static PlayerbotOptions BotOptions() => new() { AllowedMaps = [0, 1, ArathiBasin] };

    private sealed class Maps : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MapContent(
            [.. MapRegistry.DefaultContinents, new MapTemplate(ArathiBasin, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Arathi Basin", "")],
            [], [], [], []));
    }

    private delegate PlanOutcome Planner(ref TileWait wait, uint now, out PlayerbotRoute? route);

    /// <summary>
    /// Plan as the brain does: ask again, the world clock moving on, while a navigation tile is still being read
    /// (<see cref="PlanOutcome.Pending"/>), until a verdict or <see cref="MaxAsks"/> asks.
    /// </summary>
    private static async Task<(PlanOutcome Outcome, PlayerbotRoute? Route, int Asks)> PlanAsync(Terrain terrain, Planner plan)
    {
        TileWait[] wait = [new TileWait()]; // boxed: a ref local cannot be captured by the world-thread lambda
        for (int ask = 1; ask <= MaxAsks; ask++)
        {
            (PlanOutcome outcome, PlayerbotRoute? route) = await terrain.Host.OnWorldAsync(() =>
            {
                PlanOutcome answer = plan(ref wait[0], terrain.Host.World.NowMs, out PlayerbotRoute? planned);
                return (answer, planned);
            });
            if (outcome != PlanOutcome.Pending) return (outcome, route, ask);
            await Task.Delay(20); // the tile is read on a background thread: real time, not game time
            await terrain.Host.World.AdvanceClockAsync(100);
        }

        return (PlanOutcome.Pending, null, MaxAsks);
    }

    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));

    private static string Describe(Vector3 goal, PlanOutcome outcome, PlayerbotRoute? route, int asks)
    {
        if (route is null) return $"{outcome} after {asks} ask(s), no route";
        if (route.Points.Count == 0) return $"{outcome} after {asks} ask(s), a route of no points";
        Vector3 end = route.Points[^1];
        return $"{outcome} after {asks} ask(s): {route.Points.Count} points, length {route.Distance:F0}, end {end}, {Flat(end, goal):F1} yd flat / "
            + $"{Vector3.Distance(end, goal):F1} yd from the goal, navigated={route.Navigated} meshEndedShort={route.MeshEndedShort} "
            + $"partialToGoal={route.PartialToGoal} chase={route.Chase}";
    }

    private sealed record MeshAnswer(PathResult Path, PathSearchTotals Work, int Budget);

    /// <summary>
    /// One mesh query as the bots' planning makes it (<c>PlayerbotNavigation.QueryOnce</c>: player mover, steep slopes excluded, partial
    /// allowed, tiles loaded on demand) but with tiles read on the calling thread, so the answer is never "pending".
    /// </summary>
    private static Task<MeshAnswer> MeshAsync(Terrain terrain, Vector3 from, Vector3 to, int nodes) => terrain.Host.OnWorldAsync(() =>
    {
        PathSearchTotals before = PathSearchCounters.Current;
        PathResult path = terrain.Player.Map!.Collision.FindPath(from, to, new PathOptions
        {
            MaxPoints = Math.Max(2, new PlayerbotOptions().MaxPathPoints),
            Mover = PathMover.Player,
            ExcludeFlags = NavTerrain.SteepSlopes,
            AllowPartial = true,
            MaxSearchNodes = nodes,
            LoadTiles = true,
            NonBlockingTiles = false,
        });
        return new MeshAnswer(path, PathSearchCounters.Current - before, nodes);
    });

    private static string Describe(Vector3 goal, MeshAnswer answer)
    {
        PathResult path = answer.Path;
        string end = path.Points.Count == 0
            ? "no points"
            : $"end {path.End}, {Flat(path.End, goal):F1} yd flat / {Vector3.Distance(path.End, goal):F1} yd from the goal";
        return $"type={path.Type} points={path.Points.Count} {end}; node budget {answer.Budget}: {answer.Work.Nodes} nodes created, "
            + $"{answer.Work.Expansions} expanded, capped searches={answer.Work.Capped} (node cap, expansion cap or deadline), deadline hits={answer.Work.DeadlineHits}";
    }

    private static bool ReachesGoal(PathResult path, Vector3 goal)
        => (path.Type & PathType.Normal) != 0
            && (path.Type & (PathType.Incomplete | PathType.NoPath | PathType.NotUsingPath | PathType.TilesPending | PathType.Short)) == 0
            && path.Points.Count >= 2 && Flat(path.End, goal) <= 1f;

    /// <summary>
    /// Item 2. Finding (local run, real terrain): every banner is off the mesh by about 1 yd (the mesh ends 0.9-1.1 yd from it), and an
    /// exact plan from 3 yd north of each routes. This guard widens that: from every standing spot on the mesh floor (<c>TryGetMeshFloor</c>)
    /// at 2, 3 and 4 yd on all 8 compass directions, <c>PlanExact(chase:false)</c> as <c>WalkTo(..., toward: false)</c> plans a banner must
    /// route. A direction with no floor is skipped and logged as skipped. One world per banner; every spot's outcome is in the output.
    /// </summary>
    [RealTerrainBotFact]
    public async Task EveryBanner_PlannedExactlyFromAnySpotBesideIt_IsRouted()
    {
        var lines = new List<string>();
        var failures = new List<string>();
        int planned = 0;
        PlayerbotOptions options = BotOptions();
        foreach ((string name, Vector3 banner) in Banners)
        {
            // One world per banner: placed at the banner for its tile, then moved between the standing spots (near teleports).
            await using Terrain terrain = await Terrain.StartAsync(services: AddArathiBasinMap);
            await terrain.PlaceAsync(banner, ArathiBasin);
            NavMeshPathfinder nav = await terrain.Host.OnWorldAsync(() => (NavMeshPathfinder)terrain.Player.Map!.Collision.Pathfinder);
            bool bannerOnMesh = await terrain.Host.OnWorldAsync(() =>
            {
                nav.LoadTileOnDemand(ArathiBasin, banner.X, banner.Y);
                return nav.TryGetMeshFloor(ArathiBasin, banner.X, banner.Y, banner.Z, 3f, 3f, out _);
            });
            lines.Add($"{name} banner {banner} (on mesh within 3 yd: {bannerOnMesh})");
            int found = 0;
            foreach (float radius in SideRadii)
            {
                foreach ((string direction, float dx, float dy) in Compass)
                {
                    float x = banner.X + (dx * radius), y = banner.Y + (dy * radius);
                    (bool onFloor, float floor) = await terrain.Host.OnWorldAsync(() =>
                    {
                        bool ok = nav.TryGetMeshFloor(ArathiBasin, x, y, banner.Z, 3f, 3f, out float height);
                        return (ok, height);
                    });
                    if (!onFloor)
                    {
                        lines.Add($"  {radius} yd {direction}: skipped, no mesh floor");
                        continue;
                    }

                    found++;
                    var spot = new Vector3(x, y, floor);
                    await terrain.PlaceAsync(spot, ArathiBasin);
                    (PlanOutcome outcome, PlayerbotRoute? route, int asks) = await PlanAsync(terrain, (ref TileWait wait, uint now, out PlayerbotRoute? answer)
                        => PlayerbotNavigation.PlanExact(terrain.Player, banner, options, ref wait, now, out answer, chase: false));
                    planned++;
                    string line = $"  {radius} yd {direction}: bot {spot} ({Vector3.Distance(spot, banner):F1} yd): {Describe(banner, outcome, route, asks)}";
                    lines.Add(line);
                    if (outcome != PlanOutcome.Route || route is null) failures.Add($"{name}{line}");
                }
            }

            if (found == 0) failures.Add($"{name}: no standing spot with a mesh floor within {SideRadii.Max()} yd");
        }

        foreach (string line in lines) output.WriteLine(line);
        Assert.True(failures.Count == 0,
            $"{failures.Count} of {planned} exact plans to a banner were refused from a spot 2-4 yd beside it:\n" + string.Join("\n", failures)
            + "\nall spots:\n" + string.Join("\n", lines));
    }

    /// <summary>
    /// Item 3 (mesh), regression guard. Finding: from the live stall the mesh answer to the Stables banner is <c>Incomplete</c>, not capped
    /// (107 nodes, no capped search at either budget) and ends about 1.1 yd from the banner, which is itself off the mesh. So the node
    /// budget is not the cause of the live "mesh ended short". Asserts the search is not capped at the bots' budget nor the widest, and
    /// that the answer ends within <c>PlayerbotBattlegroundAI.InteractYards</c> (flat) of the banner.
    /// </summary>
    [RealTerrainBotFact]
    public async Task MeshAnswer_ToTheStablesBanner_FromTheLiveStall_IsNotCappedAndEndsWithinInteractRange()
    {
        (MeshAnswer bot, MeshAnswer widest, string report) = await MeshReportAsync("Stables banner", StablesBanner);
        Assert.True(bot.Work.Capped == 0 && widest.Work.Capped == 0, "the search was capped:\n" + report);
        Assert.True(bot.Path.Points.Count >= 2 && Flat(bot.Path.End, StablesBanner) <= PlayerbotBattlegroundAI.InteractYards,
            "the mesh answer does not end within interact range of the banner:\n" + report);
    }

    /// <summary>Item 3 (mesh): the bots' search budget for the distance reaches the Alliance start whole (Normal, within 1 yd flat).</summary>
    [RealTerrainBotFact]
    public async Task MeshReaches_TheAllianceStart_FromTheLiveStall()
    {
        (MeshAnswer bot, _, string report) = await MeshReportAsync("Alliance start", AllianceStart);
        Assert.True(ReachesGoal(bot.Path, AllianceStart), "the mesh does not reach the goal whole with the bots' own search budget:\n" + report);
    }

    private async Task<(MeshAnswer Bot, MeshAnswer Widest, string Report)> MeshReportAsync(string what, Vector3 goal)
    {
        await using Terrain terrain = await Terrain.StartAsync(services: AddArathiBasinMap);
        await terrain.PlaceAsync(LiveStall, ArathiBasin);
        int botBudget = PlayerbotNavigation.SearchNodes(LiveStall, goal);
        MeshAnswer bot = await MeshAsync(terrain, LiveStall, goal, botBudget);
        MeshAnswer widest = await MeshAsync(terrain, LiveStall, goal, PlayerbotNavigation.MaxSearchNodes);
        string report = $"{what} {goal} from {LiveStall} ({Vector3.Distance(LiveStall, goal):F0} yd straight)\n"
            + $"  bot budget: {Describe(goal, bot)}\n  widest budget: {Describe(goal, widest)}";
        output.WriteLine(report);
        return (bot, widest, report);
    }

    /// <summary>
    /// Item 3 (planner), regression guard. Finding: from the live stall every planner (PlanToward, PlanExact with and without chase) ends at
    /// the same point about 1.1 yd from the Stables banner, which is itself off the mesh (PlanToward flags meshEndedShort); to the Alliance
    /// start every planner is Normal and 0.0 yd away. So neither the toward / chase planner choice nor the node budget causes the live
    /// fault. Asserts: every planner routes to the Stables banner and ends within <c>PlayerbotBattlegroundAI.InteractYards</c> (flat) of it;
    /// to the Alliance start PlanToward is not a partial-to-goal route and PlanExact with chase ends within 1 yd (flat). The region graph is
    /// built as live has it. All outcomes and route ends are written to the test output.
    /// </summary>
    [RealTerrainBotFact]
    public async Task FollowPlans_FromTheLiveStall_ReachTheirGoals()
    {
        await FollowPlansAsync();
    }

    private static void CheckEnd(List<string> failures, string what, string planner, PlanOutcome outcome, PlayerbotRoute? route, Vector3 goal, float reach)
    {
        if (outcome != PlanOutcome.Route || route is not { Points.Count: > 0 })
            failures.Add($"{what}: {planner} gave no route ({outcome})");
        else if (Flat(route.Points[^1], goal) > reach)
            failures.Add($"{what}: {planner} ends {Flat(route.Points[^1], goal):F1} yd (flat) from the goal, past {reach} yd, at {route.Points[^1]}");
    }

    private async Task FollowPlansAsync()
    {
        await using Terrain terrain = await Terrain.StartAsync(services: AddArathiBasinMap);
        await terrain.PlaceAsync(LiveStall, ArathiBasin);
        _ = await RegionGraphAsync(terrain, ArathiBasin);
        PlayerbotOptions options = BotOptions();
        var lines = new List<string>();
        var failures = new List<string>();
        foreach ((string what, Vector3 goal) in new[] { ("Stables banner", StablesBanner), ("Alliance start", AllianceStart) })
        {
            (PlanOutcome towardOutcome, PlayerbotRoute? toward, int towardAsks) = await PlanAsync(terrain, (ref TileWait wait, uint now, out PlayerbotRoute? planned)
                => PlayerbotNavigation.PlanToward(terrain.Player, goal, options, ref wait, now, out planned));
            (PlanOutcome chaseOutcome, PlayerbotRoute? chase, int chaseAsks) = await PlanAsync(terrain, (ref TileWait wait, uint now, out PlayerbotRoute? planned)
                => PlayerbotNavigation.PlanExact(terrain.Player, goal, options, ref wait, now, out planned, chase: true));
            (PlanOutcome exactOutcome, PlayerbotRoute? exact, int exactAsks) = await PlanAsync(terrain, (ref TileWait wait, uint now, out PlayerbotRoute? planned)
                => PlayerbotNavigation.PlanExact(terrain.Player, goal, options, ref wait, now, out planned, chase: false));
            MeshAnswer mesh = await MeshAsync(terrain, LiveStall, goal, PlayerbotNavigation.SearchNodes(LiveStall, goal));
            lines.Add($"{what} {goal} from {LiveStall}:\n  PlanToward:          {Describe(goal, towardOutcome, toward, towardAsks)}"
                + $"\n  PlanExact chase:true:  {Describe(goal, chaseOutcome, chase, chaseAsks)}"
                + $"\n  PlanExact chase:false: {Describe(goal, exactOutcome, exact, exactAsks)}\n  mesh (bot budget):   {Describe(goal, mesh)}");

            if (goal == StablesBanner)
            {
                // The banner is off the mesh by about 1 yd: every planner ends beside it, within interact range.
                float reach = PlayerbotBattlegroundAI.InteractYards;
                CheckEnd(failures, what, "PlanToward", towardOutcome, toward, goal, reach);
                CheckEnd(failures, what, "PlanExact chase:true", chaseOutcome, chase, goal, reach);
                CheckEnd(failures, what, "PlanExact chase:false", exactOutcome, exact, goal, reach);
                continue;
            }

            if (towardOutcome != PlanOutcome.Route || toward is null)
                failures.Add($"{what}: PlanToward gave no route ({towardOutcome})");
            else if (toward.PartialToGoal)
                failures.Add($"{what}: PlanToward ended short by the mesh's own partial answer, at {toward.Points[^1]}");
            CheckEnd(failures, what, "PlanExact chase:true", chaseOutcome, chase, goal, 1f);
        }

        foreach (string line in lines) output.WriteLine(line);
        Assert.True(failures.Count == 0, string.Join("\n", failures) + "\n" + string.Join("\n", lines));
    }
}

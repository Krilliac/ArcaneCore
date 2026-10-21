using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Probes, not assertions on behaviour: what the navigation mesh, the region graph and the bot's planning say at the bot QA cluster C3
/// pins (the same stall point across bots and goals), on the real terrain (<c>ARCANECORE_TEST_TERRAIN_DIR</c>). Each probe writes, per
/// pin and goal: the pin's region and the number of regions joined to it; whether the goal's region is joined; a full Detour partial path
/// (very high node cap) with its node count; what <see cref="PlayerbotNavigation.TryPlanToward"/> plans from the pin (production
/// options and the live stress test's); and the query of <c>ProvesDeadEnd</c> (32,768-node cap) with its node count and whether it
/// capped. Goal positions are the spawns of existing real-terrain tests and the stall reports; each is labelled with where it is from.
/// The assertions only guard that the probe ran (the pin is on the mesh).
/// </summary>
public sealed class PlayerbotDeadEndPinProbeTests : RealTerrainNavigationTestBase
{
    public PlayerbotDeadEndPinProbeTests(ITestOutputHelper output) : base(output) { }

    private static readonly (string Label, Vector3 At)[] DurotarGoals =
    [
        ("Chief Hawkwind platform, Camp Narache (stall test)", new(-2881.9f, -229.1f, 55.2f)),
        ("Greatmother Hawkwind, Mulgore (stall test)", new(-3052.5f, -522.5f, 27.0f)),
        ("Zureetha Fargaze / Jen'shan, Valley of Trials (issue 402)", new(-629f, -4228f, 38.4f)),
        ("Innkeeper Grosk, Razor Hill (stall test)", new(340.4f, -4686.3f, 16.5f)),
        ("Ukor, Durotar (stall test)", new(-599.4f, -4715.3f, 35.2f)),
    ];

    private static readonly (string Label, Vector3 At)[] DunMoroghGoals =
    [
        ("Coldridge stall point (stall test)", new(-6135.3f, 386.1f, 395.6f)),
        ("Kharanos road, issue 537 stall", new(-5497.0f, -385.7f, 404.1f)),
        ("Kharanos to Ironforge road, issue 392 stall", new(-5266f, -698f, 390f)),
        ("Ironforge pin, issue 381", new(-4715.7f, -871.2f, 504.7f)),
        ("Coldridge pin, issue 389", new(-6494.1f, 324.9f, 406.0f)),
    ];

    /// <summary>Issues #418, #461, #552: the ridge above the Valley of Trials.</summary>
    [RealTerrainBotFact]
    public Task Probe_C3_ValleyOfTrialsRidge() => ProbePinAsync(1, new(-1.0f, -4188.3f, 79.4f), DurotarGoals);

    /// <summary>Issues #379, #402: -137.6, -4265.7.</summary>
    [RealTerrainBotFact]
    public Task Probe_C3_Pin137() => ProbePinAsync(1, new(-137.6f, -4265.7f, 105.3f), DurotarGoals);

    /// <summary>Issues #389, #375: Coldridge.</summary>
    [RealTerrainBotFact]
    public Task Probe_C3_Coldridge() => ProbePinAsync(0, new(-6494.1f, 324.9f, 406.0f), DunMoroghGoals);

    /// <summary>Issues #381, #537: inside Ironforge.</summary>
    [RealTerrainBotFact]
    public Task Probe_C3_Ironforge() => ProbePinAsync(0, new(-4715.7f, -871.2f, 504.7f), DunMoroghGoals);

    /// <summary>Issue #371: the Barrens, 18 distinct bots (z from the issue's samples).</summary>
    [RealTerrainBotFact]
    public Task Probe_C3_Barrens() => ProbePinAsync(1, new(-1173.6f, -3285.5f, 91.7f), DurotarGoals);

    private async Task ProbePinAsync(uint map, Vector3 pin, (string Label, Vector3 At)[] goals)
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(pin, map);
        Task<NavRegionGraph?> build = await terrain.Host.OnWorldAsync(() =>
            ((NavMeshPathfinder)terrain.Player.Map!.Collision.Pathfinder).RegionGraphTask(map, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!);
        NavRegionGraph graph = (await build)!;
        Assert.NotNull(graph);

        bool onMesh = await terrain.Host.OnWorldAsync(() =>
        {
            Player player = terrain.Player;
            var nav = (NavMeshPathfinder)player.Map!.Collision.Pathfinder;
            Vector3 here = new(player.X, player.Y, player.Z);
            var locate = new PathOptions { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true };
            int from = nav.RegionAt(graph, here, locate);
            output.WriteLine($"=== map {map} pin {pin} bot at {here}: region {from} of {graph.RegionCount}; skipped tiles {graph.SkippedTiles.Count}");
            if (from < 0) return false;
            output.WriteLine($"(a) regions joined to the pin's region (incl. itself): {graph.ReachableCount(from, int.MaxValue)}");

            var production = new PlayerbotOptions();
            var live = new PlayerbotOptions { MaxPathPoints = 128, MaxRouteYards = 2000 };
            output.WriteLine($"production options: MaxPathPoints {production.MaxPathPoints}, MaxRouteYards {production.MaxRouteYards}");

            foreach ((string label, Vector3 goal) in goals)
            {
                if (Vector3.Distance(goal, here) < 1f) continue;
                int to = nav.RegionAt(graph, goal, locate);
                output.WriteLine($"--- goal {label} {goal}: {Vector3.Distance(here, goal):F0} yards; goal region {to}");
                if (to < 0) { output.WriteLine("(b) goal not on the mesh"); continue; }

                NavRegionRouteStatus status = graph.FindRoute(from, here, to, goal, PlayerbotNavigation.RegionSearchNodes, out List<Vector3> portals, out int settled);
                string last = portals.Count == 0 ? "no portal" : $"last portal {portals[^1]} ({Vector3.Distance(portals[^1], here):F0} from the bot, {Vector3.Distance(portals[^1], goal):F0} from the goal)";
                output.WriteLine($"(b) region route {status}: {portals.Count} portals, {settled} settled; {last}");

                PathSearchTotals before = PathSearchCounters.Current;
                PathResult full = nav.FindPath(map, here, goal, new PathOptions { MaxPoints = 4096, Mover = PathMover.Player,
                    ExcludeFlags = NavTerrain.SteepSlopes, AllowPartial = true, MaxSearchNodes = 5_000_000, LoadTiles = true });
                PathSearchTotals after = PathSearchCounters.Current;
                output.WriteLine($"(c) full partial path: {full.Type}, {(full.HasPath ? full.Points.Count : 0)} points, end {full.End} "
                    + $"({Vector3.Distance(full.End, here):F0} from the bot, {Vector3.Distance(full.End, goal):F0} from the goal); "
                    + $"nodes {after.Nodes - before.Nodes}, expansions {after.Expansions - before.Expansions}, capped {after.Capped - before.Capped}");

                foreach ((string name, PlayerbotOptions options) in new[] { ("production", production), ("live", live) })
                {
                    before = PathSearchCounters.Current;
                    bool planned = PlayerbotNavigation.TryPlanToward(player, goal, options, out PlayerbotRoute? route);
                    after = PathSearchCounters.Current;
                    output.WriteLine($"(d) TryPlanToward {name}: {(planned && route is not null ? $"route to {route.Points[^1]} ({Vector3.Distance(route.Points[^1], here):F0} yards, {Vector3.Distance(route.Points[^1], goal):F0} from the goal)" : "refused")}; "
                        + $"nodes {after.Nodes - before.Nodes}, searches {after.Searches - before.Searches}, capped {after.Capped - before.Capped}, pending {after.TilesPending - before.TilesPending}");
                }

                before = PathSearchCounters.Current;
                PathResult proof = nav.FindPath(map, here, goal, new PathOptions { MaxPoints = Math.Max(2, production.MaxPathPoints), Mover = PathMover.Player,
                    ExcludeFlags = NavTerrain.SteepSlopes, AllowPartial = true, MaxSearchNodes = PlayerbotNavigation.MaxSearchNodes, LoadTiles = true, NonBlockingTiles = true });
                after = PathSearchCounters.Current;
                output.WriteLine($"(e) ProvesDeadEnd query: {proof.Type}, end {(proof.HasPath ? Vector3.Distance(proof.End, here).ToString("F1") : "-")} yards from the bot; "
                    + $"nodes {after.Nodes - before.Nodes}, capped {after.Capped - before.Capped}, pending {after.TilesPending - before.TilesPending}");
            }

            // The explore wander: eight headings at ExploreLegYards from the pin (production options), as WanderLegEnd lays them.
            var production2 = new PlayerbotOptions();
            var sb = new System.Text.StringBuilder();
            for (int h = 0; h < 8; h++)
            {
                float angle = h * MathF.PI / 4f;
                Vector3 dest = new(here.X + (MathF.Cos(angle) * PlayerbotBrain.ExploreLegYards), here.Y + (MathF.Sin(angle) * PlayerbotBrain.ExploreLegYards), here.Z);
                bool planned = PlayerbotNavigation.TryPlanToward(player, dest, production2, out PlayerbotRoute? route);
                sb.Append(planned && route is not null ? $" {h * 45}deg:{Vector3.Distance(route.Points[^1], here):F0}yd" : $" {h * 45}deg:refused");
            }

            output.WriteLine($"(wander) 48-yard legs from the pin:{sb}");
            return true;
        });
        Assert.True(onMesh, $"the pin {pin} is not on the navigation mesh");
    }
}

public sealed class PlayerbotDeadEndPinWalkHawkwindTests : RealTerrainNavigationTestBase
{
    public PlayerbotDeadEndPinWalkHawkwindTests(ITestOutputHelper output) : base(output) { }

    // The movement side of the pins: the bot walks (PlanToward legs, followed by the motion) to a goal the region graph and a full Detour
    // path say is reachable, for up to 120 s of game time (600 s towards Chief Hawkwind); the probe writes where it ends. A bot that moves is not stuck at the pin by
    // planning; one that does not is stuck by the motion (a rim the mesh joins but the bot cannot follow).
    // Quest 752/745 targets (Chief Hawkwind, Mulgore): the goal region is not joined to the Durotar ridge's (region route Unreachable).
    [RealTerrainBotFact]
    public Task Walk_C3_ValleyOfTrialsRidge_ToChiefHawkwind() => WalkProbeAsync(1, new(-1.0f, -4188.3f, 79.4f), new(-2881.9f, -229.1f, 55.2f), 600_000);
}

public sealed class PlayerbotDeadEndPinWalkDurotarTests : RealTerrainNavigationTestBase
{
    public PlayerbotDeadEndPinWalkDurotarTests(ITestOutputHelper output) : base(output) { }

    [RealTerrainBotFact]
    public Task Walk_C3_Pin137_ToJenshan() => WalkProbeAsync(1, new(-137.6f, -4265.7f, 105.3f), new(-629f, -4228f, 38.4f));

    [RealTerrainBotFact]
    public Task Walk_C3_ValleyOfTrialsRidge_ToRazorHill() => WalkProbeAsync(1, new(-1.0f, -4188.3f, 79.4f), new(340.4f, -4686.3f, 16.5f));
}

public sealed class PlayerbotDeadEndPinWalkOtherTests : RealTerrainNavigationTestBase
{
    public PlayerbotDeadEndPinWalkOtherTests(ITestOutputHelper output) : base(output) { }

    [RealTerrainBotFact]
    public Task Walk_C3_Barrens_ToUkor() => WalkProbeAsync(1, new(-1173.6f, -3285.5f, 91.7f), new(-599.4f, -4715.3f, 35.2f));

    [RealTerrainBotFact]
    public Task Walk_C3_Coldridge_ToKharanosRoad() => WalkProbeAsync(0, new(-6494.1f, 324.9f, 406.0f), new(-5497.0f, -385.7f, 404.1f));
}

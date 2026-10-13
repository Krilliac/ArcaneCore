using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Bot QA cluster C3 on the real terrain (<c>ARCANECORE_TEST_TERRAIN_DIR</c>): the two pins that are fragments of mesh (the ridge pocket
/// above the Valley of Trials, -137.6,-4265.7,105.3 map 1: issues #379 and #402; the sliver inside Ironforge, -4715.7,-871.2,504.7 map
/// 0: #381 and #537) and the goal no walk reaches (Chief Hawkwind's platform, Mulgore, from the Durotar ridge: #418 and #461's quests).
/// Still open, NOT covered here: the Explore stalls at -1.0,-4188.3 (map 1), Coldridge -6494.1,324.9 (map 0) and the Barrens
/// -1173,-3285 (map 1), which the probes (<c>PlayerbotDeadEndPinProbeTests</c>) found on large connected regions where plans and walks work.
/// </summary>
public sealed partial class PlayerbotRealTerrainNavigationTests
{
    private static readonly Vector3 RidgePocket = new(-137.6f, -4265.7f, 105.3f), IronforgeSliver = new(-4715.7f, -871.2f, 504.7f);
    private static readonly Vector3 DurotarRidge = new(-1.0f, -4188.3f, 79.4f), ColdridgePin = new(-6494.1f, 324.9f, 406.0f), BarrensPin = new(-1173.6f, -3285.5f, 91.7f);
    private static readonly Vector3 ChiefHawkwind = new(-2881.9f, -229.1f, 55.2f), RazorHillInn = new(340.4f, -4686.3f, 16.5f);
    private static readonly Vector3 KharanosRoad = new(-5497.0f, -385.7f, 404.1f), JenshanSpot = new(-629f, -4228f, 38.4f);

    private async Task<Terrain> StartWithGraphAsync(uint map, Vector3 at)
    {
        Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(at, map);
        Task<NavRegionGraph?> build = await terrain.Host.OnWorldAsync(() =>
            ((NavMeshPathfinder)terrain.Player.Map!.Collision.Pathfinder).RegionGraphTask(map, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!);
        Assert.NotNull(await build);
        return terrain;
    }

    /// <summary>The probes' finding: the pocket and the sliver are fragments; the other three C3 pins are on large connected regions.</summary>
    [RealTerrainBotFact]
    public async Task TheTwoFragmentPins_AreFragments_AndTheOtherC3PinsAreNot()
    {
        foreach ((uint map, Vector3 pin, bool fragment) in new[]
            { (1u, RidgePocket, true), (0u, IronforgeSliver, true), (1u, DurotarRidge, false), (0u, ColdridgePin, false), (1u, BarrensPin, false) })
        {
            await using Terrain terrain = await StartWithGraphAsync(map, pin);
            Assert.Equal(fragment, await terrain.Host.OnWorldAsync(() => PlayerbotNavigation.IsOnMeshFragment(terrain.Player)));
        }
    }

    /// <summary>
    /// A bot on a fragment leaves it: the one straight hop of the plan when the ground beside it allows (walked to the goal), else the stall
    /// path moves it to its bind point (the island escape). Either way it ends on the mesh's large part.
    /// </summary>
    [RealTerrainBotFact]
    public Task TheRidgePocket_IsLeft_ByAHopOrTheBindPoint() => LeavesTheFragmentAsync(1, RidgePocket, JenshanSpot, RazorHillInn);

    [RealTerrainBotFact]
    public Task TheIronforgeSliver_IsLeft_ByAHopOrTheBindPoint() => LeavesTheFragmentAsync(0, IronforgeSliver, KharanosRoad, KharanosRoad);

    private async Task LeavesTheFragmentAsync(uint map, Vector3 pin, Vector3 goal, Vector3 bind)
    {
        await using Terrain terrain = await StartWithGraphAsync(map, pin);
        var options = new PlayerbotOptions { MaxPathPoints = 128, MaxRouteYards = 2000 };
        PlayerbotRoute? first = null;
        for (int think = 0; think < 40 && first is null; think++)
        {
            first = await terrain.Host.OnWorldAsync(() => PlayerbotNavigation.TryPlanToward(terrain.Player, goal, options, out PlayerbotRoute? planned, nowMs: terrain.Host.World.NowMs) ? planned : null);
            if (first is null) await terrain.Host.World.AdvanceClockAsync(50);
        }

        bool hop = first is { Navigated: false };
        output.WriteLine(first is null ? "no route from the fragment" : $"first route: {(hop ? "a hop" : "a mesh route")} to {first.Points[^1]} ({first.Distance:F1} yards)");
        if (hop)
        {
            try
            {
                float left = await terrain.WalkTowardAsync(goal, 5f, 240_000, output, options);
                output.WriteLine($"walked on, {left:F0} yards from the goal");
            }
            catch (Xunit.Sdk.XunitException problem)
            {
                output.WriteLine("the walk failed: " + problem.Message.Split('\n')[0]);
            }
        }

        if (await terrain.Host.OnWorldAsync(() => PlayerbotNavigation.IsOnMeshFragment(terrain.Player)))
        {
            // No hop, or the hop did not carry the bot off: the stall path (an unhomed bot with a bind point).
            var brain = new PlayerbotBrain(terrain.Session, new PlayerbotOptions { Enabled = true });
            await terrain.Host.OnWorldAsync(() =>
            {
                terrain.Player.Home = new HomeBind(map, 40, bind.X, bind.Y, bind.Z);
                brain.HoldGoalForTests(PlayerbotGoalKind.Quest, 266, 65);
                brain.GiveUpStalledGoal(terrain.Player, shareable: false);
            });
            await terrain.WaitAsync(() => Vector2.Distance(new(terrain.Player.X, terrain.Player.Y), new(bind.X, bind.Y)) < 5f, "the teleport off the fragment to the bind point");
            output.WriteLine("left by the bind point");
        }

        Assert.False(await terrain.Host.OnWorldAsync(() => PlayerbotNavigation.IsOnMeshFragment(terrain.Player)), "still on the fragment");
    }

    /// <summary>The stall path alone: a fragment bot whose goal is given up is moved to its bind point, as an island bot is (the hop found no ground).</summary>
    [RealTerrainBotFact]
    public async Task AStalledBotOnTheRidgePocket_GoesToItsBindPoint()
    {
        await using Terrain terrain = await StartWithGraphAsync(1, RidgePocket);
        var brain = new PlayerbotBrain(terrain.Session, new PlayerbotOptions { Enabled = true });
        await terrain.Host.OnWorldAsync(() =>
        {
            terrain.Player.Home = new HomeBind(1, 40, RazorHillInn.X, RazorHillInn.Y, RazorHillInn.Z);
            brain.HoldGoalForTests(PlayerbotGoalKind.Quest, 266, 65);
            brain.GiveUpStalledGoal(terrain.Player, shareable: false);
        });
        await terrain.WaitAsync(() => Vector2.Distance(new(terrain.Player.X, terrain.Player.Y), new(RazorHillInn.X, RazorHillInn.Y)) < 5f, "the bind point");
    }

    /// <summary>
    /// Chief Hawkwind's platform from the Durotar ridge (4,900 yards, a region no walk joins to the ridge's): told unreachable from the
    /// region graph with no path search, so the world destinations skip it. Before, it drew an 847-yard leg and a 3,349-yard walk. A goal
    /// the ridge does reach (Razor Hill, Jen'shan) is not.
    /// </summary>
    [RealTerrainBotFact]
    public async Task ChiefHawkwind_FromTheDurotarRidge_IsSkipped_WithoutASearch()
    {
        await using Terrain terrain = await StartWithGraphAsync(1, DurotarRidge);
        // The goal's navigation tile is read in the background the first time (the lookup answers "no region" until it is in): ask again.
        bool hawkwind = false, razor = true, jenshan = true;
        long searches = 0;
        for (int think = 0; think < 200 && !hawkwind; think++)
        {
            (hawkwind, razor, jenshan, searches) = await terrain.Host.OnWorldAsync(() =>
            {
                long before = PathSearchCounters.Current.Searches;
                bool a = PlayerbotNavigation.IsUnreachableOnFoot(terrain.Player, ChiefHawkwind, out _, out _);
                bool b = PlayerbotNavigation.IsUnreachableOnFoot(terrain.Player, RazorHillInn, out _, out _), c = PlayerbotNavigation.IsUnreachableOnFoot(terrain.Player, JenshanSpot, out _, out _);
                return (a, b, c, PathSearchCounters.Current.Searches - before);
            });
            Assert.Equal(0, searches);
            if (!hawkwind)
            {
                await Task.Delay(20);
                await terrain.Host.World.AdvanceClockAsync(100);
            }
        }
        Assert.True(hawkwind, "Chief Hawkwind is reachable on foot");
        Assert.False(razor);
        Assert.False(jenshan);
    }
}

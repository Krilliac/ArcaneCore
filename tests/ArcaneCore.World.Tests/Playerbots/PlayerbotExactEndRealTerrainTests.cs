using System.Numerics;
using System.Reflection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotExactEndRealTerrainTests : RealTerrainNavigationTestBase
{
    private readonly ITestOutputHelper _output;

    public PlayerbotExactEndRealTerrainTests(ITestOutputHelper output) : base(output) { _output = output; }

    /// <summary>
    /// Bot QA #784 (live 2026-10-17 05:01, bots Tupizeir, Mokou and Dreirndreido, each several times): the destination below the road west
    /// of Goldshire has no walkable polygon near it, and the mesh's exact answer ended on the road 12.4 yards away and 9 yards up. The exact
    /// plan took it, the bot walked to the road, stood out of reach and planned it again. An exact plan refuses such an answer now
    /// (<see cref="PlayerbotNavigationAnomalies.PathEndShortYards"/>); a plan toward the goal still closes on it as far as the mesh goes.
    /// </summary>
    [RealTerrainBotFact]
    public async Task AnExactPlan_ToAnOffMeshSpotBelowTheGoldshireRoad_IsRefused()
    {
        Vector3 bot = new(-9247.0f, 448.4f, 89.7f), destination = new(-9235.9f, 450.5f, 84.4f);
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(bot);
        (bool planned, PlayerbotRoute? route) = await terrain.Host.OnWorldAsync(() =>
        {
            bool found = PlayerbotNavigation.TryPlan(terrain.Player, destination, new PlayerbotOptions(), out PlayerbotRoute? exact);
            return (found, exact);
        });
        Assert.False(planned, $"the exact plan took a route ending at {route?.Points[^1]}, "
            + $"{(route is null ? 0 : Vector2.Distance(new(route.Points[^1].X, route.Points[^1].Y), new(destination.X, destination.Y))):F1} yd (flat) from its destination");
    }

    /// <summary>The same rule keeps an exact plan whose destination is on the mesh: Westfall's road to Sentinel Hill.</summary>
    [RealTerrainBotFact]
    public async Task AnExactPlan_ToAnOnMeshSpot_StillReachesIt()
    {
        Vector3 bot = new(-10559.0f, 1206.9f, 31.8f), destination = new(-10532.3f, 1191.2f, 35.5f);
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(bot);
        (bool planned, PlayerbotRoute? route) = await terrain.Host.OnWorldAsync(() =>
        {
            bool found = PlayerbotNavigation.TryPlan(terrain.Player, destination, new PlayerbotOptions(), out PlayerbotRoute? exact);
            return (found, exact);
        });
        Assert.True(planned && route is not null, "no exact route to a point on the mesh");
        Assert.True(Vector3.Distance(route!.Points[^1], destination) <= 2f, $"the exact route ends at {route.Points[^1]}");
    }

    /// <summary>
    /// Bot QA #1464, Feralas Twin Colossals (map 1, -4915.7, 1722.9, 69.7): broken steep ground, the main walking component (12764) and plateau
    /// components cut off by excluded slopes. A bot exploring there must not stand refused until the 120 s stall: the component pre-check turns
    /// rays away from other components, every refusal is traced with its reason, and the bot walks within a minute of game time.
    /// </summary>
    [RealTerrainBotFact]
    public async Task ExploringAtTheFeralasTwinColossals_WalksOn_AndTracesTheRefusalReasons()
    {
        Vector3 spot = new(-4915.7f, 1722.9f, 69.7f);
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(spot, map: 1);
        var nav = Assert.IsType<NavMeshPathfinder>(await terrain.Host.OnWorldAsync(() => WorldCollision.Of(terrain.Host.World).Pathfinder));
        Task<NavRegionGraph?> building = nav.RegionGraphTask(1, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!;
        Assert.NotNull(await building);
        var brain = new PlayerbotBrain(terrain.Session, new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } });
        try
        {
            MethodInfo explore = typeof(PlayerbotBrain).GetMethod("Explore", BindingFlags.Instance | BindingFlags.NonPublic)!;
            float farthest = 0f;
            string trace = "";
            for (int think = 0; think < 120; think++)
            {
                (float away, trace) = await terrain.Host.OnWorldAsync(() =>
                {
                    Player player = terrain.Player;
                    terrain.Session.ManagedBudget = new ManagedActionBudget(4);
                    explore.Invoke(brain, [player, 500u]);
                    PlayerbotMotion.ElapseForTests(player, 500);
                    return (Vector2.Distance(new(spot.X, spot.Y), new(player.X, player.Y)), brain.FormatDiagnosticSnapshot(player));
                });
                farthest = MathF.Max(farthest, away);
                await terrain.Host.World.AdvanceClockAsync(500);
            }

            Log($"farthest {farthest:F1} yd; rotations {brain.WanderComponentRotations}; region legs {brain.WanderRegionLegs}; plans {brain.ExploreWanderPlans}; {trace}");
            Assert.True(brain.WanderComponentRotations > 0, "no ray was turned away from another component here: the test proves nothing about the pre-check");
            Assert.True(farthest > PlayerbotStallWatch.ProgressYards, $"the bot never got {PlayerbotStallWatch.ProgressYards} yd from the spot in a minute ({trace})");
        }
        finally { brain.Stop(); }
    }

    private void Log(string line) => _output.WriteLine(line);
}

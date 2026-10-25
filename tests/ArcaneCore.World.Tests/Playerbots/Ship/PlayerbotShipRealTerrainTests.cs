using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Ship;

/// <summary>
/// The ship docks on the real terrain, navigation meshes and models (skipped without <c>ARCANECORE_TEST_TERRAIN_DIR</c>, never a silent pass).
/// </summary>
public sealed class PlayerbotShipRealTerrainTests : RealTerrainNavigationTestBase
{
    public PlayerbotShipRealTerrainTests(ITestOutputHelper output) : base(output) { }

    // Grom'gol Base Camp (map 0): the zeppelin tower's stairs where bots stop (#1456 class 3), the ground under the tower, and the TaxiPathNode
    // of zeppelin 175080 (its platform floor lies at about z 31.3, the node 18 yards above it).
    private static readonly Vector3 TowerStairs = new(-12432.6f, 204.8f, 26.0f);
    private static readonly Vector3 GroundUnderTower = new(-12400f, 180f, 3f);
    private static readonly Vector3 GromGolPort = new(-12464.0f, 231.6f, 49.5f);

    /// <summary>
    /// Records what the dock logic sees at Grom'gol: the dock floor <see cref="PlayerbotShip.ResolveDockTarget"/> picks and the plan to it, from
    /// the stairs (which must count as docked: a refused plan there with the floor under the bot's feet is accepted by
    /// <see cref="PlayerbotShip.TryAcceptRelaxedDock"/>) and from the ground, which the dock height keeps out of the dock.
    /// </summary>
    [RealTerrainBotFact]
    public async Task AtGromGol_TheDockFloorAndThePlansToIt_FromTheStairsAndFromTheGround()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        foreach ((string name, Vector3 from) in new[] { ("stairs", TowerStairs), ("ground", GroundUnderTower) })
        {
            await terrain.PlaceAsync(from);
            Vector3 target = Vector3.Zero;
            PlanOutcome outcome = PlanOutcome.Pending;
            PlayerbotRoute? route = null;
            var wait = new TileWait[] { new() };
            for (int i = 0; i < 200 && outcome == PlanOutcome.Pending; i++)
            {
                (target, outcome, route) = await terrain.Host.OnWorldAsync(() =>
                {
                    Vector3 t = PlayerbotShip.ResolveDockTarget(terrain.Player.Map!, GromGolPort, from);
                    PlanOutcome o = PlayerbotNavigation.PlanToward(terrain.Player, t, new PlayerbotOptions(), ref wait[0], terrain.Host.World.NowMs, out PlayerbotRoute? r);
                    return (t, o, r);
                });
                if (outcome == PlanOutcome.Pending)
                {
                    await Task.Delay(20);
                    await terrain.Host.World.AdvanceClockAsync(100);
                }
            }

            float flat = Vector2.Distance(new(from.X, from.Y), new(GromGolPort.X, GromGolPort.Y));
            output.WriteLine($"{name}: bot {from} flat-to-port {flat:F1}; dock target {target} (dz to bot {target.Z - from.Z:F1}, flat to port "
                + $"{Vector2.Distance(new(target.X, target.Y), new(GromGolPort.X, GromGolPort.Y)):F1}); PlanToward {outcome}; route end {(route is null ? "none" : route.Points[^1].ToString())}");
            Assert.NotEqual(PlanOutcome.Pending, outcome);
        }
    }

    // The top of Teldrassil (map 1) and the Auberdine port of boat 176310 (map 1, z 0 in the harbour water, 2 800 yards south-west across the sea).
    private static readonly Vector3 TeldrassilTop = new(9053.6f, 1599.0f, 1335.4f);
    private static readonly Vector3 AuberdinePort = new(6406.2f, 823.1f, 0f);

    /// <summary>
    /// The top of Teldrassil does not "walk" to Auberdine's port: the port's tile was never read, it placed nowhere and counted as joined to
    /// every place of the map (the planner then took the Rut'theran boat and back). Placed with blocking reads, the port lies on the
    /// mainland near Auberdine, a different component. The Grom'gol tower stairs still walk to the tower's node (the route is not lost).
    /// </summary>
    [RealTerrainBotFact]
    public async Task TheTopOfTeldrassil_IsNotJoinedByFootToAuberdinesPort_AndTheGromGolStairsAreJoinedToTheirTower()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(TeldrassilTop, map: 1);
        var nav = (ArcaneCore.Game.Maps.Collision.MMaps.NavMeshPathfinder)await terrain.Host.OnWorldAsync(() => terrain.Player.Map!.Collision.Pathfinder);
        Task<ArcaneCore.Game.Maps.Collision.MMaps.NavRegionGraph?> build = nav.RegionGraphTask(1, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!;
        await build.WaitAsync(TimeSpan.FromMinutes(5));
        var auberdine = new TravelPlace(1, AuberdinePort);
        var tower = new TravelPlace(0, GromGolPort);
        ShipRoute[] ships = [new(176310, TravelMeans.Boat, TravelAccess.Both, new TravelPlace(0, new Vector3(-3900f, -750f, 0f)), auberdine, 300f), new(175080, TravelMeans.Zeppelin, TravelAccess.Both, tower, new TravelPlace(0, new Vector3(1600f, 400f, 0f)), 300f)];
        (bool top, int[] components) = await terrain.Host.OnWorldAsync(() =>
        {
            Func<TravelPlace, TravelPlace, bool> walkable = PlayerbotTravel.WalkableFor(terrain.Player, ships);
            return (walkable(TravelPlace.Of(terrain.Player), auberdine),
                PlayerbotTravel.PortComponents(nav, build.Result!, auberdine));
        });
        output.WriteLine($"top of Teldrassil walks to Auberdine's port: {top}; the port's components: [{string.Join(", ", components)}]");
        Assert.False(top);
        Assert.NotEmpty(components);

        await terrain.PlaceAsync(TowerStairs);
        var nav0 = (ArcaneCore.Game.Maps.Collision.MMaps.NavMeshPathfinder)await terrain.Host.OnWorldAsync(() => terrain.Player.Map!.Collision.Pathfinder);
        await nav0.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!.WaitAsync(TimeSpan.FromMinutes(5));
        (bool stairs, int[] towerComponents) = await terrain.Host.OnWorldAsync(() =>
            (PlayerbotTravel.WalkableFor(terrain.Player, ships)(TravelPlace.Of(terrain.Player), tower),
                PlayerbotTravel.PortComponents(nav0, nav0.RegionGraph(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!, tower)));
        output.WriteLine($"Grom'gol stairs walk to the tower's node: {stairs}; the node's components: [{string.Join(", ", towerComponents)}]");
        Assert.True(stairs);
    }
}

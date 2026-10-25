using System.Numerics;
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
}

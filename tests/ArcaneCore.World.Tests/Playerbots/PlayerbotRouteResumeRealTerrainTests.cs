using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A held mesh route resumed off the leg it walks next, on the real terrain (#776). Needs the terrain extraction
/// (<see cref="RealTerrainBotFactAttribute"/>); without it the test is reported Skipped, which is not a pass.
/// </summary>
public sealed class PlayerbotRouteResumeRealTerrainTests : RealTerrainNavigationTestBase
{
    public PlayerbotRouteResumeRealTerrainTests(ITestOutputHelper output) : base(output) { }

    [RealTerrainBotFact]
    public async Task InIronforge_AMeshRouteResumedOffItsLeg_GoesBackOntoItAroundTheWall_NotThroughIt()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(IronforgeResume);
        using AnomalyCapture capture = await terrain.Host.OnWorldAsync(() => BotAnomalyCapture.For(terrain.Player));
        var options = new PlayerbotOptions();
        var route = new PlayerbotRoute([IronforgeLegStart, IronforgeLegEnd], Vector3.Distance(IronforgeLegStart, IronforgeLegEnd), navigated: true);
        Assert.True(await terrain.Host.OnWorldAsync(() =>
        {
            var here = new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z);
            return PlayerbotMotion.OffRoute(route, here) && !PlayerbotMoveCheck.LineClear(terrain.Player.Map!.Collision, here, IronforgeLegEnd);
        }), "the bot does not stand off the leg behind a wall: the scenario proves nothing");

        // Every heartbeat is put to the crossing check (the live one samples once a second).
        for (uint elapsed = 0; elapsed <= 8000; elapsed += 100)
        {
            bool moving = await terrain.Host.OnWorldAsync(() =>
            {
                terrain.Session.ManagedBudget = new ManagedActionBudget(4);
                return PlayerbotNavigation.TryAdvance(terrain.Session, route, options, 100, terrain.Host.World.NowMs);
            });
            if (!moving) break;
            for (int half = 0; half < 2; half++)
            {
                await terrain.Host.World.AdvanceClockAsync(50);
                await terrain.Host.OnWorldAsync(() =>
                {
                    Player player = terrain.Player;
                    var before = new Vector3(player.X, player.Y, player.Z);
                    int next = route.NextPoint;
                    PlayerbotMotion.Pump(terrain.Session, player, terrain.Host.World.NowMs);
                    PlayerbotMotionAnomalies.CheckCrossing(player, player.Map!, new PlayerbotMotionAnomalies.MotionWatch(), route, next,
                        route.NextPoint, before, new Vector3(player.X, player.Y, player.Z), terrain.Host.World.NowMs);
                });
            }
        }

        output.WriteLine($"route: {string.Join(" ", route.Points)}");
        Assert.False(capture.Any(PlayerbotMotionAnomalies.Key(PlayerbotMotionAnomalies.Code.WallCrossed)), capture.Describe());
        Assert.False(capture.Any(PlayerbotMotionAnomalies.Key(PlayerbotMotionAnomalies.Code.DoorCrossed)), capture.Describe());
        float left = await terrain.Host.OnWorldAsync(() => Vector2.Distance(new(terrain.Player.X, terrain.Player.Y), new(IronforgeLegEnd.X, IronforgeLegEnd.Y)));
        Assert.True(left < 2f, $"stopped {left:F1} yards from the leg's corner: the route was not walked back onto");
    }

    // A navigated leg in Ironforge and a spot four yards off it, behind the wall the leg goes around: where a bot of the
    // travel/detour/resume probe resumed its held route and walked straight through the wall to the corner (#776).
    private static readonly Vector3 IronforgeLegStart = new(-4612.1f, -899.3f, 502.5f);
    private static readonly Vector3 IronforgeLegEnd = new(-4584.0f, -909.3f, 506.3f);
    private static readonly Vector3 IronforgeResume = new(-4599.8f, -899.9f, 502.8f);
}

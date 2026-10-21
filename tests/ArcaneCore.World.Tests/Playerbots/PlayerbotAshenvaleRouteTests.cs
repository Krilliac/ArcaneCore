using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Bot QA #199: a bot stalled at 2770.4, 1275.2, 319.1 on a ridge in Ashenvale with quest 6562 completed (ender Je'neu Sancrea, 633
/// yards away across steep ground). On the real mesh the ridge is not an island (probes to the south-west lead 60 yards and more; the
/// ones to the north and east end at once or find no polygon), so the bot is free to walk off the ridge toward other goals once the
/// unreachable quest is set aside (PlayerbotQuestReturnTests cover that decision through the brain).
/// </summary>
public sealed class PlayerbotAshenvaleRouteTests : RealTerrainNavigationTestBase
{
    public PlayerbotAshenvaleRouteTests(ITestOutputHelper output) : base(output) { }

    private static readonly Vector3 AshenvaleStall = new(2770.4f, 1275.2f, 319.1f);
    private static readonly Vector3 JeneuSancrea = new(3355.07f, 1033.14f, 6.03463f);

    [RealTerrainBotFact]
    public async Task Quest6562_FromAshenvaleRidge_TheClimbToTheEnderIsMeasuredOnSteepLegs()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(AshenvaleStall, 1);
        var options = new PlayerbotOptions { MaxPathPoints = 128, MaxRouteYards = 2000 };
        bool pending = true;
        for (int attempt = 0; attempt < 50 && pending; attempt++)
        {
            pending = await terrain.Host.OnWorldAsync(() =>
            {
                long mark = PlayerbotNavigation.PendingMark;
                _ = PlayerbotNavigation.TryPlanToward(terrain.Player, JeneuSancrea, options, out _);
                _ = PlayerbotNavigation.RequiresSteepAscent(terrain.Player, JeneuSancrea, options, out _);
                return PlayerbotNavigation.PendingSince(mark);
            });
            if (pending)
            {
                await Task.Delay(20);
                await terrain.Host.World.AdvanceClockAsync(100);
            }
        }

        await terrain.Host.OnWorldAsync(() =>
        {
            var map = terrain.Player.Map!;
            Assert.False(PlayerbotNavigation.TryPlanToward(terrain.Player, JeneuSancrea, options, out _));
            Assert.True(PlayerbotNavigation.RequiresSteepAscent(terrain.Player, JeneuSancrea, options, out float ascent));
            output.WriteLine($"steep ascent {ascent:F1} yards within the diagnosis budget {PlayerbotNavigation.DiagnosisSearchNodes}");
            Assert.True(ascent >= PlayerbotNavigation.SteepAscentYards);

            // A goal the ordinary mesh reaches from the same ridge is no steep ascent (south-west, 40 yards down the slope).
            Vector3 down = new(AshenvaleStall.X - 40f, AshenvaleStall.Y, 0);
            down.Z = map.Collision.GetHeight(down.X, down.Y, AshenvaleStall.Z + 50f);
            Assert.True(PlayerbotNavigation.TryPlanToward(terrain.Player, down, options, out _));
            Assert.False(PlayerbotNavigation.RequiresSteepAscent(terrain.Player, down, options, out _));
            return true;
        });
    }

    [RealTerrainBotFact]
    public async Task AtTheAshenvaleStall_TheRidgeIsNoMeshIsland_AndTheBrainWalksTheBotOff()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(AshenvaleStall, 1);
        var options = new PlayerbotOptions { Enabled = true };
        Assert.False(await terrain.Host.OnWorldAsync(() => PlayerbotNavigation.IsOnMeshIsland(terrain.Player, options)));
        var brain = new PlayerbotBrain(terrain.Session, options);
        float farthest = 0;
        for (int think = 0; think < 240 && farthest < 40f; think++)
        {
            farthest = await terrain.Host.OnWorldAsync(() =>
            {
                PlayerbotMovementControl.Update(terrain.Session, terrain.Player);
                terrain.Session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);
                return MathF.Max(farthest, Vector2.Distance(new(terrain.Player.X, terrain.Player.Y), new(AshenvaleStall.X, AshenvaleStall.Y)));
            });
            for (int tick = 0; tick < 10; tick++)
            {
                await terrain.Host.World.AdvanceClockAsync(50);
                await terrain.Host.OnWorldAsync(() => PlayerbotMotion.Pump(terrain.Session, terrain.Player, terrain.Host.World.NowMs));
            }
        }

        await terrain.Host.OnWorldAsync(() => brain.Stop());
        output.WriteLine($"farthest from the stall: {farthest:F1} yards; goal {brain.Goal}");
        Assert.True(farthest >= 40f, $"the bot stayed within {farthest:F1} yards of the ridge point; goal {brain.Goal}");
    }
}

using ArcaneCore.Game.Creatures;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// <see cref="RiskTestWorld"/>'s set-up must not move game time: a test that pins the clock (<c>clockStartMs</c>) relies on the first think
/// landing where it put it. <see cref="RiskTestWorld.SeeAsync"/> used to advance a think's 100 ms whenever its first look found the spawn not
/// yet visible, which depended on whether its check reached the world thread before or after that tick's map update (hosted CI: the kite
/// test's phase 350 started at 50).
/// </summary>
public sealed class RiskTestWorldClockTests
{
    private const uint MobEntry = 992_620;

    /// <summary>Spawn and see, many times over: game time stays where the manual clock started.</summary>
    [Fact]
    public async Task SeeAsync_ShowsASpawn_WithoutMovingGameTime()
    {
        RiskTestWorld world = await RiskTestWorld.StartAsync(clockStartMs: 10_000);
        await using (world)
        {
            for (int i = 0; i < 60; i++)
            {
                float dx = 3f + (i % 10), dy = -5f + (i / 10);
                Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(MobEntry), dx, dy));
                await world.SeeAsync(mob);
                Assert.Equal(10_000u, await world.OnWorldAsync(() => world.Session.World.NowMs));
            }
        }
    }
}

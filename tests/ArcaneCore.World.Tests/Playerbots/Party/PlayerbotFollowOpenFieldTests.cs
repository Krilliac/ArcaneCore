using System.Numerics;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// The catch-up must not change an ordinary follow: on open ground in Elwynn Forest, east of Goldshire, a follower 30 yards from its
/// master walks to him in every mode, without a teleport or a drop.
/// </summary>
public sealed class PlayerbotFollowOpenFieldTests(ITestOutputHelper output)
{
    [RealTerrainBotTheory]
    [InlineData(PlayerbotCatchUpMode.Teleport)]
    [InlineData(PlayerbotCatchUpMode.FightThrough)]
    [InlineData(PlayerbotCatchUpMode.Auto)]
    public async Task AnOpenFieldFollow_WalksAndNeverTeleports(PlayerbotCatchUpMode mode)
    {
        await using FollowTerrain world = await FollowTerrain.StartAsync(o => o.Follow.CatchUpMode = mode);
        await world.PlaceBotAsync(new Vector3(-9300f, -50f, 200f));
        (Vector3 bot, Vector3 master) = await world.Host.OnWorldAsync(() =>
        {
            var collision = world.Bot.Map!.Collision;
            Vector3 at = FollowTerrain.Position(world.Bot) with { Z = collision.GetHeight(-9300f, -50f, 200f) };
            return (at, new Vector3(-9272f, -40f, collision.GetHeight(-9272f, -40f, 200f)));
        });
        await world.PlaceBotAsync(bot);
        await world.PlaceMasterAsync(master);

        FollowRun run = await world.RunAsync(15_000, stop: r => r.ArrivedMs >= 0);
        output.WriteLine($"{bot} -> {master}: {run}");

        Assert.True(run.ArrivedMs >= 0, $"the follower never stood beside its master: {run}");
        Assert.Equal(0, run.Teleports);
        Assert.Equal(0, run.DropsTaken);
        Assert.Empty(run.Jumps);
        Assert.InRange(run.Walked, 20f, 45f);
    }

    /// <summary>
    /// Loop detection: a follower that cannot get any closer to a master standing still (here: rooted) re-plans after
    /// NoProgressSeconds, and after the same again gives up walking: Teleport mode teleports (reason Stalled), FightThrough keeps
    /// re-planning and never teleports.
    /// </summary>
    [RealTerrainBotTheory]
    [InlineData(PlayerbotCatchUpMode.Teleport)]
    [InlineData(PlayerbotCatchUpMode.FightThrough)]
    public async Task AFollowerThatGetsNoCloser_RePlans_ThenTeleportsOnlyInTeleportMode(PlayerbotCatchUpMode mode)
    {
        await using FollowTerrain world = await FollowTerrain.StartAsync(o =>
        {
            o.Follow.CatchUpMode = mode;
            o.Follow.NoProgressSeconds = 4;
        });
        await world.PlaceBotAsync(new Vector3(-9300f, -50f, 200f));
        (Vector3 bot, Vector3 master) = await world.Host.OnWorldAsync(() =>
        {
            MapCollision collision = world.Bot.Map!.Collision;
            return (new Vector3(-9300f, -50f, collision.GetHeight(-9300f, -50f, 200f)), new Vector3(-9272f, -40f, collision.GetHeight(-9272f, -40f, 200f)));
        });
        await world.PlaceBotAsync(bot);
        await world.PlaceMasterAsync(master);
        await world.Host.OnWorldAsync(() => MovementControl.Request(world.Bot, MovementChangeType.Root, true));

        FollowRun run = await world.RunAsync(20_000, stop: r => r.Teleports > 0);
        output.WriteLine($"{mode}: {run}");

        Assert.True(run.Replans >= 1, $"the follower never re-planned: {run}");
        if (mode == PlayerbotCatchUpMode.Teleport)
        {
            Assert.Equal(1, run.Teleports);
            Assert.Equal(PlayerbotCatchUpReason.Stalled, run.CatchUp);
        }
        else
        {
            Assert.True(run.Replans >= 3, $"the follower stopped re-planning: {run}");
            Assert.Equal(0, run.Teleports);
            Assert.True(run.MasterDistance > 20f, $"a rooted follower moved: {run}");
        }
    }
}

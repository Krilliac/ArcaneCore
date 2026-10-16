using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Ship;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// A journey rests a bot low on health between two legs (<see cref="PlayerbotTravel.IsResting"/>): the brain sits it down (no food in its
/// bags) and the next leg waits; recovered, the bot stands and the leg begins. In the middle of a leg the bot is never held for a rest.
/// On <see cref="ShipTestWorld"/>'s walk, boat and walk to map 1, driven by the brain alone.
/// </summary>
public sealed class TravelJourneyRestTests(ITestOutputHelper output)
{
    private static Vector3 At(Player p) => new(p.X, p.Y, p.Z);

    private static async Task ThinkAsync(ShipTestWorld world, PlayerbotBrain brain, int thinks)
    {
        for (int i = 0; i < thinks; i++)
        {
            await world.OnWorldAsync(() => brain.Update(100));
            await world.AdvanceAsync(50);
            await world.OnWorldAsync(() => PlayerbotMotion.Pump(world.Session, world.Player, world.Host.World.NowMs));
            await world.AdvanceAsync(50);
            await world.OnWorldAsync(() => PlayerbotMotion.Pump(world.Session, world.Player, world.Host.World.NowMs));
        }
    }

    [Fact]
    public async Task ABotLowOnHealth_RestsBeforeTheNextLeg_ThenWalksOn_ButIsNotHeldInTheMiddleOfALeg()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        await world.PlaceAsync(0, ShipTestWorld.Start);
        PlayerbotOptions options = TravelJourneyShipTests.Options();
        var brain = new PlayerbotBrain(world.Session, options);
        var travel = new PlayerbotTravel(options);
        try
        {
            // Low before the first leg: the journey rests, the brain sits the bot down, and it does not walk.
            Vector3 before = await world.OnWorldAsync(() =>
            {
                world.Player.Health = world.Player.MaxHealth * 3 / 10;
                Assert.True(travel.TryBegin(world.Session, world.Player, 1, TravelJourneyShipTests.Goal, out string? why), why);
                return At(world.Player);
            });
            await ThinkAsync(world, brain, 10);
            (bool resting, bool started, StandState stand, PlayerbotGoalKind goal, Vector3 now) = await world.OnWorldAsync(() =>
                (PlayerbotTravel.IsResting(world.Player), PlayerbotTravel.JourneyOf(world.Player)!.LegStarted, world.Player.StandState, brain.Goal,
                    At(world.Player)));
            output.WriteLine($"resting {resting} started {started} stand {stand} goal {goal} moved {Vector3.Distance(before, now):F2}");
            Assert.True(resting, "the journey did not rest a bot at 30% health between legs");
            Assert.False(started, "the leg began while the bot rested");
            Assert.Equal(PlayerbotGoalKind.Rest, goal);
            Assert.Equal(StandState.Sit, stand);
            Assert.True(Vector3.Distance(before, now) < 0.5f, "the bot walked during the rest");

            // Recovered: it stands up, and the walk to the dock begins.
            await world.OnWorldAsync(() => world.Player.Health = world.Player.MaxHealth);
            await ThinkAsync(world, brain, 20);
            (resting, started, stand, _, now) = await world.OnWorldAsync(() =>
                (PlayerbotTravel.IsResting(world.Player), PlayerbotTravel.JourneyOf(world.Player)!.LegStarted, world.Player.StandState, brain.Goal,
                    At(world.Player)));
            Assert.False(resting);
            Assert.True(started, "the leg did not begin after the rest");
            Assert.Equal(StandState.Stand, stand);
            Assert.True(Vector3.Distance(before, now) > 3f, "the bot did not walk on after the rest");

            // Low again in the middle of the walk: no rest, it walks on.
            Vector3 mid = await world.OnWorldAsync(() =>
            {
                world.Player.Health = world.Player.MaxHealth * 3 / 10;
                return At(world.Player);
            });
            await ThinkAsync(world, brain, 10);
            (resting, _, stand, _, now) = await world.OnWorldAsync(() =>
                (PlayerbotTravel.IsResting(world.Player), PlayerbotTravel.JourneyOf(world.Player)!.LegStarted, world.Player.StandState, brain.Goal,
                    At(world.Player)));
            Assert.False(resting, "the journey held the bot for a rest in the middle of a leg");
            Assert.Equal(StandState.Stand, stand);
            Assert.True(Vector3.Distance(mid, now) > 3f, "the bot stopped walking in the middle of a leg");
        }
        finally
        {
            brain.Stop();
        }
    }
}

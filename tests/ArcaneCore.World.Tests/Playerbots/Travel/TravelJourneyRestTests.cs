using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
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

    /// <summary>
    /// The rest ends at a think whose action budget is spent: the stand (CMSG_STANDSTATECHANGE) is refused, and the next leg does not begin
    /// with the bot seated; the next think sends the stand again, the bot stands, and the leg begins. The journey alone is driven, each think
    /// with the budget given.
    /// </summary>
    [Fact]
    public async Task AStandRefusedWhenTheRestEnds_IsSentAgain_AndTheNextLegDoesNotBeginSeated()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        await world.PlaceAsync(0, ShipTestWorld.Start);
        PlayerbotOptions options = TravelJourneyShipTests.Options();
        var travel = new PlayerbotTravel(options);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            void Think(int budget)
            {
                world.Session.ManagedBudget = new ManagedActionBudget(budget);
                PlayerbotTravel.Drive(world.Session, player, options, 100, out _);
            }

            player.Health = player.MaxHealth * 3 / 10;
            Assert.True(travel.TryBegin(world.Session, player, 1, TravelJourneyShipTests.Goal, out string? why), why);
            TravelJourney journey = PlayerbotTravel.JourneyOf(player)!;
            Think(4);
            Assert.True(PlayerbotTravel.IsResting(player), "the journey did not rest a bot at 30% health");

            // Seated as the brain's rest seats it (the real handler).
            world.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.True(world.Session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes((uint)StandState.Sit)));
            Assert.Equal(StandState.Sit, player.StandState);

            // Recovered, at a think with no action left: the stand is refused, and the leg waits.
            player.Health = player.MaxHealth;
            Think(0);
            Assert.False(PlayerbotTravel.IsResting(player));
            Assert.Equal(StandState.Sit, player.StandState);
            Assert.False(journey.LegStarted, "the leg began with the bot seated");
            Assert.Equal(JourneyState.Active, journey.State);

            // The next think stands the bot, and the leg begins.
            Think(4);
            Assert.Equal(StandState.Stand, player.StandState);
            Assert.True(journey.LegStarted, "the leg did not begin once the bot stood");
            Assert.False(PlayerbotTravel.IsResting(player));
            return 0;
        });
    }
}

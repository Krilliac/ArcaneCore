using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Taxi;

/// <summary>
/// A journey that runs past its bound (<see cref="TravelJourney.DeadlineMs"/>, <see cref="PlayerbotTravel.EnforceDeadline"/>) drops the taxi
/// trip its walk leg began while that trip is still on the ground (<see cref="TaxiStage.ToMaster"/>): the taxi driver, which the brain runs
/// after the journey, then has nothing to fly, so no fare is paid for a journey that is over.
/// </summary>
public sealed partial class PlayerbotTaxiTests
{
    [Fact]
    public async Task AnOverdueJourney_DropsItsWalkLegsTaxiTripBeforeTheFlight_SoNoFareIsPaid()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            WorldCollision.Of(rig.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            var travel = new PlayerbotTravel(options);
            Assert.True(travel.TryBegin(rig.Session, player, 0, rig.Near(2), out string? why), why);
            TravelJourney journey = PlayerbotTravel.JourneyOf(player)!;

            // The journey's walk leg begins the taxi trip (the journey alone thinks, so the taxi driver has not asked for the flight yet).
            for (int think = 0; think < 4 && PlayerbotTaxi.TripOf(player) is null; think++)
            {
                rig.Session.ManagedBudget = new ManagedActionBudget(4);
                PlayerbotTravel.Drive(rig.Session, player, options, 500, out _);
            }

            TaxiTrip trip = Assert.IsType<TaxiTrip>(PlayerbotTaxi.TripOf(player));
            Assert.Equal(TaxiStage.ToMaster, trip.Stage);
            Assert.True(PlayerbotTaxi.IsActive(player));
            Assert.False(PlayerbotTaxi.IsFlying(player));
            Assert.Equal(1000u, player.Money);

            // Past its bound: the journey fails, and the trip on the ground goes with it.
            journey.DeadlineMs = unchecked(rig.World.NowMs - 1);
            PlayerbotTravel.EnforceDeadline(rig.Session, player);
            Assert.Equal(JourneyState.Failed, journey.State);
            Assert.False(PlayerbotTaxi.IsActive(player), "the overdue journey left its walk leg's taxi trip to fly");
            Assert.Null(PlayerbotTaxi.TripOf(player));

            // The brain's thinks after it (the journey's answer, then the taxi driver): nothing flies, nothing is paid.
            for (int think = 0; think < 4; think++) Think(rig, player, options);
            Assert.False(PlayerbotTaxi.IsFlying(player), "a flight was taken for a journey that is over");
            Assert.Equal(1000u, player.Money);
            Assert.Equal(0u, journey.SpentCopper);
            return 0;
        });
    }
}

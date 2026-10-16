using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Taxi;

/// <summary>
/// A journey's spend cap (<see cref="PlayerbotTravelOptions.MaxTravelSpendCopper"/>) holds for the flights its walk legs take
/// (<see cref="JourneyWalk"/> through <see cref="PlayerbotTaxi.TryTravelByTaxi(WorldSession, Player, uint, Vector3, PlayerbotOptions, uint)"/>):
/// the whole journey, every plan of it, spends at most the cap, counted from the fares the server really took
/// (<see cref="TravelJourney.SpentCopper"/>).
/// </summary>
public sealed partial class PlayerbotTaxiTests
{
    private static PlayerbotOptions SpendOptions(uint cap)
    {
        PlayerbotOptions options = Options();
        options.Travel.MaxTravelSpendCopper = cap;
        return options;
    }

    /// <summary>One journey think and one taxi think, each with a fresh action budget, as the brain runs them (world thread).</summary>
    private static void Think(Rig rig, Player player, PlayerbotOptions options)
    {
        rig.Session.ManagedBudget = new ManagedActionBudget(4);
        PlayerbotTravel.Drive(rig.Session, player, options, 500, out _);
        rig.Session.ManagedBudget = new ManagedActionBudget(4);
        PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _);
    }

    [Fact]
    public async Task AJourneyWithASpendCapOfZero_NeverFliesItsWalkLeg_WhateverTheBotsMoney()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            WorldCollision.Of(rig.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = SpendOptions(0);
            var travel = new PlayerbotTravel(options);
            Assert.True(travel.TryBegin(rig.Session, player, 0, rig.Near(2), out string? why), why);
            Assert.DoesNotContain(TravelMeans.FlightPath, PlayerbotTravel.JourneyOf(player)!.Planned.Legs.Select(l => l.Means));

            for (int think = 0; think < 10; think++)
            {
                Think(rig, player, options);
                Assert.False(PlayerbotTaxi.IsActive(player), $"think {think}: the walk leg took a flight with nothing left to spend");
            }

            Assert.Null(PlayerbotTaxi.TripOf(player));
            Assert.Equal(1000u, player.Money);
            Assert.Equal(0u, PlayerbotTravel.JourneyOf(player)!.SpentCopper);
            return 0;
        });
    }

    [Fact]
    public async Task AJourney_ChargesTheFareItFlew_AndAfterwardsNeitherFliesNorPlansPastWhatTheCapLeaves()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            WorldCollision.Of(rig.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            Vector3 start = new(player.X, player.Y, player.Z);
            PlayerbotOptions options = SpendOptions(150);
            var travel = new PlayerbotTravel(options);
            Assert.True(travel.TryBegin(rig.Session, player, 0, rig.Near(2), out string? why), why);

            // The first flight (fare 100) fits the cap of 150 and is flown: the journey charges it once the server took it.
            for (int think = 0; think < 4 && !PlayerbotTaxi.IsFlying(player); think++) Think(rig, player, options);
            Assert.True(PlayerbotTaxi.IsFlying(player), "the walk leg did not fly");
            Assert.Equal(900u, player.Money);
            TravelJourney journey = PlayerbotTravel.JourneyOf(player)!;
            Think(rig, player, options);
            Assert.Equal(100u, journey.SpentCopper);
            Assert.Equal(50u, journey.SpendLeft(options.Travel));
            rig.FlyUntilLanded(player);
            Think(rig, player, options);
            TaxiTrip first = PlayerbotTaxi.TripOf(player)!;
            Assert.Equal(TaxiStage.Done, first.Stage);

            // Put back at the start (as a failed leg would leave it): the same flight would cost 100 again, more than the 50 left. The walk
            // walks, and a replan's facts plan with what is left.
            player.Relocate(start.X, start.Y, start.Z, 0f, rig.World.NowMs);
            for (int think = 0; think < 10; think++)
            {
                Think(rig, player, options);
                Assert.True(ReferenceEquals(first, PlayerbotTaxi.TripOf(player)), $"think {think}: a second flight was begun past the journey's cap");
            }

            Assert.Equal(900u, player.Money);
            Assert.Equal(100u, journey.SpentCopper);
            TravelFacts facts = PlayerbotTravel.BuildFacts(rig.Session, player, options, [], null, journey.SpendLeft(options.Travel));
            Assert.Equal(50u, facts.SpendCap);
            return 0;
        });
    }
}

using System.Numerics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Elevator;
using ArcaneCore.World.Tests.Playerbots.Tram;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// A journey that runs past its bound (<see cref="TravelJourney.DeadlineMs"/>, <see cref="PlayerbotTravel.EnforceDeadline"/>) drops the
/// Deeprun Tram trip its walk leg began only while that trip is still walking to its entrance (<see cref="TramStage.ToEntrance"/>): a bot
/// already through the entrance stands on the tram map, and dropping the trip there would strand it, so that trip is kept and rides on.
/// The taxi rule has its own test (PlayerbotTaxiJourneyDeadlineTests); the lift rule is <see cref="TravelDeadlineLiftTests"/>.
/// </summary>
public sealed class TravelDeadlineTramTests
{
    private static readonly Vector3 IronforgeGoal = new(-4918.9f, -982.5f, 501.5f);

    private static PlayerbotOptions Options() => new() { Enabled = true, ThinkIntervalMs = 100, Population = { TramMinLevel = 0 } };

    /// <summary>A journey under way with a tram trip begun for its walk leg, at the Stormwind exit (the trip's first stage).</summary>
    private static async Task<TravelJourney> BeginAsync(TramTestWorld world, PlayerbotOptions options)
    {
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        return await world.OnWorldAsync(() =>
        {
            var travel = new PlayerbotTravel(options);
            Vector3 near = TramTestData.StormwindExitLanding + new Vector3(15f, 0f, 0f);
            Assert.True(travel.TryBegin(world.Session, world.Player, 0, near, out string? why), why);
            Assert.True(PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt), "the trip did not begin");
            Assert.Equal(TramStage.ToEntrance, PlayerbotTram.TripOf(world.Player)!.Stage);
            return PlayerbotTravel.JourneyOf(world.Player)!;
        });
    }

    [Fact]
    public async Task AnOverdueJourney_DropsItsTramTrip_WhileItStillWalksToTheEntrance()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync();
        PlayerbotOptions options = Options();
        TravelJourney journey = await BeginAsync(world, options);

        await world.OnWorldAsync(() =>
        {
            journey.DeadlineMs = unchecked(world.Host.World.NowMs - 1);
            PlayerbotTravel.EnforceDeadline(world.Session, world.Player);
            Assert.Equal(JourneyState.Failed, journey.State);
            Assert.False(PlayerbotTram.IsTraveling(world.Player), "the overdue journey left its walk leg's tram trip walking to the entrance");
            Assert.Null(PlayerbotTram.TripOf(world.Player));
        });
    }

    [Fact]
    public async Task AnOverdueJourney_KeepsATramTripPastTheEntrance_UnderWayToItsEnd()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync();
        PlayerbotOptions options = Options();
        TravelJourney journey = await BeginAsync(world, options);

        // The tram driver alone takes the bot through the Stormwind entrance onto the tram map (the journey is not driven).
        TramStage? stage = TramStage.ToEntrance;
        for (int step = 0; step < 2_000 && stage == TramStage.ToEntrance; step++)
            stage = await world.TickAsync(() => { PlayerbotTram.Drive(world.Session, world.Player, options, 100, out _); return PlayerbotTram.TripOf(world.Player)?.Stage; });
        Assert.NotNull(stage);
        Assert.NotEqual(TramStage.ToEntrance, stage);
        Assert.True(await world.OnWorldAsync(() => world.Player.MapId) == TramTestData.Tram, "not on the tram map past the entrance");

        TramTrip trip = await world.OnWorldAsync(() =>
        {
            TramTrip before = PlayerbotTram.TripOf(world.Player)!;
            journey.DeadlineMs = unchecked(world.Host.World.NowMs - 1);
            PlayerbotTravel.EnforceDeadline(world.Session, world.Player);
            Assert.Equal(JourneyState.Failed, journey.State);
            Assert.Same(before, PlayerbotTram.TripOf(world.Player));
            Assert.True(PlayerbotTram.IsTraveling(world.Player), "the overdue journey dropped a tram trip already on the tram map");
            return before;
        });

        // Kept, the trip rides on to Ironforge and leaves the tram map.
        for (int step = 0; step < 4_000 && stage is not (TramStage.Done or TramStage.Failed); step++)
            stage = await world.TickAsync(() => { PlayerbotTram.Drive(world.Session, world.Player, options, 100, out _); return PlayerbotTram.TripOf(world.Player)?.Stage; });
        Assert.Equal(TramStage.Done, stage);
        Assert.Same(trip, await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)));
        Assert.Equal(0u, await world.OnWorldAsync(() => world.Player.MapId));
    }
}

/// <summary>
/// A journey that runs past its bound drops the lift trip its walk leg began while the bot has not boarded (<see cref="ElevatorStage.ToStop"/>):
/// no ride is taken for a journey that is over.
/// </summary>
[Collection(ElevatorAnomalyLedgerCollection.Name)]
public sealed class TravelDeadlineLiftTests
{
    [Fact]
    public async Task AnOverdueJourney_DropsItsLiftTrip_BeforeTheBotBoards()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        Vector3 plateau = new(-4600f, -1800f, ElevatorTestWorld.TopFloor);
        await world.PlaceAsync(plateau);
        _ = await world.GreatLiftAsync();
        PlayerbotOptions options = new() { Enabled = true, ThinkIntervalMs = 100 };

        await world.OnWorldAsync(() =>
        {
            var travel = new PlayerbotTravel(options);
            Assert.True(travel.TryBegin(world.Session, world.Player, ElevatorTestData.Kalimdor, plateau + new Vector3(15f, 0f, 0f), out string? why), why);
            TravelJourney journey = PlayerbotTravel.JourneyOf(world.Player)!;
            Assert.True(PlayerbotElevator.TryBegin(world.Session, world.Player, ElevatorTestWorld.CanyonStart, options), "the ride did not begin");
            Assert.Equal(ElevatorStage.ToStop, PlayerbotElevator.TripOf(world.Player)!.Stage);

            journey.DeadlineMs = unchecked(world.Host.World.NowMs - 1);
            PlayerbotTravel.EnforceDeadline(world.Session, world.Player);
            Assert.Equal(JourneyState.Failed, journey.State);
            Assert.False(PlayerbotElevator.IsTraveling(world.Player), "the overdue journey left its walk leg's lift trip to board");
        });
    }
}

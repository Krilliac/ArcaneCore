using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>
/// Every test world names its bot "Controlone" (guid 1), so the anomaly subject is the same in every elevator test and the process-wide
/// ledger would mix their reports; this collection runs alone, with no other test class in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ElevatorAnomalyLedgerCollection
{
    public const string Name = "ElevatorAnomalyLedger";
}

/// <summary>
/// Hardening wave H1, review 2. The behaviour changes <see cref="PlayerbotElevator"/> kept (each proved against the base behaviour: it fails
/// when the change is reverted) and a fires / stays-silent pair for every <c>bot-elevator/*</c> anomaly key.
/// </summary>
[Collection(ElevatorAnomalyLedgerCollection.Name)]
public sealed class PlayerbotElevatorH1ReviewTests
{
    private static PlayerbotOptions Options() => new() { Enabled = true, ThinkIntervalMs = 100 };

    private static readonly string[] AllCodes =
    [
        "rescue-teleport", "rescue-refused", "pit-fall", "board-missed", "board-window-short", "waiting-inside-door", "left-behind",
        "detached-mid-ride", "passed-destination", "arrived-wrong-level", "arrived-off-landing", "trip-slow", "trip-failed",
    ];

    private static bool Reported(AnomalyCapture capture, string code) => capture.Any(PlayerbotAnomalies.Key("elevator", code));

    private static int Count(AnomalyCapture capture, string code) => capture.Count(PlayerbotAnomalies.Key("elevator", code));

    /// <summary>The scripted motion state of a bot that is walking a route (what a goal's Follow leaves behind), without a path query.</summary>
    private static void MakeMotionActive(ElevatorTestWorld world)
    {
        Player player = world.Player;
        Vector3 here = new(player.X, player.Y, player.Z);
        PlayerbotMotion.MotionState state = PlayerbotMotion.Of(player);
        state.Route = new PlayerbotRoute([here, here + new Vector3(3f, 0f, 0f)], 3f) { NextPoint = 1 };
        state.Map = player.Map;
        state.Anchor = here;
        state.AnchorMs = world.Host.World.NowMs;
        state.Heading = 0f;
        state.Speed = 0f;
        Assert.True(PlayerbotMotion.IsActive(player));
    }

    // ---------------------------------------------------------------- behaviour changes kept

    /// <summary>
    /// A bot found on a car at login (<see cref="PlayerbotElevator.Settle"/> then BoardFound) that is moving along a route: the step aboard
    /// (<see cref="PlayerbotMotion.SendAboard"/>) is refused while a route is active, so BoardFound stops the bot first. Without that the
    /// trip began while the bot never reported itself on the car.
    /// </summary>
    [Fact]
    public async Task BoardFound_StopsAMovingBot_SoTheStepAboardIsNotRefused()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        ElevatorLift lift = await world.GreatLiftAsync();
        ElevatorCar top = lift.Cars.Single(c => c.SpawnGuid == 16874);
        for (int i = 0; i < 700; i++)
        {
            uint? progress = await world.OnWorldAsync(() =>
            {
                GameObjectMapSystem? objects = world.Objects();
                GameObject? car = objects?.GameObjects.FirstOrDefault(g => g.Spawn?.Guid == 16874);
                return car is null ? null : objects!.ElevatorProgressAhead(car, 0);
            });
            if (progress is >= 500 and <= 2_000) break;
            await world.AdvanceAsync(50);
        }

        await world.PlaceAsync(top.High + new Vector3(1f, 0f, 0f));
        (bool aboard, bool tripBegun, bool stillMoving) = await world.OnWorldAsync(() =>
        {
            MakeMotionActive(world);
            PlayerbotElevator.Drive(world.Session, world.Player, Options(), 100, out _);
            return ((world.Player.Movement.Flags & MovementFlags.OnTransport) != 0 || PlayerbotElevator.TripOf(world.Player)?.Stage == ElevatorStage.Done,
                PlayerbotElevator.TripOf(world.Player) is not null, PlayerbotMotion.IsActive(world.Player));
        });

        Assert.True(tripBegun, "no trip began for the bot standing on the car");
        Assert.True(aboard, "the trip began but the bot never reported itself on the car (the step aboard was refused)");
        Assert.False(stillMoving, "the route was left running");
    }

    /// <summary>
    /// Alighting with a route still active: <see cref="PlayerbotMotion.SendStepOff"/> is refused while a route runs, so Ride stops the bot
    /// first. Without that the bot stayed on the car at its stop.
    /// </summary>
    [Fact]
    public async Task TheStepOff_StopsAMovingBot_SoItAlightsAtTheStop()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        PlayerbotOptions options = Options();
        await PlayerbotElevatorSafetyTests.RideIntoTheShaftAsync(world, options, above: -10f); // aboard, still at the foot
        (ElevatorStep step, bool aboard) = await world.OnWorldAsync(() =>
        {
            PlayerbotElevator.Cancel(world.Player);
            MakeMotionActive(world);
            ElevatorStep s = PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _);
            return (s, (world.Player.Movement.Flags & MovementFlags.OnTransport) != 0);
        });

        Assert.Equal(ElevatorStep.Arrived, step);
        Assert.False(aboard, "the bot is still on the car: the step off was refused");
    }

    /// <summary>
    /// The car's heartbeat (<c>Beat</c>) is refused by <see cref="PlayerbotMotion.SendAboard"/> while a route runs, so a bot riding with a
    /// stray route never refreshed its place on the car; Beat stops the bot first.
    /// </summary>
    [Fact]
    public async Task TheRideHeartbeat_StopsAMovingBot_SoItIsStillSentWhileAboard()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        PlayerbotOptions options = Options();
        await PlayerbotElevatorSafetyTests.RideIntoTheShaftAsync(world, options, above: 30f);
        await world.AdvanceAsync(PlayerbotElevator.HeartbeatMs + 100);
        (bool beat, bool moving, bool aboard) = await world.OnWorldAsync(() =>
        {
            ElevatorTrip trip = PlayerbotElevator.TripOf(world.Player)!;
            MakeMotionActive(world);
            uint before = trip.LastBeatMs;
            PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _);
            return (trip.LastBeatMs != before, PlayerbotMotion.IsActive(world.Player), (world.Player.Movement.Flags & MovementFlags.OnTransport) != 0);
        });

        Assert.True(beat, "no heartbeat was sent: the refused step aboard left LastBeatMs alone");
        Assert.False(moving);
        Assert.True(aboard);
    }

    // ---------------------------------------------------------------- a clean ride is silent for every key

    [Fact]
    public async Task ACleanRideUpTheGreatLift_ReportsNoElevatorAnomaly()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        ElevatorLift lift = await world.GreatLiftAsync();
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.OnWorldAsync(() => PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.Low, ElevatorTestWorld.Ratchet));
        var brain = new PlayerbotBrain(world.Session, Options());
        ElevatorStage? stage = null;
        for (int i = 0; i < 3_000 && stage is not (ElevatorStage.Done or ElevatorStage.Failed); i++)
            stage = await world.TickAsync(() => { brain.Update(100); return PlayerbotElevator.TripOf(world.Player)?.Stage; });

        Assert.Equal(ElevatorStage.Done, stage);
        foreach (string code in AllCodes) Assert.False(Reported(capture, code), $"{code} fired on a clean ride: {capture.Describe()}");
    }

    // ---------------------------------------------------------------- left-behind / detached-mid-ride

    [Fact]
    public async Task LeftBehind_FiresWhenTheCarIsFarFromTheBot_AndIsSilentWithinHeartbeatLag()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        await PlayerbotElevatorSafetyTests.RideIntoTheShaftAsync(world, Options(), above: 20f);
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);
        (bool laggingOk, bool leftOk, int count) = await world.OnWorldAsync(() =>
        {
            Player p = world.Player;
            ElevatorTrip trip = PlayerbotElevator.TripOf(p)!;
            GameObject live = trip.CarObject!;
            bool lagging = PlayerbotElevator.CheckAboardState(world.Session, p, trip, live, new Vector3(p.X, p.Y, p.Z + 8f));
            Assert.False(Reported(capture, "left-behind"), "an 8-yard lag (a clean ride's heartbeat lag) was reported");
            bool left = PlayerbotElevator.CheckAboardState(world.Session, p, trip, live, new Vector3(p.X, p.Y, p.Z + 40f));
            PlayerbotElevator.CheckAboardState(world.Session, p, trip, live, new Vector3(p.X, p.Y, p.Z + 41f));
            return (lagging, left, Count(capture, "left-behind"));
        });

        Assert.True(laggingOk);
        Assert.True(leftOk, "the check must report, not end the ride");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DetachedMidRide_FiresWhenTheTransportIsGone_AndIsSilentAboard()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        await PlayerbotElevatorSafetyTests.RideIntoTheShaftAsync(world, Options(), above: 20f);
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.OnWorldAsync(() =>
        {
            Player p = world.Player;
            ElevatorTrip trip = PlayerbotElevator.TripOf(p)!;
            GameObject live = trip.CarObject!;
            Vector3 carAt = new(p.X, p.Y, p.Z);
            Assert.True(PlayerbotElevator.CheckAboardState(world.Session, p, trip, live, carAt));
            Assert.False(Reported(capture, "detached-mid-ride"), capture.Describe());

            PlayerbotElevator.ClearTransport(world.Session, p);
            Assert.False(PlayerbotElevator.CheckAboardState(world.Session, p, trip, live, carAt));
            PlayerbotElevator.CheckAboardState(world.Session, p, trip, live, carAt);
            Assert.Equal(1, Count(capture, "detached-mid-ride"));
        });
    }

    [Fact]
    public async Task ABotOnAnotherTransport_IsReported_ButRidesOn_AsTheOnTransportFlagAloneDecidedBefore()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        await PlayerbotElevatorSafetyTests.RideIntoTheShaftAsync(world, Options(), above: 20f);
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.OnWorldAsync(() =>
        {
            Player p = world.Player;
            ElevatorTrip trip = PlayerbotElevator.TripOf(p)!;
            GameObject live = trip.CarObject!;
            MovementInfo movement = p.Movement;
            movement.TransportGuid = live.Guid.Value + 1;
            p.ApplyMovement(movement, world.Host.World.NowMs);
            Assert.True((p.Movement.Flags & MovementFlags.OnTransport) != 0, "the fixture could not set the foreign transport");
            Assert.NotEqual(live.Guid.Value, p.Movement.TransportGuid);

            Assert.True(PlayerbotElevator.CheckAboardState(world.Session, p, trip, live, new Vector3(p.X, p.Y, p.Z)), "a report changed the ride");
            Assert.Equal(1, Count(capture, "detached-mid-ride"));
        });
    }

    // ---------------------------------------------------------------- pit-fall / rescue-teleport / rescue-refused / trip-failed

    [Fact]
    public async Task ACarLeavingTheWorldOverAPit_ReportsPitFall_RescueTeleport_AndTripFailed()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync(shaftFloor: -49.4f);
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        PlayerbotOptions options = Options();
        await PlayerbotElevatorSafetyTests.RideIntoTheShaftAsync(world, options, above: 10f);
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.OnWorldAsync(() => Assert.True(world.Objects()!.Remove(PlayerbotElevator.TripOf(world.Player)!.CarObject!)));

        ElevatorStep step = ElevatorStep.Traveling;
        for (int i = 0; i < 200 && step == ElevatorStep.Traveling; i++)
            step = await world.TickAsync(() => PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _));

        Assert.Equal(ElevatorStep.Failed, step);
        Assert.Equal(1, Count(capture, "pit-fall"));
        Assert.True(Reported(capture, "rescue-teleport"), capture.Describe());
        Assert.Equal(1, Count(capture, "trip-failed"));
        Assert.False(Reported(capture, "rescue-refused"), capture.Describe());
    }

    [Fact]
    public async Task AFallThatIsNotIntoAPit_ReportsTripFailed_ButNeitherPitFallNorARescue()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync(shaftFloor: ElevatorTestWorld.BottomFloor);
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        PlayerbotOptions options = Options();
        await PlayerbotElevatorSafetyTests.RideIntoTheShaftAsync(world, options, above: 10f);
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.OnWorldAsync(() => Assert.True(world.Objects()!.Remove(PlayerbotElevator.TripOf(world.Player)!.CarObject!)));

        ElevatorStep step = ElevatorStep.Traveling;
        for (int i = 0; i < 200 && step == ElevatorStep.Traveling; i++)
            step = await world.TickAsync(() => PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _));

        Assert.Equal(ElevatorStep.Failed, step);
        Assert.Equal(1, Count(capture, "trip-failed"));
        Assert.False(Reported(capture, "pit-fall"), capture.Describe());
        Assert.False(Reported(capture, "rescue-teleport"), capture.Describe());
        Assert.False(Reported(capture, "rescue-refused"), capture.Describe());
    }

    [Fact]
    public async Task ARefusedRescue_IsReportedOncePerTrip_AndASuccessfulOneIsNot()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync(shaftFloor: -49.4f);
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        await PlayerbotElevatorSafetyTests.RideIntoTheShaftAsync(world, Options(), above: 10f);
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.OnWorldAsync(() =>
        {
            Player p = world.Player;
            ElevatorTrip trip = PlayerbotElevator.TripOf(p)!;
            // A far teleport under way refuses every rescue teleport (TeleportService.Check).
            Assert.True(world.Teleports.TeleportTo(p, 0, 0f, 0f, 83.5f, 0f), "the far teleport was refused");
            uint now = world.Host.World.NowMs;
            PlayerbotElevator.Drop(world.Session, p, p.Map!, trip, now, "elevator: test drop");
            PlayerbotElevator.Drop(world.Session, p, p.Map!, trip, now, "elevator: test drop");
            Assert.Equal(1, Count(capture, "rescue-refused"));
            Assert.False(Reported(capture, "rescue-teleport"), capture.Describe());
        });
    }

    // ---------------------------------------------------------------- board-window-short / board-missed / waiting-inside-door

    [Fact]
    public async Task CarsThatNeverStandLongEnough_ReportBoardWindowShort_AndFailTheTrip()
    {
        var restless = new TransportAnimationCatalog([.. ((uint[])[11898, 11899]).SelectMany(entry => new (uint, TransportAnimationNode)[]
        {
            (entry, new TransportAnimationNode(0, 0, 0, 0)),
            (entry, new TransportAnimationNode(15_000, 0, 0, entry == 11899 ? 129.55f : -129.3f)),
            (entry, new TransportAnimationNode(30_000, 0, 0, 0)),
        })]);
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync(animation: restless);
        ElevatorLift lift = await world.GreatLiftAsync();
        await world.PlaceAsync(new Vector3(lift.Centre.X - 12f, lift.Centre.Y, ElevatorTestWorld.BottomFloor));
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);
        PlayerbotOptions options = Options();
        await world.OnWorldAsync(() => PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.Low, ElevatorTestWorld.Ratchet));
        ElevatorStep step = ElevatorStep.Traveling;
        for (int i = 0; i < 1_500 && step == ElevatorStep.Traveling; i++)
            step = await world.TickAsync(() => PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _));

        Assert.Equal(1, Count(capture, "board-window-short"));
        Assert.Equal(ElevatorStep.Failed, step);
        Assert.Equal(1, Count(capture, "trip-failed"));
    }

    [Fact]
    public async Task BoardMissed_FiresWhenAWideStopPassedUnboarded_AndIsSilentForAShortOrUnseenStop()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        ElevatorLift lift = await world.GreatLiftAsync();
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.OnWorldAsync(() =>
        {
            ElevatorTrip trip = PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.Low, ElevatorTestWorld.Ratchet);

            trip.StopActiveAtFrom = false; // no stop was seen
            trip.StopMaxStandsForMs = 5_000;
            PlayerbotElevator.FlushStopAtFrom(world.Session, world.Player, trip);
            Assert.False(Reported(capture, "board-missed"));

            trip.StopActiveAtFrom = true; // a stop too short to board anyway
            trip.StopMaxStandsForMs = PlayerbotElevator.BoardMarginMs - 1;
            PlayerbotElevator.FlushStopAtFrom(world.Session, world.Player, trip);
            Assert.False(Reported(capture, "board-missed"));

            trip.StopActiveAtFrom = true; // a stop long enough to board that passed
            trip.StopMaxStandsForMs = PlayerbotElevator.BoardMarginMs + 3_000;
            trip.StopSkipReason = "drifted-from-landing";
            PlayerbotElevator.FlushStopAtFrom(world.Session, world.Player, trip);
            Assert.Equal(1, Count(capture, "board-missed"));
            Assert.False(trip.StopActiveAtFrom);
        });
    }

    [Fact]
    public async Task WaitingInsideTheShaft_IsReportedOnce_AndWaitingAtALandingIsSilent()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        ElevatorLift lift = await world.GreatLiftAsync();
        ElevatorCar car = lift.Cars.Single(c => c.SpawnGuid == 16876);
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.OnWorldAsync(() =>
        {
            ElevatorTrip trip = PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.Low, ElevatorTestWorld.Ratchet);
            Vector3 stop = car.At(ElevatorLevel.Low);

            trip.Landing = stop + new Vector3(PlayerbotElevator.LandingRadii[0], 0f, 0f);
            PlayerbotElevator.CheckWaitingLanding(world.Session, world.Player, world.Player.Map!, trip, car);
            Assert.False(Reported(capture, "waiting-inside-door"), capture.Describe());

            trip.Landing = stop + new Vector3(1f, 0f, 0f);
            PlayerbotElevator.CheckWaitingLanding(world.Session, world.Player, world.Player.Map!, trip, car);
            PlayerbotElevator.CheckWaitingLanding(world.Session, world.Player, world.Player.Map!, trip, car);
            Assert.Equal(1, Count(capture, "waiting-inside-door"));
        });
    }

    // ---------------------------------------------------------------- passed-destination

    [Fact]
    public async Task PassedDestination_FiresWhenTheCarLeavesTheStopUnalighted_AndIsSilentWhileItStandsThere()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        await PlayerbotElevatorSafetyTests.RideIntoTheShaftAsync(world, Options(), above: 20f);
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.OnWorldAsync(() =>
        {
            Player p = world.Player;
            ElevatorTrip trip = PlayerbotElevator.TripOf(p)!;
            GameObject live = trip.CarObject!;
            ElevatorCar car = trip.Car!;
            GameObjectMapSystem system = world.Objects()!;
            float toZ = car.At(trip.To).Z;
            uint now = world.Host.World.NowMs;

            trip.DestStopActive = true; // at the stop: nothing is wrong yet
            PlayerbotElevator.ObserveDestinationWindow(world.Session, p, system, live, car, new Vector3(p.X, p.Y, toZ), trip, now);
            Assert.False(Reported(capture, "passed-destination"), capture.Describe());

            PlayerbotElevator.ObserveDestinationWindow(world.Session, p, system, live, car, new Vector3(p.X, p.Y, toZ - 30f), trip, now);
            Assert.Equal(1, Count(capture, "passed-destination"));
            Assert.False(trip.DestStopActive);
        });
    }

    // ---------------------------------------------------------------- arrival checks

    private static async Task<(ElevatorTrip Trip, Vector3 Stop)> ArrivalTripAsync(ElevatorTestWorld world)
    {
        ElevatorLift lift = await world.GreatLiftAsync();
        ElevatorCar top = lift.Cars.Single(c => c.SpawnGuid == 16874);
        ElevatorTrip trip = await world.OnWorldAsync(() =>
        {
            ElevatorTrip begun = PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.Low, ElevatorTestWorld.Ratchet);
            begun.Car = top;
            return begun;
        });
        return (trip, top.At(ElevatorLevel.High));
    }

    [Fact]
    public async Task ArrivedWrongLevel_FiresOnTheFloorBelow_AndIsSilentOnTheLanding()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        (ElevatorTrip trip, Vector3 stop) = await ArrivalTripAsync(world);
        await world.PlaceAsync(new Vector3(stop.X - 14f, stop.Y, ElevatorTestWorld.BottomFloor)); // the canyon floor below the lift, within the arrival radius
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.OnWorldAsync(() => PlayerbotElevator.CheckArrivedAnomalies(world.Session, world.Player, world.Player.Map!, trip, Options(), trip.BeganMs + 1_000));
        Assert.True(Reported(capture, "arrived-wrong-level"), capture.Describe());

        await world.PlaceAsync(new Vector3(-4659f, -1827.67f, ElevatorTestWorld.TopFloor));
        using AnomalyCapture onLanding = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.OnWorldAsync(() => PlayerbotElevator.CheckArrivedAnomalies(world.Session, world.Player, world.Player.Map!, trip, Options(), trip.BeganMs + 1_000));
        Assert.False(Reported(onLanding, "arrived-wrong-level"), onLanding.Describe());
        Assert.False(Reported(onLanding, "arrived-off-landing"), onLanding.Describe());
        Assert.False(Reported(onLanding, "trip-slow"), onLanding.Describe());
    }

    [Fact]
    public async Task ArrivalChecks_AreSkippedForABotFarFromTheStop()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart); // ~117 yd from the lift, on the wrong level
        (ElevatorTrip trip, _) = await ArrivalTripAsync(world);
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.OnWorldAsync(() => PlayerbotElevator.CheckArrivedAnomalies(world.Session, world.Player, world.Player.Map!, trip, Options(), trip.BeganMs + 3_600_000));
        Assert.False(Reported(capture, "arrived-wrong-level"), capture.Describe());
        Assert.False(Reported(capture, "arrived-off-landing"), capture.Describe());
        Assert.False(Reported(capture, "trip-slow"), capture.Describe());
    }

    [Fact]
    public async Task RelocatedAfterStepOff_TheTripIsAbandonedNotReportedAsAWrongLevelArrival()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        (ElevatorTrip trip, Vector3 stop) = await ArrivalTripAsync(world);
        await world.PlaceAsync(new Vector3(stop.X, stop.Y, stop.Z) + new Vector3(PlayerbotElevator.LandingRadii[0] + 1f, 0f, 0f));
        await world.OnWorldAsync(() =>
        {
            trip.StepOffLevel = ElevatorLevel.High;
            trip.Stage = ElevatorStage.Aboard;
            trip.PendingStep = ElevatorStep.Arrived; // the pump stepped it off; the brain's Drive has not reported it yet
        });

        await world.PlaceAsync(ElevatorTestWorld.CanyonStart); // a GM send / spirit-healer resurrection moves it away first
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);
        ElevatorStep step = await world.OnWorldAsync(() => PlayerbotElevator.Drive(world.Session, world.Player, Options(), 100, out _));

        Assert.Equal(ElevatorStep.Idle, step);
        Assert.False(Reported(capture, "arrived-wrong-level"), capture.Describe());
        Assert.Null(await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)));
        Assert.True(await world.OnWorldAsync(() => !PlayerbotMotion.TryGetState(world.Player, out PlayerbotMotion.MotionState? state) || state.ElevatorTrip is null));
        Assert.Equal(0, await world.OnWorldAsync(() => PlayerbotTransitBackoff.Failures(world.Player, TransitKind.Lift, trip.Lift.Key)));
        Assert.False(await world.OnWorldAsync(() => PlayerbotTransitBackoff.IsBackedOff(world.Player, TransitKind.Lift, trip.Lift.Key, world.Host.World.NowMs)));
    }

    [Fact]
    public async Task ArrivedNearTheStop_IsStillReportedByDrive_WhenOnTheWrongLevel()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        (ElevatorTrip trip, Vector3 stop) = await ArrivalTripAsync(world);
        await world.PlaceAsync(new Vector3(stop.X - 14f, stop.Y, ElevatorTestWorld.BottomFloor));
        await world.OnWorldAsync(() =>
        {
            trip.StepOffLevel = ElevatorLevel.High;
            trip.Stage = ElevatorStage.Aboard;
            trip.PendingStep = ElevatorStep.Arrived;
        });

        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);
        ElevatorStep step = await world.OnWorldAsync(() => PlayerbotElevator.Drive(world.Session, world.Player, Options(), 100, out _));

        Assert.Equal(ElevatorStep.Arrived, step);
        Assert.True(Reported(capture, "arrived-wrong-level"), capture.Describe());
    }

    // ---------------------------------------------------------------- real step-off flow (Pump parks Arrived, Drive reports it)

    private sealed record StepOffRig(ElevatorTrip Trip, ElevatorCar CarA, ElevatorCar CarB);

    /// <summary>A trip begun on the canyon floor with <c>Car</c> the Great Lift's car 16874; its twin 16876 stands 22.6 yd away.</summary>
    private static async Task<StepOffRig> BeginFromTheCanyonAsync(ElevatorTestWorld world)
    {
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        ElevatorLift lift = await world.GreatLiftAsync();
        ElevatorCar a = lift.Cars.Single(c => c.SpawnGuid == 16874);
        ElevatorCar b = lift.Cars.Single(c => c.SpawnGuid == 16876);
        ElevatorTrip trip = await world.OnWorldAsync(() =>
        {
            ElevatorTrip begun = PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.Low, ElevatorTestWorld.Ratchet);
            begun.Car = a;
            return begun;
        });
        return new StepOffRig(trip, a, b);
    }

    /// <summary>The pump's turn: it completes the trip on the destination floor and parks <c>Arrived</c> for the next Drive.</summary>
    private static Task PumpOnceAsync(ElevatorTestWorld world, ElevatorTrip trip)
        => world.OnWorldAsync(() =>
        {
            uint now = world.Host.World.NowMs;
            trip.LastDriveMs = unchecked(now - 1_000);
            trip.LastPumpMs = unchecked(now - 1_000);
            PlayerbotElevator.Pump(world.Session, world.Player, now);
            return 0;
        });

    [Fact]
    public async Task SteppingOffBesideTheOtherCar_FarFromTripCar_IsStillAnArrival_AndClearsTheBackoff()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        StepOffRig rig = await BeginFromTheCanyonAsync(world);
        Vector3 aHigh = rig.CarA.At(ElevatorLevel.High);
        Vector3 bHigh = rig.CarB.At(ElevatorLevel.High);
        Vector2 away = Vector2.Normalize(new Vector2(bHigh.X - aHigh.X, bHigh.Y - aHigh.Y));
        rig.Trip.Goal = bHigh + new Vector3(away * 50f, 0f); // the landing is looked for on the side away from the trip's car
        await world.PlaceAsync(new Vector3(bHigh.X + (away.X * 12f), bHigh.Y + (away.Y * 12f), ElevatorTestWorld.TopFloor));
        uint key = rig.Trip.Lift.Key;
        await world.OnWorldAsync(() => PlayerbotTransitBackoff.Fail(world.Player, TransitKind.Lift, key, world.Host.World.NowMs, Options()));
        Assert.Equal(1, await world.OnWorldAsync(() => PlayerbotTransitBackoff.Failures(world.Player, TransitKind.Lift, key)));

        ElevatorStep step = await world.OnWorldAsync(() => PlayerbotElevator.Drive(world.Session, world.Player, Options(), 100, out _));

        float fromTripCar = await world.OnWorldAsync(() => Vector2.Distance(new Vector2(world.Player.X, world.Player.Y), new Vector2(aHigh.X, aHigh.Y)));
        Assert.True(fromTripCar > 25f, $"the bot stands {fromTripCar:F1} yd from the trip car's stop");
        Assert.Equal(ElevatorStep.Arrived, step);
        Assert.Equal(ElevatorStage.Done, rig.Trip.Stage);
        Assert.Equal(0, await world.OnWorldAsync(() => PlayerbotTransitBackoff.Failures(world.Player, TransitKind.Lift, key)));
    }

    [Fact]
    public async Task RelocatedOntoTheOtherLevelNearTheStop_AfterTheStepOff_IsNotAWrongLevelArrival()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        StepOffRig rig = await BeginFromTheCanyonAsync(world);
        Vector3 stop = rig.CarA.At(ElevatorLevel.High);
        await world.PlaceAsync(stop + new Vector3(PlayerbotElevator.LandingRadii[0] + 1f, 0f, 0f));
        await PumpOnceAsync(world, rig.Trip);
        Assert.Equal(ElevatorStep.Arrived, rig.Trip.PendingStep);

        await world.PlaceAsync(new Vector3(stop.X - 14f, stop.Y, ElevatorTestWorld.BottomFloor)); // 14 yd from the stop, on the canyon floor
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);
        ElevatorStep step = await world.OnWorldAsync(() => PlayerbotElevator.Drive(world.Session, world.Player, Options(), 100, out _));

        Assert.Equal(ElevatorStep.Idle, step);
        Assert.False(Reported(capture, "arrived-wrong-level"), capture.Describe());
        Assert.Null(await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)));
    }

    [Fact]
    public async Task ASlowArrivalNearTheStepOffPoint_StillReportsTripSlow()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        StepOffRig rig = await BeginFromTheCanyonAsync(world);
        Vector3 stop = rig.CarA.At(ElevatorLevel.High);
        await world.PlaceAsync(stop + new Vector3(PlayerbotElevator.LandingRadii[0] + 1f, 0f, 0f));
        await world.AdvanceAsync(300_000);
        await PumpOnceAsync(world, rig.Trip);
        Assert.Equal(ElevatorStep.Arrived, rig.Trip.PendingStep);

        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);
        ElevatorStep step = await world.OnWorldAsync(() => PlayerbotElevator.Drive(world.Session, world.Player, Options(), 100, out _));

        Assert.Equal(ElevatorStep.Arrived, step);
        Assert.True(Reported(capture, "trip-slow"), capture.Describe());
        Assert.False(Reported(capture, "arrived-wrong-level"), capture.Describe());
    }

    [Fact]
    public async Task ArrivedOffLanding_FiresInsideTheShaft_AndIsSilentBesideTheCar()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        (ElevatorTrip trip, Vector3 stop) = await ArrivalTripAsync(world);
        await world.PlaceAsync(stop + new Vector3(PlayerbotElevator.LandingRadii[0] + 1f, 0f, 0f));
        using AnomalyCapture beside = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.OnWorldAsync(() => PlayerbotElevator.CheckArrivedAnomalies(world.Session, world.Player, world.Player.Map!, trip, Options(), trip.BeganMs + 1_000));
        Assert.False(Reported(beside, "arrived-off-landing"), beside.Describe());

        await world.PlaceAsync(stop + new Vector3(1f, 0f, 0f));
        using AnomalyCapture inside = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.OnWorldAsync(() => PlayerbotElevator.CheckArrivedAnomalies(world.Session, world.Player, world.Player.Map!, trip, Options(), trip.BeganMs + 1_000));
        Assert.True(Reported(inside, "arrived-off-landing"), inside.Describe());
    }

    [Fact]
    public async Task TripSlow_FiresOnATripFarPastItsBudget_AndIsSilentOnAQuickOne()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        (ElevatorTrip trip, Vector3 stop) = await ArrivalTripAsync(world);
        await world.PlaceAsync(stop + new Vector3(PlayerbotElevator.LandingRadii[0] + 1f, 0f, 0f));
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(world.Player);

        await world.OnWorldAsync(() => PlayerbotElevator.CheckArrivedAnomalies(world.Session, world.Player, world.Player.Map!, trip, Options(), trip.BeganMs + 20_000));
        Assert.False(Reported(capture, "trip-slow"), capture.Describe());

        await world.OnWorldAsync(() => PlayerbotElevator.CheckArrivedAnomalies(world.Session, world.Player, world.Player.Map!, trip, Options(), trip.BeganMs + 3_600_000));
        Assert.True(Reported(capture, "trip-slow"), capture.Describe());
    }
}

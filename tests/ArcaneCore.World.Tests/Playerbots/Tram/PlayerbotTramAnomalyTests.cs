using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Tram;

/// <summary>
/// The <c>bot-tram</c> anomaly keys of <see cref="PlayerbotTram"/> (Hardening Wave H1): each fires once per episode on the failure it names
/// and stays silent on the healthy path. The ledger is process-wide, so every test reads only its own bot's reports
/// (<see cref="BotAnomalyCapture"/>: the bot's subject on this world's thread). The last test drives the tram pump
/// (<see cref="PlayerbotTram.Pump"/>, called from <see cref="PlayerbotMotion.Pump"/>) with thinks 12 s apart, as a large population spaces them.
/// </summary>
public sealed class PlayerbotTramAnomalyTests(ITestOutputHelper output)
{
    private static readonly Vector3 IronforgeGoal = new(-4918.9f, -982.5f, 501.5f);

    private static PlayerbotOptions Options() => new() { Enabled = true, ThinkIntervalMs = 100, Population = { TramMinLevel = 0 } };

    /// <summary>The fixture's car cycle (TramTestData.SyntheticAnimation: 120 s) and the slow bound derived from it.</summary>
    private static readonly uint SlowBoundMs = PlayerbotTram.SlowBoundMs(120_000);

    private static string Key(string code) => PlayerbotAnomalies.Key("tram", code);

    private static string Report(AnomalyCapture capture, string code)
        => capture.Notices.FirstOrDefault(n => n.Key == Key(code))?.Detail ?? "(none)";

    private static TramStep Drive(TramTestWorld world, PlayerbotOptions options)
        => PlayerbotTram.Drive(world.Session, world.Player, options, 100, out _);

    /// <summary>One 100 ms step per iteration, the trip driven, until <paramref name="reached"/> (read on the world thread after the drive).</summary>
    private static async Task<TramStep> DriveUntilAsync(TramTestWorld world, PlayerbotOptions options, Func<bool> reached, int maxSteps = 6_000)
    {
        for (int step = 0; step < maxSteps; step++)
        {
            (TramStep result, bool hit) = await world.TickAsync(() => (Drive(world, options), reached()));
            if (hit) return result;
        }

        throw new TimeoutException("the condition was never reached; stage " + await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)?.Stage.ToString()));
    }

    private static TramStage? Stage(TramTestWorld world) => PlayerbotTram.TripOf(world.Player)?.Stage;

    private static async Task<TramTestWorld> StartAtStormwindWithTripAsync(IPathfinder? pathfinder = null)
    {
        TramTestWorld world = await TramTestWorld.StartAsync(pathfinder: pathfinder);
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)), "the trip did not begin");
        return world;
    }

    private static bool OnTramWaiting(TramTestWorld world)
        => world.Player.MapId == TramTestData.Tram && PlayerbotTram.TripOf(world.Player) is { Car: not null, Stage: TramStage.ToPlatform or TramStage.Waiting };

    private static void ReplaceMovement(TramTestWorld world, Func<MovementInfo, MovementInfo> change)
    {
        MovementInfo changed = change(world.Player.Movement);
        world.Player.ApplyMovement(changed, world.Host.World.NowMs);
    }

    // --- healthy ride: every key silent --------------------------------------------------------------------------------------------

    [Fact]
    public async Task HealthyRide_ReportsNoTramAnomaly()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        await DriveUntilAsync(world, Options(), () => Stage(world) is TramStage.Done or TramStage.Failed);
        Assert.Equal(TramStage.Done, await world.OnWorldAsync(() => Stage(world)));
        Assert.Empty(capture.OfSubsystem(PlayerbotAnomalies.Subsystem("tram")));
        Assert.False(capture.Any(Key("trip-failed")), capture.Describe());
    }

    // --- trip-failed / teleport-out (no platforms) ---------------------------------------------------------------------------------

    private sealed class NoTriggers : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MapContent(TramTestData.Maps, [], [], [], []));
    }

    private static async Task<TramTestWorld> StartOnTramWithoutDataAsync(bool bindWorks)
    {
        TramTestWorld world = await TramTestWorld.StartAsync(configure: services => services.AddSingleton<IMapDataStore>(new NoTriggers()));
        await world.PlaceAsync(TramTestData.Tram, TramTestData.StormwindPlatform);
        await world.OnWorldAsync(() => world.Player.Home = bindWorks
            ? new HomeBind(0, 1519, TramTestData.StormwindExitLanding.X, TramTestData.StormwindExitLanding.Y, TramTestData.StormwindExitLanding.Z)
            : new HomeBind(9999, 0, 0, 0, 0));
        return world;
    }

    /// <summary>
    /// FINDING (not asserted here): the first failing Drive reports trip-failed twice, once from the no-platforms branch (PlayerbotTram.cs
    /// ~580, reason=no-platforms) and once from the Drive wrapper (~539, reason=failed, because no trip exists to mark FailureReported).
    /// This test pins what holds: the no-platforms report is made exactly once, and later thinks add nothing.
    /// </summary>
    [Fact]
    public async Task TripFailed_Fires_WhenTheTramMapHasNoPlatforms_AndLaterThinksAddNothing()
    {
        await using TramTestWorld world = await StartOnTramWithoutDataAsync(bindWorks: false);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        PlayerbotOptions options = Options();
        await world.TickAsync(() => Drive(world, options));
        int afterFirstThink = capture.Count(Key("trip-failed"));
        for (int i = 0; i < 100; i++) await world.TickAsync(() => Drive(world, options));

        output.WriteLine(capture.Describe());
        Assert.True(afterFirstThink >= 1, "no trip-failed report");
        Assert.Equal(afterFirstThink, capture.Count(Key("trip-failed")));
        Assert.Equal(1, capture.Notices.Count(n => n.Key == Key("trip-failed") && n.Detail.Contains("reason=no-platforms", StringComparison.Ordinal)));
        Assert.Equal(AnomalySeverity.Warning, capture.Notices.First(n => n.Key == Key("trip-failed")).Severity);
    }

    [Fact]
    public async Task TripFailed_StaysSilent_ForABotInAChaosProbe_ThoughTheTripStillFailsClosed()
    {
        await using TramTestWorld world = await StartOnTramWithoutDataAsync(bindWorks: false);
        world.Session.IsInChaosProbe = true;
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        TramStep first = await world.OnWorldAsync(() => Drive(world, Options()));
        Assert.Equal(TramStep.Failed, first);
        Assert.Empty(capture.OfSubsystem(PlayerbotAnomalies.Subsystem("tram")));
    }

    [Fact]
    public async Task TeleportOut_Fires_WhenTheBindPointTeleportIsSent_AndTripFailedFollows()
    {
        await using TramTestWorld world = await StartOnTramWithoutDataAsync(bindWorks: true);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        PlayerbotOptions options = Options();
        for (int i = 0; i < 100; i++) await world.TickAsync(() => Drive(world, options));

        Assert.Equal(1, capture.Count(Key("teleport-out")));
        Assert.Contains("reason=no-platforms-homebind", Report(capture, "teleport-out"));
        Assert.True(capture.Any(Key("trip-failed")), capture.Describe());
    }

    [Fact]
    public async Task TeleportOut_StaysSilent_WhenTheBindPointTeleportIsRefused()
    {
        await using TramTestWorld world = await StartOnTramWithoutDataAsync(bindWorks: false);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        PlayerbotOptions options = Options();
        for (int i = 0; i < 100; i++) await world.TickAsync(() => Drive(world, options));

        Assert.False(capture.Any(Key("teleport-out")), capture.Describe());
        Assert.True(capture.Any(Key("trip-failed")));
    }

    // --- teleport-out (exit unreachable) -------------------------------------------------------------------------------------------

    private sealed class NoWay : IPathfinder
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.None(start);
    }

    [Fact]
    public async Task TeleportOut_Fires_WhenTheExitCannotBeWalkedInto()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync(pathfinder: new NoWay());
        await world.PlaceAsync(TramTestData.Tram, TramTestData.StormwindPlatform with { X = 40f });
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        PlayerbotOptions options = Options();
        // No progress for PlayerbotTram.NoProgressMs (60 s) ends the walk; the trigger's own teleport follows.
        await DriveUntilAsync(world, options, () => world.Player.MapId == 0, maxSteps: 1_500);

        Assert.Equal(1, capture.Count(Key("teleport-out")));
        Assert.Contains("reason=exit-unreachable", Report(capture, "teleport-out"));
    }

    [Fact]
    public async Task TeleportOut_StaysSilent_WhenTheBotWalksOutByTheExit()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync();
        await world.PlaceAsync(TramTestData.Tram, TramTestData.StormwindPlatform with { X = 40f });
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        await DriveUntilAsync(world, Options(), () => world.Player.MapId == 0, maxSteps: 1_500);

        Assert.False(capture.Any(Key("teleport-out")), capture.Describe());
        Assert.False(capture.Any(Key("trip-failed")), capture.Describe());
    }

    // --- trip-slow -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TripSlow_Fires_OnceWhenTheBotHasBeenOnTheTramMapPastTwoCyclesAndTheWalkAllowance()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => world.Player.MapId == TramTestData.Tram);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);

        await world.AdvanceAsync(SlowBoundMs + 10_000);
        await world.OnWorldAsync(() => Drive(world, options));
        Assert.Equal(1, capture.Count(Key("trip-slow")));
        Assert.Contains("stage=", Report(capture, "trip-slow"));

        await world.OnWorldAsync(() => Drive(world, options));
        await world.AdvanceAsync(1_000);
        await world.OnWorldAsync(() => Drive(world, options));
        Assert.Equal(1, capture.Count(Key("trip-slow")));
    }

    [Fact]
    public async Task TripSlow_StaysSilent_JustInsideTheLimit()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => world.Player.MapId == TramTestData.Tram);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        uint sinceMs = await world.OnWorldAsync(() => world.Host.World.NowMs - PlayerbotTram.TripOf(world.Player)!.PlatformSinceMs);

        await world.AdvanceAsync(SlowBoundMs - sinceMs - 2_000);
        await world.OnWorldAsync(() => Drive(world, options));
        Assert.False(capture.Any(Key("trip-slow")), capture.Describe());
    }

    /// <summary>
    /// #1499: a legitimate trip (walk, a wait for the next car, the ride, the walk out) can pass 180 s on the tram map. The synthetic
    /// fixture has a 120 s cycle; 200 s on the map is under two cycles and is no slow trip.
    /// </summary>
    [Fact]
    public async Task TripSlow_StaysSilent_ForALegitimateTripOfAbout200Seconds()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => world.Player.MapId == TramTestData.Tram);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        uint sinceMs = await world.OnWorldAsync(() => world.Host.World.NowMs - PlayerbotTram.TripOf(world.Player)!.PlatformSinceMs);

        await world.AdvanceAsync(200_000 - sinceMs);
        await world.OnWorldAsync(() => Drive(world, options));
        Assert.False(capture.Any(Key("trip-slow")), capture.Describe());
    }

    /// <summary>#1499: a missed car is reported and dropped by the per-tick pump (not at the next, 12-23 s later, think) when the bot is not beside the soonest new spot.</summary>
    [Fact]
    public async Task MissedCar_IsReportedAndDropped_ByThePump_WhenTheBotIsNotBesideTheSoonestSpot()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => OnTramWaiting(world));
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        uint departMs = await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)!.Dock.DepartMs);

        await world.AdvanceAsync(departMs + 5_000);
        await world.OnWorldAsync(() => PlayerbotMotion.Pump(world.Session, world.Player, world.Host.World.NowMs));

        (bool hasCar, bool hasRoute, TramStage stage) = await world.OnWorldAsync(() =>
        {
            TramTrip trip = PlayerbotTram.TripOf(world.Player)!;
            return (trip.Car is not null, trip.Route is not null, trip.Stage);
        });
        output.WriteLine($"hasCar={hasCar} hasRoute={hasRoute} stage={stage}");
        Assert.False(hasCar, "the bot is not beside the new spot (it was still walking): the choice is dropped for the next think");
        Assert.False(hasRoute, "the pump keeps a car only if no walk is needed (it cannot start one)");
        Assert.Equal(1, capture.Count(Key("board-missed")));
    }

    /// <summary>
    /// #1499: a bot waiting beside its car's spot when the car leaves keeps the choice of the next stop made by the pump (no walk is
    /// needed, so nothing is planned and thrown away), and boards it without a think.
    /// </summary>
    [Fact]
    public async Task MissedCar_IsKeptByThePump_WhenTheBotIsAlreadyBesideTheNewSpot()
    {
        // One car only: its next stop is the soonest, and the bot stands beside it.
        await using TramTestWorld world = await TramTestWorld.StartAsync(animation: TramTestData.SyntheticAnimation(176080));
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)), "the trip did not begin");
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => world.Player.MapId == TramTestData.Tram && Stage(world) is TramStage.Waiting);
        (uint departMs, uint chosenMs) = await world.OnWorldAsync(() =>
        {
            TramTrip trip = PlayerbotTram.TripOf(world.Player)!;
            return (trip.Dock.DepartMs, trip.DockChosenMs);
        });

        await world.AdvanceAsync(departMs + 5_000);
        uint now = await world.OnWorldAsync(() =>
        {
            PlayerbotMotion.Pump(world.Session, world.Player, world.Host.World.NowMs);
            return world.Host.World.NowMs;
        });

        (uint rechosenMs, bool hasCar, bool hasRoute, TramStage stage) = await world.OnWorldAsync(() =>
        {
            TramTrip trip = PlayerbotTram.TripOf(world.Player)!;
            return (trip.DockChosenMs, trip.Car is not null, trip.Route is not null, trip.Stage);
        });
        output.WriteLine($"hasCar={hasCar} hasRoute={hasRoute} stage={stage}");
        Assert.NotEqual(chosenMs, rechosenMs);
        Assert.Equal(now, rechosenMs);
        Assert.True(hasCar, "the bot stands beside the spot: the choice is kept");
        Assert.False(hasRoute);
        Assert.Equal(TramStage.Waiting, stage);
    }

    /// <summary>
    /// #1499 (review B1): with both trains running, a bot beside its missed car's spot must not have the pump lock in that car's next
    /// stop (a full cycle later) over the other train's, which stops sooner on the other track. The pump keeps the soonest stop or none,
    /// so here (the other train stops sooner on the other track, and the bot is not beside it) the pump keeps nothing and the next think
    /// chooses the other train's car.
    /// </summary>
    [Fact]
    public async Task MissedCar_PumpNeverKeepsALaterStop_ThanAFullChoice()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => world.Player.MapId == TramTestData.Tram && Stage(world) is TramStage.Waiting);
        (uint departMs, uint missedGuid) = await world.OnWorldAsync(() =>
        {
            TramTrip trip = PlayerbotTram.TripOf(world.Player)!;
            return (trip.Dock.DepartMs, trip.Car!.Guid.Entry);
        });

        await world.AdvanceAsync(departMs + 5_000);
        (uint? keptArrive, uint fullArrive, uint fullEntry) = await world.OnWorldAsync(() =>
        {
            PlayerbotMotion.Pump(world.Session, world.Player, world.Host.World.NowMs);
            TramTrip trip = PlayerbotTram.TripOf(world.Player)!;
            uint? kept = trip.Car is not null ? trip.Dock.ArriveMs : null;
            trip.Car = null;
            Drive(world, options);
            return (kept, trip.Dock.ArriveMs, trip.Car!.Guid.Entry);
        });

        output.WriteLine($"missed={missedGuid} keptArrive={keptArrive} fullArrive={fullArrive} fullCar={fullEntry}");
        Assert.NotEqual(missedGuid, fullEntry);
        Assert.Null(keptArrive);
    }

    /// <summary>
    /// #1499: a bot that misses its car still finishes inside the slow bound: the wait for the next stop is under one cycle (the other
    /// train stops sooner), so the whole trip stays under 2 cycles + 60 s and is reported neither slow nor failed.
    /// </summary>
    [Fact]
    public async Task MissedCar_TripDuration_StaysInsideTheSlowBound()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        PlayerbotOptions options = Options();
        uint startMs = await world.OnWorldAsync(() => world.Host.World.NowMs);
        await DriveUntilAsync(world, options, () => world.Player.MapId == TramTestData.Tram && Stage(world) is TramStage.Waiting);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        uint departMs = await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)!.Dock.DepartMs);

        await world.AdvanceAsync(departMs + 5_000);
        await DriveUntilAsync(world, options, () => Stage(world) is TramStage.Done or TramStage.Failed);
        uint tripMs = await world.OnWorldAsync(() => world.Host.World.NowMs - startMs);

        output.WriteLine($"tripMs={tripMs} bound={SlowBoundMs}");
        Assert.Equal(TramStage.Done, await world.OnWorldAsync(() => Stage(world)));
        Assert.True(tripMs < SlowBoundMs, $"trip took {tripMs} ms, bound {SlowBoundMs} ms");
        Assert.False(capture.Any(Key("trip-slow")), capture.Describe());
    }

    /// <summary>
    /// #1499: a trip that already failed (no car runs: the bot leaves by the platform it stands at) is a failed trip. It is reported as
    /// <c>trip-failed</c> when the bot is out, and never as <c>trip-slow</c>, however long the way out takes.
    /// </summary>
    [Fact]
    public async Task FailedTrip_IsReportedAsFailed_NotSlow()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync(animation: TransportAnimationCatalog.Empty);
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)), "the trip did not begin");
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => Stage(world) is TramStage.ToExit && world.Player.MapId == TramTestData.Tram);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        Assert.NotNull(await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)!.Failure));

        await world.AdvanceAsync(400_000);
        await DriveUntilAsync(world, options, () => Stage(world) is TramStage.Done or TramStage.Failed);

        Assert.True(capture.Any(Key("trip-failed")), capture.Describe());
        Assert.Contains("reason=no-car", Report(capture, "trip-failed"));
        Assert.False(capture.Any(Key("trip-slow")), capture.Describe());
    }

    // --- board-missed ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BoardMissed_Fires_OnceWhenTheChosenCarHasLeftWithoutTheBot()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => OnTramWaiting(world));
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        uint departMs = await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)!.Dock.DepartMs);

        await world.AdvanceAsync(departMs + 5_000);
        await world.OnWorldAsync(() => Drive(world, options));
        Assert.Equal(1, capture.Count(Key("board-missed")));
        Assert.Contains("platform=", Report(capture, "board-missed"));

        // The car chosen next is a new episode only when it too is missed: the same choice reports once.
        await world.OnWorldAsync(() => Drive(world, options));
        Assert.Equal(1, capture.Count(Key("board-missed")));
    }

    [Fact]
    public async Task BoardMissed_StaysSilent_WhileTheChosenCarHasNotYetLeft()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => OnTramWaiting(world));
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        uint departMs = await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)!.Dock.DepartMs);
        output.WriteLine("departMs=" + departMs);
        Assert.True(departMs > 3_000, "the fixture's dock leaves too soon for the boundary to be tested");

        await world.AdvanceAsync(departMs - 2_000);
        await world.OnWorldAsync(() => Drive(world, options));
        Assert.False(capture.Any(Key("board-missed")), capture.Describe());
    }

    // --- detached-mid-ride -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, "transport-flag-cleared")]
    [InlineData(false, "transport-guid-mismatch")]
    public async Task DetachedMidRide_Fires_OnceWhenTheBotNoLongerStandsOnItsCar(bool clearFlag, string reason)
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => Stage(world) == TramStage.Aboard);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        await world.OnWorldAsync(() => ReplaceMovement(world, m =>
        {
            if (clearFlag)
            {
                m.Flags &= ~MovementFlags.OnTransport;
                m.TransportGuid = 0;
            }
            else
            {
                m.TransportGuid += 12345;
            }

            return m;
        }));

        await world.OnWorldAsync(() => Drive(world, options));
        Assert.Equal(1, capture.Count(Key("detached-mid-ride")));
        Assert.Contains("reason=" + reason, Report(capture, "detached-mid-ride"));
        for (int i = 0; i < 20; i++) await world.TickAsync(() => Drive(world, options));
        Assert.Equal(1, capture.Count(Key("detached-mid-ride")));
    }

    [Fact]
    public async Task DetachedMidRide_StaysSilent_WhileTheBotStandsOnItsCar()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => Stage(world) == TramStage.Aboard);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        for (int i = 0; i < 30; i++) await world.TickAsync(() => Drive(world, options));

        Assert.False(capture.Any(Key("detached-mid-ride")), capture.Describe());
    }

    // --- left-behind -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LeftBehind_Fires_OnceWhenTheBotLagsFarBehindItsSeat()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => Stage(world) == TramStage.Aboard);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        await world.OnWorldAsync(() => ReplaceMovement(world, m => { m.X += PlayerbotTram.LeftBehindYards + 50f; return m; }));

        await world.OnWorldAsync(() => Drive(world, options));
        Assert.Equal(1, capture.Count(Key("left-behind")));
        Assert.Contains("driftYards=", Report(capture, "left-behind"));
        await world.OnWorldAsync(() => ReplaceMovement(world, m => { m.X += 10f; return m; }));
        await world.OnWorldAsync(() => Drive(world, options));
        Assert.Equal(1, capture.Count(Key("left-behind")));
    }

    [Fact]
    public async Task LeftBehind_StaysSilent_WhenTheBotLagsLessThanTheLimit()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => Stage(world) == TramStage.Aboard);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        await world.OnWorldAsync(() => ReplaceMovement(world, m => { m.X += PlayerbotTram.LeftBehindYards - 50f; return m; }));

        await world.OnWorldAsync(() => Drive(world, options));
        Assert.False(capture.Any(Key("left-behind")), capture.Describe());
    }

    // --- passed-destination ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PassedDestination_Fires_OnceWhenTheCarNeverStandsAtTheDestinationPlatform()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => Stage(world) == TramStage.Aboard);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);

        // No thinks for more than a cycle and 30 s: the car went to the far platform and back unseen. Look when it is mid-tunnel, so it is
        // not standing at the destination at that think (which would step off instead).
        await world.AdvanceAsync(151_000);
        for (int attempt = 0; attempt < 40; attempt++)
        {
            Vector3 before = await world.OnWorldAsync(() => CarAt(world));
            await world.AdvanceAsync(500);
            Vector3 after = await world.OnWorldAsync(() => CarAt(world));
            if (Vector3.Distance(before, after) > 5f) break;
        }

        await world.OnWorldAsync(() => Drive(world, options));
        Assert.True(capture.Count(Key("passed-destination")) == 1, capture.Describe());
        await world.OnWorldAsync(() => Drive(world, options));
        Assert.Equal(1, capture.Count(Key("passed-destination")));
    }

    [Fact]
    public async Task PassedDestination_StaysSilent_WhenTheCarStandsAtTheDestinationAndTheBotStepsOff()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => Stage(world) == TramStage.Aboard);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        await DriveUntilAsync(world, options, () => Stage(world) is TramStage.ToExit or TramStage.Done or TramStage.Failed);

        Assert.NotEqual(TramStage.Failed, await world.OnWorldAsync(() => Stage(world)));
        Assert.False(capture.Any(Key("passed-destination")), capture.Describe());
    }

    private static Vector3 CarAt(TramTestWorld world)
    {
        GameObject car = PlayerbotTram.TripOf(world.Player)!.Car!;
        (float x, float y, float z) = world.TramObjects()!.ElevatorPosition(car)!.Value;
        return new Vector3(x, y, z);
    }

    // --- arrived-wrong-station -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ArrivedWrongStation_Fires_WhenTheExitPutsTheBotFarFromTheIntendedArrival()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => OnTramWaiting(world));
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)!.Stage = TramStage.ToExit);
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding); // the trip meant Ironforge

        TramStep step = await world.OnWorldAsync(() => Drive(world, options));
        Assert.Equal(TramStep.Failed, step);
        Assert.Equal(1, capture.Count(Key("arrived-wrong-station")));
        Assert.Contains("intended=", Report(capture, "arrived-wrong-station"));
        await world.OnWorldAsync(() => Drive(world, options));
        Assert.Equal(1, capture.Count(Key("arrived-wrong-station")));
    }

    [Fact]
    public async Task ArrivedWrongStation_StaysSilent_WhenTheExitPutsTheBotAtTheIntendedArrival()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => OnTramWaiting(world));
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)!.Stage = TramStage.ToExit);
        await world.PlaceAsync(0, TramTestData.IronforgeExitLanding);

        TramStep step = await world.OnWorldAsync(() => Drive(world, options));
        Assert.Equal(TramStep.Arrived, step);
        Assert.Empty(capture.OfSubsystem(PlayerbotAnomalies.Subsystem("tram")));
    }

    // --- the pump (H1): a bot that boards is carried through and arrives with thinks 12 s apart -------------------------------------

    /// <summary>
    /// The tram pump samples the trip every 250 ms from the world tick, so a population that spaces brain thinks 12 s apart still
    /// boards a car that stands for ~13 s, and rides it. Thinks (<see cref="PlayerbotTram.Drive"/>) run once per 12 s here; every
    /// 50 ms tick pumps. The bot must reach Aboard on a tick that ran no think, and finish at Ironforge with no tram anomaly.
    /// </summary>
    [Fact]
    public async Task WithThinksTwelveSecondsApart_ThePumpBoardsTheCar_AndTheBotArrivesAtIronforge()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        PlayerbotOptions options = Options();
        // Up to the platform at the brain's normal cadence: the walk to the entrance and the car choice are the brain's.
        await DriveUntilAsync(world, options, () => OnTramWaiting(world));

        const int ThinkEveryTicks = 120; // 12 s of 100 ms ticks
        bool boardedByPump = false;
        TramStage? last = await world.OnWorldAsync(() => Stage(world));
        var stages = new List<TramStage>();
        for (int tick = 1; tick < 8_000; tick++)
        {
            bool think = tick % ThinkEveryTicks == 0;
            TramStage? stage = await world.TickAsync(() =>
            {
                if (think) Drive(world, options);
                return Stage(world);
            });
            if (stage is { } s && (stages.Count == 0 || stages[^1] != s)) stages.Add(s);
            if (!think && last is TramStage.ToPlatform or TramStage.Waiting && stage == TramStage.Aboard) boardedByPump = true;
            last = stage;
            if (stage is TramStage.Done or TramStage.Failed) break;
        }

        output.WriteLine("stages after the platform: " + string.Join(" > ", stages));
        Assert.True(boardedByPump, "the bot was not boarded by the pump between thinks");
        Assert.Equal(TramStage.Done, stages[^1]);
        (uint mapId, Vector3 at) = await world.OnWorldAsync(() => (world.Player.MapId, new Vector3(world.Player.X, world.Player.Y, world.Player.Z)));
        Assert.Equal(0u, mapId);
        Assert.True(Vector2.Distance(new(at.X, at.Y), new(TramTestData.IronforgeExitLanding.X, TramTestData.IronforgeExitLanding.Y)) < 5f,
            $"out at {at}, not at the Ironforge exit");
        Assert.Empty(capture.OfSubsystem(PlayerbotAnomalies.Subsystem("tram")));
    }

    [Fact]
    public async Task EnteringTramMap_AfterLongContinentWalkInToEntrance_TransitionsToPlatformBeforeSlowCheck()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        PlayerbotOptions options = Options();

        await world.AdvanceAsync(SlowBoundMs + 30_000);
        await world.PlaceAsync(TramTestData.Tram, TramTestData.StormwindPlatform);

        TramStep step = await world.OnWorldAsync(() => Drive(world, options));
        Assert.NotEqual(TramStep.Failed, step);
        Assert.NotEqual(TramStage.ToEntrance, await world.OnWorldAsync(() => Stage(world)));
        Assert.False(capture.Any(Key("trip-slow")), capture.Describe());
    }

    /// <summary>Open on the continent, nothing on the tram map: a trip that reaches the tram finds no way to a car (<c>no-way-to-platform</c>).</summary>
    private sealed class OpenExceptOnTheTram : IPathfinder
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => mapId == TramTestData.Tram ? PathResult.None(start) : PathResult.StraightLine(start, end, PathType.Normal);
    }

    /// <summary>
    /// #1499 with a running car (cycle 120 s, so the slow bound is live): a trip that failed at the platform (no way to a car) and then
    /// takes longer than the bound to get out is reported as failed, never as slow.
    /// </summary>
    [Fact]
    public async Task FailedTrip_WithARunningCar_PastTheSlowBound_IsReportedAsFailed_NotSlow()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync(new OpenExceptOnTheTram());
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => world.Player.MapId == TramTestData.Tram && Stage(world) is TramStage.ToExit && PlayerbotTram.TripOf(world.Player)!.Failure is not null);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        Assert.NotNull(await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)!.FailureCategory));

        await world.AdvanceAsync(SlowBoundMs + 30_000);
        await DriveUntilAsync(world, options, () => Stage(world) is TramStage.Done or TramStage.Failed);

        Assert.True(capture.Any(Key("trip-failed")), capture.Describe());
        Assert.False(capture.Any(Key("trip-slow")), capture.Describe());
    }

    /// <summary>
    /// A car missed with thinks 12 s apart: the pump chooses again at once but cannot walk, so a car whose spot needs a walk is left
    /// to the next think (route and car never both set after a pump-only tick), and the bot still boards a car and arrives, with the
    /// miss reported once.
    /// </summary>
    [Fact]
    public async Task MissedCar_WithThinksTwelveSecondsApart_IsNeverLeftWithAWalkOnlyAThinkCanStart()
    {
        await using TramTestWorld world = await StartAtStormwindWithTripAsync();
        PlayerbotOptions options = Options();
        await DriveUntilAsync(world, options, () => OnTramWaiting(world));
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        uint departMs = await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)!.Dock.DepartMs);
        await world.AdvanceAsync(departMs + 5_000);

        const int ThinkEveryTicks = 120;
        bool walkLeftToThePump = false;
        TramStage? stage = null;
        uint lastChosenMs = await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)!.DockChosenMs);
        for (int tick = 1; tick < 12_000 && stage is not (TramStage.Done or TramStage.Failed); tick++)
        {
            bool think = tick % ThinkEveryTicks == 0;
            uint before = lastChosenMs;
            (stage, lastChosenMs, bool chosenByPumpWithWalk) = await world.TickAsync(() =>
            {
                if (think) Drive(world, options);
                TramTrip? trip = PlayerbotTram.TripOf(world.Player);
                return (trip?.Stage, trip?.DockChosenMs ?? before, !think && trip is { Car: not null, Route: not null } && trip.DockChosenMs != before);
            });
            walkLeftToThePump |= chosenByPumpWithWalk;
        }

        Assert.False(walkLeftToThePump, "the pump chose a car whose walk only a think can start");
        Assert.Equal(TramStage.Done, stage);
        Assert.Equal(1, capture.Count(Key("board-missed")));
    }
}

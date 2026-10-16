using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// H1 outcome checks of the game object system: <c>door/collision-missing</c>, <c>door/state-desync</c> (the door audit) and the
/// <c>elevator-car</c> family (<c>no-animation</c>, <c>stop-too-short</c>, <c>position-non-finite</c>). Every scenario spawns its own entry,
/// and each capture filters on that entry, because the ledger is process-wide.
/// </summary>
public sealed class DoorElevatorAnomalyTests
{
    private const uint MissingDisplayDoor = 948001;
    private const uint ZeroSizeDoor = 948002;
    private const uint NanSizeDoor = 948003;
    private const uint HealthyDoor = 948004;
    private const uint OpenNoDisplayDoor = 948005;
    private const uint RearmDoor = 948006;
    private const uint DesyncDoor = 948010;
    private const uint DesyncRearmDoor = 948011;
    private const uint OpenedByUseDoor = 948012;
    private const uint SpawnedOpenDoor = 948013;
    private const uint ChestEntry = 948014;
    private const uint NoAnimationCar = 948020;
    private const uint HealthyCar = 948021;
    private const uint ShortStopCar = 948022;
    private const uint StopAtLimitCar = 948023;
    private const uint StopBelowLimitCar = 948024;
    private const uint NanRotationCar = 948025;
    private const uint NanNodeCar = 948026;
    private const uint FiniteCar = 948027;
    private const uint WrapStopTram = 948028;
    private const uint BouncingLift = 948029;
    private const uint SampledTram = 948030;
    private const uint SampledShortLift = 948031;

    private sealed class Rig : IDisposable
    {
        public Rig(GameObjectTemplate[] templates, GameObjectSpawn[] spawns, TransportAnimationCatalog? animations = null)
        {
            World = TestWorld.CreateRuntime();
            Map map = World.GetMap(0);
            System = new GameObjectMapSystem(map, new GameObjectContent(templates, spawns, [], [], [])) { Random = new Random(5) };
            _animations = animations;
            map.AddUpdater(System);
            Player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
            World.AddPlayer(Player);
        }

        /// <summary>Let the first tick load the grid; call it after the capture is armed, because a check reports once per episode.</summary>
        public void Load()
        {
            if (_animations is not null)
            {
                System.ElevatorAnimations = _animations; // validates the car spawns, once per episode
            }

            World.RunTick(50);
        }

        private readonly TransportAnimationCatalog? _animations;

        public WorldRuntime World { get; }

        public GameObjectMapSystem System { get; }

        public Player Player { get; }

        public GameObject Object(uint entry) => System.GameObjects.Single(g => g.Entry == entry);

        public void Tick(int count = 1)
        {
            for (int i = 0; i < count; i++)
            {
                World.RunTick(100);
            }
        }

        public void Dispose() => World.Dispose();
    }

    private static AnomalyCapture CaptureEntry(string key, uint entry)
        => AnomalyLedger.Shared.Capture(n => n.Key == key && n.Subject.Contains($"entry {entry} "));

    private static GameObjectTemplate Door(uint entry, Func<GameObjectTemplate, GameObjectTemplate>? shape = null)
    {
        GameObjectTemplate template = GoTemplate(entry, GameObjectType.Door);
        return shape is null ? template : shape(template);
    }

    // --- door/collision-missing ---------------------------------------------------------------------------

    [Theory]
    [InlineData(MissingDisplayDoor, 0u, 1.0f)]
    [InlineData(ZeroSizeDoor, 4000u, 0.0f)]
    [InlineData(NanSizeDoor, 4000u, float.NaN)]
    public void AClosedDoorWithoutAModelOrSize_ReportsCollisionMissing_OncePerEpisode(uint entry, uint displayId, float size)
    {
        using var rig = new Rig([Door(entry, t => t with { DisplayId = displayId, Size = size })], [GoSpawn(1, entry, 3, 0)]);
        using AnomalyCapture capture = CaptureEntry("door/collision-missing", entry);
        rig.Load();

        rig.Tick(5);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("Closed door", notice.Detail);
        Assert.Equal(AnomalySeverity.Warning, notice.Severity);
    }

    [Fact]
    public void AClosedDoorWithAModelAndASize_ReportsNothing()
    {
        using var rig = new Rig([Door(HealthyDoor)], [GoSpawn(1, HealthyDoor, 3, 0)]);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key.StartsWith("door/", StringComparison.Ordinal) && n.Subject.Contains($"entry {HealthyDoor} "));
        rig.Load();

        rig.Tick(5);

        Assert.NotNull(rig.Object(HealthyDoor));
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void AnOpenDoorWithoutAModel_IsNotReported_BecauseOnlyAClosedDoorNeedsCollision()
    {
        // spawn state 0 = active/open
        using var rig = new Rig([Door(OpenNoDisplayDoor, t => t with { DisplayId = 0 })], [GoSpawn(1, OpenNoDisplayDoor, 3, 0) with { State = 0 }]);
        using AnomalyCapture capture = CaptureEntry("door/collision-missing", OpenNoDisplayDoor);
        rig.Load();

        rig.Tick(5);

        Assert.Equal(GameObjectState.Active, rig.Object(OpenNoDisplayDoor).State);
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void ACollisionEpisodeEnds_WhenTheDoorOpens_AndItReportsAgainWhenItClosesStillBroken()
    {
        using var rig = new Rig([Door(RearmDoor, t => t with { DisplayId = 0 })], [GoSpawn(1, RearmDoor, 3, 0)]);
        using AnomalyCapture capture = CaptureEntry("door/collision-missing", RearmDoor);
        rig.Load();
        GameObject door = rig.Object(RearmDoor);

        rig.Tick(2);
        Assert.Single(capture.Notices);

        door.State = GameObjectState.Active;
        rig.Tick(2);
        door.State = GameObjectState.Ready;
        rig.Tick(2);

        Assert.Equal(2, capture.Notices.Count);
    }

    // --- door/state-desync --------------------------------------------------------------------------------

    [Fact]
    public void AnActivatedDoorWhoseStateIsStillReady_ReportsStateDesync_OncePerEpisode()
    {
        using var rig = new Rig([Door(DesyncDoor)], [GoSpawn(1, DesyncDoor, 3, 0)]);
        using AnomalyCapture capture = CaptureEntry("door/state-desync", DesyncDoor);
        rig.Load();
        GameObject door = rig.Object(DesyncDoor);

        door.LootState = GameObjectLootState.Activated; // activated, yet State and the spawn state both read Ready (closed)
        rig.Tick(5);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("LootState=Activated", notice.Detail);
    }

    [Fact]
    public void AStateDesyncEpisodeEnds_WhenTheDoorIsConsistentAgain_AndReportsAgainOnTheNextDesync()
    {
        using var rig = new Rig([Door(DesyncRearmDoor)], [GoSpawn(1, DesyncRearmDoor, 3, 0)]);
        using AnomalyCapture capture = CaptureEntry("door/state-desync", DesyncRearmDoor);
        rig.Load();
        GameObject door = rig.Object(DesyncRearmDoor);

        door.LootState = GameObjectLootState.Activated;
        rig.Tick(2);
        door.LootState = GameObjectLootState.Ready;
        rig.Tick(2);
        Assert.Single(capture.Notices);

        door.LootState = GameObjectLootState.Activated;
        rig.Tick(2);

        Assert.Equal(2, capture.Notices.Count);
    }

    [Fact]
    public void ADoorOpenedByAPlayerUse_IsActivatedWithoutADesync_AndClosesAgainWithoutOne()
    {
        // data2 = auto close after 3 s (stored as seconds * 0x10000)
        var template = GoTemplate(OpenedByUseDoor, GameObjectType.Door, (2, 3 * 65536));
        using var rig = new Rig([template], [GoSpawn(1, OpenedByUseDoor, 3, 0)]);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key.StartsWith("door/", StringComparison.Ordinal) && n.Subject.Contains($"entry {OpenedByUseDoor} "));
        rig.Load();
        GameObject door = rig.Object(OpenedByUseDoor);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(rig.Player, door.Guid));
        Assert.Equal(GameObjectLootState.Activated, door.LootState);
        Assert.Equal(GameObjectState.Active, door.State);
        rig.Tick(5);
        Assert.Empty(capture.Notices);

        rig.Tick(40); // past the auto-close: the door resets to its spawn state
        Assert.Equal(GameObjectLootState.Ready, door.LootState);
        Assert.Equal(GameObjectState.Ready, door.State);
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void ADoorSpawnedOpen_ThatIsActivated_IsNotReported()
    {
        using var rig = new Rig([Door(SpawnedOpenDoor)], [GoSpawn(1, SpawnedOpenDoor, 3, 0) with { State = 0 }]);
        using AnomalyCapture capture = CaptureEntry("door/state-desync", SpawnedOpenDoor);
        rig.Load();
        GameObject door = rig.Object(SpawnedOpenDoor);

        door.LootState = GameObjectLootState.Activated;
        door.State = GameObjectState.Ready; // a spawned-open door that was then used is closed by it; the spawn state says it normally stands open
        rig.Tick(5);

        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void ANonDoorObject_IsNeverAuditedAsADoor()
    {
        GameObjectTemplate chest = GoTemplate(ChestEntry, GameObjectType.Chest) with { DisplayId = 0 };
        using var rig = new Rig([chest], [GoSpawn(1, ChestEntry, 3, 0)]);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key.StartsWith("door/", StringComparison.Ordinal) && n.Subject.Contains($"entry {ChestEntry} "));
        rig.Load();
        GameObject go = rig.Object(ChestEntry);

        go.LootState = GameObjectLootState.Activated;
        rig.Tick(5);

        Assert.Empty(capture.Notices);
    }

    // --- elevator-car -------------------------------------------------------------------------------------

    /// <summary>WorldObject.SetFloat refuses a non-finite value, so a corrupt rotation can only arrive as raw field bits (a bad row, a bad packet read).</summary>
    private static void SetRawRotation(GameObject go, float value)
        => go.SetUInt32(UpdateFields.GameobjectRotation, BitConverter.SingleToUInt32Bits(value));

    private static GameObjectTemplate Car(uint entry) => GoTemplate(entry, GameObjectType.Transport, (0, 3000));

    /// <summary>A lift cycle: stationary at the bottom for <paramref name="stopMs"/>, a 5 s rise, stationary at the top for <paramref name="stopMs"/>, a 5 s drop.</summary>
    private static TransportAnimationCatalog Lift(uint entry, uint stopMs) => new(
    [
        (entry, new TransportAnimationNode(0, 0, 0, 0)),
        (entry, new TransportAnimationNode(stopMs, 0, 0, 0)),
        (entry, new TransportAnimationNode(stopMs + 5000, 0, 0, 20)),
        (entry, new TransportAnimationNode((2 * stopMs) + 5000, 0, 0, 20)),
        (entry, new TransportAnimationNode((2 * stopMs) + 10000, 0, 0, 0)),
    ]);

    [Fact]
    public void ATransportWithoutAnimationKeyframes_ReportsNoAnimation_OnceNotEveryTick()
    {
        using var rig = new Rig([Car(NoAnimationCar)], [GoSpawn(1, NoAnimationCar, 3, 0)]);
        using AnomalyCapture capture = CaptureEntry("elevator-car/no-animation", NoAnimationCar);
        rig.Load();

        rig.Tick(10);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("no TransportAnimation keyframes", notice.Detail);
    }

    [Fact]
    public void ALiftWithLongStops_ReportsNeitherNoAnimationNorShortStops()
    {
        using var rig = new Rig([Car(HealthyCar)], [GoSpawn(1, HealthyCar, 3, 0)], Lift(HealthyCar, 5000));
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key.StartsWith("elevator-car/", StringComparison.Ordinal) && n.Subject.Contains($"entry {HealthyCar} "));
        rig.Load();

        rig.Tick(20);

        Assert.True(rig.System.ElevatorCycle(rig.Object(HealthyCar)) > 0, "the lift has no cycle, so the silence proves nothing");
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void ALiftThatStopsForHalfASecond_ReportsStopTooShort_WithTheShortestStop()
    {
        using var rig = new Rig([Car(ShortStopCar)], [GoSpawn(1, ShortStopCar, 3, 0)], Lift(ShortStopCar, 500));
        using AnomalyCapture capture = CaptureEntry("elevator-car/stop-too-short", ShortStopCar);
        rig.Load();

        rig.Tick(10);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("floor stop duration 500 ms", notice.Detail);
    }

    [Fact]
    public void TheStopLimit_IsInclusive_At1000Ms_AndReportsAt999Ms()
    {
        using var atLimit = new Rig([Car(StopAtLimitCar)], [GoSpawn(1, StopAtLimitCar, 3, 0)], Lift(StopAtLimitCar, GameObjectMapSystem.MinElevatorStopMs));
        using var belowLimit = new Rig([Car(StopBelowLimitCar)], [GoSpawn(1, StopBelowLimitCar, 3, 0)], Lift(StopBelowLimitCar, GameObjectMapSystem.MinElevatorStopMs - 1));
        using AnomalyCapture at = CaptureEntry("elevator-car/stop-too-short", StopAtLimitCar);
        using AnomalyCapture below = CaptureEntry("elevator-car/stop-too-short", StopBelowLimitCar);

        atLimit.Load();
        belowLimit.Load();
        atLimit.Tick(5);
        belowLimit.Tick(5);

        Assert.Empty(at.Notices);
        Assert.Single(below.Notices);
    }

    /// <summary>
    /// The Deeprun Tram car 176083 of TransportAnimation.dbc (5875), abridged to its stops: it rests at its platform from 129,333 ms to the
    /// 143,333 ms end of its cycle and on from 0 to 867 ms (the cycle wraps), and 11,233 ms at the far platform.
    /// </summary>
    private static TransportAnimationCatalog WrapStop(uint entry) => new(
    [
        (entry, new TransportAnimationNode(0, 0, 0, 0)),
        (entry, new TransportAnimationNode(867, 0, 0, 0)),
        (entry, new TransportAnimationNode(4951, -209.990f, 0, -0.107f)),
        (entry, new TransportAnimationNode(59500, -2481.768f, 0, -1.027f)),
        (entry, new TransportAnimationNode(70733, -2481.768f, 0, -1.027f)),
        (entry, new TransportAnimationNode(125308, -209.990f, 0, -0.107f)),
        (entry, new TransportAnimationNode(129333, 0, 0, 0)),
        (entry, new TransportAnimationNode(142465, 0, 0, 0)),
        (entry, new TransportAnimationNode(143333, 0, 0, 0)),
    ]);

    /// <summary>
    /// The Blackrock Spire lift 149045 of TransportAnimation.dbc (5875), abridged: 3,233 ms at the bottom, up to its top floor for 3,267 ms,
    /// then a settling bounce that holds 133 ms within 1.4 yd of the top (27,100-27,233 ms in the DBC) before it goes back down.
    /// </summary>
    private static TransportAnimationCatalog Bouncing(uint entry) => new(
    [
        (entry, new TransportAnimationNode(0, 0, 0, 0)),
        (entry, new TransportAnimationNode(3233, 0, 0, 0)),
        (entry, new TransportAnimationNode(23533, 0, 0, 61.392f)),
        (entry, new TransportAnimationNode(26800, 0, 0, 61.392f)),
        (entry, new TransportAnimationNode(27100, 0, 0, 59.997f)),
        (entry, new TransportAnimationNode(27233, 0, 0, 60.039f)),
        (entry, new TransportAnimationNode(27400, 0, 0, 59.839f)),
        (entry, new TransportAnimationNode(49933, 0, 0, 0.119f)),
    ]);

    [Fact]
    public void ATramWhosePlatformStopSpansTheCycleWrap_IsOneLongStop_NotAShortOne()
    {
        using var rig = new Rig([Car(WrapStopTram)], [GoSpawn(1, WrapStopTram, 3, 0)], WrapStop(WrapStopTram));
        using AnomalyCapture capture = CaptureEntry("elevator-car/stop-too-short", WrapStopTram);
        rig.Load();

        rig.Tick(5);

        Assert.Equal((11233u, 2), GameObjectMapSystem.ShortestFloorStop(WrapStop(WrapStopTram).Nodes(WrapStopTram)));
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void ALiftThatBouncesBrieflyAtAFloorItRestsAt_IsNotAShortStop()
    {
        using var rig = new Rig([Car(BouncingLift)], [GoSpawn(1, BouncingLift, 3, 0)], Bouncing(BouncingLift));
        using AnomalyCapture capture = CaptureEntry("elevator-car/stop-too-short", BouncingLift);
        rig.Load();

        rig.Tick(5);

        Assert.Equal((3233u, 2), GameObjectMapSystem.ShortestFloorStop(Bouncing(BouncingLift).Nodes(BouncingLift)));
        Assert.Empty(capture.Notices);
    }

    /// <summary>
    /// The floor-stop analysis depends only on the entry's animation, so it runs once per entry however often the car is ticked or sampled
    /// (the playerbot planners call <see cref="GameObjectMapSystem.ElevatorPositionAt"/> every 250 ms over two cycles: ~1,150 samples for the
    /// 143,333 ms Deeprun Tram), for every spawn of the entry, healthy or already reported.
    /// </summary>
    [Fact]
    public void TheFloorStopAnalysis_RunsOncePerEntry_NotPerTickOrSample()
    {
        using var rig = new Rig(
            [Car(SampledTram), Car(SampledShortLift)],
            [GoSpawn(1, SampledTram, 3, 0), GoSpawn(2, SampledTram, 6, 0), GoSpawn(3, SampledShortLift, 9, 0)],
            new TransportAnimationCatalog([.. WrapStop(SampledTram).Nodes(SampledTram).Select(n => (SampledTram, n)), .. Lift(SampledShortLift, 500).Nodes(SampledShortLift).Select(n => (SampledShortLift, n))]));
        using AnomalyCapture capture = CaptureEntry("elevator-car/stop-too-short", SampledShortLift);
        rig.Load();
        rig.Tick(20);

        foreach (GameObject car in rig.System.GameObjects.Where(g => g.Entry is SampledTram or SampledShortLift))
        {
            uint cycle = rig.System.ElevatorCycle(car);
            // Offset 125 ms: progress 0 is the first node's own time, which has no earlier node and so no position (TransportAnimationCatalog.Offset).
            for (uint progress = 125; progress < 2 * cycle; progress += 250)
            {
                Assert.NotNull(rig.System.ElevatorPositionAt(car, progress % cycle));
            }
        }

        Assert.Equal(2, rig.System.ElevatorFloorStopAnalyses);
        Assert.Single(capture.Notices);
    }

    /// <summary>Every elevator and tram of the real 5875 TransportAnimation.dbc rests at least <see cref="GameObjectMapSystem.MinElevatorStopMs"/> at each floor.</summary>
    [RealTransportAnimationDbcFact]
    public void EveryElevatorOfTheRealTransportAnimationDbc_RestsAtLeastASecondAtEachFloor()
    {
        TransportAnimationCatalog catalog = ArcaneCore.Data.Content.Transports.TransportAnimationDbcReader.Load(RealTransportAnimationDbcFactAttribute.Path()!);
        var shortStops = new List<string>();
        int withFloors = 0;
        foreach (uint entry in new uint[] { 2093, 4170, 4171, 4172, 4173, 11898, 11899, 20649, 20650, 20651, 20652, 20653, 20654, 20655, 20656, 20657, 47296, 47297, 80022, 80023, 85556, 128970, 149045, 149046, 152614, 176080, 176081, 176082, 176083, 176084, 176085 })
        {
            (uint shortest, int floors) = GameObjectMapSystem.ShortestFloorStop(catalog.Nodes(entry));
            if (floors > 0)
            {
                withFloors++;
            }

            if (floors == 0 || shortest < GameObjectMapSystem.MinElevatorStopMs)
            {
                shortStops.Add($"{entry}: {shortest} ms over {floors} floors");
            }
        }

        Assert.Equal(31, withFloors);
        Assert.Empty(shortStops);
    }

    // --- elevator-car/position-non-finite -----------------------------------------------------------------

    [Fact]
    public void AnElevatorWhoseRotationIsNotFinite_ReportsPositionNonFinite_AndReportsAgainAfterItRecovers()
    {
        using var rig = new Rig([Car(NanRotationCar)], [GoSpawn(1, NanRotationCar, 3, 0)], Lift(NanRotationCar, 5000));
        using AnomalyCapture capture = CaptureEntry("elevator-car/position-non-finite", NanRotationCar);
        rig.Load();
        GameObject car = rig.Object(NanRotationCar);
        car.PathProgress = 7500; // mid-rise: the animation yields an offset

        SetRawRotation(car, float.NaN);
        rig.Tick(3);
        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal(AnomalySeverity.Error, notice.Severity);
        Assert.Contains("non-finite elevator input", notice.Detail);
        Assert.Null(rig.System.ElevatorPositionAt(car, 7500));

        SetRawRotation(car, 0f);
        Assert.NotNull(rig.System.ElevatorPositionAt(car, 7500)); // finite again: the episode is over
        SetRawRotation(car, float.PositiveInfinity);
        Assert.Null(rig.System.ElevatorPositionAt(car, 7500));

        Assert.Equal(2, capture.Notices.Count);
    }

    [Fact]
    public void AnAnimationNodeThatIsNotFinite_ReportsPositionNonFinite()
    {
        var animation = new TransportAnimationCatalog(
        [
            (NanNodeCar, new TransportAnimationNode(0, 0, 0, 0)),
            (NanNodeCar, new TransportAnimationNode(5000, 0, 0, float.NaN)),
            (NanNodeCar, new TransportAnimationNode(10000, 0, 0, 0)),
        ]);
        using var rig = new Rig([Car(NanNodeCar)], [GoSpawn(1, NanNodeCar, 3, 0)], animation);
        using AnomalyCapture capture = CaptureEntry("elevator-car/position-non-finite", NanNodeCar);
        rig.Load();

        Assert.Null(rig.System.ElevatorPositionAt(rig.Object(NanNodeCar), 7500));

        Assert.Single(capture.Notices);
    }

    [Fact]
    public void AnElevatorWithFiniteInputs_ReportsNothing_AndYieldsAPosition()
    {
        using var rig = new Rig([Car(FiniteCar)], [GoSpawn(1, FiniteCar, 3, 0)], Lift(FiniteCar, 5000));
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "elevator-car/position-non-finite" && n.Subject.Contains($"entry {FiniteCar} "));
        rig.Load();
        GameObject car = rig.Object(FiniteCar);

        (float X, float Y, float Z)? position = rig.System.ElevatorPositionAt(car, 7500);

        Assert.NotNull(position);
        Assert.Equal(car.Z + 10f, position!.Value.Z, 0.01f); // halfway up the 5 s, 20 yd rise
        Assert.Empty(capture.Notices);
    }
}

/// <summary>Skipped (reported as Skipped, not Passed) unless ARCANECORE_TEST_DBC_DIR names a directory holding the build-5875 TransportAnimation.dbc.</summary>
internal sealed class RealTransportAnimationDbcFactAttribute : FactAttribute
{
    public RealTransportAnimationDbcFactAttribute()
    {
        if (Path() is null)
        {
            Skip = "ARCANECORE_TEST_DBC_DIR must name a directory holding the build-5875 TransportAnimation.dbc; the real-animation check did NOT run.";
        }
    }

    public static string? Path()
    {
        string? directory = Environment.GetEnvironmentVariable("ARCANECORE_TEST_DBC_DIR");
        string? file = string.IsNullOrWhiteSpace(directory) ? null : System.IO.Path.Combine(directory, "TransportAnimation.dbc");
        return file is not null && File.Exists(file) ? file : null;
    }
}

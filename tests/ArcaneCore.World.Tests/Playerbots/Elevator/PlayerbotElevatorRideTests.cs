using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>
/// The lift ride (<see cref="PlayerbotElevator"/>) in the fixture world (<see cref="ElevatorTestWorld"/>: the Great Lift's two cars on
/// their real 30-second paths, a cliff between the canyon floor and the plateau). Issue #186: bots in Thousand Needles with quest 1111
/// complete stalled against the canyon wall on their way to Wharfmaster Dizzywig in Ratchet.
/// </summary>
public sealed class PlayerbotElevatorRideTests(ITestOutputHelper output)
{
    private static PlayerbotOptions Options() => new() { Enabled = true, ThinkIntervalMs = 100, Population = { TramMinLevel = 0 } };

    private const uint Dizzywig = 3453;

    /// <summary>The lift is a transit like the tram: a bot with no home below <c>Population:TramMinLevel</c> begins no ride.</summary>
    [Fact]
    public async Task AHomelessLevelOneBot_BeginsNoLiftRide()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync(creatures: RatchetEnder());
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        await world.OnWorldAsync(() => world.Player.Level = 1);
        var destinations = new PlayerbotWorldDestinations(world.Session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100 });
        await world.OnWorldAsync(() => destinations.Update(world.Player, Dizzywig, 100));
        Assert.Null(await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)));

        // Control: the same bot at the tram level does begin it.
        await world.OnWorldAsync(() => world.Player.Level = 10);
        var grown = new PlayerbotWorldDestinations(world.Session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100 });
        await world.OnWorldAsync(() => grown.Update(world.Player, Dizzywig, 100));
        Assert.NotNull(await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)));
    }

    private static CreatureContent RatchetEnder() => new(
        [new CreatureTemplate { Entry = Dizzywig, Name = "Wharfmaster Dizzywig", DisplayIds = [49] }],
        [new CreatureSpawn { Guid = 14419, Entry = Dizzywig, MapId = ElevatorTestData.Kalimdor, X = ElevatorTestWorld.Ratchet.X,
            Y = ElevatorTestWorld.Ratchet.Y, Z = ElevatorTestWorld.Ratchet.Z }], [], [], []);

    /// <summary>
    /// A bot on the canyon floor heads for Ratchet (a destination of <see cref="PlayerbotWorldDestinations"/>, as a quest ender is): the
    /// destination begins a lift ride instead of a walk into the cliff. The brain walks the bot to the landing beside a car at the foot,
    /// it waits, steps aboard when the car stands there, reports itself on the car (MOVEFLAG_ONTRANSPORT, the car's GUID, at the car's
    /// position) all the way up, and steps off onto the plateau; the destination then walks on from there. No step of the bot is a jump
    /// farther than stepping aboard or off, and the anticheat, judging the bot as a client, finds nothing.
    /// </summary>
    [Fact]
    public async Task ABotInThousandNeedles_RidesTheGreatLiftUp_ToRatchet()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync(creatures: RatchetEnder());
        var findings = new ConcurrentQueue<AntiCheatFinding>();
        AntiCheatFeature anticheat = world.Host.WorldServices.GetRequiredService<AntiCheatFeature>();
        anticheat.FindingRecorded += (_, finding, _, _) => findings.Enqueue(finding);
        await world.OnWorldAsync(() => anticheat.ApplyOptions(new AntiCheatOptions
        {
            ExemptManagedBots = false, TerrainChecks = false, BaselineGapMs = 600_000, SpeedClock = { Enabled = false },
        }));
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        PlayerbotOptions options = Options();
        var destinations = new PlayerbotWorldDestinations(world.Session, options);
        Assert.True(await world.OnWorldAsync(() => destinations.Update(world.Player, Dizzywig, 100)), "the destination attempted nothing");
        ElevatorTrip? begun = await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player));
        Assert.NotNull(begun);
        Assert.Equal(ElevatorLevel.Low, begun.From);
        Assert.Equal(Dizzywig, destinations.TargetEntry);

        var brain = new PlayerbotBrain(world.Session, options);
        ElevatorStage? stage = null, last = null;
        Vector3 previous = ElevatorTestWorld.CanyonStart;
        float longestStep = 0, highestAboard = float.MinValue, lowestAboard = float.MaxValue, farthestFromCar = 0;
        int aboardBeats = 0;
        for (int step = 0; step < 3_000 && stage is not (ElevatorStage.Done or ElevatorStage.Failed); step++)
        {
            (ElevatorStage? s, Vector3 at, bool aboard, ulong transport, Vector3? car) = await world.TickAsync(() =>
            {
                brain.Update(100);
                Player p = world.Player;
                GameObject? riding = world.Objects()!.GameObjects.FirstOrDefault(g => g.Guid.Value == p.Movement.TransportGuid);
                Vector3? carAt = riding is not null && world.Objects()!.ElevatorPosition(riding) is { } c ? new Vector3(c.X, c.Y, c.Z) : null;
                return (PlayerbotElevator.TripOf(p)?.Stage, new Vector3(p.X, p.Y, p.Z), (p.Movement.Flags & MovementFlags.OnTransport) != 0,
                    p.Movement.TransportGuid, carAt);
            });
            stage = s;
            if (s != last)
            {
                output.WriteLine($"{world.Host.World.NowMs / 1000.0:F1}s {s} at {at}");
                last = s;
            }

            longestStep = MathF.Max(longestStep, Vector3.Distance(previous, at));
            previous = at;
            if (aboard)
            {
                aboardBeats++;
                Assert.NotEqual(0ul, transport);
                Assert.NotNull(car);
                highestAboard = MathF.Max(highestAboard, at.Z);
                lowestAboard = MathF.Min(lowestAboard, at.Z);
                farthestFromCar = MathF.Max(farthestFromCar, Vector2.Distance(new(at.X, at.Y), new(car.Value.X, car.Value.Y)));
            }
        }

        string? failure = await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)?.Failure);
        output.WriteLine($"end {stage} ({failure}); aboard {aboardBeats} thinks from z {lowestAboard:F1} to {highestAboard:F1}; " +
            $"farthest from the car {farthestFromCar:F1}; longest step {longestStep:F1}");
        Assert.Equal(ElevatorStage.Done, stage);
        Assert.True(aboardBeats >= 50, $"aboard for only {aboardBeats} thinks: no ride");
        Assert.True(lowestAboard < ElevatorTestWorld.BottomFloor + 5f && highestAboard > ElevatorTestWorld.TopFloor - 5f,
            $"rode only from {lowestAboard:F1} to {highestAboard:F1}");
        Assert.True(farthestFromCar <= PlayerbotElevator.CarHalfYards, $"reported {farthestFromCar:F1} yards from the car's centre while aboard");
        Assert.True(longestStep <= 12f, $"a step of {longestStep:F1} yards: a teleport, not a ride");
        (Vector3 end, bool stillAboard) = await world.OnWorldAsync(() => (new Vector3(world.Player.X, world.Player.Y, world.Player.Z),
            (world.Player.Movement.Flags & MovementFlags.OnTransport) != 0));
        Assert.False(stillAboard, "still on the car after the ride");
        Assert.True(end.X >= ElevatorTestWorld.Cliff && MathF.Abs(end.Z - ElevatorTestWorld.TopFloor) < 1f, $"off at {end}, not on the plateau");

        // On the plateau the same destination is an ordinary walk: no second ride.
        (bool walking, ElevatorStage? after) = await world.TickAsync(() =>
            (destinations.Update(world.Player, Dizzywig, 100), PlayerbotElevator.TripOf(world.Player)?.Stage));
        Assert.True(walking, "no walk towards Ratchet after the ride");
        Assert.Equal(ElevatorStage.Done, after);
        Assert.True(await world.OnWorldAsync(() => PlayerbotMotion.IsActive(world.Player)), "not walking towards Ratchet");
        foreach (AntiCheatFinding finding in findings) output.WriteLine($"anticheat: {finding.Type} {finding}");
        Assert.DoesNotContain(findings, f => f.Type is AntiCheatViolation.Flag or AntiCheatViolation.Speed or AntiCheatViolation.Teleport);
    }

    /// <summary>
    /// The way back: from the plateau to a destination on the canyon floor the bot boards at the top and steps off at the foot; at the
    /// test's 100 ms thinks and at the default <see cref="PlayerbotOptions.ThinkIntervalMs"/> (500 ms, a heartbeat's interval).
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    public async Task ABotOnThePlateau_RidesTheGreatLiftDown(int thinkMs)
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(new Vector3(-4600f, -1800f, ElevatorTestWorld.TopFloor));
        ElevatorLift lift = await world.GreatLiftAsync();
        PlayerbotOptions options = new() { Enabled = true, ThinkIntervalMs = thinkMs };
        Assert.Equal(500, new PlayerbotOptions().ThinkIntervalMs);
        // The mesh's way down ends at the cliff's edge, 63 yards short: that is no way, and the ride begins.
        Assert.True(await world.OnWorldAsync(() => PlayerbotElevator.TryBegin(world.Session, world.Player, ElevatorTestWorld.CanyonStart, options)));
        Assert.Equal(ElevatorLevel.High, await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)!.From));
        var brain = new PlayerbotBrain(world.Session, options);
        ElevatorStage? stage = await RunAsync(world, brain, 3_000);
        Assert.Equal(ElevatorStage.Done, stage);
        Vector3 end = await world.OnWorldAsync(() => new Vector3(world.Player.X, world.Player.Y, world.Player.Z));
        Assert.True(end.X < ElevatorTestWorld.Cliff && MathF.Abs(end.Z - ElevatorTestWorld.BottomFloor) < 1f, $"off at {end}, not on the canyon floor");
        Assert.True(Vector2.Distance(new(end.X, end.Y), lift.Centre) < 25f, $"off at {end}, not beside the lift");
    }

    /// <summary>
    /// The timeout: cars that never stand still at the foot (a path without a stop) are never boarded. The bot waits at the landing for
    /// two cycles and 30 s (90 s), then the trip fails with its reason, the brain gives the leg up (LastTransitFailure) without setting the quest aside, the bot is still on the
    /// canyon floor, and the lift is backed off (<see cref="PlayerbotTransitBackoff"/>).
    /// </summary>
    [Fact]
    public async Task CarsThatNeverStandAtTheFoot_TheWaitTimesOut_AndTheRideFails()
    {
        // Up and down without a stop, 30 s a cycle (the real rise, 129.5 yards).
        var restless = new TransportAnimationCatalog([.. ((uint[])[11898, 11899]).SelectMany(entry => new (uint, TransportAnimationNode)[]
        {
            (entry, new TransportAnimationNode(0, 0, 0, 0)),
            (entry, new TransportAnimationNode(15_000, 0, 0, entry == 11899 ? 129.55f : -129.3f)),
            (entry, new TransportAnimationNode(30_000, 0, 0, 0)),
        })]);
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync(animation: restless);
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        ElevatorLift lift = await world.GreatLiftAsync();
        PlayerbotOptions options = Options();
        var brain = new PlayerbotBrain(world.Session, options);
        await world.OnWorldAsync(() => PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.Low, ElevatorTestWorld.Ratchet));

        string? stall = null;
        uint waitingSince = 0, failedAt = 0;
        for (int step = 0; step < 3_000 && stall is null; step++)
        {
            (string? report, ElevatorStage? stage) = await world.TickAsync(() =>
            {
                int before = brain.TransitFailureCount;
                brain.Update(100);
                return (brain.TransitFailureCount != before ? brain.LastTransitFailure : null, PlayerbotElevator.TripOf(world.Player)?.Stage);
            });
            if (stage == ElevatorStage.Waiting && waitingSince == 0) waitingSince = world.Host.World.NowMs;
            Assert.NotEqual(ElevatorStage.Aboard, stage);
            stall = report;
            if (stall is not null) failedAt = world.Host.World.NowMs;
        }

        output.WriteLine($"waited from {waitingSince / 1000.0:F1}s; {stall} at {failedAt / 1000.0:F1}s");
        Assert.NotNull(stall);
        Assert.StartsWith("elevator: no car stood at the lower landing of the Great Lift", stall);
        Assert.NotEqual(0u, waitingSince);
        Assert.InRange(failedAt - waitingSince, 90_000u, 92_000u);
        await world.OnWorldAsync(() =>
        {
            Assert.Equal(ElevatorStage.Failed, PlayerbotElevator.TripOf(world.Player)!.Stage);
            Assert.True(MathF.Abs(world.Player.Z - ElevatorTestWorld.BottomFloor) < 1f, $"at {world.Player.Z}, not on the canyon floor");
            Assert.False(PlayerbotElevator.TryBegin(world.Session, world.Player, ElevatorTestWorld.Ratchet, options), "a new ride began at once");
            Assert.True(PlayerbotElevator.IsHeldOff(world.Session, world.Player, ElevatorTestWorld.Ratchet));
            PlayerbotElevator.Cancel(world.Player);
        });

        // Only the lift's own backoff holds it (TransitBackoffSeconds, 60 s at first): ten seconds after it ran out the ride begins again,
        // long before any ten-minute hold of the bot's whole lift use would have ended.
        uint backoffEnds = failedAt + ((uint)options.Travel.TransitBackoffSeconds * 1000) + 10_000;
        uint now = await world.OnWorldAsync(() => world.Host.World.NowMs);
        if (unchecked((int)(backoffEnds - now)) > 0) await world.AdvanceAsync(backoffEnds - now);
        await world.OnWorldAsync(() =>
        {
            Assert.True(world.Host.World.NowMs - failedAt < 600_000, "the clock ran ten minutes past the failure: the check proves nothing");
            Assert.False(PlayerbotElevator.IsHeldOff(world.Session, world.Player, ElevatorTestWorld.Ratchet), "still held off after the lift's backoff");
            Assert.True(PlayerbotElevator.TryBegin(world.Session, world.Player, ElevatorTestWorld.Ratchet, options), "the ride did not begin after the backoff");
        });
    }

    /// <summary>
    /// Bot QA #1482: a bot in the Barrens farther from the Great Lift than one route reaches (<see cref="PlayerbotOptions.MaxRouteYards"/>)
    /// began the trip with a goal in Thousand Needles and walked the straight line until the 60 s no-progress bound failed it. It begins
    /// none until it is within reach; then it does.
    /// </summary>
    [Fact]
    public async Task ABotBeyondOneRoutesReachOfTheLift_BeginsNoTrip_UntilItIsWithinReach()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        PlayerbotOptions options = Options();
        ElevatorLift lift = await world.GreatLiftAsync();
        float far = options.MaxRouteYards + 500f;
        await world.PlaceAsync(new Vector3(lift.Centre.X + far, lift.Centre.Y, ElevatorTestWorld.TopFloor));
        Assert.False(await world.OnWorldAsync(() => PlayerbotElevator.TryBegin(world.Session, world.Player, ElevatorTestWorld.CanyonStart, options)),
            $"a trip began {far:F0} yards from the lift");
        Assert.Null(await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)));

        await world.PlaceAsync(new Vector3(lift.Centre.X + (options.MaxRouteYards - 500f), lift.Centre.Y, ElevatorTestWorld.TopFloor));
        Assert.True(await world.OnWorldAsync(() => PlayerbotElevator.TryBegin(world.Session, world.Player, ElevatorTestWorld.CanyonStart, options)),
            "no trip began within reach of the lift");
    }

    /// <summary>
    /// A bot sent to a lift's level it cannot walk to (the top, from the canyon floor: the way ends at the cliff) gives the ride up once it
    /// has gained nothing for <see cref="PlayerbotElevator.NoProgressMs"/>, with the reason, and does not walk at the cliff for ever.
    /// </summary>
    [Fact]
    public async Task ABotThatCannotReachItsLanding_GivesTheRideUp()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        ElevatorLift lift = await world.GreatLiftAsync();
        PlayerbotOptions options = Options();
        await world.OnWorldAsync(() => PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.High, ElevatorTestWorld.CanyonStart));
        string? failure = null;
        ElevatorStep step = ElevatorStep.Traveling;
        uint began = await world.OnWorldAsync(() => world.Host.World.NowMs);
        for (int i = 0; i < 2_000 && step == ElevatorStep.Traveling; i++)
            (step, failure) = await world.TickAsync(() => (PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out string? why), why));
        uint took = await world.OnWorldAsync(() => world.Host.World.NowMs) - began;
        output.WriteLine($"{step} after {took / 1000.0:F1}s: {failure}");
        Assert.Equal(ElevatorStep.Failed, step);
        Assert.StartsWith("elevator: no way to", failure);
        Assert.Contains("the Great Lift", failure);
        Assert.InRange(took, 1u, PlayerbotElevator.NoProgressMs + 60_000); // the refused plans end it before the progress bound
        Assert.True(await world.OnWorldAsync(() => world.Player.X < ElevatorTestWorld.Cliff), "the bot crossed the cliff");
    }

    [Fact]
    public async Task WhenThinksAreSpaced15SecondsApart_PerTickPumpStillCatchesThe4SecondElevatorStopAndAlightsAtTheTop()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        ElevatorLift lift = await world.GreatLiftAsync();
        PlayerbotOptions options = Options();
        await world.OnWorldAsync(() => PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.Low, ElevatorTestWorld.Ratchet));

        // Simulate a 900-bot server where each bot only runs Drive (Think) once every 15,000 ms, while PlayerbotMotion.Pump
        // runs every 50 ms world tick. Without PlayerbotElevator.Pump, the 4-second elevator stop can slip between 15-second thinks.
        ElevatorStep step = ElevatorStep.Traveling;
        string? failure = null;
        bool sawAboard = false;
        for (uint tick = 0; tick < 1_800 && step == ElevatorStep.Traveling; tick++)
        {
            if (tick % 300 == 0)
            {
                (step, failure) = await world.OnWorldAsync(() =>
                {
                    PlayerbotMotion.NoteThinkGap(world.Player, 15_000);
                    ElevatorStep s = PlayerbotElevator.Drive(world.Session, world.Player, options, 15_000, out string? why);
                    return (s, why);
                });
                if (step != ElevatorStep.Traveling) break;
            }

            await world.AdvanceAsync(50);
            bool aboardNow = await world.OnWorldAsync(() =>
            {
                PlayerbotMotion.Pump(world.Session, world.Player, world.Host.World.NowMs);
                return PlayerbotElevator.TripOf(world.Player)?.Stage == ElevatorStage.Aboard
                    && (world.Player.Movement.Flags & MovementFlags.OnTransport) != 0;
            });
            if (aboardNow) sawAboard = true;
        }

        Assert.Null(failure);
        Assert.True(sawAboard, "the per-tick pump never boarded the car between 15s thinks");
        Assert.Equal(ElevatorStep.Arrived, step);
        Vector3 end = await world.OnWorldAsync(() => new Vector3(world.Player.X, world.Player.Y, world.Player.Z));
        Assert.True(end.X >= ElevatorTestWorld.Cliff && MathF.Abs(end.Z - ElevatorTestWorld.TopFloor) < 1f, $"off at {end}, not on the plateau");
    }

    private static async Task<ElevatorStage?> RunAsync(ElevatorTestWorld world, PlayerbotBrain brain, int thinks)
    {
        ElevatorStage? stage = null;
        for (int step = 0; step < thinks && stage is not (ElevatorStage.Done or ElevatorStage.Failed); step++)
            stage = await world.TickAsync(() => { brain.Update(100); return PlayerbotElevator.TripOf(world.Player)?.Stage; });
        return stage;
    }
}

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
    private static PlayerbotOptions Options() => new() { Enabled = true, ThinkIntervalMs = 100 };

    private const uint Dizzywig = 3453;

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
    /// two cycles and 30 s (90 s), then the trip fails with its reason, the brain reports it as the bot's own stall, the bot is still on the
    /// canyon floor, and no new ride begins for <see cref="PlayerbotElevator.RetryAfterFailureMs"/>.
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
                int before = brain.StallCount;
                brain.Update(100);
                return (brain.StallCount != before ? brain.StallReport : null, PlayerbotElevator.TripOf(world.Player)?.Stage);
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
        });
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
        Assert.InRange(took, PlayerbotElevator.NoProgressMs, PlayerbotElevator.NoProgressMs + 60_000);
        Assert.True(await world.OnWorldAsync(() => world.Player.X < ElevatorTestWorld.Cliff), "the bot crossed the cliff");
    }

    /// <summary>
    /// A falling bot: the car leaves the world under a bot riding it, high in the shaft. The bot steps off and falls as a client does
    /// (MOVEFLAG_FALLINGFAR, then the landing with the fall's time), lands on the canyon floor below (the server takes the fall damage),
    /// and the trip fails with its reason; it is never left hanging in the air.
    /// </summary>
    [Fact]
    public async Task TheCarLeavesTheWorldUnderTheBot_TheBotFallsToTheFloor()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        ElevatorLift lift = await world.GreatLiftAsync();
        PlayerbotOptions options = Options();
        await world.OnWorldAsync(() => PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.Low, ElevatorTestWorld.Ratchet));

        // Ride until 40 yards up the shaft.
        float z = float.MinValue;
        ElevatorStage? stage = null;
        for (int i = 0; i < 2_000 && !(stage == ElevatorStage.Aboard && z > 0f); i++)
        {
            (stage, z) = await world.TickAsync(() =>
            {
                PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _);
                return (PlayerbotElevator.TripOf(world.Player)?.Stage, world.Player.Z);
            });
        }

        Assert.Equal(ElevatorStage.Aboard, stage);
        uint healthBefore = await world.OnWorldAsync(() =>
        {
            GameObject car = PlayerbotElevator.TripOf(world.Player)!.CarObject!;
            Assert.True(world.Objects()!.Remove(car), "the car was not removed");
            return world.Player.Health;
        });

        bool fell = false;
        ElevatorStep step = ElevatorStep.Traveling;
        string? failure = null;
        for (int i = 0; i < 200 && step == ElevatorStep.Traveling; i++)
        {
            (step, failure, ElevatorStage? s) = await world.TickAsync(() =>
                (PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out string? why), why, PlayerbotElevator.TripOf(world.Player)?.Stage));
            fell |= s == ElevatorStage.Falling;
        }

        (Vector3 at, bool aboard, uint health, bool alive) = await world.OnWorldAsync(() => (new Vector3(world.Player.X, world.Player.Y, world.Player.Z),
            (world.Player.Movement.Flags & MovementFlags.OnTransport) != 0, world.Player.Health, world.Player.IsAlive));
        output.WriteLine($"{step}: {failure}; at {at}, health {healthBefore} -> {health}, alive {alive}");
        Assert.True(fell, "the bot never fell");
        Assert.Equal(ElevatorStep.Failed, step);
        Assert.StartsWith("elevator: the car of the Great Lift", failure);
        Assert.Contains("fell", failure);
        Assert.False(aboard, "still on a car that left the world");
        Assert.True(MathF.Abs(at.Z - ElevatorTestWorld.BottomFloor) < 1f, $"at {at}: not on the canyon floor");
        Assert.True(!alive || health < healthBefore, "a fall of 40 yards and more cost nothing");
    }

    /// <summary>Drive a ride from the canyon floor until the bot is aboard and at least <paramref name="above"/> yards up the shaft.</summary>
    private static async Task RideIntoTheShaftAsync(ElevatorTestWorld world, PlayerbotOptions options, float above)
    {
        ElevatorLift lift = await world.GreatLiftAsync();
        await world.OnWorldAsync(() => PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.Low, ElevatorTestWorld.Ratchet));
        float z = float.MinValue;
        ElevatorStage? stage = null;
        for (int i = 0; i < 2_000 && !(stage == ElevatorStage.Aboard && z > ElevatorTestWorld.BottomFloor + above); i++)
        {
            (stage, z) = await world.TickAsync(() =>
            {
                PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _);
                return (PlayerbotElevator.TripOf(world.Player)?.Stage, world.Player.Z);
            });
        }

        Assert.Equal(ElevatorStage.Aboard, stage);
        Assert.True(await world.OnWorldAsync(() => (world.Player.Movement.Flags & MovementFlags.OnTransport) != 0), "not on the car");
    }

    /// <summary>
    /// The review's blocking case: a real player invites a riding bot into a party (ManagedPlayerbotFeature cancels the ride, the party AI
    /// moves the bot next) while the car is between floors. The bot leaves the car falling: it ends on the canyon floor below, off the
    /// transport, with the fall's damage, never hanging in the air for the party AI to slide along.
    /// </summary>
    [Fact]
    public async Task ACancelledRide_BetweenFloors_FallsTheBotToTheFloor()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        await RideIntoTheShaftAsync(world, Options(), above: 40f);
        (Vector3 at, bool aboard, ulong transport, uint healthBefore, uint health) = await world.OnWorldAsync(() =>
        {
            uint before = world.Player.Health;
            PlayerbotElevator.Cancel(world.Player);
            Player p = world.Player;
            return (new Vector3(p.X, p.Y, p.Z), (p.Movement.Flags & MovementFlags.OnTransport) != 0, p.Movement.TransportGuid, before, p.Health);
        });
        output.WriteLine($"after the cancel: {at}, aboard {aboard}, health {healthBefore} -> {health}");
        Assert.Null(await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)));
        Assert.False(aboard, "still on the transport after the cancel");
        Assert.Equal(0ul, transport);
        Assert.True(MathF.Abs(at.Z - ElevatorTestWorld.BottomFloor) < 1f, $"at {at}: left in the air, not on the canyon floor");
        Assert.True(health < healthBefore, "a fall of 40 yards and more cost nothing");
    }

    /// <summary>A cancel while the car stands at a floor is a plain step off: no fall, no damage.</summary>
    [Fact]
    public async Task ACancelledRide_AtAFloor_StepsTheBotOff()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        await RideIntoTheShaftAsync(world, Options(), above: -10f); // aboard, still at the foot
        (Vector3 at, bool aboard, uint healthBefore, uint health) = await world.OnWorldAsync(() =>
        {
            uint before = world.Player.Health;
            PlayerbotElevator.Cancel(world.Player);
            return (new Vector3(world.Player.X, world.Player.Y, world.Player.Z), (world.Player.Movement.Flags & MovementFlags.OnTransport) != 0,
                before, world.Player.Health);
        });
        Assert.False(aboard);
        Assert.True(MathF.Abs(at.Z - ElevatorTestWorld.BottomFloor) < 1f, $"at {at}");
        Assert.Equal(healthBefore, health);
    }

    /// <summary>
    /// A bot killed aboard: the death ends the ride (the brain cancels it) and the corpse loses the transport (flag, GUID and offset), so
    /// it is not reported riding a car it no longer stands on.
    /// </summary>
    [Fact]
    public async Task ABotKilledAboard_LosesTheTransport()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        PlayerbotOptions options = Options();
        var brain = new PlayerbotBrain(world.Session, options);
        await RideIntoTheShaftAsync(world, options, above: 20f);
        (bool alive, bool aboard, ulong transport, Vector3 offset, ElevatorTrip? trip) = await world.OnWorldAsync(() =>
        {
            Player p = world.Player;
            p.Health = 0;
            p.Map!.Combat.KillPlayer(p);
            brain.Update(1);
            return (p.IsAlive, (p.Movement.Flags & MovementFlags.OnTransport) != 0, p.Movement.TransportGuid,
                new Vector3(p.Movement.TransportX, p.Movement.TransportY, p.Movement.TransportZ), PlayerbotElevator.TripOf(p));
        });
        Assert.False(alive);
        Assert.Null(trip);
        Assert.False(aboard, "the corpse still rides the car");
        Assert.Equal(0ul, transport);
        Assert.Equal(Vector3.Zero, offset);
    }

    /// <summary>
    /// A bot saved mid-ride logs in in the shaft, in the air, with no trip. At its first think it falls to the floor below (and is off any
    /// transport); a bot standing on the plateau beside the lift is not touched.
    /// </summary>
    [Fact]
    public async Task ABotLoggedInMidShaft_FallsToTheFloorAtItsFirstThink()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        ElevatorLift lift = await world.GreatLiftAsync();
        ElevatorCar bottom = lift.Cars.Single(c => c.SpawnGuid == 16876);
        var midShaft = new Vector3(bottom.Low.X + 1f, bottom.Low.Y, 20f);
        await world.PlaceAsync(midShaft);
        Assert.True(await world.OnWorldAsync(() => world.Player.Z) > 10f, "the fixture put the bot on the floor already");
        var brain = new PlayerbotBrain(world.Session, Options());
        (Vector3 at, bool aboard) = await world.TickAsync(() =>
        {
            brain.Update(100);
            return (new Vector3(world.Player.X, world.Player.Y, world.Player.Z), (world.Player.Movement.Flags & MovementFlags.OnTransport) != 0);
        });
        output.WriteLine($"after the first think: {at}");
        Assert.False(aboard);
        Assert.True(MathF.Abs(at.Z - ElevatorTestWorld.BottomFloor) < 1f, $"at {at}: still in the air");
    }

    private static async Task<ElevatorStage?> RunAsync(ElevatorTestWorld world, PlayerbotBrain brain, int thinks)
    {
        ElevatorStage? stage = null;
        for (int step = 0; step < thinks && stage is not (ElevatorStage.Done or ElevatorStage.Failed); step++)
            stage = await world.TickAsync(() => { brain.Update(100); return PlayerbotElevator.TripOf(world.Player)?.Stage; });
        return stage;
    }
}

using System.Numerics;
using ArcaneCore.Data.Content.Transports;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>
/// Needs the live server's terrain extraction with Kalimdor's navigation mesh and models (<c>ARCANECORE_TEST_TERRAIN_DIR</c>: maps/,
/// vmaps/, mmaps/001.mmap) and the client's TransportAnimation.dbc (<c>ARCANECORE_TEST_DBC_DIR</c>). Neither is committed; without them the
/// test is reported Skipped, never a silent pass.
/// </summary>
internal sealed class RealLiftRideFactAttribute : FactAttribute
{
    public RealLiftRideFactAttribute()
    {
        string? terrain = Environment.GetEnvironmentVariable(RealElevatorFactAttribute.TerrainVariable);
        string? dbc = Environment.GetEnvironmentVariable(RealElevatorFactAttribute.DbcVariable);
        if (string.IsNullOrWhiteSpace(terrain) || !File.Exists(Path.Combine(terrain, "mmaps", "001.mmap")))
            Skip = $"{RealElevatorFactAttribute.TerrainVariable} is not set to a terrain data root with mmaps/001.mmap: the real Great Lift ride did NOT run.";
        else if (string.IsNullOrWhiteSpace(dbc) || !File.Exists(Path.Combine(dbc, "TransportAnimation.dbc")))
            Skip = $"{RealElevatorFactAttribute.DbcVariable} is not set to a directory with TransportAnimation.dbc: the real Great Lift ride did NOT run.";
    }
}

/// <summary>
/// The Great Lift on the real data: Kalimdor's terrain, models and navigation mesh, the client's paths of the two cars, the classic-db car
/// rows (<see cref="ElevatorTestWorld.StartRealAsync"/>). The ride itself, and (R1-R7, the review of 80a6eb11, cycle 2) what becomes of a
/// bot whose ride is cut short: the real data has no static floor under a car at either stop, so a bot moved off a car where it stands
/// dropped 131 yards and died (a party invite at the top stop: z 85.4 to -47.9, health 0). Game time only (manual clock).
/// </summary>
public sealed class PlayerbotElevatorRealRideTests(ITestOutputHelper output)
{
    private static readonly Vector3 Foot = new(-4700f, -1870f, -48f);

    private static PlayerbotOptions Options() => new() { Enabled = true, ThinkIntervalMs = 100 };

    private static Task<ElevatorTestWorld> StartAsync()
        => ElevatorTestWorld.StartRealAsync(Environment.GetEnvironmentVariable(RealElevatorFactAttribute.TerrainVariable)!,
            TransportAnimationDbcReader.Load(Path.Combine(Environment.GetEnvironmentVariable(RealElevatorFactAttribute.DbcVariable)!, "TransportAnimation.dbc")));

    private static (Vector3 At, bool Aboard, bool Falling, uint Health, bool Alive) State(ElevatorTestWorld world)
    {
        Player p = world.Player;
        return (new Vector3(p.X, p.Y, p.Z), (p.Movement.Flags & MovementFlags.OnTransport) != 0,
            (p.Movement.Flags & MovementFlags.FallingFar) != 0, p.Health, p.IsAlive);
    }

    /// <summary>The landing beside <paramref name="car"/> at <paramref name="level"/>, from the real collision data.</summary>
    private static Task<Vector3> LandingAsync(ElevatorTestWorld world, ElevatorCar car, ElevatorLevel level)
        => world.OnWorldAsync(() => PlayerbotElevator.LandingBeside(world.Host.World.GetMap(ElevatorTestData.Kalimdor), car.At(level), car.At(level))
            ?? throw new InvalidOperationException($"no landing beside car {car.SpawnGuid} at {level}"));

    /// <summary>On a landing of the lift: within 3 yards of one of its levels and at least 5.5 yards from both cars' shafts.</summary>
    private static void AssertOnALanding(ElevatorLift lift, Vector3 at, string what)
    {
        bool onALevel = MathF.Abs(at.Z - lift.LowZ) < 3f || MathF.Abs(at.Z - lift.HighZ) < 3f;
        float fromShafts = lift.Cars.Min(c => Vector2.Distance(new(c.Low.X, c.Low.Y), new(at.X, at.Y)));
        Assert.True(onALevel && fromShafts >= PlayerbotElevator.LandingRadii[0] - 0.5f, $"{what}: at {at}, {fromShafts:F1} yards from a shaft: not on a landing");
    }

    private static async Task AcknowledgeAsync(ElevatorTestWorld world)
    {
        for (int i = 0; i < 60; i++)
        {
            bool pending = await world.OnWorldAsync(() => PlayerbotMovementControl.Update(world.Session, world.Player)
                || world.Teleports.StageOf(world.Player) is not null);
            if (!pending) return;
            await world.AdvanceAsync(50);
        }

        Assert.Fail("the bot's acknowledgements never settled");
    }

    /// <summary>Drive the trip until the bot is aboard (and, with <paramref name="above"/>, that far above where it boarded).</summary>
    private static async Task<float> BoardAsync(ElevatorTestWorld world, PlayerbotOptions options, float? above = null)
    {
        float boardedZ = float.NaN;
        for (int i = 0; i < 3_000; i++)
        {
            (ElevatorStage? stage, float z, string? failure) = await world.TickAsync(() =>
            {
                PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _);
                return (PlayerbotElevator.TripOf(world.Player)?.Stage, world.Player.Z, PlayerbotElevator.TripOf(world.Player)?.Failure);
            });
            Assert.True(stage != ElevatorStage.Failed, $"the ride failed before boarding: {failure}");
            if (stage != ElevatorStage.Aboard) continue;
            if (float.IsNaN(boardedZ)) boardedZ = z;
            if (above is not { } rise || z > boardedZ + rise) return z;
        }

        throw new TimeoutException("never aboard");
    }

    /// <summary>Drive until the trip is over; the lowest z the bot reported meanwhile.</summary>
    private static async Task<float> DriveToTheEndAsync(ElevatorTestWorld world, PlayerbotOptions options)
    {
        float lowest = float.MaxValue;
        for (int i = 0; i < 1_500 && PlayerbotElevator.IsTraveling(world.Player); i++)
        {
            lowest = MathF.Min(lowest, await world.TickAsync(() =>
            {
                PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _);
                return world.Player.Z;
            }));
        }

        return MathF.Min(lowest, await world.OnWorldAsync(() => world.Player.Z));
    }

    [RealLiftRideFact]
    public async Task ABot_RidesTheRealGreatLift_UpFromThousandNeedles()
    {
        await using ElevatorTestWorld world = await StartAsync();
        await world.PlaceAsync(Foot);
        PlayerbotOptions options = Options();
        var brain = new PlayerbotBrain(world.Session, options);
        ElevatorLift lift = await world.GreatLiftAsync();
        await world.OnWorldAsync(() => PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.Low, ElevatorTestData.Dizzywig));

        ElevatorStage? last = null;
        float highestAboard = float.MinValue;
        for (int step = 0; step < 3_000; step++)
        {
            (ElevatorStage? stage, bool aboard, Vector3 at) = await world.TickAsync(() =>
            {
                brain.Update(100);
                return (PlayerbotElevator.TripOf(world.Player)?.Stage, (world.Player.Movement.Flags & MovementFlags.OnTransport) != 0,
                    new Vector3(world.Player.X, world.Player.Y, world.Player.Z));
            });
            if (stage != last)
            {
                output.WriteLine($"{world.Host.World.NowMs / 1000.0:F1}s {stage} at {at}");
                last = stage;
            }

            if (aboard) highestAboard = MathF.Max(highestAboard, at.Z);
            if (stage is ElevatorStage.Done or ElevatorStage.Failed) break;
        }

        (ElevatorStage? end, string? failure, Vector3 off) = await world.OnWorldAsync(() => (PlayerbotElevator.TripOf(world.Player)?.Stage,
            PlayerbotElevator.TripOf(world.Player)?.Failure, new Vector3(world.Player.X, world.Player.Y, world.Player.Z)));
        output.WriteLine($"end {end} ({failure}) at {off}; rode up to z {highestAboard:F1}");
        Assert.Equal(ElevatorStage.Done, end);
        Assert.True(highestAboard > lift.HighZ - 2f, $"rode only up to {highestAboard:F1}");
        Assert.True(MathF.Abs(off.Z - lift.HighZ) < 3f, $"off at {off}, not at the top");

        // From the top the real mesh leads on towards Ratchet.
        bool onward = await world.OnWorldAsync(() => PlayerbotNavigation.TryPlanToward(world.Player, ElevatorTestData.Dizzywig, options, out PlayerbotRoute? route)
            && route is not null && Vector2.Distance(new(route.Points[^1].X, route.Points[^1].Y), new(ElevatorTestData.Dizzywig.X, ElevatorTestData.Dizzywig.Y))
                < Vector2.Distance(new(off.X, off.Y), new(ElevatorTestData.Dizzywig.X, ElevatorTestData.Dizzywig.Y)) - 50f);
        Assert.True(onward, "no way on towards Ratchet from where the bot got off");
    }

    /// <summary>R1: under every Great Lift car, at both of its stops, the static collision data has no floor within 5 yards: the drops.</summary>
    [RealLiftRideFact]
    public async Task R1_UnderTheCars_AtEveryStop_ThereIsNoStaticFloor()
    {
        await using ElevatorTestWorld world = await StartAsync();
        await world.PlaceAsync(Foot);
        ElevatorLift lift = await world.GreatLiftAsync();
        foreach (ElevatorCar car in lift.Cars)
        {
            foreach (ElevatorLevel level in (ElevatorLevel[])[ElevatorLevel.Low, ElevatorLevel.High])
            {
                Vector3 stop = car.At(level);
                float floor = await world.OnWorldAsync(() =>
                    world.Host.World.GetMap(ElevatorTestData.Kalimdor).Collision.GetHeight(stop.X, stop.Y, stop.Z + 0.5f, useModels: true, maxSearchDistance: 500f));
                bool none = !float.IsFinite(floor) || floor <= TerrainTile.InvalidHeight;
                output.WriteLine(none ? $"car {car.SpawnGuid} at {level} ({stop.Z:F1}): no static floor"
                    : $"car {car.SpawnGuid} at {level} ({stop.Z:F1}): static floor {floor:F1}, a drop of {stop.Z - floor:F1} yards");
                Assert.True(none || stop.Z - floor >= 5f, $"car {car.SpawnGuid} at {level}: a static floor {stop.Z - floor:F1} yards under the stop");
            }
        }
    }

    /// <summary>R2: a cancel (a party invite) while the car stands at the top with the bot aboard: the bot alights on the top landing, unhurt.</summary>
    [RealLiftRideFact]
    public async Task R2_ACancelAboard_AtTheTop_AlightsOnTheTopLanding()
    {
        await using ElevatorTestWorld world = await StartAsync();
        await world.PlaceAsync(Foot);
        ElevatorLift lift = await world.GreatLiftAsync();
        await world.PlaceAsync(await LandingAsync(world, lift.Cars[0], ElevatorLevel.High));
        PlayerbotOptions options = Options();
        await world.OnWorldAsync(() => PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.High, Foot));
        await BoardAsync(world, options);
        (uint healthBefore, ElevatorCar car) = await world.OnWorldAsync(() =>
        {
            ElevatorCar riding = PlayerbotElevator.TripOf(world.Player)!.Car!;
            uint health = world.Player.Health;
            PlayerbotElevator.Cancel(world.Player);
            return (health, riding);
        });
        float lowest = await DriveToTheEndAsync(world, options);
        await AcknowledgeAsync(world);
        (Vector3 at, bool aboard, _, uint health, bool alive) = await world.OnWorldAsync(() => State(world));
        output.WriteLine($"at {at}, aboard {aboard}, health {healthBefore} -> {health}, alive {alive}, lowest z {lowest:F1}");
        Assert.True(alive, "dead");
        Assert.Equal(healthBefore, health);
        Assert.False(aboard);
        Assert.True(MathF.Abs(at.Z - 85.4f) < 3f, $"at {at}: not at the top");
        Assert.True(Vector2.Distance(new(at.X, at.Y), new(car.High.X, car.High.Y)) >= 6f, $"at {at}: still on the car");
    }

    /// <summary>R3: boarded at the foot, cancelled before the car leaves: the bot alights on the foot's landing.</summary>
    [RealLiftRideFact]
    public async Task R3_ACancelAboard_AtTheFoot_AlightsOnTheFootLanding()
    {
        await using ElevatorTestWorld world = await StartAsync();
        await world.PlaceAsync(Foot);
        ElevatorLift lift = await world.GreatLiftAsync();
        PlayerbotOptions options = Options();
        await world.OnWorldAsync(() => PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.Low, ElevatorTestData.Dizzywig));
        await BoardAsync(world, options);
        (uint healthBefore, ElevatorCar car) = await world.OnWorldAsync(() =>
        {
            ElevatorCar riding = PlayerbotElevator.TripOf(world.Player)!.Car!;
            uint health = world.Player.Health;
            PlayerbotElevator.Cancel(world.Player);
            return (health, riding);
        });
        await DriveToTheEndAsync(world, options);
        await AcknowledgeAsync(world);
        (Vector3 at, bool aboard, _, uint health, bool alive) = await world.OnWorldAsync(() => State(world));
        output.WriteLine($"at {at}, aboard {aboard}, health {healthBefore} -> {health}, alive {alive}");
        Assert.True(alive, "dead");
        Assert.Equal(healthBefore, health);
        Assert.False(aboard);
        Assert.True(MathF.Abs(at.Z - -44.1f) < 3f, $"at {at}: not at the foot");
        Assert.True(Vector2.Distance(new(at.X, at.Y), new(car.Low.X, car.Low.Y)) >= 6f, $"at {at}: still on the car");
    }

    /// <summary>R4: a cancel 20 yards up the shaft on the way up: the bot rides on and alights on the top landing, unhurt.</summary>
    [RealLiftRideFact]
    public async Task R4_ACancelMidShaft_AlightsOnTheNextLanding()
    {
        await using ElevatorTestWorld world = await StartAsync();
        await world.PlaceAsync(Foot);
        ElevatorLift lift = await world.GreatLiftAsync();
        PlayerbotOptions options = Options();
        await world.OnWorldAsync(() => PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.Low, ElevatorTestData.Dizzywig));
        float cancelledAt = await BoardAsync(world, options, above: 20f);
        uint healthBefore = await world.OnWorldAsync(() =>
        {
            uint health = world.Player.Health;
            PlayerbotElevator.Cancel(world.Player);
            return health;
        });
        ElevatorStage? arrived = null;
        for (int i = 0; i < 1_500 && arrived is not (ElevatorStage.Done or ElevatorStage.Failed); i++)
        {
            arrived = await world.TickAsync(() =>
            {
                PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _);
                return PlayerbotElevator.TripOf(world.Player)?.Stage;
            });
        }

        await AcknowledgeAsync(world);
        (Vector3 at, bool aboard, _, uint health, bool alive) = await world.OnWorldAsync(() => State(world));
        output.WriteLine($"cancelled at z {cancelledAt:F1}; {arrived} at {at}, aboard {aboard}, health {healthBefore} -> {health}, alive {alive}");
        Assert.True(alive, "dead");
        Assert.Equal(healthBefore, health);
        Assert.Equal(ElevatorStage.Done, arrived);
        Assert.False(aboard);
        Assert.True(MathF.Abs(at.Z - lift.HighZ) < 3f, $"at {at}: not on the top landing");
        AssertOnALanding(lift, at, "after the ride");
    }

    /// <summary>R5: a bot logged in on the car parked at the top boards it and alights on the plateau, alive.</summary>
    [RealLiftRideFact]
    public async Task R5_ALoginOnTheParkedTopCar_AlightsOnThePlateau()
    {
        await using ElevatorTestWorld world = await StartAsync();
        await world.PlaceAsync(Foot);
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
        uint healthBefore = await world.OnWorldAsync(() => world.Player.Health);
        var brain = new PlayerbotBrain(world.Session, Options());
        float lowest = float.MaxValue;
        for (int i = 0; i < 100 && PlayerbotElevator.TripOf(world.Player) is not { Stage: ElevatorStage.Done or ElevatorStage.Failed }; i++)
            lowest = MathF.Min(lowest, await world.TickAsync(() => { brain.Update(100); return world.Player.Z; }));
        await AcknowledgeAsync(world);
        (Vector3 at, bool aboard, _, uint health, bool alive) = await world.OnWorldAsync(() => State(world));
        ElevatorTrip? trip = await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player));
        output.WriteLine($"trip {trip?.Stage} on car {trip?.Car?.SpawnGuid}; at {at}, lowest z {lowest:F1}, health {healthBefore} -> {health}");
        Assert.True(alive, "dead");
        Assert.Equal(healthBefore, health);
        Assert.False(aboard);
        Assert.True(trip is { Alighting: true, Car.SpawnGuid: 16874 }, "not boarded on the car it stood on");
        Assert.True(lowest > lift.HighZ - 3f, $"went down to z {lowest:F1}");
        AssertOnALanding(lift, at, "after the login");
    }

    /// <summary>R6: a bot logged in hanging in the shaft with the car elsewhere is placed on a landing, alive and unhurt, without a fall.</summary>
    [RealLiftRideFact]
    public async Task R6_ALoginHangingInTheShaft_IsPlacedOnALanding()
    {
        await using ElevatorTestWorld world = await StartAsync();
        await world.PlaceAsync(Foot);
        ElevatorLift lift = await world.GreatLiftAsync();
        ElevatorCar bottom = lift.Cars.Single(c => c.SpawnGuid == 16876);
        float carZ = await world.OnWorldAsync(() => world.Objects()!.ElevatorPosition(world.Objects()!.GameObjects.First(g => g.Spawn?.Guid == 16876))!.Value.Z);
        float z = MathF.Abs(carZ - 20f) < 20f ? 60f : 20f;
        await world.PlaceAsync(new Vector3(bottom.Low.X + 1f, bottom.Low.Y, z));
        uint healthBefore = await world.OnWorldAsync(() => world.Player.Health);
        var brain = new PlayerbotBrain(world.Session, Options());
        bool fell = false;
        // Until the rescue's near teleport is acknowledged (the brain then goes about its goals, and walks off the landing).
        for (int i = 0; i < 40; i++)
        {
            (bool falling, bool placed) = await world.TickAsync(() =>
            {
                brain.Update(100);
                return ((world.Player.Movement.Flags & MovementFlags.FallingFar) != 0, world.Teleports.StageOf(world.Player) is null
                    && Vector2.Distance(new(world.Player.X, world.Player.Y), new(bottom.Low.X + 1f, bottom.Low.Y)) > 3f);
            });
            fell |= falling;
            if (placed) break;
        }

        (Vector3 at, bool aboard, _, uint health, bool alive) = await world.OnWorldAsync(() => State(world));
        output.WriteLine($"placed at z {z} (car at {carZ:F1}); now at {at}, health {healthBefore} -> {health}, alive {alive}");
        Assert.True(alive, "dead");
        Assert.False(fell, "the bot fell");
        Assert.Equal(healthBefore, health);
        Assert.False(aboard);
        AssertOnALanding(lift, at, "after the login");
    }

    /// <summary>
    /// R8: the car leaves the world under a bot aboard at the foot. The static floor under it is the pit at the shaft's bottom (R1: 5.3 to
    /// 5.5 yards down, an enclosed recess no way leads out of): the bot is placed on the foot's landing, never dropped into the pit.
    /// </summary>
    [RealLiftRideFact]
    public async Task R8_TheCarLeavesTheWorldAtTheFoot_TheBotIsPlacedOnTheLanding_NotInThePit()
    {
        await using ElevatorTestWorld world = await StartAsync();
        await world.PlaceAsync(Foot);
        ElevatorLift lift = await world.GreatLiftAsync();
        PlayerbotOptions options = Options();
        await world.OnWorldAsync(() => PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.Low, ElevatorTestData.Dizzywig));
        await BoardAsync(world, options);
        (uint healthBefore, float boardedZ) = await world.OnWorldAsync(() =>
        {
            // To the middle of the car (a bot boards at its edge, over the ground beside the recess): over the pit.
            ElevatorTrip trip = PlayerbotElevator.TripOf(world.Player)!;
            GameObject car = trip.CarObject!;
            var carAt = world.Objects()!.ElevatorPosition(car)!.Value;
            trip.Offset = Vector3.Zero;
            Assert.True(PlayerbotMotion.SendAboard(world.Session, world.Player, car.Guid.Value, new Vector3(carAt.X, carAt.Y, carAt.Z),
                world.Player.Orientation, Vector3.Zero, 0f), "the move to the car's middle was refused");
            float under = world.Host.World.GetMap(ElevatorTestData.Kalimdor).Collision.GetHeight(carAt.X, carAt.Y, carAt.Z + 0.5f, useModels: true, maxSearchDistance: 500f);
            Assert.True(under < carAt.Z - 3f, $"no pit under the car's middle: the floor is at {under:F1}");
            Assert.True(world.Objects()!.Remove(car), "the car was not removed");
            return (world.Player.Health, world.Player.Z);
        });
        await DriveToTheEndAsync(world, options);
        await AcknowledgeAsync(world);
        (Vector3 at, bool aboard, _, uint health, bool alive) = await world.OnWorldAsync(() => State(world));
        string? failure = await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)?.Failure);
        output.WriteLine($"aboard at z {boardedZ:F1}; {failure}; now at {at}, health {healthBefore} -> {health}");
        Assert.True(alive, "dead");
        Assert.False(aboard);
        Assert.True(at.Z > lift.LowZ - 3f, $"at {at}: in the pit");
        AssertOnALanding(lift, at, "after the car left");
        Assert.Equal(healthBefore, health);
    }

    /// <summary>R7: a bot killed aboard: an observer is told the corpse is off the transport, at its world position.</summary>
    [RealLiftRideFact]
    public async Task R7_ADeathAboard_ObserversSeeTheCorpseOffTheTransport()
    {
        await using ElevatorTestWorld world = await StartAsync();
        await world.PlaceAsync(Foot);
        await using WorldTestClient watcher = await world.ObserverAsync("WATCHER", "Watcher", Foot + new Vector3(10f, 0f, 2f));
        ElevatorLift lift = await world.GreatLiftAsync();
        PlayerbotOptions options = Options();
        var brain = new PlayerbotBrain(world.Session, options);
        // On the bottom car's landing while that car stands at the foot (so it is the car chosen, and boarded at once).
        ElevatorCar bottom = lift.Cars.Single(c => c.SpawnGuid == 16876);
        await world.PlaceAsync(await LandingAsync(world, bottom, ElevatorLevel.Low));
        for (int i = 0; i < 700; i++)
        {
            uint? progress = await world.OnWorldAsync(() =>
            {
                GameObjectMapSystem? objects = world.Objects();
                GameObject? car = objects?.GameObjects.FirstOrDefault(g => g.Spawn?.Guid == 16876);
                return car is null ? null : objects!.ElevatorProgressAhead(car, 0);
            });
            if (progress is >= 500 and <= 2_000) break;
            await world.AdvanceAsync(50);
        }

        await world.OnWorldAsync(() => PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.Low, ElevatorTestData.Dizzywig));
        await BoardAsync(world, options, above: 20f);
        await world.AdvanceAsync(100);
        await watcher.CollectAsync(TimeSpan.FromMilliseconds(200));
        Vector3 body = await world.OnWorldAsync(() =>
        {
            Player p = world.Player;
            p.Map!.Combat.Kill(null, p);
            brain.Update(1);
            return new Vector3(p.X, p.Y, p.Z);
        });
        await world.AdvanceAsync(100);
        List<(WorldOpcode Opcode, byte[] Payload)> seen = await watcher.CollectAsync(TimeSpan.FromMilliseconds(300));
        MovementInfo? told = null;
        foreach ((WorldOpcode opcode, byte[] payload) in seen)
        {
            if (opcode is not (WorldOpcode.MsgMoveRoot or WorldOpcode.MsgMoveHeartbeat)) continue;
            var reader = new PacketReader(payload);
            reader.ReadPackedGuid();
            MovementInfo block = MovementInfo.Read(ref reader);
            if (!block.HasFlag(MovementFlags.OnTransport)) told = block;
        }

        output.WriteLine($"seen: {string.Join(", ", seen.Select(p => p.Opcode))}; body at {body}");
        Assert.True(told is not null, "the observer was never told the corpse is off the transport");
        Assert.Equal(0ul, told.Value.TransportGuid);
        Assert.True(Vector3.Distance(new Vector3(told.Value.X, told.Value.Y, told.Value.Z), body) < 0.01f);
    }
}

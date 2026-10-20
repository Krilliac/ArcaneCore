using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>
/// What becomes of a bot on a lift's car when its ride is cut short (<see cref="PlayerbotElevator"/>): a party taking it over, a car that
/// leaves the world under it, a login or a revival in the shaft, a death aboard. The fixture world has no floor under the cars (the
/// real data has none: a car is a dynamic object in no collision data, and under it lies only the bottom of the shaft), so a bot moved off
/// a car where it stands would hang in the air or drop 130 yards. The review of 80a6eb11 (cycle 2) reproduced exactly that on the real
/// Great Lift: a party invite at the top stop, z 85.4 to -47.9, the bot dead.
/// </summary>
public sealed class PlayerbotElevatorSafetyTests(ITestOutputHelper output)
{
    private static PlayerbotOptions Options() => new() { Enabled = true, ThinkIntervalMs = 100 };

    /// <summary>Drive a ride from the canyon floor until the bot is aboard and at least <paramref name="above"/> yards up the shaft.</summary>
    internal static async Task RideIntoTheShaftAsync(ElevatorTestWorld world, PlayerbotOptions options, float above)
    {
        ElevatorLift lift = await world.GreatLiftAsync();
        // The login settle first (one think without a trip; the bot stands outside every car's footprint): it is spent, so nothing but
        // the ride under test can board or move the bot afterwards.
        await world.OnWorldAsync(() => PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _));
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

    /// <summary>The bot's acknowledgements (a rescue's near teleport) answered, as its brain answers them every update.</summary>
    private static async Task AcknowledgeAsync(ElevatorTestWorld world)
    {
        for (int i = 0; i < 40; i++)
        {
            bool pending = await world.OnWorldAsync(() => PlayerbotMovementControl.Update(world.Session, world.Player)
                || world.Teleports.StageOf(world.Player) is not null);
            if (!pending) return;
            await world.AdvanceAsync(50);
        }

        Assert.Fail("the bot's acknowledgements never settled");
    }

    /// <summary>On a landing of the fixture lift: on one of the two floors, outside both cars' footprints.</summary>
    private static void AssertOnALanding(Vector3 at, string what)
    {
        bool onAFloor = MathF.Abs(at.Z - ElevatorTestWorld.BottomFloor) < 1f || MathF.Abs(at.Z - ElevatorTestWorld.TopFloor) < 1f;
        float fromShafts = ElevatorTestWorld.Shafts.Min(s => Vector2.Distance(s, new Vector2(at.X, at.Y)));
        Assert.True(onAFloor && fromShafts >= PlayerbotElevator.LandingRadii[0] - 0.5f, $"{what}: at {at}, {fromShafts:F1} yards from a shaft: not on a landing");
    }

    private static (Vector3 At, bool Aboard, bool Falling, uint Health, bool Alive) State(ElevatorTestWorld world)
    {
        Player p = world.Player;
        return (new Vector3(p.X, p.Y, p.Z), (p.Movement.Flags & MovementFlags.OnTransport) != 0,
            (p.Movement.Flags & MovementFlags.FallingFar) != 0, p.Health, p.IsAlive);
    }

    /// <summary>
    /// The review's blocking case: a party takes a riding bot over between floors (ManagedPlayerbotFeature cancels the ride). The ride is
    /// kept, alighting at the next stop, and holds the bot until then (<see cref="PlayerbotElevator.HoldsForLanding"/>); the bot steps off
    /// on the plateau, unhurt, never in the air.
    /// </summary>
    [Fact]
    public async Task ACancelledRide_BetweenFloors_AlightsAtTheNextStop()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        PlayerbotOptions options = Options();
        await RideIntoTheShaftAsync(world, options, above: 40f);
        uint healthBefore = await world.OnWorldAsync(() =>
        {
            uint health = world.Player.Health;
            PlayerbotElevator.Cancel(world.Player);
            return health;
        });
        await world.OnWorldAsync(() =>
        {
            Assert.True(PlayerbotElevator.HoldsForLanding(world.Player), "the cancel let go of the bot mid-shaft");
            Assert.Equal(ElevatorStage.Aboard, PlayerbotElevator.TripOf(world.Player)?.Stage);
            Assert.True(PlayerbotElevator.TripOf(world.Player)!.Alighting);
        });

        ElevatorStep step = ElevatorStep.Traveling;
        float lowest = float.MaxValue;
        for (int i = 0; i < 600 && step == ElevatorStep.Traveling; i++)
        {
            (step, bool holds, float z) = await world.TickAsync(() => (PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _),
                PlayerbotElevator.HoldsForLanding(world.Player), world.Player.Z));
            if (step == ElevatorStep.Traveling) Assert.True(holds, "the hold ended before the bot stood on a landing");
            lowest = MathF.Min(lowest, z);
        }

        (Vector3 at, bool aboard, _, uint health, bool alive) = await world.OnWorldAsync(() => State(world));
        output.WriteLine($"{step}: at {at}, aboard {aboard}, health {healthBefore} -> {health}, lowest z {lowest:F1}");
        Assert.Equal(ElevatorStep.Arrived, step);
        Assert.False(await world.OnWorldAsync(() => PlayerbotElevator.HoldsForLanding(world.Player)));
        Assert.True(alive);
        Assert.False(aboard, "still on the transport");
        Assert.True(MathF.Abs(at.Z - ElevatorTestWorld.TopFloor) < 1f && at.X >= ElevatorTestWorld.Cliff, $"at {at}: not on the plateau");
        Assert.Equal(healthBefore, health);
    }

    /// <summary>A cancel while the car stands at the foot: the bot alights there at once, onto the canyon landing, unhurt.</summary>
    [Fact]
    public async Task ACancelledRide_AtAFloor_AlightsOntoTheLanding()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        PlayerbotOptions options = Options();
        await RideIntoTheShaftAsync(world, options, above: -10f); // aboard, still at the foot
        uint healthBefore = await world.OnWorldAsync(() => world.Player.Health);
        ElevatorCar car = (await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)!.Car))!;
        ElevatorStep step = await world.OnWorldAsync(() =>
        {
            PlayerbotElevator.Cancel(world.Player);
            return PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _);
        });
        (Vector3 at, bool aboard, _, uint health, bool alive) = await world.OnWorldAsync(() => State(world));
        output.WriteLine($"{step}: at {at}, {Vector2.Distance(new(at.X, at.Y), new(car.Low.X, car.Low.Y)):F1} yards from the car");
        Assert.True(alive);
        Assert.False(aboard);
        Assert.True(Vector2.Distance(new(at.X, at.Y), new(car.Low.X, car.Low.Y)) >= PlayerbotElevator.LandingRadii[0] - 0.5f, $"at {at}: still on the car");
        Assert.True(MathF.Abs(at.Z - ElevatorTestWorld.BottomFloor) < 1f, $"at {at}");
        Assert.Equal(healthBefore, health);
        Assert.Equal(ElevatorStep.Arrived, step);
    }

    /// <summary>
    /// The car leaves the world 10 yards up a shaft with a floor at its bottom: a fall that costs nothing (under 14.57 yards) is taken
    /// as a client takes it, and the trip fails with its reason.
    /// </summary>
    [Fact]
    public async Task TheCarLeavesTheWorld_LowInAShaftWithAFloor_TheBotFalls()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync(shaftFloor: ElevatorTestWorld.BottomFloor);
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        PlayerbotOptions options = Options();
        await RideIntoTheShaftAsync(world, options, above: 10f);
        uint healthBefore = await world.OnWorldAsync(() =>
        {
            Assert.True(world.Objects()!.Remove(PlayerbotElevator.TripOf(world.Player)!.CarObject!), "the car was not removed");
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

        (Vector3 at, bool aboard, _, uint health, bool alive) = await world.OnWorldAsync(() => State(world));
        output.WriteLine($"{step}: {failure}; at {at}, health {healthBefore} -> {health}");
        Assert.True(fell, "the bot never fell");
        Assert.Equal(ElevatorStep.Failed, step);
        Assert.Contains("fell", failure);
        Assert.False(aboard);
        Assert.True(alive);
        Assert.True(MathF.Abs(at.Z - ElevatorTestWorld.BottomFloor) < 1f, $"at {at}");
        Assert.Equal(healthBefore, health);
    }

    /// <summary>
    /// The car leaves the world 60 yards up a shaft with no floor under it (the real data): no fall; the bot is placed on a landing (a
    /// near teleport its client acknowledges), alive and unhurt, and the trip fails with the reason and where it was placed.
    /// </summary>
    [Fact]
    public async Task TheCarLeavesTheWorld_HighInTheShaft_TheBotIsPlacedOnALanding()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        PlayerbotOptions options = Options();
        await RideIntoTheShaftAsync(world, options, above: 60f);
        uint healthBefore = await world.OnWorldAsync(() =>
        {
            Assert.True(world.Objects()!.Remove(PlayerbotElevator.TripOf(world.Player)!.CarObject!), "the car was not removed");
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

        await AcknowledgeAsync(world);
        (Vector3 at, bool aboard, bool falling, uint health, bool alive) = await world.OnWorldAsync(() => State(world));
        output.WriteLine($"{step}: {failure}; at {at}, health {healthBefore} -> {health}");
        Assert.False(fell, "a fall into the shaft");
        Assert.Equal(ElevatorStep.Failed, step);
        Assert.Contains("placed on", failure);
        Assert.True(alive);
        Assert.False(aboard);
        Assert.False(falling);
        AssertOnALanding(at, "after the rescue");
        Assert.Equal(healthBefore, health);
    }

    /// <summary>
    /// A bot logged in hanging in a shaft (saved mid-ride) with the car elsewhere: at its first think it is placed on a landing by a near
    /// teleport, alive, and never falls.
    /// </summary>
    [Fact]
    public async Task ABotLoggedInHangingInTheShaft_IsPlacedOnALanding()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        ElevatorLift lift = await world.GreatLiftAsync();
        ElevatorCar bottom = lift.Cars.Single(c => c.SpawnGuid == 16876);
        await world.PlaceAsync(new Vector3(bottom.Low.X + 1f, bottom.Low.Y, 0f));
        // Away from where the car is now (it passes any height for a moment twice a cycle).
        float carZ = await world.OnWorldAsync(() => world.Objects()!.ElevatorPosition(world.Objects()!.GameObjects.First(g => g.Spawn?.Guid == 16876))!.Value.Z);
        float z = MathF.Abs(carZ) < 15f ? 50f : 0f;
        if (z != 0f) await world.PlaceAsync(new Vector3(bottom.Low.X + 1f, bottom.Low.Y, z));
        uint healthBefore = await world.OnWorldAsync(() => world.Player.Health);
        var brain = new PlayerbotBrain(world.Session, Options());
        // The first think places the bot (a near teleport); whatever the brain does afterwards (a walk) is not the rescue's.
        bool rescued = await world.OnWorldAsync(() =>
        {
            brain.Update(100);
            return world.Teleports.StageOf(world.Player) is not null;
        });
        Assert.True(rescued, "no rescue at the bot's first think: it was left hanging in the shaft");
        await AcknowledgeAsync(world);
        bool fell = await world.OnWorldAsync(() => (world.Player.Movement.Flags & MovementFlags.FallingFar) != 0);
        (Vector3 at, bool aboard, _, uint health, bool alive) = await world.OnWorldAsync(() => State(world));
        output.WriteLine($"placed in the shaft at z {z} (car at {carZ:F1}); now at {at}");
        Assert.False(fell, "the bot fell");
        Assert.True(alive);
        Assert.False(aboard);
        AssertOnALanding(at, "after the login");
        Assert.Equal(healthBefore, health);
    }

    /// <summary>
    /// A bot logged in on the car parked at the top (saved aboard): it boards the car again (the same world position, now on the
    /// transport), rides nowhere it need not, and alights on the plateau, never below it.
    /// </summary>
    [Fact]
    public async Task ABotLoggedInOnAParkedCar_BoardsIt_AndAlightsOnThePlateau()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        ElevatorLift lift = await world.GreatLiftAsync();
        ElevatorCar top = lift.Cars.Single(c => c.SpawnGuid == 16874);
        // Until the top car stands at the top with seconds to spare (its path: there from 0 to 5 s and again at the cycle's end).
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
        var brain = new PlayerbotBrain(world.Session, Options());
        float lowest = await world.TickAsync(() => { brain.Update(100); return world.Player.Z; });
        // The car stands at the top: the bot boards it and alights onto the landing in the same think (Drive drives a ride Settle began).
        ElevatorTrip? trip = await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player));
        output.WriteLine($"after the first think: trip {trip?.Stage}, car {trip?.Car?.SpawnGuid}, alighting {trip?.Alighting}");
        Assert.True(trip is { Alighting: true, Car.SpawnGuid: 16874 }, "the bot was not boarded on the car it stood on");

        for (int i = 0; i < 600 && PlayerbotElevator.IsTraveling(world.Player); i++)
            lowest = MathF.Min(lowest, await world.TickAsync(() => { brain.Update(100); return world.Player.Z; }));
        (Vector3 at, bool aboard, _, _, bool alive) = await world.OnWorldAsync(() => State(world));
        output.WriteLine($"off at {at}; lowest z {lowest:F1}");
        Assert.Equal(ElevatorStage.Done, await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)?.Stage));
        Assert.True(alive);
        Assert.False(aboard);
        Assert.True(lowest >= ElevatorTestWorld.TopFloor - 1f, $"went down to z {lowest:F1}");
        Assert.True(MathF.Abs(at.Z - ElevatorTestWorld.TopFloor) < 1f && at.X >= ElevatorTestWorld.Cliff, $"at {at}: not on the plateau");
    }

    /// <summary>
    /// A bot logged in on a car sinking through the shaft (saved aboard mid-ride): it boards the car (on the transport, the car's GUID),
    /// rides it down and alights on the canyon landing, alive, never below the canyon floor.
    /// </summary>
    [Fact]
    public async Task ABotLoggedInOnAMovingCar_BoardsIt_AndAlightsAtTheNextStop()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        // Until the top car sinks through the middle of the shaft (its path: down from 5 s to 15 s).
        for (int i = 0; i < 700; i++)
        {
            uint? progress = await world.OnWorldAsync(() =>
            {
                GameObjectMapSystem? objects = world.Objects();
                GameObject? car = objects?.GameObjects.FirstOrDefault(g => g.Spawn?.Guid == 16874);
                return car is null ? null : objects!.ElevatorProgressAhead(car, 0);
            });
            if (progress is >= 8_000 and <= 8_500) break;
            await world.AdvanceAsync(50);
        }

        // Where the car will be a moment from now (the near teleport takes a world tick or two).
        (Vector3 onCar, ulong carGuid) = await world.OnWorldAsync(() =>
        {
            GameObjectMapSystem objects = world.Objects()!;
            GameObject car = objects.GameObjects.First(g => g.Spawn?.Guid == 16874);
            var at = objects.ElevatorPositionAt(car, objects.ElevatorProgressAhead(car, 100)!.Value)!.Value;
            return (new Vector3(at.X + 1f, at.Y, at.Z), car.Guid.Value);
        });
        await world.PlaceAsync(onCar);
        var brain = new PlayerbotBrain(world.Session, Options());
        (bool boarded, ulong transport, float z) = await world.TickAsync(() =>
        {
            brain.Update(100);
            return ((world.Player.Movement.Flags & MovementFlags.OnTransport) != 0, world.Player.Movement.TransportGuid, world.Player.Z);
        });
        ElevatorTrip? trip = await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player));
        output.WriteLine($"placed at {onCar}; boarded {boarded}, trip {trip?.Stage} to {trip?.To}, alighting {trip?.Alighting}");
        Assert.True(boarded, "the bot did not board the car it stood on");
        Assert.Equal(carGuid, transport);
        Assert.True(trip is { Stage: ElevatorStage.Aboard, Alighting: true, To: ElevatorLevel.Low });

        float lowest = z;
        for (int i = 0; i < 600 && PlayerbotElevator.IsTraveling(world.Player); i++)
            lowest = MathF.Min(lowest, await world.TickAsync(() => { brain.Update(100); return world.Player.Z; }));
        (Vector3 at, bool aboard, _, _, bool alive) = await world.OnWorldAsync(() => State(world));
        output.WriteLine($"off at {at}; lowest z {lowest:F1}");
        Assert.Equal(ElevatorStage.Done, await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)?.Stage));
        Assert.True(alive);
        Assert.False(aboard);
        Assert.True(lowest >= ElevatorTestWorld.BottomFloor - 1f, $"went down to z {lowest:F1}");
        AssertOnALanding(at, "after the ride");
    }

    /// <summary>
    /// A bot killed aboard: the corpse loses the transport, and an observer is told. The death roots the bot; the brain cancels the ride
    /// before it acknowledges the root, so the acknowledgement relayed as MSG_MOVE_ROOT carries the cleared block at the body's world
    /// position. A bot already rooted when it died gets no new root: the cleared block is relayed as MSG_MOVE_HEARTBEAT.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABotKilledAboard_LosesTheTransport_AndObserversAreTold(bool rootedBefore)
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        await using WorldTestClient watcher = await world.ObserverAsync("WATCHER", "Watcher", ElevatorTestWorld.CanyonStart + new Vector3(20f, 0f, 0f));
        PlayerbotOptions options = Options();
        var brain = new PlayerbotBrain(world.Session, options);
        await RideIntoTheShaftAsync(world, options, above: 20f);
        if (rootedBefore)
        {
            await world.OnWorldAsync(() =>
            {
                world.Player.SetRooted(true);
                PlayerbotMovementControl.Update(world.Session, world.Player);
                Assert.True(world.Player.Movement.HasFlag(MovementFlags.Root), "the bot was not rooted");
            });
        }

        await world.AdvanceAsync(100);
        await watcher.CollectAsync(TimeSpan.FromMilliseconds(200));
        (Vector3 body, ElevatorTrip? trip, bool aboard) = await world.OnWorldAsync(() =>
        {
            Player p = world.Player;
            p.Map!.Combat.Kill(null, p); // JUST_DIED: the death roots the bot (MapCombat.SetDeathState)
            brain.Update(1);
            return (new Vector3(p.X, p.Y, p.Z), PlayerbotElevator.TripOf(p), (p.Movement.Flags & MovementFlags.OnTransport) != 0);
        });
        Assert.Null(trip);
        Assert.False(aboard, "the corpse still rides the car");
        await world.AdvanceAsync(100);

        WorldOpcode expected = rootedBefore ? WorldOpcode.MsgMoveHeartbeat : WorldOpcode.MsgMoveRoot;
        List<(WorldOpcode Opcode, byte[] Payload)> seen = await watcher.CollectAsync(TimeSpan.FromMilliseconds(300));
        MovementInfo? told = null;
        foreach ((WorldOpcode opcode, byte[] payload) in seen)
        {
            if (opcode != expected) continue;
            var reader = new PacketReader(payload);
            reader.ReadPackedGuid();
            MovementInfo block = MovementInfo.Read(ref reader);
            if (!block.HasFlag(MovementFlags.OnTransport)) told = block;
        }

        output.WriteLine($"seen: {string.Join(", ", seen.Select(p => p.Opcode))}");
        Assert.True(told is not null, $"no {expected} without the transport reached the observer");
        Assert.Equal(0ul, told.Value.TransportGuid);
        Assert.True(Vector3.Distance(new Vector3(told.Value.X, told.Value.Y, told.Value.Z), body) < 0.01f, $"told {told.Value.X},{told.Value.Y},{told.Value.Z}, body at {body}");
    }

    /// <summary>
    /// A party takes the bot over while it rides between floors, through ManagedPlayerbotFeature's own hand-off: the feature holds the
    /// bot (its ride drives it, the party does not) until the bot stands on a landing, and only then does the party AI drive it.
    /// </summary>
    [Fact]
    public async Task APartyInvite_WhileAboard_WaitsForTheLanding()
    {
        await using ElevatorPartyWorld world = await ElevatorPartyWorld.StartAsync("Liftparty");
        await world.RideIntoTheShaftAsync(above: 40f);
        await world.InviteAsync();
        (bool partyDrove, bool aboardAtHandOver, float lowest, bool held, Vector3? offCarElsewhere) = await world.WatchHandOverAsync();
        output.WriteLine($"party drove {partyDrove}, aboard at hand-over {aboardAtHandOver}, held for the landing {held}, lowest z {lowest:F1}");
        Assert.True(partyDrove, "the party never took the bot over");
        Assert.True(held, "the feature never held the bot for the landing");
        Assert.False(aboardAtHandOver, "the party took the bot over on the car");
        Assert.True(offCarElsewhere is null, $"the bot stood off the car at {offCarElsewhere}: not on the plateau");
        Assert.True(lowest > ElevatorTestWorld.BottomFloor + 30f, $"went down to z {lowest:F1} after the invite");
    }

    [Fact]
    public async Task ToStopAndWaiting_WhileCarAway_GuardShaftAndClampLandingOutsideFootprint()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        ElevatorLift lift = await world.GreatLiftAsync();
        ElevatorCar topCar = lift.Cars.Single(c => c.SpawnGuid == 16874);

        await world.PlaceAsync(new Vector3(ElevatorTestWorld.Cliff + 8f, topCar.High.Y, ElevatorTestWorld.TopFloor));

        // Advance until topCar is down in the shaft (at least 15 yd below High.Z).
        for (int i = 0; i < 700; i++)
        {
            bool away = await world.OnWorldAsync(() =>
            {
                GameObjectMapSystem? objects = world.Objects();
                GameObject? car = objects?.GameObjects.FirstOrDefault(g => g.Spawn?.Guid == 16874);
                return car is not null && objects!.ElevatorPosition(car) is { } pos && MathF.Abs(pos.Z - topCar.High.Z) > 15f;
            });
            if (away) break;
            await world.AdvanceAsync(50);
        }

        PlayerbotOptions options = Options();
        await world.OnWorldAsync(() =>
        {
            PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _); // spend login settle
            PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.High, ElevatorTestWorld.CanyonStart);
            PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _);
        });

        await world.PlaceAsync(topCar.High + new Vector3(2f, 0f, 0f));
        await world.OnWorldAsync(() =>
        {
            ElevatorTrip trip = PlayerbotElevator.TripOf(world.Player)!;
            trip.Car = topCar;
            trip.CarObject = world.Objects()!.GameObjects.FirstOrDefault(g => g.Spawn?.Guid == 16874);
            trip.Landing = topCar.High;
            bool guarded = PlayerbotElevator.GuardPlatformShaft(world.Session, world.Player, world.Player.Map!, trip);
            Assert.True(guarded, "expected GuardPlatformShaft to step the bot out of the open shaft");

            float landingDist = Vector2.Distance(ElevatorNetwork.Flat(trip.Landing), ElevatorNetwork.Flat(topCar.High));
            Assert.True(landingDist >= PlayerbotElevator.CarHalfYards, $"expected clamped landing outside shaft, got {landingDist:F2} yd");

            // Also verify that SafeWaitOutsideShaft on an Undercity vault lift (map 0) stays outside VaultDoorOuterYards (10.6 yd).
            var ucStop = new Vector3(1595f, 189f, 1.18f);
            Vector3 ucSafe = PlayerbotElevator.SafeWaitOutsideShaft(world.Player.Map!, ucStop, ucStop + new Vector3(20f, 0f, 0f), mapIdOverride: 0);
            float ucDist = Vector2.Distance(ElevatorNetwork.Flat(ucSafe), ElevatorNetwork.Flat(ucStop));
            Assert.True(ucDist >= PlayerbotElevator.VaultDoorOuterYards, $"expected vault wait radius >= {PlayerbotElevator.VaultDoorOuterYards:F1}, got {ucDist:F2}");
        });
    }

    [Fact]
    public async Task WaitingBot_DroppedToDestinationFloor_CompletesTripInsteadOf90sWaitTimeout()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        ElevatorLift lift = await world.GreatLiftAsync();
        ElevatorCar topCar = lift.Cars.Single(c => c.SpawnGuid == 16874);

        await world.PlaceAsync(new Vector3(ElevatorTestWorld.Cliff + 8f, topCar.High.Y, ElevatorTestWorld.TopFloor));
        PlayerbotOptions options = Options();
        await world.OnWorldAsync(() =>
        {
            PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _); // spend login settle
            PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.High, ElevatorTestWorld.CanyonStart);
        });

        // Drive until the bot reaches Waiting at the top landing.
        for (int i = 0; i < 40 && await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)?.Stage) == ElevatorStage.ToStop; i++)
        {
            await world.TickAsync(() => PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _));
        }

        Assert.Equal(ElevatorStage.Waiting, await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)?.Stage));

        // Place the bot at the bottom floor near the shaft (within 0.24 yd of BottomFloor so InPit is false),
        // reproducing the flight-recorder scenario where the bot reached the bottom floor while still in Waiting.
        await world.PlaceAsync(new Vector3(topCar.Low.X - 6.5f, topCar.Low.Y, ElevatorTestWorld.BottomFloor - 0.2f));

        (ElevatorStep step, string? why) = await world.TickAsync(() =>
            (PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out string? f), f));

        Assert.Null(why);
        Assert.Equal(ElevatorStep.Arrived, step);
        Assert.Equal(ElevatorStage.Done, await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)?.Stage));
    }

    [Fact]
    public async Task WaitingBot_DisplacedOnPumpTicksOnly_TransitionsBackToToStopWhileCarAway()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        ElevatorLift lift = await world.GreatLiftAsync();
        ElevatorCar bottomCar = lift.Cars.Single(c => c.SpawnGuid == 16876);

        // Advance until the bottom car is up in the shaft so it is not standing at Low.
        for (int i = 0; i < 700; i++)
        {
            uint? progress = await world.OnWorldAsync(() =>
            {
                GameObjectMapSystem? objects = world.Objects();
                GameObject? car = objects?.GameObjects.FirstOrDefault(g => g.Spawn?.Guid == 16876);
                return car is null ? null : objects!.ElevatorProgressAhead(car, 0);
            });
            if (progress is >= 14_000 and <= 16_000) break;
            await world.AdvanceAsync(50);
        }

        await world.PlaceAsync(new Vector3(bottomCar.Low.X - 6.5f, bottomCar.Low.Y, ElevatorTestWorld.BottomFloor));
        PlayerbotOptions options = Options();
        await world.OnWorldAsync(() =>
        {
            PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _); // spend login settle
            PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.Low, ElevatorTestWorld.Ratchet);
        });

        for (int i = 0; i < 40 && await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)?.Stage) == ElevatorStage.ToStop; i++)
        {
            await world.TickAsync(() => PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out _));
        }

        Assert.Equal(ElevatorStage.Waiting, await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)?.Stage));

        // Displace the bot far from the landing while the car is still away in the shaft, and run Pump only (no Drive).
        await world.PlaceAsync(new Vector3(bottomCar.Low.X - 25f, bottomCar.Low.Y, ElevatorTestWorld.BottomFloor));
        await world.AdvanceAsync(300);
        ElevatorStage? stageAfterPump = await world.OnWorldAsync(() =>
        {
            PlayerbotElevator.Pump(world.Session, world.Player, world.Host.World.NowMs);
            return PlayerbotElevator.TripOf(world.Player)?.Stage;
        });

        Assert.Equal(ElevatorStage.ToStop, stageAfterPump);
    }
}

using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Transports;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Transports.TransportTestKit;

namespace ArcaneCore.Game.Tests.Transports;

/// <summary>
/// vmangos <c>ShipTransport</c> (Transport.cpp) and the transport parts of <c>Map</c>: spawning, the timer-driven motion,
/// the map-wide create/out-of-range packets, passengers moving with the ship and the map change at a dock.
/// </summary>
public sealed class ShipTransportTests
{
    [Fact]
    public void Install_SpawnsEachContinentRouteOnce_AtItsFirstKeyFrame()
    {
        WorldRuntime world = ManualWorld();
        TransportSystem system = Install(world, Ferry, Crossing);

        Assert.Equal(2, system.Ships.Count);
        ShipTransport ferry = system.FindByEntry(Ferry)!;
        Assert.Equal(new ObjectGuid(0x1FC0_0000_0000_0000UL | Ferry), ferry.Guid);
        Assert.Same(world.FindMap(0), ferry.CurrentMap);
        Assert.Null(ferry.Map); // not a grid object
        Assert.Equal((100f, 0f, 0f), (ferry.X, ferry.Y, ferry.Z));
        Assert.Equal(0u, ferry.PathProgress);
        Assert.Equal([ferry, system.FindByEntry(Crossing)!], system.ShipsOn(world.FindMap(0)!));
        Assert.Empty(system.ShipsOn(world.GetMap(1)));
    }

    [Fact]
    public void Ship_WaitsAtTheStop_ThenAcceleratesAndCruises_OnTheWorldClock()
    {
        WorldRuntime world = ManualWorld();
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;

        Advance(world, 5000);
        Assert.False(ferry.IsMoving);
        Assert.Equal(100f, ferry.X);

        Advance(world, 10000); // t = 15 s: 5 s of acceleration = 12.5 yd
        Assert.True(ferry.IsMoving);
        Assert.Equal(112.5f, ferry.X, 1);

        Advance(world, 15000); // t = 30 s: 50 yd accelerating + 10 s at 10 yd/s
        Assert.Equal(250f, ferry.X, 1);
        Assert.Equal(MathF.PI, ferry.Orientation, 3);
        Assert.Equal(30000u, ferry.PathProgress);
    }

    [Fact]
    public void Ship_AtTheLastStop_JumpsBackToTheFirstFrame_AndKeepsCounting()
    {
        WorldRuntime world = ManualWorld();
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;

        Advance(world, 49000);
        Assert.InRange(ferry.X, 399f, 400f);
        Advance(world, 2000); // arrival at the last (teleport) frame: the route wraps to the first frame
        Assert.Equal(100f, ferry.X, 1);
        Advance(world, 15000); // 66 s = 6 s into the next period: still waiting at the first stop
        Assert.False(ferry.IsMoving);
        Assert.Equal(66000u, ferry.PathProgress);
    }

    [Fact]
    public void PlayerEnteringTheMap_GetsEveryShip_BeforeItsOwnCreateBlock()
    {
        WorldRuntime world = ManualWorld();
        Install(world, Ferry, Crossing);
        var session = new FakeSession(1);
        Player player = TestWorld.CreatePlayer(1, 10, 10, session);

        world.AddPlayer(player);

        (WorldOpcode Opcode, byte[] Payload)[] sent = session.Sent.ToArray();
        int ships = Array.FindIndex(sent, p => p.Opcode == WorldOpcode.SmsgUpdateObject && p.Payload[4] == 1);
        int self = Array.FindIndex(sent, p => p.Opcode == WorldOpcode.SmsgUpdateObject && p.Payload[4] == 0);
        Assert.True(ships >= 0 && ships < self, "the ships come first");
        byte[] payload = sent[ships].Payload;
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(payload));

        // First block: CREATE_OBJECT, packed GUID 1FC0...entry, TYPEID_GAMEOBJECT, flags TRANSPORT|ALL|HAS_POSITION, 0, 0, 0, o, 1, progress.
        var reader = new PacketReader(payload.AsSpan(5));
        Assert.Equal((byte)ObjectUpdateType.CreateObject, reader.ReadByte());
        Assert.Equal(0x1FC0_0000_0000_0000UL | Ferry, reader.ReadPackedGuid());
        Assert.Equal((byte)TypeId.GameObject, reader.ReadByte());
        Assert.Equal((byte)0x52, reader.ReadByte());
        Assert.Equal((0f, 0f, 0f), (reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
        Assert.Equal(MathF.PI, reader.ReadSingle(), 4);
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32()); // path progress at creation
    }

    [Fact]
    public void Passenger_MovesWithTheShip_AtItsRotatedOffset()
    {
        WorldRuntime world = ManualWorld();
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;
        var session = new FakeSession(1);
        Player player = TestWorld.CreatePlayer(1, 100, 0, session);
        world.AddPlayer(player);
        player.ApplyMovement(Aboard(ferry, 3f, 2f, 5f), world.NowMs);

        Assert.True(ferry.AddPassenger(player));
        Assert.Same(ferry, player.Transport);
        Assert.Equal((3f, 2f, 5f), (player.Movement.TransportX, player.Movement.TransportY, player.Movement.TransportZ)); // same ship: offset kept

        Advance(world, 30000); // the ship is at x=250 facing pi: the offset is turned half round
        Assert.Equal(250f - 3f, player.X, 1);
        Assert.Equal(-2f, player.Y, 1);
        Assert.Equal(5f, player.Z, 1);
        Assert.True(player.Movement.HasFlag(MovementFlags.OnTransport));
        Assert.Equal(ferry.Guid.Value, player.Movement.TransportGuid);
        Assert.Equal(player.X, player.Movement.X);
    }

    [Fact]
    public void Boarding_FromAnotherShipOrLand_ComputesTheOffsetFromTheWorldPosition()
    {
        WorldRuntime world = ManualWorld();
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;
        Player player = TestWorld.CreatePlayer(1, 104, 1, new FakeSession(1));
        world.AddPlayer(player);

        ferry.AddPassenger(player);

        // Ship at (100, 0, 0) facing pi: world (104, 1, 83.5) is offset (-4, -1, 83.5).
        Assert.Equal(-4f, player.Movement.TransportX, 3);
        Assert.Equal(-1f, player.Movement.TransportY, 3);
        Assert.Equal(83.5f, player.Movement.TransportZ, 3);
        float x = player.Movement.TransportX, y = player.Movement.TransportY, z = player.Movement.TransportZ, o = player.Movement.TransportOrientation;
        ferry.CalculatePassengerPosition(ref x, ref y, ref z, ref o);
        Assert.Equal(104f, x, 3);
        Assert.Equal(1f, y, 3);
    }

    [Fact]
    public void RemovePassenger_ClearsTheLinkAndTheOffset()
    {
        WorldRuntime world = ManualWorld();
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;
        Player player = TestWorld.CreatePlayer(1, 100, 0, new FakeSession(1));
        world.AddPlayer(player);
        ferry.AddPassenger(player);

        Assert.True(ferry.RemovePassenger(player));
        Assert.False(ferry.RemovePassenger(player));
        Assert.Null(player.Transport);
        Assert.Empty(ferry.Passengers);
        Assert.Equal(0UL, player.Movement.TransportGuid);
    }

    [Fact]
    public void MapChange_LeavesTheOldMapsPlayers_ArrivesOnTheNewMap_AndCarriesThePassenger()
    {
        WorldRuntime world = ManualWorld();
        TransportSystem system = Install(world, Crossing);
        ShipTransport ship = system.FindByEntry(Crossing)!;
        var teleports = new TeleportService(world, _ => { }, _ => { });
        system.TeleportPassenger = (p, map, x, y, z, o) => teleports.TeleportTo(p, map, x, y, z, o, TeleportOptions.NotLeaveTransport);

        var riderSession = new FakeSession(1);
        Player rider = TestWorld.CreatePlayer(1, 100, 0, riderSession);
        world.AddPlayer(rider);
        rider.ApplyMovement(Aboard(ship, 1f, 0f, 2f), world.NowMs);
        ship.AddPassenger(rider);

        var dockSession = new FakeSession(2);
        Player dock = TestWorld.CreatePlayer(2, 300, 0, dockSession);
        world.AddPlayer(dock);
        var farSession = new FakeSession(3);
        Player far = TestWorld.CreatePlayer(3, 1100, 0, farSession, mapId: 1);
        world.AddPlayer(far);
        riderSession.Clear();
        dockSession.Clear();
        farSession.Clear();

        Advance(world, 36000);

        Assert.Same(world.FindMap(1), ship.CurrentMap);
        Assert.Equal(1u, ship.MapId);
        Assert.Equal(1100f, ship.X, 1);

        // The player on the dock loses the ship, the player on map 1 gets it, the passenger hears neither.
        Assert.Contains(TransportUpdates(dockSession), p => p[5] == (byte)ObjectUpdateType.OutOfRangeObjects);
        Assert.Contains(TransportUpdates(farSession), p => p[5] == (byte)ObjectUpdateType.CreateObject);
        Assert.DoesNotContain(TransportUpdates(riderSession), p => p[5] == (byte)ObjectUpdateType.OutOfRangeObjects);

        // The passenger is on its way: SMSG_TRANSFER_PENDING names the ship and the old map, SMSG_NEW_WORLD carries the offset.
        byte[] pending = Assert.Single(riderSession.Sent.ToArray(), p => p.Opcode == WorldOpcode.SmsgTransferPending).Payload;
        Assert.Equal([1u, Crossing, 0u], Enumerable.Range(0, 3).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(pending.AsSpan(i * 4))));
        byte[] newWorld = Assert.Single(riderSession.Sent.ToArray(), p => p.Opcode == WorldOpcode.SmsgNewWorld).Payload;
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(newWorld));
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(newWorld.AsSpan(4)));
        Assert.Equal(2f, BinaryPrimitives.ReadSingleLittleEndian(newWorld.AsSpan(12)));
        Assert.Null(rider.Map);
        Assert.Same(ship, rider.Transport);

        // The client loads the map: the passenger enters map 1 at its place on the ship and gets the ship first.
        riderSession.Clear();
        Assert.True(teleports.HandleWorldportAck(rider));
        world.RunTick(50);
        Assert.Same(world.FindMap(1), rider.Map);
        Assert.Same(ship, rider.Transport);
        Assert.Equal(ship.X - 1f, rider.X, 1); // facing pi: offset +1 x is world -1 x
        Assert.Equal(2f, rider.Z, 1);
        Assert.True(rider.Movement.HasFlag(MovementFlags.OnTransport));
        Assert.Equal(1f, rider.Movement.TransportX);
        Assert.NotEmpty(TransportUpdates(riderSession));

        Advance(world, 10000); // the ship departs (40 s) and the passenger goes with it on map 1
        Assert.True(ship.X > 1100f);
        Assert.Equal(ship.X - 1f, rider.X, 1);
    }

    [Fact]
    public void MapChange_WithoutATeleportHook_LeavesThePassengerAtTheDock()
    {
        WorldRuntime world = ManualWorld();
        TransportSystem system = Install(world, Crossing);
        ShipTransport ship = system.FindByEntry(Crossing)!;
        Player rider = TestWorld.CreatePlayer(1, 100, 0, new FakeSession(1));
        world.AddPlayer(rider);
        ship.AddPassenger(rider);

        Advance(world, 36000);

        Assert.Null(rider.Transport);
        Assert.Same(world.FindMap(0), rider.Map);
        Assert.False(rider.Movement.HasFlag(MovementFlags.OnTransport));
    }

    [Fact]
    public void OrdinaryTeleport_TakesThePassengerOffTheShip()
    {
        WorldRuntime world = ManualWorld();
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;
        var teleports = new TeleportService(world, _ => { }, _ => { });
        Player player = TestWorld.CreatePlayer(1, 100, 0, new FakeSession(1));
        world.AddPlayer(player);
        ferry.AddPassenger(player);

        Assert.True(teleports.TeleportTo(player, 0, 50, 50, 80, 0));

        Assert.Null(player.Transport);
        Assert.Empty(ferry.Passengers);
        Assert.False(player.Movement.HasFlag(MovementFlags.OnTransport));
        Assert.Equal(TeleportStage.Near, teleports.StageOf(player));
    }

    [Fact]
    public void LoggingOut_LeavesTheShip()
    {
        WorldRuntime world = ManualWorld();
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;
        Player player = TestWorld.CreatePlayer(1, 100, 0, new FakeSession(1));
        world.AddPlayer(player);
        ferry.AddPassenger(player);

        world.RemovePlayer(player);

        Assert.Null(player.Transport);
        Assert.Empty(ferry.Passengers);
    }

    [Fact]
    public void PassengerOffset_RoundTripsThroughWorldCoordinates()
    {
        float x = 12f, y = -7f, z = 3f, o = 1f;
        ShipTransport.CalculatePassengerPosition(ref x, ref y, ref z, ref o, 500f, 600f, 10f, 2.2f);
        ShipTransport.CalculatePassengerOffset(ref x, ref y, ref z, ref o, 500f, 600f, 10f, 2.2f);

        Assert.Equal(12f, x, 3);
        Assert.Equal(-7f, y, 3);
        Assert.Equal(3f, z, 3);
        Assert.Equal(1f, o, 3);
    }
}

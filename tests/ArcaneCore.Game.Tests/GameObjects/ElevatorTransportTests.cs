using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// Elevators and trams (GAMEOBJECT_TYPE_TRANSPORT, 11) against vmangos GameObject::Create (GameObject.cpp:207, 244-250), Object::BuildMovementUpdate
/// (Object.cpp:540-560, 590-598) and ElevatorTransport (Transports/Transport.cpp:383-430): a transport guid, the transport update flag, the pause
/// in the level field, the start state from startOpen, the transport and no-despawn flags, and the cycle progress from TransportAnimation.dbc in
/// the create block.
/// </summary>
public sealed class ElevatorTransportTests
{
    private const uint Elevator = 4170;      // classic-db "Mesa Elevator"
    private const uint Tram = 176080;        // classic-db "Subway"

    private static (WorldRuntime World, GameObjectMapSystem System, Player Player) Start(TransportAnimationCatalog? animations = null)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new GameObjectMapSystem(map, new GameObjectContent(
            [GoTemplate(Elevator, GameObjectType.Transport, (0, 3000)), GoTemplate(Tram, GameObjectType.Transport, (1, 1)), GoTemplate(1, GameObjectType.Door)],
            [GoSpawn(1, Elevator, 3, 0), GoSpawn(2, Tram, 6, 0), GoSpawn(3, 1, 9, 0)], [], [], []))
        {
            ElevatorAnimations = animations ?? TransportAnimationCatalog.Empty,
        };
        map.AddUpdater(system);
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(50);
        return (world, system, player);
    }

    [Fact]
    public void AnElevator_IsCreatedAsATransport_WithItsPauseStartStateAndFlags()
    {
        (WorldRuntime world, GameObjectMapSystem system, _) = Start();
        using (world)
        {
            GameObject elevator = system.GameObjects.Single(g => g.Entry == Elevator);
            GameObject tram = system.GameObjects.Single(g => g.Entry == Tram);
            GameObject door = system.GameObjects.Single(g => g.Entry == 1);

            Assert.Equal(HighGuid.Transport, elevator.Guid.High);
            Assert.Equal(HighGuid.GameObject, door.Guid.High);
            Assert.True((elevator.CreateUpdateFlags & ObjectUpdateFlags.Transport) != 0);
            Assert.False((door.CreateUpdateFlags & ObjectUpdateFlags.Transport) != 0);
            Assert.Equal(3000u, elevator.GetUInt32(UpdateFields.GameobjectLevel));        // transport.pause
            Assert.Equal(GameObjectState.Ready, elevator.State);                           // startOpen 0
            Assert.Equal(GameObjectState.Active, tram.State);                              // startOpen 1
            Assert.Equal(GameObjectFlags.Transport | GameObjectFlags.NoDespawn, elevator.Flags & (GameObjectFlags.Transport | GameObjectFlags.NoDespawn));
            Assert.Same(elevator, system.Find(elevator.Guid));
        }
    }

    [Fact]
    public void TheCreateBlock_CarriesTheCycleProgress_FromTheAnimation_AndZeroWithoutOne()
    {
        var animations = new TransportAnimationCatalog(
        [
            (Elevator, new TransportAnimationNode(0, 0, 0, 0)),
            (Elevator, new TransportAnimationNode(5000, 0, 0, 20)),
            (Elevator, new TransportAnimationNode(10000, 0, 0, 0)),
        ]);
        (WorldRuntime world, GameObjectMapSystem system, Player player) = Start(animations);
        using (world)
        {
            GameObject elevator = system.GameObjects.Single(g => g.Entry == Elevator);
            GameObject tram = system.GameObjects.Single(g => g.Entry == Tram);
            for (int i = 0; i < 25; i++)
            {
                world.RunTick(100);
            }

            // About 2.5 s since the object was made (the grid loaded within the first tick).
            uint progress = elevator.PathProgress;
            Assert.InRange(progress, 2400u, 2600u);
            Assert.Equal(progress, CreateBlockProgress(elevator, player));
            Assert.Equal(0u, tram.PathProgress);                     // no animation row
            Assert.Equal(0u, CreateBlockProgress(tram, player));

            // The animation's position at that progress: the 0 -> 20 rise over 5 s, turned by the spawn's facing about Z (unchanged), plus the
            // stationary position.
            (float X, float Y, float Z) at = system.ElevatorPosition(elevator)!.Value;
            Assert.Equal(elevator.X, at.X, 0.001f);
            Assert.Equal(elevator.Y, at.Y, 0.001f);
            Assert.Equal(elevator.Z + (20f * progress / 5000), at.Z, 0.01f);
            Assert.Null(system.ElevatorPosition(tram));
        }
    }

    /// <summary>The path progress field of the create block (vmangos Object.cpp:590-598): flags, the four position floats, ALL's uint, then it.</summary>
    private static uint CreateBlockProgress(GameObject go, Player viewer)
    {
        var writer = new PacketWriter();
        UpdateBlockWriter.WriteCreateBlock(writer, go, viewer, isNewObject: false, serverTimeMs: 123_456);
        var reader = new PacketReader(writer.ToArray());
        reader.ReadByte();
        reader.ReadPackedGuid();
        reader.ReadByte();
        var flags = (ObjectUpdateFlags)reader.ReadByte();
        Assert.Equal(ObjectUpdateFlags.All | ObjectUpdateFlags.HasPosition | ObjectUpdateFlags.Transport, flags);
        Assert.Equal(go.X, reader.ReadSingle());
        Assert.Equal(go.Y, reader.ReadSingle());
        Assert.Equal(go.Z, reader.ReadSingle());
        reader.ReadSingle();
        reader.ReadUInt32();
        return reader.ReadUInt32();
    }
}

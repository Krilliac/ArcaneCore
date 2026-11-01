using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using Xunit;
using static ArcaneCore.Game.Tests.Transports.TransportTestKit;

namespace ArcaneCore.Game.Tests.Cluster;

/// <summary>
/// S4a: a worker that does not own a map (Cluster:Worker:OwnedMaps) refuses it on the paths that reach maps: far teleports (before any
/// loading screen), the login map, and continent ship spawn and sailing.
/// </summary>
public sealed class WorkerOwnershipRefusalTests
{
    private static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
        ],
        [], [], [], []);

    private static void Own(WorldRuntime world, string list)
    {
        Assert.True(MapOwnership.TryParse(list, allowInstanceMaps: true, out MapOwnership ownership, out string? error), error);
        world.Ownership = ownership;
    }

    [Fact]
    public void FarTeleportToAnUnownedMap_IsRefusedBeforeAnyTransferPacket()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Own(world, "0");
        WorldMaps.Of(world).Load(Content);
        var teleports = new TeleportService(world, _ => { }, _ => { });
        world.PlayerLoggingOut += teleports.Forget;
        var session = new FakeSession(1);
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(50);
        session.Clear();

        Assert.False(teleports.CanTeleportTo(player, 1, 1000, 2000, 30, 0));
        Assert.False(teleports.TeleportTo(player, 1, 1000, 2000, 30, 0));
        world.RunTick(50);

        Assert.Null(teleports.StageOf(player));
        Assert.Same(world.FindMap(0), player.Map);
        Assert.Null(world.FindMap(1));
        Assert.DoesNotContain(session.Sent, p => p.Opcode is WorldOpcode.SmsgTransferPending or WorldOpcode.SmsgNewWorld);

        Assert.True(teleports.TeleportTo(player, 0, 500, 0, 90, 0)); // an owned map is still reachable
    }

    [Fact]
    public void LoginOnAnUnownedMap_ThrowsSoTheLoginHandlerKicksTheSession()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Own(world, "0");
        Player player = TestWorld.CreatePlayer(2, 1000, 0, new FakeSession(2), mapId: 1);

        // CharacterHandlers catches this around ResolveLoginMap and kicks ("login map could not be resolved").
        Assert.Throws<MapNotOwnedException>(() => world.ResolveLoginMap(player));
        Assert.Null(world.FindMap(1));
    }

    [Fact]
    public void ContinentShipsOnAnUnownedMap_AreSkippedAtInstall()
    {
        WorldRuntime world = ManualWorld();
        Own(world, "1");

        TransportSystem system = Install(world, Ferry); // the ferry's path starts on map 0, owned by another worker

        Assert.Empty(system.Ships);
        Assert.Null(world.FindMap(0));
    }

    [Fact]
    public void AShipWhoseNextFrameIsOnAnUnownedMap_IsHeldInsteadOfThrowing()
    {
        WorldRuntime world = ManualWorld();
        Own(world, "0");
        TransportSystem system = Install(world, Crossing);
        ShipTransport ship = system.FindByEntry(Crossing)!;

        Player player = TestWorld.CreatePlayer(3, 100, 0, new FakeSession(3));
        world.AddPlayer(player);
        ClientMoves(world, player, Aboard(ship, 4f, 0f, 6f));
        Assert.Same(ship, player.Transport);

        Advance(world, 40000); // past the map change at 35 s: the ship is held at the last frame of map 0
        (float X, float Y, float Z) shipHeld = (ship.X, ship.Y, ship.Z);
        (float X, float Y, float Z) playerHeld = (player.X, player.Y, player.Z);
        Assert.True(shipHeld.X < 1000f, "the ship must not have been dragged to the map 1 route");

        Advance(world, 8000); // the path keeps asking for the unowned map; the ship must not move or carry anyone

        Assert.Equal(0u, ship.MapId);
        Assert.Same(world.FindMap(0), ship.CurrentMap);
        Assert.Null(world.FindMap(1));
        Assert.Equal(shipHeld, (ship.X, ship.Y, ship.Z));
        Assert.Equal(playerHeld, (player.X, player.Y, player.Z));
        Assert.Same(ship, player.Transport);
        Assert.Same(world.FindMap(0), player.Map);
    }

    private static void ClientMoves(WorldRuntime world, Player player, MovementInfo movement)
    {
        MovementInfo previous = player.Movement;
        var context = new MovementObserverContext(player, world, WorldOpcode.MsgMoveHeartbeat);
        MovementObservers.Before(context, in previous, ref movement);
        player.ApplyClientMovement(movement, world.NowMs);
        MovementObservers.After(context, in previous);
    }
}

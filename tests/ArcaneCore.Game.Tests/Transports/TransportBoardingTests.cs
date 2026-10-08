using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Transports;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Transports.TransportTestKit;

namespace ArcaneCore.Game.Tests.Transports;

/// <summary>
/// Boarding and leaving through client movement (vmangos <c>WorldSession::HandleMoverRelocation</c>, MovementHandler.cpp:1075-1102),
/// run through the real movement observer pipeline the movement handlers use.
/// </summary>
public sealed class TransportBoardingTests
{
    private static void ClientMoves(WorldRuntime world, Player player, MovementInfo movement)
    {
        MovementInfo previous = player.Movement;
        var context = new MovementObserverContext(player, world, WorldOpcode.MsgMoveHeartbeat);
        MovementObservers.Before(context, in previous, ref movement);
        player.ApplyClientMovement(movement, world.NowMs);
        MovementObservers.After(context, in previous);
    }

    [Fact]
    public void MovementOnTheShip_Boards_KeepingTheClientOffset()
    {
        WorldRuntime world = ManualWorld();
        TransportSystem system = Install(world, Ferry);
        ShipTransport ferry = system.FindByEntry(Ferry)!;
        Player player = TestWorld.CreatePlayer(1, 100, 0, new FakeSession(1));
        world.AddPlayer(player);

        ClientMoves(world, player, Aboard(ferry, 4f, -1f, 6f));

        Assert.Same(ferry, player.Transport);
        Assert.Equal([player], ferry.Passengers);
        Assert.Equal((4f, -1f, 6f), (player.Movement.TransportX, player.Movement.TransportY, player.Movement.TransportZ));
        Assert.True(system.TakeJustBoarded(player));
        Assert.False(system.TakeJustBoarded(player));
    }

    [Fact]
    public void MovementAboard_TakesTheWorldPositionFromTheShip_NotFromTheClient()
    {
        WorldRuntime world = ManualWorld();
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;
        Player player = TestWorld.CreatePlayer(1, 100, 0, new FakeSession(1));
        world.AddPlayer(player);
        ClientMoves(world, player, Aboard(ferry, 4f, 0f, 6f));

        MovementInfo lying = Aboard(ferry, 2f, 0f, 6f);
        lying.X = 5000f; // a client's stale or forged world position
        lying.Y = 5000f;
        ClientMoves(world, player, lying);

        Assert.Equal(100f - 2f, player.X, 3);
        Assert.Equal(0f, player.Y, 3);
        Assert.Equal(6f, player.Z, 3);
        Assert.Equal(2f, player.Movement.TransportX);
    }

    [Fact]
    public void MovementWithoutTheFlag_LeavesTheShip()
    {
        WorldRuntime world = ManualWorld();
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;
        Player player = TestWorld.CreatePlayer(1, 100, 0, new FakeSession(1));
        world.AddPlayer(player);
        ClientMoves(world, player, Aboard(ferry, 4f, 0f, 6f));

        ClientMoves(world, player, new MovementInfo { X = 90f, Y = 5f, Z = 0f });

        Assert.Null(player.Transport);
        Assert.Empty(ferry.Passengers);
        Assert.Equal(90f, player.X);
    }

    [Fact]
    public void AnUnknownTransportGuid_BoardsNothing()
    {
        WorldRuntime world = ManualWorld();
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;
        Player player = TestWorld.CreatePlayer(1, 100, 0, new FakeSession(1));
        world.AddPlayer(player);
        MovementInfo movement = Aboard(ferry, 4f);
        movement.TransportGuid = ShipTransport.GuidFor(123456).Value;

        ClientMoves(world, player, movement);

        Assert.Null(player.Transport);
        Assert.Empty(ferry.Passengers);
    }

    [Fact]
    public void WithoutATransportSystem_TheFlagIsJustStored()
    {
        WorldRuntime world = ManualWorld();
        Player player = TestWorld.CreatePlayer(1, 100, 0, new FakeSession(1));
        world.AddPlayer(player);
        var movement = new MovementInfo
        {
            Flags = MovementFlags.OnTransport, X = 100, Y = 0, Z = 0, TransportGuid = ShipTransport.GuidFor(Ferry).Value, TransportX = 1,
        };

        ClientMoves(world, player, movement);

        Assert.Null(player.Transport);
        Assert.True(player.Movement.HasFlag(MovementFlags.OnTransport));
        Assert.Equal(100f, player.X);
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Transports.TransportTestKit;

namespace ArcaneCore.Game.Tests.Transports;

/// <summary>
/// The ship seat stored with the character (vmangos characters.transport_guid / transport_x..o: Player::SaveToDB,
/// Player.cpp:16427-16434, and Player::LoadFromDB, Player.cpp:14794-14838).
/// </summary>
public sealed class TransportSeatTests
{
    private static Player Load(uint id, IPlayerSession session, TransportSeat? seat, uint mapId = 0, float x = 100f)
    {
        Player player = TestWorld.CreatePlayer(id, x, 0, session, mapId);
        player.LoginTransportSeat = seat;
        return player;
    }

    [Fact]
    public void Snapshot_StoresTheShipAndTheOffset_WhileAboard_AndNothingOnLand()
    {
        WorldRuntime world = ManualWorld();
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;
        Player player = TestWorld.CreatePlayer(1, 100, 0, new FakeSession(1));
        world.AddPlayer(player);
        Assert.Null(player.CreateSnapshot(world.NowMs).Transport);

        player.ApplyMovement(Aboard(ferry, 3f, -1f, 4f, 0.5f), world.NowMs);
        ferry.AddPassenger(player);

        Assert.Equal(new TransportSeat(Ferry, 3f, -1f, 4f, 0.5f), player.CreateSnapshot(world.NowMs).Transport);
    }

    [Fact]
    public void Logout_Aboard_SavesTheSeat_ThoughThePlayerLeavesTheShip()
    {
        var saves = new RecordingSaveQueue();
        WorldRuntime world = TestWorld.CreateRuntime(saves);
        world.UseManualClock();
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;
        Player player = TestWorld.CreatePlayer(1, 100, 0, new FakeSession(1));
        world.AddPlayer(player);
        player.ApplyMovement(Aboard(ferry, 3f, -1f, 4f), world.NowMs);
        ferry.AddPassenger(player);

        world.RemovePlayer(player);

        Assert.Empty(ferry.Passengers);
        Assert.Equal(new TransportSeat(Ferry, 3f, -1f, 4f, 0f), Assert.Single(saves.Saved).Transport);
    }

    [Fact]
    public void Login_WithASeat_PutsThePlayerBackOnItsShip_AtItsOffset()
    {
        WorldRuntime world = ManualWorld();
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;
        Advance(world, 30000); // the ferry is at x=250 now, facing pi
        Player player = Load(1, new FakeSession(1), new TransportSeat(Ferry, 3f, 2f, 5f, 0f), x: 120f);

        world.AddPlayer(player);

        Assert.Same(ferry, player.Transport);
        Assert.Null(player.LoginTransportSeat);
        Assert.Equal(250f - 3f, player.X, 1);
        Assert.Equal(-2f, player.Y, 1);
        Assert.Equal(5f, player.Z, 1);
        Assert.Equal((3f, 2f, 5f), (player.Movement.TransportX, player.Movement.TransportY, player.Movement.TransportZ));
    }

    [Fact]
    public void Login_WithTheShipOnTheOtherMap_FollowsItThereAboard()
    {
        WorldRuntime world = ManualWorld();
        TransportSystem system = Install(world, Crossing);
        ShipTransport ship = system.FindByEntry(Crossing)!;
        var teleports = new TeleportService(world, _ => { }, _ => { });
        system.TeleportPassenger = (p, map, x, y, z, o) => teleports.TeleportTo(p, map, x, y, z, o, TeleportOptions.NotLeaveTransport);
        Advance(world, 36000); // the ship is on map 1
        var session = new FakeSession(1);
        Player player = Load(1, session, new TransportSeat(Crossing, 1f, 0f, 2f, 0f), mapId: 0);

        world.AddPlayer(player);

        // vmangos never puts the ship of the other map on the wire here (Player::LoadFromDB moves the character to the ship's
        // map first): the self create on the saved map is a player on land, with no transport GUID the client was not sent.
        Assert.Null(player.Transport);
        Assert.False(player.Movement.HasFlag(MovementFlags.OnTransport));
        SelfCreate onSavedMap = Assert.Single(SelfCreates(session, player));
        Assert.False(onSavedMap.Movement.HasFlag(MovementFlags.OnTransport));
        Assert.Equal(0UL, onSavedMap.Movement.TransportGuid);
        Assert.Equal(0, onSavedMap.HasTransport);

        world.RunTick(50); // the teleport starts after the map update
        world.RunTick(50); // and the far transfer runs after the next one

        Assert.Same(ship, player.Transport);
        Assert.Equal(TeleportStage.Far, teleports.StageOf(player));
        Assert.Contains(session.Sent.ToArray(), p => p.Opcode == WorldOpcode.SmsgTransferPending && p.Payload.Length == 12);
        Assert.True(teleports.HandleWorldportAck(player));
        world.RunTick(50);
        Assert.Equal(1u, player.Map!.MapId);
        Assert.Equal(ship.X - 1f, player.X, 1);

        // On the ship's map the self packet carries the ship ahead of the player (vmangos Map::SendInitSelf).
        SelfCreate aboard = SelfCreates(session, player)[^1];
        Assert.True(aboard.Movement.HasFlag(MovementFlags.OnTransport));
        Assert.Equal(ship.Guid.Value, aboard.Movement.TransportGuid);
        Assert.Equal(1, aboard.HasTransport);
        Assert.Equal(ship.Guid.Value, aboard.FirstBlockGuid);
    }

    [Fact]
    public void Login_AboardOnTheSameMap_TheShipComesInTheSelfPacket_NotTheMapsShipPacket()
    {
        WorldRuntime world = ManualWorld();
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;
        var session = new FakeSession(1);
        Player player = Load(1, session, new TransportSeat(Ferry, 3f, 2f, 5f, 0f));

        world.AddPlayer(player);

        // vmangos SendInitTransports leaves the player's own ship out; SendInitSelf sends it first in the self packet with
        // hasTransport = 1 (Map.cpp:1690-1734). The ferry is the only ship of the map, so no ships-only packet goes out.
        SelfCreate self = Assert.Single(SelfCreates(session, player));
        Assert.Single(TransportUpdates(session));
        Assert.Equal(1, self.HasTransport);
        Assert.Equal(ferry.Guid.Value, self.FirstBlockGuid);
        Assert.True(self.Movement.HasFlag(MovementFlags.OnTransport));
        Assert.Equal(ferry.Guid.Value, self.Movement.TransportGuid);
        Assert.Equal((3f, 2f, 5f), (self.Movement.TransportX, self.Movement.TransportY, self.Movement.TransportZ));
    }

    [Fact]
    public void Login_WithAShipThatDoesNotSail_GoesToTheBindPoint()
    {
        WorldRuntime world = ManualWorld();
        TransportSystem system = Install(world, Ferry);
        var homebound = new List<Player>();
        system.TeleportToHomebind = p =>
        {
            homebound.Add(p);
            return true;
        };
        Player player = Load(1, new FakeSession(1), new TransportSeat(424242, 1f, 0f, 0f, 0f));

        world.AddPlayer(player);
        Assert.Empty(homebound); // after the map update, not while entering
        world.RunTick(50);

        Assert.Equal([player], homebound);
        Assert.Null(player.Transport);
    }

    [Fact]
    public void Login_WithAnOffsetOffTheShip_GoesToTheBindPoint()
    {
        WorldRuntime world = ManualWorld();
        TransportSystem system = Install(world, Ferry);
        int homebound = 0;
        system.TeleportToHomebind = _ => ++homebound > 0;
        Player player = Load(1, new FakeSession(1), new TransportSeat(Ferry, 251f, 0f, 0f, 0f));

        world.AddPlayer(player);
        world.RunTick(50);

        Assert.Equal(1, homebound);
        Assert.Null(player.Transport);
    }
}

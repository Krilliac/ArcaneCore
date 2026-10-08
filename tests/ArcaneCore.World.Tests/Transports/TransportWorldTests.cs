using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Transports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Transports.TransportWorldContent;

namespace ArcaneCore.World.Tests.Transports;

/// <summary>
/// The transport feature in the world daemon over loopback: the config gate, routes built from game object templates and
/// TaxiPathNode rows, the period overrides, the ship sent ahead of the player's own create block at login, boarding through
/// the real movement handler and the re-send at the first CMSG_MOVE_TIME_SKIPPED.
/// </summary>
public sealed class TransportWorldTests
{
    [Fact]
    public async Task Disabled_ByDefault_NoShipsAndNoSystem()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => Register(services, enabled: false));
        TransportFeature feature = host.WorldServices.GetRequiredService<TransportFeature>();

        Assert.False(feature.Options.Enabled);
        Assert.Null(await host.OnWorldAsync(() => feature.System));
        Assert.Null(await host.OnWorldAsync(() => TransportSystem.Of(host.World)));
    }

    [Fact]
    public async Task Enabled_BuildsTheUsableRoutes_RefusesTheBrokenOne_AndAppliesTheNewestPeriodUpTo5875()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => Register(services, withPeriodOverride: true));
        TransportFeature feature = host.WorldServices.GetRequiredService<TransportFeature>();
        await host.WaitForWorldAsync(() => feature.System is { Ships.Count: 2 }, "two ships sail");

        (uint[] entries, uint ferryPeriod, uint crossingPeriod, uint[] maps) = await host.OnWorldAsync(() =>
        {
            TransportSystem system = feature.System!;
            return (system.Ships.Select(s => s.Entry).Order().ToArray(), system.FindByEntry(Ferry)!.Period,
                system.FindByEntry(Crossing)!.Period, system.Ships.Select(s => s.MapId).ToArray());
        });
        Assert.Equal([Ferry, Crossing], entries);
        Assert.Equal(FerryPeriodOverride, ferryPeriod);
        Assert.NotEqual(FerryPeriodOverride, crossingPeriod);
        Assert.Equal([0u, 0u], maps);
        Assert.Equal((Broken, TransportTemplateError.NoPath), Assert.Single(feature.Refused));
    }

    [Fact]
    public async Task Login_SendsTheShipsOfTheMap_BeforeThePlayersOwnCreateBlock()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => Register(services));
        TransportFeature feature = host.WorldServices.GetRequiredService<TransportFeature>();
        await host.WaitForWorldAsync(() => feature.System is { Ships.Count: 2 }, "two ships sail");

        List<(WorldOpcode Opcode, byte[] Payload)> packets = await LoginAsync(host, "SAILOR", "Sailor");

        int ships = packets.FindIndex(p => p.Opcode == WorldOpcode.SmsgUpdateObject && p.Payload[4] == 1);
        int self = packets.FindIndex(p => p.Opcode == WorldOpcode.SmsgUpdateObject && p.Payload[4] == 0);
        Assert.True(ships >= 0, "the ships were sent");
        Assert.True(ships < self, "the ships come before the player's own create block");
        byte[] payload = packets[ships].Payload;
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(payload));
        var reader = new PacketReader(payload.AsSpan(5));
        Assert.Equal((byte)ObjectUpdateType.CreateObject, reader.ReadByte());
        Assert.Equal(ShipTransport.GuidFor(Ferry).Value, reader.ReadPackedGuid());
    }

    [Fact]
    public async Task MovementOnTheShip_Boards_AndTheFirstTimeSkipResendsTheShip()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => Register(services));
        TransportFeature feature = host.WorldServices.GetRequiredService<TransportFeature>();
        await host.WaitForWorldAsync(() => feature.System is { Ships.Count: 2 }, "two ships sail");
        await using WorldTestClient client = await EnterAsync(host, "SAILOR", "Sailor");
        Player player = await host.PlayerAsync("Sailor");
        ShipTransport ferry = await host.OnWorldAsync(() => feature.System!.FindByEntry(Ferry)!);

        MovementInfo aboard = await host.OnWorldAsync(() =>
        {
            float x = 2f, y = 1f, z = 4f, o = 0f;
            ferry.CalculatePassengerPosition(ref x, ref y, ref z, ref o);
            return new MovementInfo
            {
                Flags = MovementFlags.OnTransport, Time = 1000, X = x, Y = y, Z = z, Orientation = o,
                TransportGuid = ferry.Guid.Value, TransportX = 2f, TransportY = 1f, TransportZ = 4f,
            };
        });
        var heartbeat = new PacketWriter(64);
        aboard.Write(heartbeat);
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, heartbeat.ToArray());
        await host.WaitForWorldAsync(() => ReferenceEquals(player.Transport, ferry), "the player boards the ferry");
        Assert.Equal((2f, 1f, 4f), await host.PlayerStateAsync("Sailor", p => (p.Movement.TransportX, p.Movement.TransportY, p.Movement.TransportZ)));
        await client.CollectAsync();

        var skipped = new PacketWriter(12);
        skipped.WriteUInt64(player.Guid.Value);
        skipped.WriteUInt32(250);
        await client.SendAsync(WorldOpcode.CmsgMoveTimeSkipped, skipped.ToArray());
        List<(WorldOpcode Opcode, byte[] Payload)> resent = (await client.CollectAsync(TimeSpan.FromMilliseconds(300)))
            .Where(p => p.Opcode == WorldOpcode.SmsgUpdateObject && p.Payload[4] == 1).ToList();

        Assert.Equal(2, resent.Count);
        Assert.Equal((byte)ObjectUpdateType.OutOfRangeObjects, resent[0].Payload[5]);
        Assert.Equal((byte)ObjectUpdateType.CreateObject, resent[1].Payload[5]);
    }

    [Fact]
    public async Task LoggingOutAboard_SavesTheSeat_AndTheNextLoginIsBackOnTheShip()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => Register(services));
        TransportFeature feature = host.WorldServices.GetRequiredService<TransportFeature>();
        await host.WaitForWorldAsync(() => feature.System is { Ships.Count: 2 }, "two ships sail");
        Login first = await LoginCoreAsync(host, "SAILOR", "Sailor");
        ShipTransport ferry = await host.OnWorldAsync(() => feature.System!.FindByEntry(Ferry)!);
        Player player = await host.PlayerAsync("Sailor");
        await BoardAsync(host, first.Client, player, ferry, 2f, 1f, 4f);

        await first.Client.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Sailor") is null, "the session to leave the world");
        await WorldTestHost.WaitForAsync(() => host.Characters.GetByIdAsync(first.CharacterId).Result is { TransportGuid: Ferry },
            "the logout save of the seat to reach the store");
        CharacterRecord saved = (await host.Characters.GetByIdAsync(first.CharacterId))!;
        Assert.Equal((2f, 1f, 4f), (saved.TransportX, saved.TransportY, saved.TransportZ));
        Assert.Empty(await host.OnWorldAsync(() => ferry.Passengers.ToArray()));

        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("SAILOR", first.Key);
        await SendLoginAsync(host, again, first.CharacterId, "Sailor");

        Player back = await host.PlayerAsync("Sailor");
        await host.WaitForWorldAsync(() => ReferenceEquals(back.Transport, ferry), "the player is back on the ferry");
        Assert.Equal((2f, 1f, 4f), await host.PlayerStateAsync("Sailor", p => (p.Movement.TransportX, p.Movement.TransportY, p.Movement.TransportZ)));
    }

    private sealed record Login(WorldTestClient Client, byte[] Key, int CharacterId, List<(WorldOpcode Opcode, byte[] Payload)> Packets);

    private static async Task<WorldTestClient> EnterAsync(WorldTestHost host, string account, string character)
        => (await LoginCoreAsync(host, account, character)).Client;

    private static async Task<List<(WorldOpcode Opcode, byte[] Payload)>> LoginAsync(WorldTestHost host, string account, string character)
    {
        Login login = await LoginCoreAsync(host, account, character);
        await login.Client.DisposeAsync();
        return login.Packets;
    }

    // The ordinary LoginAsync helper expects the player's own create block as the first update; with ships on the map the first
    // update is theirs, so the login is driven by hand and every packet collected.
    private static async Task<Login> LoginCoreAsync(WorldTestHost host, string account, string character)
    {
        byte[] key = await host.AddAccountAsync(account, AccountSecurity.Player);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(character);
        Account stored = (await host.Accounts.FindByUsernameAsync(account))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(stored.Id)).Single(c => c.Name == character);
        List<(WorldOpcode, byte[])> packets = await SendLoginAsync(host, client, record.Id, character);
        return new Login(client, key, record.Id, packets);
    }

    private static async Task<List<(WorldOpcode, byte[])>> SendLoginAsync(WorldTestHost host, WorldTestClient client, int characterId, string character)
    {
        var login = new PacketWriter(8);
        login.WriteUInt64((ulong)characterId);
        await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(character) is { IsInWorld: true }, character + " is in the world");
        return await client.CollectAsync(TimeSpan.FromMilliseconds(300));
    }

    private static async Task BoardAsync(WorldTestHost host, WorldTestClient client, Player player, ShipTransport ship, float ox, float oy, float oz)
    {
        MovementInfo aboard = await host.OnWorldAsync(() =>
        {
            float x = ox, y = oy, z = oz, o = 0f;
            ship.CalculatePassengerPosition(ref x, ref y, ref z, ref o);
            return new MovementInfo
            {
                Flags = MovementFlags.OnTransport, Time = 1000, X = x, Y = y, Z = z, Orientation = o,
                TransportGuid = ship.Guid.Value, TransportX = ox, TransportY = oy, TransportZ = oz,
            };
        });
        var heartbeat = new PacketWriter(64);
        aboard.Write(heartbeat);
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, heartbeat.ToArray());
        await host.WaitForWorldAsync(() => ReferenceEquals(player.Transport, ship), "the player boards the ship");
    }
}

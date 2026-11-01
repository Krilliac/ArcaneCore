using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Transports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Transports.TransportWorldContent;

namespace ArcaneCore.World.Tests.Transports;

/// <summary>
/// Two relog desyncs. A ship passenger's SMSG_LOGIN_VERIFY_WORLD carries the transport-relative position, as vmangos
/// <c>HandlePlayerLogin</c> sends <c>m_movementInfo.GetTransportPos()</c> for a player <c>LoadFromDB</c> already boarded
/// (CharacterHandler.cpp:515-517). A logout while a teleport is pending saves the destination (vmangos <c>Player::SaveToDB</c>,
/// Player.cpp:16408-16424), so the next login lands where the teleport was going.
/// </summary>
public sealed class RelogTransportSeatTests
{
    [Fact]
    public async Task ShipPassengerWhoRelogs_GetsLoginVerifyWorldAtTheTransportOffset()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => Register(services));
        TransportFeature feature = host.WorldServices.GetRequiredService<TransportFeature>();
        await host.WaitForWorldAsync(() => feature.System is { Ships.Count: 2 }, "two ships sail");
        (WorldTestClient first, byte[] key, int characterId) = await LoginFirstAsync(host, "RELOGSEAT", "Seatrelog");
        ShipTransport ferry = await host.OnWorldAsync(() => feature.System!.FindByEntry(Ferry)!);
        Player player = await host.PlayerAsync("Seatrelog");
        await BoardAsync(host, first, player, ferry, 2f, 1f, 4f);

        await first.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Seatrelog") is null, "the session to leave the world");
        await WorldTestHost.WaitForAsync(() => host.Characters.GetByIdAsync(characterId).Result is { TransportGuid: Ferry },
            "the logout save of the seat to reach the store");

        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("RELOGSEAT", key);
        List<(WorldOpcode Opcode, byte[] Payload)> packets = await SendLoginAsync(host, again, characterId, "Seatrelog");

        byte[] verify = packets.First(p => p.Opcode == WorldOpcode.SmsgLoginVerifyWorld).Payload;
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(verify.AsSpan(0, 4)));
        Assert.Equal(2f, BinaryPrimitives.ReadSingleLittleEndian(verify.AsSpan(4, 4)));
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(verify.AsSpan(8, 4)));
        Assert.Equal(4f, BinaryPrimitives.ReadSingleLittleEndian(verify.AsSpan(12, 4)));
    }

    [Fact]
    public async Task LogoutMidTeleport_SavesTheDestination()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => Register(services));
        TeleportFeature teleports = host.WorldServices.GetRequiredService<TeleportFeature>();
        (WorldTestClient client, _, int characterId) = await LoginFirstAsync(host, "RELOGTELE", "Telerelog");
        Player player = await host.PlayerAsync("Telerelog");

        float destX = StartX + 40f;
        float destY = StartY + 25f;
        bool started = await host.OnWorldAsync(() => teleports.Teleports.TeleportTo(player, 0, destX, destY, StartZ, 1.5f));
        Assert.True(started, "the near teleport starts");
        Assert.True(await host.OnWorldAsync(() => teleports.Teleports.IsBeingTeleported(player)), "the teleport is pending (no client ack)");

        await client.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Telerelog") is null, "the session to leave the world");
        await WorldTestHost.WaitForAsync(() => host.Characters.GetByIdAsync(characterId).Result is { } r && r.X == destX,
            "the logout save of the destination to reach the store");

        CharacterRecord saved = (await host.Characters.GetByIdAsync(characterId))!;
        Assert.Equal((0u, destX, destY, StartZ), (saved.MapId, saved.X, saved.Y, saved.Z));
    }

    private static async Task<(WorldTestClient Client, byte[] Key, int CharacterId)> LoginFirstAsync(WorldTestHost host, string account, string character)
    {
        byte[] key = await host.AddAccountAsync(account, AccountSecurity.Player);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(character);
        Account stored = (await host.Accounts.FindByUsernameAsync(account))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(stored.Id)).Single(c => c.Name == character);
        await SendLoginAsync(host, client, record.Id, character);
        return (client, key, record.Id);
    }

    private static async Task<List<(WorldOpcode Opcode, byte[] Payload)>> SendLoginAsync(WorldTestHost host, WorldTestClient client, int characterId, string character)
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

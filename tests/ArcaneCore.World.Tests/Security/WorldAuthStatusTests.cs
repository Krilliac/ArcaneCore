using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Security;

/// <summary>
/// The world daemon must not admit an account the realm would have refused. vmangos joins
/// account_banned in the CMSG_AUTH_SESSION query and answers AUTH_BANNED (permanent) or
/// AUTH_SUSPENDED (temporary) before the session exists (WorldSocket.cpp:283-345).
/// </summary>
public sealed class WorldAuthStatusTests
{
    [Fact]
    public void AuthResponseCodes_MatchWowMessagesWorldResult()
    {
        // D:\refs\wow_messages world/enums/world_result.wowm:35,57,59,63,65
        Assert.Equal(0x10, (byte)AuthResponseCode.Unavailable);
        Assert.Equal(0x1C, (byte)AuthResponseCode.Banned);
        Assert.Equal(0x1D, (byte)AuthResponseCode.AlreadyOnline);
        Assert.Equal(0x1F, (byte)AuthResponseCode.DbBusy);
        Assert.Equal(0x20, (byte)AuthResponseCode.Suspended);
    }

    [Theory]
    [InlineData(AccountStatus.Banned, AuthResponseCode.Banned)]
    [InlineData(AccountStatus.Suspended, AuthResponseCode.Suspended)]
    public async Task NonActiveAccountWithValidSessionKey_IsRefusedAndNeverRegistered(
        AccountStatus status, AuthResponseCode expected)
    {
        await using var host = WorldTestHost.Start();
        byte[] key = RandomNumberGenerator.GetBytes(WowSrp6.SessionKeyLength);
        await host.Accounts.CreateAsync(new Account
        {
            Username = "BADACCT", Salt = new byte[32], Verifier = new byte[32], SessionKey = key, Status = status,
        });

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
        await using NetworkStream client = tcp.GetStream();

        byte[] seedPacket = await ReadAsync(client);
        uint serverSeed = BinaryPrimitives.ReadUInt32LittleEndian(seedPacket.AsSpan(2));
        uint clientSeed = 0xCAFE;
        byte[] digest = Sha1.Hash(Encoding.ASCII.GetBytes("BADACCT"), new byte[4], Le(clientSeed), Le(serverSeed), key);

        var w = new PacketWriter(64);
        w.WriteUInt32(ClientBuild.Vanilla1121);
        w.WriteUInt32(0);
        w.WriteCString("BADACCT");
        w.WriteUInt32(clientSeed);
        w.WriteBytes(digest);
        w.WriteUInt32(0); // empty addon block
        byte[] payload = w.AsMemory().ToArray();
        byte[] frame = new byte[WorldHeaderCrypt.IncomingHeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)(payload.Length + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2, 4), (uint)WorldOpcode.CmsgAuthSession);
        payload.CopyTo(frame, WorldHeaderCrypt.IncomingHeaderLength);
        await client.WriteAsync(frame);

        byte[] reply = await ReadAsync(client);
        Assert.Equal(WorldOpcode.SmsgAuthResponse, (WorldOpcode)BinaryPrimitives.ReadUInt16LittleEndian(reply));
        Assert.Equal((byte)expected, reply[2]);
    }

    private static async Task<byte[]> ReadAsync(NetworkStream s)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] header = new byte[4];
        await s.ReadExactlyAsync(header, cts.Token);
        int len = BinaryPrimitives.ReadUInt16BigEndian(header) - 2;
        byte[] body = new byte[2 + len];
        header.AsSpan(2, 2).CopyTo(body);
        await s.ReadExactlyAsync(body.AsMemory(2), cts.Token);
        return body;
    }

    private static byte[] Le(uint v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }
}

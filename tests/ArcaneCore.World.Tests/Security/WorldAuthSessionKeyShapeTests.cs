using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Security;

/// <summary>
/// A stored session key that is not the 40-byte SRP6 interleave is a damaged or foreign account row
/// (docs/ops/invariants.md). The world daemon refuses the login (AUTH_FAILED) and counts the invariant
/// failure instead of keying the header cipher with it, even when the client's digest matches that key.
/// </summary>
public sealed class WorldAuthSessionKeyShapeTests
{
    [Fact]
    public async Task StoredKeyOfTheWrongLength_IsRefusedWithAuthFailed_AndCounted()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = RandomNumberGenerator.GetBytes(20); // a SHA-1 digest stored where K belongs
        await host.Accounts.CreateAsync(new Account
        {
            Username = "SHORTKEY", Salt = new byte[32], Verifier = new byte[32], SessionKey = key, Status = AccountStatus.Active,
        });

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
        await using NetworkStream client = tcp.GetStream();

        byte[] seedPacket = await ReadAsync(client);
        uint serverSeed = BinaryPrimitives.ReadUInt32LittleEndian(seedPacket.AsSpan(2));
        uint clientSeed = 0xBEEF;
        byte[] digest = Sha1.Hash(Encoding.ASCII.GetBytes("SHORTKEY"), new byte[4], Le(clientSeed), Le(serverSeed), key);

        var w = new PacketWriter(64);
        w.WriteUInt32(ClientBuild.Vanilla1121);
        w.WriteUInt32(0);
        w.WriteCString("SHORTKEY");
        w.WriteUInt32(clientSeed);
        w.WriteBytes(digest);
        w.WriteUInt32(0);
        byte[] payload = w.AsMemory().ToArray();
        byte[] frame = new byte[WorldHeaderCrypt.IncomingHeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)(payload.Length + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2, 4), (uint)WorldOpcode.CmsgAuthSession);
        payload.CopyTo(frame, WorldHeaderCrypt.IncomingHeaderLength);

        long before = Invariant.FailureCount;
        await client.WriteAsync(frame);

        byte[] reply = await ReadAsync(client);
        Assert.Equal(WorldOpcode.SmsgAuthResponse, (WorldOpcode)BinaryPrimitives.ReadUInt16LittleEndian(reply));
        Assert.Equal((byte)AuthResponseCode.Failed, reply[2]);
        Assert.True(Invariant.FailureCount > before, "the wrong-length key must be counted as an invariant failure");
        Assert.Contains(Invariant.Failures(), f => f.File == "WorldSession.cs" && f.LastMessage!.Contains("session key of 20 bytes", StringComparison.Ordinal));
    }

    private static async Task<byte[]> ReadAsync(NetworkStream s)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] header = new byte[4];
        await s.ReadExactlyAsync(header, cts.Token);
        int len = BinaryPrimitives.ReadUInt16BigEndian(header) - 2;
        byte[] body = new byte[2 + len];
        header.AsSpan(2, 2).CopyTo(body);
        if (len > 0)
        {
            await s.ReadExactlyAsync(body.AsMemory(2), cts.Token);
        }

        return body;
    }

    private static byte[] Le(uint v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }
}

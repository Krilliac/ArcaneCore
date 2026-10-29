using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.World.Tests.Net;

/// <summary>
/// A client header the server rejects is logged with enough to tell a malformed client opcode from a cipher desync: a classification,
/// the ciphertext and the decrypted bytes. Drives a real authenticated session over loopback with the paired client-side
/// <see cref="WorldHeaderCrypt"/>, so the ciphertext the test sends is the ciphertext the server must print.
/// </summary>
public sealed class BadHeaderDiagnosticsTests
{
    private const string Username = "BADHEADER";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Plain header 00 04 DC 00 E4 3C: size 4 (in range), raw opcode 0x3CE400DC. The low 16 bits are 0x00DC (MSG_MOVE_WORLDPORT_ACK), the
    /// high 16 bits are 0x3CE4: a client that wrote a known opcode with junk above it. Must be classified as a malformed opcode, not a desync.
    /// </summary>
    [Fact]
    public async Task KnownLowOpcodeWithJunkHighBits_IsClassifiedClientMalformedOpcode()
    {
        byte[] plain = [0x00, 0x04, 0xDC, 0x00, 0xE4, 0x3C];
        (string warn, byte[] cipher, bool closed) = await SendBadHeaderAsync(plain);

        Assert.Contains("client-malformed-opcode", warn, StringComparison.Ordinal);
        Assert.Contains("MSG_MOVE_WORLDPORT_ACK", warn, StringComparison.Ordinal);
        Assert.Contains("plain 0004DC00E43C", warn, StringComparison.Ordinal);
        Assert.Contains("cipher " + Convert.ToHexString(cipher), warn, StringComparison.Ordinal);
        Assert.True(closed, "the session still disconnects");
    }

    /// <summary>Plain size 0xFFFF is outside [4, 0x2800]: what a plaintext or desynchronised stream decrypts to. Classified as cipher-desync-or-plaintext.</summary>
    [Fact]
    public async Task OutOfRangeSize_IsClassifiedCipherDesyncOrPlaintext()
    {
        byte[] plain = [0xFF, 0xFF, 0x01, 0x00, 0x00, 0x00];
        (string warn, byte[] cipher, bool closed) = await SendBadHeaderAsync(plain);

        Assert.Contains("cipher-desync-or-plaintext", warn, StringComparison.Ordinal);
        Assert.DoesNotContain("client-malformed-opcode", warn, StringComparison.Ordinal);
        Assert.Contains("plain FFFF01000000", warn, StringComparison.Ordinal);
        Assert.Contains("cipher " + Convert.ToHexString(cipher), warn, StringComparison.Ordinal);
        Assert.True(closed, "the session still disconnects");
    }

    /// <summary>
    /// Regression (passes before and after the change): a header delivered 1, 2, then 3 bytes at a time decrypts to the same plaintext as
    /// a one-shot delivery, so the server answers CMSG_CHAR_ENUM. Guards the diagnostic change against disturbing the cipher stream.
    /// </summary>
    [Fact]
    public async Task HeaderDeliveredInChunks_DecryptsLikeOneShot()
    {
        var log = new CapturingLogger();
        await using var host = WorldTestHost.Start(sessionLogger: log);
        byte[] key = await host.AddAccountAsync(Username);

        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
        await using NetworkStream client = tcp.GetStream();
        WorldHeaderCrypt crypt = await AuthenticateAsync(client, key);

        byte[] header = new byte[WorldHeaderCrypt.IncomingHeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2, 4), (uint)WorldOpcode.CmsgCharEnum);
        crypt.EncryptHeader(header);

        foreach ((int from, int count) in new[] { (0, 1), (1, 2), (3, 3) })
        {
            await client.WriteAsync(header.AsMemory(from, count));
            await client.FlushAsync();
            await Task.Delay(50);
        }

        using var cts = new CancellationTokenSource(Bound);
        (WorldOpcode op, byte[] payload) = await ReadServerPacketAsync(client, crypt, cts.Token);
        Assert.Equal(WorldOpcode.SmsgCharEnum, op);
        Assert.Equal(0, payload[0]);
        lock (log.Messages)
        {
            Assert.DoesNotContain(log.Messages, m => m.Contains("bad packet header", StringComparison.Ordinal));
        }
    }

    // --- harness -----------------------------------------------------------------

    /// <summary>Authenticates, encrypts <paramref name="plain"/> with the paired client crypt, sends it and waits for the WARN and the close.</summary>
    private static async Task<(string Warn, byte[] Cipher, bool Closed)> SendBadHeaderAsync(byte[] plain)
    {
        var log = new CapturingLogger();
        await using var host = WorldTestHost.Start(sessionLogger: log);
        byte[] key = await host.AddAccountAsync(Username);

        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
        await using NetworkStream client = tcp.GetStream();
        WorldHeaderCrypt crypt = await AuthenticateAsync(client, key);

        byte[] cipher = (byte[])plain.Clone();
        crypt.EncryptHeader(cipher);
        await client.WriteAsync(cipher);
        await client.FlushAsync();

        await WorldTestHost.WaitForAsync(() => FindWarn(log) is not null, "the bad-header WARN");
        string warn = FindWarn(log)!;

        bool closed = false;
        using var cts = new CancellationTokenSource(Bound);
        byte[] one = new byte[1];
        try
        {
            while (await client.ReadAsync(one, cts.Token) > 0)
            {
            }

            closed = true;
        }
        catch (IOException)
        {
            closed = true;
        }
        catch (OperationCanceledException)
        {
        }

        return (warn, cipher, closed);
    }

    private static string? FindWarn(CapturingLogger log)
    {
        lock (log.Messages)
        {
            return log.Messages.FirstOrDefault(m => m.Contains("bad packet header", StringComparison.Ordinal));
        }
    }

    /// <summary>The handshake of <see cref="WorldHandshakeTests"/>: challenge, digest, encryption engaged, AUTH_RESPONSE and ADDON_INFO drained.</summary>
    private static async Task<WorldHeaderCrypt> AuthenticateAsync(NetworkStream client, byte[] sessionKey)
    {
        using var cts = new CancellationTokenSource(Bound);
        var crypt = new WorldHeaderCrypt();
        (WorldOpcode op, byte[] payload) = await ReadServerPacketAsync(client, crypt, cts.Token);
        Assert.Equal(WorldOpcode.SmsgAuthChallenge, op);
        uint serverSeed = BinaryPrimitives.ReadUInt32LittleEndian(payload);

        uint clientSeed = 0xDEADBEEF;
        byte[] digest = Sha1.Hash(Encoding.ASCII.GetBytes(Username), new byte[4], Le(clientSeed), Le(serverSeed), sessionKey);
        var writer = new PacketWriter(128);
        writer.WriteUInt32(ClientBuild.Vanilla1121);
        writer.WriteUInt32(0);
        writer.WriteCString(Username);
        writer.WriteUInt32(clientSeed);
        writer.WriteBytes(digest);
        writer.WriteBytes(BuildAddonBlock());
        await SendClientPacketAsync(client, crypt, WorldOpcode.CmsgAuthSession, writer.AsMemory().ToArray());
        crypt.Initialize(sessionKey);

        (op, payload) = await ReadServerPacketAsync(client, crypt, cts.Token);
        Assert.Equal(WorldOpcode.SmsgAuthResponse, op);
        Assert.Equal((byte)AuthResponseCode.Ok, payload[0]);
        (op, _) = await ReadServerPacketAsync(client, crypt, cts.Token);
        Assert.Equal(WorldOpcode.SmsgAddonInfo, op);
        return crypt;
    }

    private static byte[] BuildAddonBlock()
    {
        var inner = new PacketWriter(64);
        inner.WriteCString("Blizzard_AuthChallenge");
        inner.WriteByte(1);
        inner.WriteUInt32(0x4C1C776D);
        inner.WriteUInt32(0);
        byte[] uncompressed = inner.AsMemory().ToArray();

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(uncompressed);
        }

        byte[] body = compressed.ToArray();
        byte[] block = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(block, (uint)uncompressed.Length);
        body.CopyTo(block, 4);
        return block;
    }

    private static async Task<(WorldOpcode, byte[])> ReadServerPacketAsync(NetworkStream stream, WorldHeaderCrypt crypt, CancellationToken token)
    {
        byte[] header = new byte[WorldHeaderCrypt.OutgoingHeaderLength];
        await stream.ReadExactlyAsync(header, token);
        crypt.DecryptHeader(header);

        ushort size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
        var opcode = (WorldOpcode)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
        int payloadLength = size - 2;
        byte[] payload = payloadLength > 0 ? new byte[payloadLength] : [];
        if (payloadLength > 0)
        {
            await stream.ReadExactlyAsync(payload, token);
        }

        return (opcode, payload);
    }

    private static async Task SendClientPacketAsync(NetworkStream stream, WorldHeaderCrypt crypt, WorldOpcode opcode, byte[] payload)
    {
        byte[] frame = new byte[WorldHeaderCrypt.IncomingHeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)(payload.Length + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2, 4), (uint)opcode);
        crypt.EncryptHeader(frame.AsSpan(0, WorldHeaderCrypt.IncomingHeaderLength));
        payload.CopyTo(frame, WorldHeaderCrypt.IncomingHeaderLength);
        await stream.WriteAsync(frame);
    }

    private static byte[] Le(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }
}

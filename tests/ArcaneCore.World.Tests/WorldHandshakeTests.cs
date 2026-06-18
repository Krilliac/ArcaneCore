using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// Drives a real <see cref="WorldSession"/> over a loopback socket with a simulated client,
/// exercising the full M2 handshake: SMSG_AUTH_CHALLENGE → CMSG_AUTH_SESSION (digest
/// validated), header encryption engaging, and reaching the empty character list.
/// </summary>
public sealed class WorldHandshakeTests
{
    private const string Username = "WORLDTESTER";

    [Fact]
    public async Task ValidSession_CompletesHandshakeAndReachesCharacterList()
    {
        byte[] sessionKey = RandomNumberGenerator.GetBytes(WowSrp6.SessionKeyLength);
        var accounts = new InMemoryAccountStore();
        await accounts.CreateAsync(new Account
        {
            Username = Username,
            Salt = new byte[32],
            Verifier = new byte[32],
            SessionKey = sessionKey,
        });

        await using NetworkStream client = await StartSessionAsync(accounts);
        var crypt = new WorldHeaderCrypt();

        // 1. SMSG_AUTH_CHALLENGE (plaintext header) carries the server seed.
        (WorldOpcode op, byte[] payload) = await ReadServerPacketAsync(client, crypt);
        Assert.Equal(WorldOpcode.SmsgAuthChallenge, op);
        uint serverSeed = BinaryPrimitives.ReadUInt32LittleEndian(payload);

        // 2. CMSG_AUTH_SESSION with a correct digest; encryption engages immediately after.
        uint clientSeed = 0xDEADBEEF;
        byte[] digest = ComputeDigest(Username, clientSeed, serverSeed, sessionKey);
        await SendClientPacketAsync(client, crypt, WorldOpcode.CmsgAuthSession,
            BuildAuthSession(Username, clientSeed, digest));
        crypt.Initialize(sessionKey);

        // 3. SMSG_AUTH_RESPONSE (encrypted header) = AUTH_OK.
        (op, payload) = await ReadServerPacketAsync(client, crypt);
        Assert.Equal(WorldOpcode.SmsgAuthResponse, op);
        Assert.Equal((byte)AuthResponseCode.Ok, payload[0]);

        // 4. SMSG_ADDON_INFO follows.
        (op, _) = await ReadServerPacketAsync(client, crypt);
        Assert.Equal(WorldOpcode.SmsgAddonInfo, op);

        // 5. CMSG_CHAR_ENUM → SMSG_CHAR_ENUM with an empty list.
        await SendClientPacketAsync(client, crypt, WorldOpcode.CmsgCharEnum, []);
        (op, payload) = await ReadServerPacketAsync(client, crypt);
        Assert.Equal(WorldOpcode.SmsgCharEnum, op);
        Assert.Equal(0, payload[0]); // zero characters
    }

    [Fact]
    public async Task WrongDigest_IsRejected()
    {
        byte[] sessionKey = RandomNumberGenerator.GetBytes(WowSrp6.SessionKeyLength);
        var accounts = new InMemoryAccountStore();
        await accounts.CreateAsync(new Account
        {
            Username = Username,
            Salt = new byte[32],
            Verifier = new byte[32],
            SessionKey = sessionKey,
        });

        await using NetworkStream client = await StartSessionAsync(accounts);
        var crypt = new WorldHeaderCrypt();

        (_, byte[] payload) = await ReadServerPacketAsync(client, crypt);
        uint serverSeed = BinaryPrimitives.ReadUInt32LittleEndian(payload);

        byte[] wrongDigest = new byte[20]; // all zeros — will not match
        await SendClientPacketAsync(client, crypt, WorldOpcode.CmsgAuthSession,
            BuildAuthSession(Username, 0x1234, wrongDigest));

        // Failure response header is still plaintext (server did not engage encryption).
        (WorldOpcode op, byte[] resp) = await ReadServerPacketAsync(client, crypt);
        Assert.Equal(WorldOpcode.SmsgAuthResponse, op);
        Assert.Equal((byte)AuthResponseCode.Failed, resp[0]);
    }

    // --- session host ------------------------------------------------------------

    private static async Task<NetworkStream> StartSessionAsync(IAccountStore accounts)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync();
            listener.Stop();
            await using NetworkStream stream = server.GetStream();
            var session = new WorldSession(stream, accounts, NullLogger.Instance, "test");
            await session.RunAsync(CancellationToken.None);
        });

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client.GetStream();
    }

    // --- digest + packet construction --------------------------------------------

    private static byte[] ComputeDigest(string account, uint clientSeed, uint serverSeed, byte[] sessionKey)
        => Sha1.Hash(Encoding.ASCII.GetBytes(account), new byte[4], Le(clientSeed), Le(serverSeed), sessionKey);

    private static byte[] BuildAuthSession(string account, uint clientSeed, byte[] digest)
    {
        var writer = new PacketWriter(128);
        writer.WriteUInt32(ClientBuild.Vanilla1121);
        writer.WriteUInt32(0); // server id
        writer.WriteCString(account);
        writer.WriteUInt32(clientSeed);
        writer.WriteBytes(digest);
        writer.WriteBytes(BuildAddonBlock());
        return writer.AsMemory().ToArray();
    }

    /// <summary>One Blizzard addon record, zlib-compressed, prefixed with its size.</summary>
    private static byte[] BuildAddonBlock()
    {
        var inner = new PacketWriter(64);
        inner.WriteCString("Blizzard_AuthChallenge");
        inner.WriteByte(1);            // enabled
        inner.WriteUInt32(0x4C1C776D); // correct modulus crc
        inner.WriteUInt32(0);          // url crc
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

    // --- framing helpers ---------------------------------------------------------

    private static async Task<(WorldOpcode, byte[])> ReadServerPacketAsync(NetworkStream stream, WorldHeaderCrypt crypt)
    {
        byte[] header = new byte[WorldHeaderCrypt.OutgoingHeaderLength]; // SMSG header = 4 bytes
        await stream.ReadExactlyAsync(header);
        crypt.DecryptHeader(header);

        ushort size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
        var opcode = (WorldOpcode)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
        int payloadLength = size - 2; // SMSG size counts the 2 opcode bytes

        byte[] payload = payloadLength > 0 ? new byte[payloadLength] : [];
        if (payloadLength > 0)
        {
            await stream.ReadExactlyAsync(payload);
        }

        return (opcode, payload);
    }

    private static async Task SendClientPacketAsync(
        NetworkStream stream, WorldHeaderCrypt crypt, WorldOpcode opcode, byte[] payload)
    {
        byte[] frame = new byte[WorldHeaderCrypt.IncomingHeaderLength + payload.Length]; // CMSG header = 6 bytes
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

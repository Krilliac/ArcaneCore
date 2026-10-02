using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// A simulated 1.12.1 world client over a loopback socket: performs the auth handshake and
/// then sends/receives header-encrypted world packets.
/// </summary>
internal sealed class WorldTestClient : IAsyncDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly WorldHeaderCrypt _crypt = new();

    public WorldTestClient(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    /// <summary>Bytes received but not yet read.</summary>
    public int Available => _client.Available;

    /// <summary>Run the auth handshake and assert it succeeds; leaves the client at character select.</summary>
    public async Task AuthenticateAsync(string account, byte[] sessionKey)
    {
        (WorldOpcode op, byte[] payload) = await ReadAsync();
        Assert.Equal(WorldOpcode.SmsgAuthChallenge, op);
        uint serverSeed = BinaryPrimitives.ReadUInt32LittleEndian(payload);

        uint clientSeed = 0x1BADD00D;
        byte[] digest = Sha1.Hash(
            Encoding.ASCII.GetBytes(account.ToUpperInvariant()), new byte[4],
            Le(clientSeed), Le(serverSeed), sessionKey);

        var session = new PacketWriter(64);
        session.WriteUInt32(ClientBuild.Vanilla1121);
        session.WriteUInt32(0);
        session.WriteCString(account.ToUpperInvariant());
        session.WriteUInt32(clientSeed);
        session.WriteBytes(digest);
        session.WriteUInt32(0); // empty addon block (decompresses to nothing; server tolerates it)
        await SendAsync(WorldOpcode.CmsgAuthSession, session.ToArray());
        _crypt.Initialize(sessionKey);

        (op, payload) = await ReadAsync();
        Assert.Equal(WorldOpcode.SmsgAuthResponse, op);
        Assert.Equal((byte)AuthResponseCode.Ok, payload[0]);

        (op, _) = await ReadAsync(); // SMSG_ADDON_INFO
        Assert.Equal(WorldOpcode.SmsgAddonInfo, op);
    }

    /// <summary>Create a character (human warrior by default) and assert success.</summary>
    public async Task CreateCharacterAsync(string name, byte race = 1, byte cls = 1, byte gender = 0)
    {
        var create = new PacketWriter(32);
        create.WriteCString(name);
        create.WriteByte(race);
        create.WriteByte(cls);
        create.WriteByte(gender);
        for (int i = 0; i < 6; i++)
        {
            create.WriteByte(0); // skin, face, hair style, hair color, facial hair, outfit
        }

        await SendAsync(WorldOpcode.CmsgCharCreate, create.ToArray());
        (WorldOpcode op, byte[] payload) = await ReadAsync();
        Assert.Equal(WorldOpcode.SmsgCharCreate, op);
        Assert.Equal((byte)CharResult.CharCreateSuccess, payload[0]);
    }

    /// <summary>
    /// Log a character in and consume the login sequence (vmangos order: verify world,
    /// tutorials, initial spells, time speed, self create). Returns the self-create body.
    /// </summary>
    public async Task<byte[]> LoginAsync(ulong guid)
    {
        var login = new PacketWriter(8);
        login.WriteUInt64(guid);
        await SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());

        Assert.Equal(WorldOpcode.SmsgLoginVerifyWorld, (await ReadAsync()).Opcode);
        Assert.Equal(WorldOpcode.SmsgTutorialFlags, (await ReadAsync()).Opcode);
        Assert.Equal(WorldOpcode.SmsgInitialSpells, (await ReadAsync()).Opcode);
        Assert.Equal(WorldOpcode.SmsgLoginSettimespeed, (await ReadAsync()).Opcode);
        return await ReadUpdateAsync();
    }

    public async Task SendAsync(WorldOpcode opcode, byte[] payload)
    {
        byte[] frame = new byte[WorldHeaderCrypt.IncomingHeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)(payload.Length + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2, 4), (uint)opcode);
        _crypt.EncryptHeader(frame.AsSpan(0, WorldHeaderCrypt.IncomingHeaderLength));
        payload.CopyTo(frame, WorldHeaderCrypt.IncomingHeaderLength);
        await _stream.WriteAsync(frame);
    }

    public async Task<(WorldOpcode Opcode, byte[] Payload)> ReadAsync()
    {
        // Bounded so a test expecting a packet the server never sends fails instead of hanging.
        using var timeout = new CancellationTokenSource(ReadTimeout);
        byte[] header = new byte[WorldHeaderCrypt.OutgoingHeaderLength];
        await _stream.ReadExactlyAsync(header, timeout.Token);
        _crypt.DecryptHeader(header);

        ushort size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
        var opcode = (WorldOpcode)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
        int payloadLength = size - 2;

        byte[] payload = payloadLength > 0 ? new byte[payloadLength] : [];
        if (payloadLength > 0)
        {
            await _stream.ReadExactlyAsync(payload, timeout.Token);
        }

        return (opcode, payload);
    }

    /// <summary>Read an update packet, plain or compressed, and return its uncompressed body.</summary>
    public async Task<byte[]> ReadUpdateAsync()
    {
        (WorldOpcode op, byte[] payload) = await ReadAsync();
        return op switch
        {
            WorldOpcode.SmsgUpdateObject => payload,
            WorldOpcode.SmsgCompressedUpdateObject => Inflate(payload),
            _ => throw new Xunit.Sdk.XunitException($"expected an update packet, got {WorldOpcodeNames.GetName(op)}"),
        };
    }

    /// <summary>SMSG_COMPRESSED_UPDATE_OBJECT body: u32 uncompressed size + zlib stream.</summary>
    public static byte[] Inflate(byte[] payload)
    {
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);
        using var input = new MemoryStream(payload, 4, payload.Length - 4);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        byte[] body = new byte[size];
        zlib.ReadExactly(body);
        return body;
    }

    /// <summary>Assert the server sends nothing more within <paramref name="window"/>.</summary>
    public async Task AssertSilentAsync(TimeSpan window)
    {
        await Task.Delay(window);
        Assert.Equal(0, _client.Available);
    }

    /// <summary>True once the server has closed the connection.</summary>
    public async Task<bool> IsClosedByServerAsync()
    {
        using var timeout = new CancellationTokenSource(ReadTimeout);
        byte[] one = new byte[1];
        try
        {
            while (true)
            {
                int read = await _stream.ReadAsync(one, timeout.Token);
                if (read == 0)
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _client.Dispose();
    }

    private static byte[] Le(uint value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }
}

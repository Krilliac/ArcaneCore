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
        => Assert.Equal((byte)CharResult.CharCreateSuccess, await TryCreateCharacterAsync(name, race, cls, gender));

    /// <summary>Send CMSG_CHAR_CREATE and return the SMSG_CHAR_CREATE result code.</summary>
    public async Task<byte> TryCreateCharacterAsync(string name, byte race = 1, byte cls = 1, byte gender = 0)
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
        return payload[0];
    }

    /// <summary>Every packet of the last <see cref="LoginAsync"/>, in arrival order.</summary>
    public List<(WorldOpcode Opcode, byte[] Payload)> LastLoginPackets { get; } = [];

    /// <summary>
    /// Log a character in and consume the login sequence, asserting the vmangos order: verify
    /// world, account data hashes, friend and ignore lists, MOTD lines, rest start, bind point,
    /// tutorials, initial spells, action buttons, reputations, time speed, self create, world
    /// states. Returns the self-create body; every packet is kept in <see cref="LastLoginPackets"/>.
    /// </summary>
    public async Task<byte[]> LoginAsync(ulong guid)
    {
        var login = new PacketWriter(8);
        login.WriteUInt64(guid);
        await SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        LastLoginPackets.Clear();

        await ExpectLoginPacketAsync(WorldOpcode.SmsgLoginVerifyWorld);
        await ExpectLoginPacketAsync(WorldOpcode.SmsgAccountDataMd5);
        await ExpectLoginPacketAsync(WorldOpcode.SmsgFriendList);
        await ExpectLoginPacketAsync(WorldOpcode.SmsgIgnoreList);

        (WorldOpcode op, byte[] payload) = await ReadAsync();
        while (op == WorldOpcode.SmsgMessagechat) // MOTD lines
        {
            LastLoginPackets.Add((op, payload));
            (op, payload) = await ReadAsync();
        }

        Assert.Equal(WorldOpcode.SmsgSetRestStart, op);
        LastLoginPackets.Add((op, payload));
        await ExpectLoginPacketAsync(WorldOpcode.SmsgBindpointupdate);
        await ExpectLoginPacketAsync(WorldOpcode.SmsgTutorialFlags);
        await ExpectLoginPacketAsync(WorldOpcode.SmsgInitialSpells);
        await ExpectLoginPacketAsync(WorldOpcode.SmsgActionButtons);
        await ExpectLoginPacketAsync(WorldOpcode.SmsgInitializeFactions);
        await ExpectLoginPacketAsync(WorldOpcode.SmsgLoginSettimespeed);
        byte[] self = await ReadUpdateAsync();
        LastLoginPackets.Add((WorldOpcode.SmsgUpdateObject, self));
        await ExpectLoginPacketAsync(WorldOpcode.SmsgInitWorldStates);
        return self;
    }

    /// <summary>The payload of the login packet with this opcode (after <see cref="LoginAsync"/>).</summary>
    public byte[] LoginPacket(WorldOpcode opcode) => LastLoginPackets.First(p => p.Opcode == opcode).Payload;

    /// <summary>
    /// Read every packet that arrives until none has arrived for <paramref name="quiet"/>
    /// (default 150 ms). Frames are only read once data is waiting, so no read is ever cut off.
    /// </summary>
    public async Task<List<(WorldOpcode Opcode, byte[] Payload)>> CollectAsync(TimeSpan? quiet = null)
    {
        TimeSpan window = quiet ?? TimeSpan.FromMilliseconds(150);
        var packets = new List<(WorldOpcode, byte[])>();
        while (true)
        {
            DateTime deadline = DateTime.UtcNow + window;
            while (_client.Available == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(5);
            }

            if (_client.Available == 0)
            {
                return packets;
            }

            packets.Add(await ReadAsync());
        }
    }

    /// <summary>
    /// Read (up to the read timeout) until the first <paramref name="first"/> packet, then collect everything that
    /// follows it until <paramref name="quiet"/> passes with nothing new. Every packet read is returned, including
    /// those that arrived before <paramref name="first"/>. Use this, not <see cref="CollectAsync"/>, right after
    /// sending a request: a loaded machine can take longer than the quiet window to answer at all.
    /// </summary>
    public async Task<List<(WorldOpcode Opcode, byte[] Payload)>> CollectFromAsync(WorldOpcode first, TimeSpan? quiet = null)
    {
        var packets = new List<(WorldOpcode Opcode, byte[] Payload)>();
        while (true)
        {
            (WorldOpcode Opcode, byte[] Payload) packet;
            try
            {
                packet = await ReadAsync();
            }
            catch (OperationCanceledException ex)
            {
                throw new TimeoutException($"timed out waiting for {first}; read {packets.Count} other packets: {string.Join(", ", packets.TakeLast(12).Select(p => p.Opcode))}", ex);
            }

            packets.Add(packet);
            if (packet.Opcode == first)
            {
                break;
            }
        }

        packets.AddRange(await CollectAsync(quiet));
        return packets;
    }

    /// <summary>The text of every chat line answering a request: waits for the first, then collects the rest.</summary>
    internal async Task<string[]> CollectChatLinesAsync()
        => [.. (await CollectFromAsync(WorldOpcode.SmsgMessagechat)).Where(p => p.Opcode == WorldOpcode.SmsgMessagechat).Select(p => ChatMessage.Parse(p.Payload).Text)];

    /// <summary>CMSG_MESSAGECHAT: u32 type, u32 language, [target], message.</summary>
    public Task SendChatAsync(ChatType type, Language language, string message, string? target = null)
    {
        var chat = new PacketWriter(16 + message.Length);
        chat.WriteUInt32((uint)type);
        chat.WriteUInt32((uint)language);
        if (type is ChatType.Whisper or ChatType.Channel)
        {
            chat.WriteCString(target ?? string.Empty);
        }

        chat.WriteCString(message);
        return SendAsync(WorldOpcode.CmsgMessagechat, chat.ToArray());
    }

    /// <summary>Read until the next SMSG_MESSAGECHAT and decode it.</summary>
    public async Task<ChatMessage> ReadChatAsync() => ChatMessage.Parse(await ReadUntilAsync(WorldOpcode.SmsgMessagechat));

    /// <summary>Read packets until one with <paramref name="opcode"/> arrives; fails on timeout.</summary>
    public async Task<byte[]> ReadUntilAsync(WorldOpcode opcode)
    {
        List<(WorldOpcode Op, byte[] Payload)> skipped = [];
        while (true)
        {
            (WorldOpcode op, byte[] payload) packet;
            try
            {
                packet = await ReadAsync();
            }
            catch (OperationCanceledException ex)
            {
                throw new TimeoutException($"timed out waiting for {opcode}; skipped {skipped.Count} packets: {string.Join(", ", skipped.TakeLast(12).Select(s => $"{s.Op}[{Convert.ToHexString(s.Payload.AsSpan(0, Math.Min(s.Payload.Length, 16)))}]"))}", ex);
            }

            if (packet.op == opcode)
            {
                return packet.payload;
            }

            skipped.Add((packet.op, packet.payload));
        }
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

    private async Task ExpectLoginPacketAsync(WorldOpcode expected)
    {
        (WorldOpcode op, byte[] payload) = await ReadAsync();
        Assert.Equal(expected, op);
        LastLoginPackets.Add((op, payload));
    }

    private static byte[] Le(uint value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }
}

/// <summary>A decoded player/system SMSG_MESSAGECHAT (vmangos ChatHandler::BuildChatPacket layout).</summary>
internal sealed record ChatMessage(ChatType Type, Language Language, ulong Sender, ulong? Sender2, string Text, ChatTag Tag)
{
    public static ChatMessage Parse(byte[] payload)
    {
        var reader = new PacketReader(payload);
        var type = (ChatType)reader.ReadByte();
        var language = (Language)reader.ReadUInt32();
        ulong sender = reader.ReadUInt64();
        ulong? sender2 = type is ChatType.Say or ChatType.Party or ChatType.Yell ? reader.ReadUInt64() : null;
        uint length = reader.ReadUInt32();
        string text = reader.ReadCString();
        Assert.Equal((uint)Encoding.UTF8.GetByteCount(text) + 1, length);
        var tag = (ChatTag)reader.ReadByte();
        Assert.Equal(0, reader.Remaining);
        return new ChatMessage(type, language, sender, sender2, text, tag);
    }
}

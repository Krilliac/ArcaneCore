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

    /// <summary>
    /// Bound of each read of the connection setup (auth, character create, login). Setup is off-world-thread work (database writes
    /// and loads, the session's async login), so a starved host can exceed <see cref="ReadTimeout"/> without any bug. The reads still
    /// assert opcode, order and result code; only the hang guard is wider. Replies to commands keep <see cref="ReadTimeout"/>.
    /// </summary>
    private static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The per-read bound of the setup steps; settable so a test can prove the guard fires.</summary>
    internal TimeSpan HandshakeTimeout { get; set; } = DefaultHandshakeTimeout;

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
        (WorldOpcode op, byte[] payload) = await SetupAsync(WorldOpcode.SmsgAuthChallenge, "auth", t => ReadAsync(t));
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

        (op, payload) = await SetupAsync(WorldOpcode.SmsgAuthResponse, "auth", t => ReadAsync(t));
        Assert.Equal(WorldOpcode.SmsgAuthResponse, op);
        Assert.Equal((byte)AuthResponseCode.Ok, payload[0]);

        (op, _) = await SetupAsync(WorldOpcode.SmsgAddonInfo, "auth", t => ReadAsync(t)); // SMSG_ADDON_INFO
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
        (WorldOpcode op, byte[] payload) = await SetupAsync(WorldOpcode.SmsgCharCreate, "char create", t => ReadAsync(t));
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

        (WorldOpcode op, byte[] payload) = await SetupAsync(WorldOpcode.SmsgSetRestStart, "login MOTD/rest start", t => ReadAsync(t));
        while (op == WorldOpcode.SmsgMessagechat) // MOTD lines
        {
            LastLoginPackets.Add((op, payload));
            (op, payload) = await SetupAsync(WorldOpcode.SmsgSetRestStart, "login MOTD/rest start", t => ReadAsync(t));
        }

        Assert.Equal(WorldOpcode.SmsgSetRestStart, op);
        LastLoginPackets.Add((op, payload));
        await ExpectLoginPacketAsync(WorldOpcode.SmsgBindpointupdate);
        await ExpectLoginPacketAsync(WorldOpcode.SmsgTutorialFlags);
        await ExpectLoginPacketAsync(WorldOpcode.SmsgInitialSpells);
        await ExpectLoginPacketAsync(WorldOpcode.SmsgActionButtons);
        await ExpectLoginPacketAsync(WorldOpcode.SmsgInitializeFactions);
        await ExpectLoginPacketAsync(WorldOpcode.SmsgLoginSettimespeed);
        byte[] self = await SetupAsync(WorldOpcode.SmsgUpdateObject, "login self create", t => ReadUpdateAsync(t));
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

    /// <summary>
    /// The text of every chat line answering a request: waits for the first <paramref name="atLeast"/> lines (read timeout each), then
    /// collects the rest. A command's lines are sent one by one; on a loaded machine the handler or the session writer can be descheduled
    /// between two of them for longer than the quiet window, so a test that knows how many lines it expects waits for that many.
    /// </summary>
    internal async Task<string[]> CollectChatLinesAsync(int atLeast = 1)
    {
        var lines = new List<string>();
        while (lines.Count < atLeast)
        {
            byte[] payload = await ReadUntilAsync(WorldOpcode.SmsgMessagechat);
            lines.Add(ChatMessage.Parse(payload).Text);
        }

        lines.AddRange((await CollectRepliesAsync()).Where(p => p.Opcode == WorldOpcode.SmsgMessagechat).Select(p => ChatMessage.Parse(p.Payload).Text));
        return [.. lines];
    }

    /// <summary>
    /// Everything the server sends for the packets this client has already sent, however late it is scheduled: sends a barrier,
    /// CMSG_PLAYED_TIME (empty, no side effects, not audited, not mute-gated, answered by exactly one SMSG_PLAYED_TIME), and reads
    /// until its answer. The barrier takes the same path as a chat command: <c>WorldSession.DispatchAsync</c> puts every
    /// <c>OnWorld</c> packet on the session's one FIFO world queue and the world thread is its only consumer, and a command writes all
    /// of its reply lines synchronously inside its handler. So the barrier is handled after the command, and all of the command's
    /// lines come before the answer, with nothing to guess about timing. Unlike CMSG_PING, which the read loop answers at once,
    /// the answer cannot overtake a command still waiting in the queue. The barrier answer itself is not returned.
    /// Only a reply the command posts to a later world tick (<c>CommandContext.CanReplyLater</c>) can still arrive after it.
    /// The caller must be in the world and must not have another CMSG_PLAYED_TIME in flight. If the server closes the connection the
    /// packets read so far are returned, since nothing more will come.
    /// </summary>
    public async Task<List<(WorldOpcode Opcode, byte[] Payload)>> CollectThroughBarrierAsync()
    {
        var packets = new List<(WorldOpcode Opcode, byte[] Payload)>();
        try
        {
            await SendAsync(WorldOpcode.CmsgPlayedTime, []);
            while (true)
            {
                (WorldOpcode Opcode, byte[] Payload) packet = await ReadAsync();
                if (packet.Opcode == WorldOpcode.SmsgPlayedTime)
                {
                    return packets;
                }

                packets.Add(packet);
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException($"timed out waiting for the SMSG_PLAYED_TIME barrier; read {packets.Count} packets: {string.Join(", ", packets.TakeLast(12).Select(p => p.Opcode))}", ex);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
        {
            return packets; // the server closed the connection (a kick, a ban): the reply is whatever arrived before it
        }
    }

    /// <summary>
    /// <see cref="CollectThroughBarrierAsync"/>, then whatever else follows until <paramref name="quiet"/> passes with nothing new
    /// (<see cref="CollectAsync"/>). The barrier makes the reply complete; the quiet tail only keeps the old behaviour of also
    /// taking replies a command posts to a later tick. Use it, not <see cref="CollectAsync"/>, to gather a command's reply.
    /// </summary>
    public async Task<List<(WorldOpcode Opcode, byte[] Payload)>> CollectRepliesAsync(TimeSpan? quiet = null)
    {
        List<(WorldOpcode Opcode, byte[] Payload)> packets = await CollectThroughBarrierAsync();
        packets.AddRange(await CollectAsync(quiet));
        return packets;
    }

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
    public async Task<ChatMessage> ReadChatAsync(TimeSpan? timeout = null)
        => ChatMessage.Parse(await ReadUntilAsync(WorldOpcode.SmsgMessagechat, timeout));

    /// <summary>
    /// Read until the next <see cref="ChatType.System"/> chat line, the type every command reply uses, and decode it. Unlike
    /// <see cref="ReadChatAsync"/> it skips /say, emote and whisper lines from other players: a managed bot near the client greets
    /// a newly arrived player on its own schedule, and that greeting can land between two lines of a command's reply.
    /// </summary>
    public async Task<ChatMessage> ReadSystemChatAsync(TimeSpan? timeout = null)
    {
        while (true)
        {
            ChatMessage message = await ReadChatAsync(timeout);
            if (message.Type == ChatType.System) return message;
        }
    }

    /// <summary>Read packets until one with <paramref name="opcode"/> arrives; fails on timeout.</summary>
    public async Task<byte[]> ReadUntilAsync(WorldOpcode opcode, TimeSpan? timeout = null)
    {
        List<(WorldOpcode Op, byte[] Payload)> skipped = [];
        while (true)
        {
            (WorldOpcode op, byte[] payload) packet;
            try
            {
                packet = await ReadAsync(timeout);
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

    public async Task<(WorldOpcode Opcode, byte[] Payload)> ReadAsync(TimeSpan? readTimeout = null)
    {
        // Bounded so a test expecting a packet the server never sends fails instead of hanging.
        using var timeout = new CancellationTokenSource(readTimeout ?? ReadTimeout);
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
    public async Task<byte[]> ReadUpdateAsync(TimeSpan? readTimeout = null)
    {
        (WorldOpcode op, byte[] payload) = await ReadAsync(readTimeout);
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
        (WorldOpcode op, byte[] payload) = await SetupAsync(expected, "login packet " + (LastLoginPackets.Count + 1), t => ReadAsync(t));
        Assert.Equal(expected, op);
        LastLoginPackets.Add((op, payload));
    }

    /// <summary>Run a setup read with <see cref="HandshakeTimeout"/>; a timeout names the expected opcode, the step and the time spent.</summary>
    private async Task<T> SetupAsync<T>(WorldOpcode expected, string step, Func<TimeSpan, Task<T>> read)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return await read(HandshakeTimeout);
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException($"setup timed out waiting for {WorldOpcodeNames.GetName(expected)} ({step}) after {watch.Elapsed.TotalSeconds:F1} s (bound {HandshakeTimeout.TotalSeconds:F0} s)", ex);
        }
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

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using ArcaneCore.Cryptography;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Net;

/// <summary>Per-connection limits.</summary>
public sealed class WorldSessionOptions
{
    /// <summary>Disconnect a client whose unsent data exceeds this many bytes (it stopped reading).</summary>
    public long MaxOutboundBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>In-world packets handled per session per tick; the rest wait for the next tick.</summary>
    public int MaxWorldPacketsPerTick { get; set; } = 150;
}

/// <summary>
/// One world connection: framing, header encryption, the M2 handshake, and dispatch through
/// the <see cref="OpcodeTable"/>. Character-screen packets are handled on this session's own
/// task; in-world packets are queued and handled on the world thread during the map update.
/// <para>
/// Sends never touch the socket: frames are header-encrypted under a lock (keeping cipher
/// order equal to queue order) and handed to a single writer task through a channel, so the
/// world thread cannot be stalled by a slow client.
/// </para>
/// Verified against vmangos WorldSocket.cpp / WorldSession.cpp.
/// </summary>
public sealed class WorldSession : IPlayerSession
{
    /// <summary>vmangos WorldSocket::handle_input_header rejects sizes outside [4, 0x2800].</summary>
    public const int MaxClientPacketSize = 0x2800;

    /// <summary>The SMSG size field is 16 bits and counts the 2 opcode bytes.</summary>
    public const int MaxServerPayload = ushort.MaxValue - 2;

    private readonly Stream _stream;
    private readonly OpcodeTable _opcodes;
    private readonly SessionRegistry _registry;
    private readonly WorldSessionOptions _options;
    private readonly ILogger _logger;
    private readonly WorldHeaderCrypt _crypt = new();
    private readonly object _sendLock = new();
    private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentQueue<QueuedPacket> _worldQueue = new();
    private readonly CancellationTokenSource _kick = new();
    private long _outboundBytes;
    private volatile SessionState _state = SessionState.Connected;
    private uint _serverSeed;

    public WorldSession(
        Stream stream,
        string remoteEndpoint,
        IServiceProvider services,
        OpcodeTable opcodes,
        WorldRuntime world,
        SessionRegistry registry,
        WorldSessionOptions options,
        ILogger logger)
    {
        _stream = stream;
        RemoteEndpoint = remoteEndpoint;
        Services = services;
        _opcodes = opcodes;
        World = world;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    public string RemoteEndpoint { get; }

    /// <summary>This connection's DI scope (stores are scoped per connection).</summary>
    public IServiceProvider Services { get; }

    public WorldRuntime World { get; }

    public SessionState State => _state;

    public int AccountId { get; private set; }

    public string AccountName { get; private set; } = string.Empty;

    /// <summary>The in-world player. Written on the world thread only.</summary>
    public Player? Player { get; private set; }

    public ILogger Logger => _logger;

    /// <summary>Run the connection until the client disconnects, is kicked, or the server stops.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Task writer = Task.Run(RunWriterAsync, CancellationToken.None);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _kick.Token);
        try
        {
            _serverSeed = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
            Span<byte> challenge = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(challenge, _serverSeed);
            Send(WorldOpcode.SmsgAuthChallenge, challenge);

            await ReadLoopAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException or ObjectDisposedException)
        {
            // disconnect, kick or shutdown
        }
        finally
        {
            Close();
            await writer.ConfigureAwait(false);
        }
    }

    // --- IPlayerSession -----------------------------------------------------------

    /// <summary>Queue a packet. Thread-safe; drops silently once the session is closed.</summary>
    public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxServerPayload)
        {
            throw new ArgumentException($"{WorldOpcodeNames.GetName(opcode)} payload of {payload.Length} bytes exceeds the SMSG size field");
        }

        byte[] frame = new byte[WorldHeaderCrypt.OutgoingHeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)(payload.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), (ushort)opcode);
        payload.CopyTo(frame.AsSpan(WorldHeaderCrypt.OutgoingHeaderLength));

        lock (_sendLock)
        {
            if (_state == SessionState.Closed)
            {
                return;
            }

            _crypt.EncryptHeader(frame.AsSpan(0, WorldHeaderCrypt.OutgoingHeaderLength));
            if (!_outbound.Writer.TryWrite(frame))
            {
                return;
            }
        }

        if (Interlocked.Add(ref _outboundBytes, frame.Length) > _options.MaxOutboundBytes)
        {
            _logger.LogWarning("[{Endpoint}] outbound queue over {Limit} bytes; disconnecting", RemoteEndpoint, _options.MaxOutboundBytes);
            Kick();
        }
    }

    public void Send(WorldOpcode opcode, PacketWriter payload) => Send(opcode, payload.AsSpan());

    /// <summary>Handle queued in-world packets (world thread, from the player's map update).</summary>
    public void ProcessWorldPackets(Player player)
    {
        for (int budget = _options.MaxWorldPacketsPerTick; budget > 0 && _worldQueue.TryDequeue(out QueuedPacket packet); budget--)
        {
            if (_state != SessionState.InWorld || !ReferenceEquals(Player, player))
            {
                continue;
            }

            try
            {
                packet.Handler.World!(this, player, packet.Payload);
            }
            catch (ArgumentOutOfRangeException)
            {
                _logger.LogWarning("[{Endpoint}] malformed {Opcode}; disconnecting",
                    RemoteEndpoint, WorldOpcodeNames.GetName(packet.Handler.Opcode));
                Kick();
                return;
            }
        }
    }

    public void Kick()
    {
        try
        {
            _kick.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already torn down
        }
    }

    // --- world-thread state transitions (called by handlers) ------------------------

    /// <summary>Character screen → loading. Fails if the session is no longer at the character screen.</summary>
    public bool TryBeginLogin()
    {
        lock (_sendLock)
        {
            if (_state != SessionState.CharacterSelect)
            {
                return false;
            }

            _state = SessionState.LoggingIn;
            return true;
        }
    }

    /// <summary>Loading → in world (world thread). False if the client went away meanwhile.</summary>
    public bool TryEnterWorld(Player player)
    {
        lock (_sendLock)
        {
            if (_state != SessionState.LoggingIn)
            {
                return false;
            }

            Player = player;
            _state = SessionState.InWorld;
            return true;
        }
    }

    /// <summary>Loading failed: back to the character screen.</summary>
    public void AbortLogin()
    {
        lock (_sendLock)
        {
            if (_state == SessionState.LoggingIn)
            {
                _state = SessionState.CharacterSelect;
            }
        }
    }

    // --- read path -----------------------------------------------------------------

    private async Task ReadLoopAsync(CancellationToken token)
    {
        byte[] header = new byte[WorldHeaderCrypt.IncomingHeaderLength];
        while (true)
        {
            int read = await _stream.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            await _stream.ReadExactlyAsync(header.AsMemory(1), token).ConfigureAwait(false);
            _crypt.DecryptHeader(header);

            ushort size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
            uint rawOpcode = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2, 4));
            if (size < 4 || size > MaxClientPacketSize || rawOpcode > ushort.MaxValue)
            {
                _logger.LogWarning("[{Endpoint}] bad packet header (size {Size}, opcode 0x{Opcode:X}); disconnecting",
                    RemoteEndpoint, size, rawOpcode);
                return;
            }

            int payloadLength = size - 4;
            byte[] payload = payloadLength > 0 ? new byte[payloadLength] : [];
            if (payloadLength > 0)
            {
                await _stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
            }

            if (!await DispatchAsync((WorldOpcode)rawOpcode, payload).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task<bool> DispatchAsync(WorldOpcode opcode, byte[] payload)
    {
        try
        {
            if (opcode == WorldOpcode.CmsgPing)
            {
                HandlePing(payload); // vmangos answers pings in WorldSocket, in any state
                return true;
            }

            if (_state == SessionState.Connected)
            {
                if (opcode == WorldOpcode.CmsgAuthSession)
                {
                    return await HandleAuthSessionAsync(payload).ConfigureAwait(false);
                }

                _logger.LogWarning("[{Endpoint}] {Opcode} before authentication; disconnecting",
                    RemoteEndpoint, WorldOpcodeNames.GetName(opcode));
                return false;
            }

            if (!_opcodes.TryGet(opcode, out OpcodeHandler handler))
            {
                _logger.LogDebug("[{Endpoint}] unhandled {Opcode} ({Length} bytes)",
                    RemoteEndpoint, WorldOpcodeNames.GetName(opcode), payload.Length);
                return true;
            }

            if (handler.World is not null)
            {
                if (_state is SessionState.LoggingIn or SessionState.InWorld)
                {
                    _worldQueue.Enqueue(new QueuedPacket(handler, payload));
                }

                return true;
            }

            if (_state != handler.RequiredState)
            {
                _logger.LogDebug("[{Endpoint}] {Opcode} ignored in state {State}",
                    RemoteEndpoint, WorldOpcodeNames.GetName(opcode), _state);
                return true;
            }

            await handler.Session!(this, payload).ConfigureAwait(false);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            _logger.LogWarning("[{Endpoint}] malformed {Opcode}; disconnecting", RemoteEndpoint, WorldOpcodeNames.GetName(opcode));
            return false;
        }
    }

    private void HandlePing(byte[] payload)
    {
        // CMSG_PING: u32 sequence, u32 latency → SMSG_PONG: u32 sequence (vmangos WorldSocket::HandlePing).
        var reader = new PacketReader(payload);
        uint sequence = reader.ReadUInt32();
        Span<byte> pong = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(pong, sequence);
        Send(WorldOpcode.SmsgPong, pong);
    }

    private async Task<bool> HandleAuthSessionAsync(byte[] payload)
    {
        // CMSG_AUTH_SESSION (vmangos WorldSocket::HandleAuthSession, build 5875 layout):
        // u32 build, u32 server id, CString account, u32 client seed, u8[20] digest, addon block.
        var reader = new PacketReader(payload);
        uint build = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        string account = reader.ReadCString().ToUpperInvariant();
        uint clientSeed = reader.ReadUInt32();
        byte[] clientDigest = reader.ReadBytes(20).ToArray();
        byte[] addonBlock = reader.ReadToEnd().ToArray();

        if (build != ClientBuild.Vanilla1121)
        {
            SendAuthResponse(AuthResponseCode.VersionMismatch);
            return false;
        }

        IAccountStore accounts = Services.GetRequiredService<IAccountStore>();
        Account? stored = await accounts.FindByUsernameAsync(account).ConfigureAwait(false);
        if (stored?.SessionKey is null)
        {
            SendAuthResponse(AuthResponseCode.UnknownAccount);
            return false;
        }

        // digest = SHA1(account, u32 0, clientSeed, serverSeed, K) (vmangos WorldSocket::HandleAuthSession)
        byte[] expected = Sha1.Hash(Encoding.ASCII.GetBytes(account), new byte[4], Le(clientSeed), Le(_serverSeed), stored.SessionKey);
        if (!CryptographicOperations.FixedTimeEquals(expected, clientDigest))
        {
            SendAuthResponse(AuthResponseCode.Failed);
            return false;
        }

        lock (_sendLock)
        {
            _crypt.Initialize(stored.SessionKey);
            AccountId = stored.Id;
            AccountName = account;
            _state = SessionState.CharacterSelect;
        }

        // One world session per account: a reconnect replaces (and disconnects) the old one,
        // as vmangos World::AddSession_ does.
        _registry.Register(this);
        _logger.LogInformation("[{Endpoint}] '{Account}' authenticated", RemoteEndpoint, account);

        SendAuthResponse(AuthResponseCode.Ok);
        Send(WorldOpcode.SmsgAddonInfo, AddonInfo.BuildResponse(addonBlock));
        return true;
    }

    private void SendAuthResponse(AuthResponseCode code) => Send(WorldOpcode.SmsgAuthResponse, [(byte)code]);

    // --- teardown ------------------------------------------------------------------

    private void Close()
    {
        lock (_sendLock)
        {
            if (_state == SessionState.Closed)
            {
                return;
            }

            _state = SessionState.Closed;
            _outbound.Writer.TryComplete();
        }

        _registry.Unregister(this);

        // Remove the player on the world thread; queued after any pending login command, which
        // sees the Closed state and backs out.
        World.Post(() =>
        {
            if (Player is { } player)
            {
                World.RemovePlayer(player);
                Player = null;
            }
        });
        _logger.LogInformation("[{Endpoint}] session closed", RemoteEndpoint);
    }

    private async Task RunWriterAsync()
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (await _outbound.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                int used = 0;
                while (_outbound.Reader.TryRead(out byte[]? frame))
                {
                    Interlocked.Add(ref _outboundBytes, -frame.Length);
                    if (used + frame.Length > buffer.Length)
                    {
                        if (used > 0)
                        {
                            await _stream.WriteAsync(buffer.AsMemory(0, used)).ConfigureAwait(false);
                            used = 0;
                        }

                        if (frame.Length > buffer.Length)
                        {
                            await _stream.WriteAsync(frame).ConfigureAwait(false);
                            continue;
                        }
                    }

                    frame.CopyTo(buffer, used);
                    used += frame.Length;
                }

                if (used > 0)
                {
                    await _stream.WriteAsync(buffer.AsMemory(0, used)).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Kick(); // the socket is gone; stop reading too
        }
    }

    private static byte[] Le(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private readonly record struct QueuedPacket(OpcodeHandler Handler, byte[] Payload);
}

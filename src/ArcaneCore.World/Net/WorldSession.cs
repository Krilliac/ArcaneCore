using System.Buffers.Binary;
using System.Diagnostics;
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

    /// <summary>
    /// Most world-handler packets one session may have queued for the world thread; the session
    /// is disconnected when a packet would exceed it. 0 disables the bound. The drain rate is
    /// <see cref="MaxWorldPacketsPerTick"/> per 50 ms tick (3000 per second) and a retail client
    /// sends a few tens of packets per second (movement heartbeats, casts, chat), so 8192 only
    /// fills if the world thread stalled for minutes or the peer floods. Hardening: vmangos
    /// (WorldSession.cpp:307-332 <c>QueuePacket</c> into <c>m_recvQueue</c>) and mangos-classic
    /// (WorldSession.cpp:256-285) queue without any bound. Bound from World:MaxQueuedWorldPackets.
    /// </summary>
    public int MaxQueuedWorldPackets { get; set; } = 8192;

    /// <summary>
    /// Most payload bytes one session may have queued for the world thread (see
    /// <see cref="MaxQueuedWorldPackets"/>); 0 disables the bound. Mirrors the 8 MiB outbound
    /// bound. The largest client frame is <see cref="WorldSession.MaxClientPacketSize"/> bytes, so
    /// this holds a few hundred maximum-size packets. Bound from World:MaxQueuedWorldBytes.
    /// </summary>
    public long MaxQueuedWorldBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// How long a closing session lets the writer flush queued frames (for example a refusal
    /// reply) before the stream is torn down so a client that stopped reading cannot hold the
    /// connection, its DI scope and its queued frames forever. Hardening (vmangos has no
    /// equivalent): <see cref="TimeSpan.Zero"/>, the default, waits indefinitely as retail does.
    /// Bound from World:WriterDrainGrace (for example "00:00:05").
    /// </summary>
    public TimeSpan WriterDrainGrace { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// How long a world connection may stay unauthenticated (no valid CMSG_AUTH_SESSION yet),
    /// counted from accept; the session is closed when it expires. This is the retail rule:
    /// vmangos <c>Network.TimeoutSecsIfNoAuth = 10</c> (WorldSocket.cpp:621-628,
    /// mangosd.conf.dist.in:3039) closes the socket the same way. It stops a client that withholds
    /// the packet header (or trickles it) from holding a socket, a writer task and a DI scope
    /// forever (Codex finding 2). <see cref="TimeSpan.Zero"/> disables it. Bound from
    /// World:PreAuthTimeout (for example "00:00:10").
    /// </summary>
    public TimeSpan PreAuthTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Hardening beyond retail: also require pitch, jump speeds/angles and spline elevation to be
    /// finite before a movement block is stored and relayed. Retail (vmangos VerifyMovementInfo)
    /// does not check them, so the default is off. Bound from World:StrictMovementFiniteness.
    /// </summary>
    public bool StrictMovementFiniteness { get; set; }
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
public sealed partial class WorldSession : IPlayerSession
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
    private readonly CancellationTokenSource _writerAbort = new();
    private long _outboundBytes;
    private int _queuedPackets;
    private long _queuedBytes;
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
        RemoteAddress = AccountBanEvaluator.AddressOfEndpoint(remoteEndpoint);
        Services = services;
        _opcodes = opcodes;
        World = world;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    public string RemoteEndpoint { get; }

    /// <summary>
    /// The client's IP address, parsed once from <see cref="RemoteEndpoint"/> and normalised (an IPv4-mapped
    /// IPv6 address becomes IPv4). Null when the endpoint text is not an address (tests pass a placeholder).
    /// Matched against IP bans at authentication and by the live ban enforcement.
    /// </summary>
    public string? RemoteAddress { get; }

    /// <summary>This connection's DI scope (stores are scoped per connection).</summary>
    public IServiceProvider Services { get; }

    public WorldRuntime World { get; }

    /// <summary>World:StrictMovementFiniteness (default off, retail).</summary>
    public bool StrictMovementFiniteness => _options.StrictMovementFiniteness;

    public SessionState State => _state;

    /// <summary>World-handler packets queued for the world thread and not yet handled or dropped.</summary>
    public int QueuedWorldPackets => Volatile.Read(ref _queuedPackets);

    /// <summary>Payload bytes of <see cref="QueuedWorldPackets"/>.</summary>
    public long QueuedWorldBytes => Interlocked.Read(ref _queuedBytes);

    public int AccountId { get; private set; }

    public string AccountName { get; private set; } = string.Empty;

    /// <summary>The account's GM level, read at authentication.</summary>
    public AccountSecurity Security { get; private set; }

    /// <summary>
    /// The account's stored client settings and tutorial flags, loaded at authentication.
    /// Mutated only by session handlers (this session's task); the world thread receives copies.
    /// </summary>
    public AccountSettings Settings { get; private set; } = new();

    /// <summary>The in-world player. Written on the world thread only.</summary>
    public Player? Player { get; private set; }

    public ILogger Logger => _logger;

    /// <summary>Run the connection until the client disconnects, is kicked, or the server stops.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Task writer = Task.Run(RunWriterAsync, CancellationToken.None);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _kick.Token);

        // vmangos WorldSocket::Start (WorldSocket.cpp:621-628): a connection that has not
        // authenticated within the timeout is closed. The timer never touches an authenticated session.
        using Timer? preAuth = _options.PreAuthTimeout > TimeSpan.Zero
            ? new Timer(_ => ExpirePreAuth(), null, _options.PreAuthTimeout, Timeout.InfiniteTimeSpan)
            : null;
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
            await DrainWriterAsync(writer).ConfigureAwait(false);
        }
    }

    private void ExpirePreAuth()
    {
        if (_state == SessionState.Connected)
        {
            _logger.LogInformation("[{Endpoint}] no authentication within {Timeout}; closing", RemoteEndpoint, _options.PreAuthTimeout);
            Kick();
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

        if (_managed)
        {
            CaptureManagedPacket(opcode, payload);
            return;
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
            Release(packet);
            if (_kick.IsCancellationRequested)
            {
                return;
            }
            if (_state != SessionState.InWorld || !ReferenceEquals(Player, player))
            {
                continue;
            }

            // Settlement freezes this character, while the world continues updating others.
            // Drop requests rather than replaying actions captured against pre-reward state.
            if (player.IsQuestSettlementPending)
            {
                continue;
            }

            // Between SMSG_NEW_WORLD and MSG_MOVE_WORLDPORT_ACK the player is in no map: only
            // the ack is handled, everything else is dropped (vmangos STATUS_TRANSFER /
            // WorldSession::Update skips STATUS_LOGGEDIN packets while !IsInWorld()).
            if (player.Map is null && packet.Handler.Opcode != WorldOpcode.MsgMoveWorldportAck)
            {
                continue;
            }

            long handlerStart = Stopwatch.GetTimestamp();
            try
            {
                packet.Handler.World!(this, player, packet.Payload);
                LogIfSlowPacket(packet.Handler.Opcode, handlerStart);
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

    /// <summary>vmangos PerformanceLog.SlowPackets (WorldSession.cpp:620): a handler over the threshold is logged; 0 disables.</summary>
    private void LogIfSlowPacket(WorldOpcode opcode, long startTimestamp)
    {
        int threshold = World.Options.Perf.SlowPackets;
        if (threshold <= 0)
        {
            return;
        }

        long micros = (Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000 / Stopwatch.Frequency;
        if (micros > threshold * 1000L)
        {
            _logger.LogWarning(PerformanceLogOptions.PerfEventId, "[{Endpoint}] Slow packet {Opcode}: {DurationMs} ms",
                RemoteEndpoint, WorldOpcodeNames.GetName(opcode), micros / 1000);
        }
    }

    public void Kick()
    {
        if (_managed)
        {
            CloseManaged();
        }
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

            DiscardQueuedPackets(); // nothing from a previous stay in the world may leak into this one
            _state = SessionState.LoggingIn;
            return true;
        }
    }

    /// <summary>
    /// World thread, after the player left the world through a logout: back to the character
    /// screen (vmangos WorldSession::LogoutPlayer sends SMSG_LOGOUT_COMPLETE).
    /// </summary>
    public void OnLoggedOut()
    {
        lock (_sendLock)
        {
            if (_state != SessionState.InWorld)
            {
                return;
            }

            Player = null;
            DiscardQueuedPackets();
            _state = SessionState.CharacterSelect;
        }

        Send(WorldOpcode.SmsgLogoutComplete, []);
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
                    // The read loop is the only producer, so this check-then-add cannot race another
                    // enqueue; the world thread only ever lowers the counters.
                    int maxPackets = _options.MaxQueuedWorldPackets;
                    long maxBytes = _options.MaxQueuedWorldBytes;
                    if ((maxPackets > 0 && _queuedPackets >= maxPackets)
                        || (maxBytes > 0 && Interlocked.Read(ref _queuedBytes) + payload.Length > maxBytes))
                    {
                        // Counts and limits only: nothing from the payload is logged.
                        _logger.LogWarning(
                            "[{Endpoint}] inbound world queue over its bound ({Packets} packets, {Bytes} bytes queued; limits {MaxPackets}/{MaxBytes}); disconnecting",
                            RemoteEndpoint, _queuedPackets, Interlocked.Read(ref _queuedBytes), maxPackets, maxBytes);
                        return false;
                    }

                    Interlocked.Increment(ref _queuedPackets);
                    Interlocked.Add(ref _queuedBytes, payload.Length);
                    _worldQueue.Enqueue(new QueuedPacket(handler, payload));
                }

                return true;
            }

            if (!handler.AllowsState(_state))
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

    private void Release(QueuedPacket packet)
    {
        Interlocked.Decrement(ref _queuedPackets);
        Interlocked.Add(ref _queuedBytes, -packet.Payload.Length);
    }

    /// <summary>Drop every queued packet, keeping the count and byte accounting exact.</summary>
    private void DiscardQueuedPackets()
    {
        while (_worldQueue.TryDequeue(out QueuedPacket packet))
        {
            Release(packet);
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

        // Account status is enforced here too: the logon server only gates the SRP exchange, and
        // a session key may have been issued before the ban. vmangos has a single ban reply,
        // AUTH_BANNED, for any banned account (WorldSocket.cpp:333-345) after the digest check;
        // it also refuses IP-banned addresses there (checked below against the ban rows).
        if (stored.Status != AccountStatus.Active)
        {
            _logger.LogInformation("[{Endpoint}] refused world login for {Status} account", RemoteEndpoint, stored.Status);
            SendAuthResponse(AuthResponseCode.Banned);
            return false;
        }

        // Ban rows and IP bans (live bans): a store error propagates and closes the connection (fail closed).
        // Unlike retail's cached IP list (AccountMgr.cpp:317-327) this reads the rows, so a fresh ban is seen at once.
        IBanStore? bans = Services.GetService<IBanStore>();
        if (bans is not null && await IsBannedAsync(bans, stored.Id).ConfigureAwait(false))
        {
            _logger.LogInformation("[{Endpoint}] refused world login for banned account or address", RemoteEndpoint);
            SendAuthResponse(AuthResponseCode.Banned);
            return false;
        }

        AccountSettings settings = await Services.GetRequiredService<IAccountDataStore>()
            .GetAsync(stored.Id).ConfigureAwait(false);

        lock (_sendLock)
        {
            AccountId = stored.Id;
            AccountName = account;
            Security = stored.Security;
            Settings = settings;
        }

        // One world session per account: a reconnect replaces (and disconnects) the old one,
        // as vmangos World::AddSession_ does.
        _registry.Register(this);

        // A ban (or status change) committed between the reads above and Register could not reach this
        // not-yet-registered session through the live-ban events, so look once more now that it can be found.
        Account? recheck = await accounts.FindByUsernameAsync(account).ConfigureAwait(false);
        if ((recheck is not null && recheck.Status != AccountStatus.Active)
            || (bans is not null && await IsBannedAsync(bans, stored.Id).ConfigureAwait(false)))
        {
            _logger.LogInformation("[{Endpoint}] '{Account}' was banned while authenticating; refusing", RemoteEndpoint, account);
            SendAuthResponse(AuthResponseCode.Banned); // still plain: the header cipher is not initialised yet
            return false;
        }

        // Only now does the session become usable: header encryption on and the character screen reachable.
        // It may have been kicked (a live ban event) while it was registered but not yet authenticated: Kick() only
        // cancels the kick token (the state becomes Closed later, in Close()), so the token is what must be tested.
        lock (_sendLock)
        {
            if (_state == SessionState.Closed || _kick.IsCancellationRequested)
            {
                return false;
            }

            _crypt.Initialize(stored.SessionKey);
            _state = SessionState.CharacterSelect;
        }

        _logger.LogInformation("[{Endpoint}] '{Account}' authenticated", RemoteEndpoint, account);

        SendAuthResponse(AuthResponseCode.Ok);
        Send(WorldOpcode.SmsgAddonInfo, AddonInfo.BuildResponse(addonBlock));
        return true;
    }

    /// <summary>An account ban row in force, or an IP ban on this connection's address (WorldSocket.cpp:339).</summary>
    private async Task<bool> IsBannedAsync(IBanStore bans, int accountId)
    {
        if (await bans.GetActiveAccountBanAsync(accountId).ConfigureAwait(false) is not null)
        {
            return true;
        }

        return RemoteAddress is { } address && await bans.GetActiveIpBanAsync(address).ConfigureAwait(false) is not null;
    }

    private void SendAuthResponse(AuthResponseCode code) => Send(WorldOpcode.SmsgAuthResponse, [(byte)code]);

    // --- teardown ------------------------------------------------------------------

    /// <summary>
    /// Wait for the writer to flush what is queued. A client that stopped reading parks the writer
    /// inside WriteAsync forever, so after the grace period the write is cancelled and the stream
    /// disposed; that releases the socket, the DI scope and the queued frames.
    /// </summary>
    private async Task DrainWriterAsync(Task writer)
    {
        TimeSpan grace = _options.WriterDrainGrace;
        if (grace <= TimeSpan.Zero)
        {
            await writer.ConfigureAwait(false); // retail: wait for the writer however long it takes
            return;
        }

        if (await Task.WhenAny(writer, Task.Delay(grace)).ConfigureAwait(false) == writer)
        {
            await writer.ConfigureAwait(false);
            return;
        }

        _logger.LogWarning("[{Endpoint}] writer did not drain within {Grace}; tearing the connection down", RemoteEndpoint, grace);
        _writerAbort.Cancel();
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // the socket is already gone
        }

        if (await Task.WhenAny(writer, Task.Delay(grace)).ConfigureAwait(false) == writer)
        {
            await writer.ConfigureAwait(false);
        }
        else
        {
            _logger.LogError("[{Endpoint}] writer ignored cancellation and stream disposal; abandoning it", RemoteEndpoint);
        }
    }

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
        DiscardQueuedPackets(); // a closed session retains nothing

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
                            await _stream.WriteAsync(buffer.AsMemory(0, used), _writerAbort.Token).ConfigureAwait(false);
                            used = 0;
                        }

                        if (frame.Length > buffer.Length)
                        {
                            await _stream.WriteAsync(frame, _writerAbort.Token).ConfigureAwait(false);
                            continue;
                        }
                    }

                    frame.CopyTo(buffer, used);
                    used += frame.Length;
                }

                if (used > 0)
                {
                    await _stream.WriteAsync(buffer.AsMemory(0, used), _writerAbort.Token).ConfigureAwait(false);
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

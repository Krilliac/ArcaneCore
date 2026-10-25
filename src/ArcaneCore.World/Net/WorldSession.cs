using System.Buffers.Binary;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using ArcaneCore.Cryptography;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Kernel.Net;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net.Versioning;
using ArcaneCore.Protocol.Versioning;
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
    /// connection, its DI scope and its queued frames forever. <see cref="TimeSpan.Zero"/>, the
    /// default, means the built-in 5 s bound; the wait is never unbounded. vmangos does not wait at
    /// all: its CloseSocket shuts the socket down at once (AsyncSocket_windows.cpp:318-329).
    /// Bound from World:WriterDrainGrace (for example "00:00:02").
    /// </summary>
    public TimeSpan WriterDrainGrace { get; set; } = TimeSpan.Zero;

    /// <summary>The drain bound a zero (or negative) <see cref="WriterDrainGrace"/> stands for.</summary>
    public static readonly TimeSpan DefaultWriterDrainBound = TimeSpan.FromSeconds(5);

    /// <summary>The drain bound a closing session applies: <see cref="WriterDrainGrace"/> when positive, else <see cref="DefaultWriterDrainBound"/>.</summary>
    internal TimeSpan EffectiveWriterDrainBound => WriterDrainGrace > TimeSpan.Zero ? WriterDrainGrace : DefaultWriterDrainBound;

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
/// <para>
/// Transport protections (docs/ops/netguard.md): the frame read deadline comes from
/// <c>Net:Protection</c> (through the listener's <see cref="NetGuard"/>, or the defaults when a
/// host constructs the session without one, so it is never off by accident); the per-address
/// failure budget needs the guard's table and is skipped without it. A malformed packet (the
/// controlled <see cref="MalformedPacket"/> outcome of the readers) closes the connection with one
/// Warning and never leaves the session or the world thread; any other exception from a handler is
/// a server bug and is logged at Error with its stack trace by the host that owns the thread.
/// </para>
/// </summary>
public sealed partial class WorldSession : IPlayerSession
{
    /// <summary>vmangos WorldSocket::handle_input_header rejects sizes outside [4, 0x2800].</summary>
    public const int MaxClientPacketSize = WorldFrameCodec.MaxClientPacketSize;

    /// <summary>The SMSG size field is 16 bits and counts the 2 opcode bytes.</summary>
    public const int MaxServerPayload = WorldFrameCodec.MaxServerPayload;

    private static readonly NetProtectionOptions DefaultProtection = new();

    private readonly Stream _stream;
    private readonly OpcodeTable _opcodes;
    private readonly SessionRegistry _registry;
    private readonly WorldSessionOptions _options;
    private readonly ILogger _logger;
    private readonly IOutboundPacketObserver? _outboundPacketObserver;
    private readonly NetGuard? _guard;
    private readonly UnhandledOpcodeTracker _unhandled;
    private readonly DevOpcodeTracker _devOpcodes;
    private readonly NetProtectionOptions _protection;
    private readonly IpKey? _address;
    // The client build's wire protocol (multi-version design S0). Pre-auth framing uses ClientProtocols.PreAuth;
    // CMSG_AUTH_SESSION binds the build's protocol once. Only build 5875 exists, so the cipher instance never changes.
    private IClientProtocol _protocol = ClientProtocols.PreAuth;
    private readonly IWorldHeaderCrypt _crypt = ClientProtocols.PreAuth.CreateHeaderCrypt();
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
    private readonly OpcodeRateLimiter? _packetLimiter;
    private readonly LogGate _packetGate = new(TimeSpan.FromSeconds(10));
    private int _latencyMs = -1;
    private uint _currentPacketReceivedMs;

    public WorldSession(
        Stream stream,
        string remoteEndpoint,
        IServiceProvider services,
        OpcodeTable opcodes,
        WorldRuntime world,
        SessionRegistry registry,
        WorldSessionOptions options,
        ILogger logger,
        NetGuard? guard = null)
    {
        _stream = stream;
        RemoteEndpoint = remoteEndpoint;
        RemoteAddress = AccountBanEvaluator.AddressOfEndpoint(remoteEndpoint);
        _address = IpKey.TryParse(remoteEndpoint, out IpKey parsed) ? parsed : null; // once per connection, never per attempt
        Services = services;
        _opcodes = opcodes;
        World = world;
        _registry = registry;
        _options = options;
        _logger = logger;
        _outboundPacketObserver = services.GetService<IOutboundPacketObserver>();
        _unhandled = services.GetService<UnhandledOpcodeTracker>() ?? UnhandledOpcodeTracker.Shared;
        _devOpcodes = services.GetService<DevOpcodeTracker>() ?? DevOpcodeTracker.Shared;
        _guard = guard;
        _protection = guard?.Options ?? DefaultProtection;
        _packetLimiter = NetGuard.CreatePacketLimiter(_protection);
        _dispatchGate = CreateDispatchGate(services);
        InitObservability(services);
        InitPacketHeuristics(services);
    }

    /// <summary>
    /// The client's latency in milliseconds as an exponentially weighted moving average (alpha 1/5) of the value it reports in
    /// every CMSG_PING (vmangos WorldSocket::HandlePing stores the last one in m_latency); 0 until the first ping. Read by
    /// the anticheat's latency slack (docs/areas/anticheat.md). Thread-safe.
    /// </summary>
    public int LatencyMs => Math.Max(0, Volatile.Read(ref _latencyMs));

    /// <summary>
    /// When the in-world packet being handled arrived (monotonic milliseconds, <see cref="Clock.Milliseconds"/> truncated to
    /// 32 bits), so a check can compare the client's own movement clock with an independent one that a world-thread stall
    /// does not bunch up. Set on the world thread before each handler runs; meaningless outside a handler.
    /// </summary>
    public uint CurrentPacketReceivedMs => _currentPacketReceivedMs;

    /// <summary>Fold one latency sample from a CMSG_PING into <see cref="LatencyMs"/> (samples above 60 s are clamped).</summary>
    internal void RecordLatencySample(uint latency)
    {
        int sample = (int)Math.Min(latency, 60_000u);
        int previous = Volatile.Read(ref _latencyMs);
        Volatile.Write(ref _latencyMs, previous < 0 ? sample : ((previous * 4) + sample) / 5);
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

    private long _sessionGeneration;
    private AccountSessionClaim? _sessionClaim;

    public int AccountId { get; private set; }

    /// <summary>
    /// Monotonic per-account generation assigned when this session was registered in <see cref="SessionRegistry"/>
    /// (<c>0</c> before authentication or on the synthetic server-console session).
    /// </summary>
    public long SessionGeneration => Volatile.Read(ref _sessionGeneration);

    /// <summary>The claim token assigned when this session was registered, or <c>null</c> before authentication.</summary>
    public AccountSessionClaim? SessionClaim => Volatile.Read(ref _sessionClaim);

    /// <summary>
    /// True while this session still holds the active claim for its account (or is the unauthenticated server-console session).
    /// Once another session supersedes or invalidates this account's claim, late packets and login transitions are fenced off.
    /// </summary>
    public bool IsCurrentSession => AccountId == 0 || _registry.IsCurrent(this);

    internal void BindClaim(AccountSessionClaim claim)
    {
        Volatile.Write(ref _sessionClaim, claim);
        Volatile.Write(ref _sessionGeneration, claim.Generation);
    }

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
            Kick("no authentication within the pre-auth timeout");
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

        ObserveOutbound(opcode, payload);

        if (_remoteOutput is not null)
        {
            SendRemote(opcode, payload); // gateway-backed session (WorldSession.Remote.cs, Tier 3a)
            return;
        }

        if (_managed)
        {
            CaptureManagedPacket(opcode, payload);
            return;
        }

        int headerLength = _crypt.OutgoingHeaderLength;
        byte[] frame = new byte[headerLength + payload.Length];
        WorldFrameCodec.WriteServerSize(frame, payload.Length);
        WorldFrameCodec.WriteServerOpcode(frame, _protocol.Opcodes.ToWire(opcode));
        payload.CopyTo(frame.AsSpan(headerLength));

        lock (_sendLock)
        {
            if (_state == SessionState.Closed)
            {
                NoteSendAfterClose(opcode);
                return;
            }

            _crypt.EncryptHeader(frame.AsSpan(0, headerLength));
            if (!_outbound.Writer.TryWrite(frame))
            {
                return;
            }

            _outboundPacketObserver?.Observe(opcode, payload.ToArray());
            CapturePacket(false, opcode, payload);
        }

        ArcaneCore.Kernel.Ops.Metrics.ArcaneMeters.PacketOut(frame.Length);
        ArcaneCore.World.Ops.Metrics.WorldPacketMetrics.Table.RecordOut((uint)opcode, frame.Length);

        if (Interlocked.Add(ref _outboundBytes, frame.Length) > _options.MaxOutboundBytes)
        {
            _logger.LogWarning("[{Endpoint}] outbound queue over {Limit} bytes; disconnecting", RemoteEndpoint, _options.MaxOutboundBytes);
            Kick("outbound queue over the byte limit");
        }
    }

    public void Send(WorldOpcode opcode, PacketWriter payload) => Send(opcode, payload.AsSpan());

    /// <summary>Handle queued in-world packets (world thread, from the player's map update).</summary>
    public void ProcessWorldPackets(Player player)
    {
        try
        {
            ProcessWorldPacketsCore(player);
        }
        finally
        {
            _injectWorld = false; // report only: the heuristics' "this packet came from a packet tool" flag never outlives the batch
        }
    }

    private void ProcessWorldPacketsCore(Player player)
    {
        // Player and map state are owned by the world thread; this is the only consumer of _worldQueue.
        Invariant.Assert(World.IsWorldThread, "in-world packets are handled on the world thread");
        CheckLogoutOverdue(player);
        for (int budget = _options.MaxWorldPacketsPerTick; budget > 0 && _worldQueue.TryDequeue(out QueuedPacket packet); budget--)
        {
            Release(packet);
            _injectWorld = packet.Injected;
            if (_kick.IsCancellationRequested || !IsCurrentSession)
            {
                return;
            }
            if (_state != SessionState.InWorld || !ReferenceEquals(Player, player))
            {
                HeuristicsWrongState(packet.Handler.Opcode, "world packet dequeued after the session left the world", WrongStateGrace.LeftWorld);
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
            // Client loading screens routinely emit CMSG_CANCEL_TRADE on map transitions; handle
            // it without logging a dropped-packet warning.
            if (player.Map is null && packet.Handler.Opcode != WorldOpcode.MsgMoveWorldportAck)
            {
                if (packet.Handler.Opcode == WorldOpcode.CmsgCancelTrade)
                {
                    packet.Handler.World?.Invoke(this, player, packet.Payload);
                    continue;
                }

                if (!IsPlayerbot && _packetGate.TryEnter(out int suppressed))
                {
                    _logger.LogWarning(
                        "[{Endpoint}] dropping {Opcode} for '{Player}' (guid={Guid}) while player.Map is null (awaiting MSG_MOVE_WORLDPORT_ACK to map {MapId}; {Suppressed} prior drops suppressed)",
                        RemoteEndpoint, WorldOpcodeNames.GetName(packet.Handler.Opcode), player.Name, player.Guid.Low, player.MapId, suppressed);
                }

                HeuristicsWrongState(packet.Handler.Opcode, "world packet while player.Map is null (awaiting MSG_MOVE_WORLDPORT_ACK)", WrongStateGrace.Transfer);
                continue;
            }

            HeuristicsWorldPacket(player, packet.Handler.Opcode);
            long handlerStart = Stopwatch.GetTimestamp();
            _currentPacketReceivedMs = packet.ReceivedMs;
            try
            {
                packet.Handler.World!(this, player, packet.Payload);
                RecordHandlerLatency(packet.Handler.Opcode, handlerStart);

                LogIfSlowPacket(packet.Handler.Opcode, handlerStart);
            }
            catch (Exception ex) when (IsMalformed(ex))
            {
                // The controlled outcome of PacketReader: the packet did not fit its layout. Contained
                // here so a client fault never reaches the map update. A server bug in a handler is
                // not a client fault: see the next clause.
                _logger.LogWarning(
                    "[{Endpoint}] malformed {Opcode} ({PayloadBytes} bytes, {Reason}) for '{Player}' (guid={Guid}, map={MapId}); disconnecting",
                    RemoteEndpoint, WorldOpcodeNames.GetName(packet.Handler.Opcode), packet.Payload.Length, ex.Message, player.Name, player.Guid.Low, player.MapId);
                HeuristicsMalformed(packet.Handler.Opcode, packet.Payload.Length, ex.Message);
                Kick("malformed packet");
                return;
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or ArcaneCore.Kernel.Diagnostics.InvariantViolationException))
            {
                // A server bug in a handler: the player is told in the client's own words and the fault is logged with
                // its exception and recorded. Only allowlisted read-only opcodes then go on (unless closing or in a
                // storm); every other opcode disconnects after the reply (WorldSession.HandlerFaults.cs,
                // docs/areas/client-errors.md). InvariantViolationException (the Debug stop signal) is not absorbed.
                if (!ContainWorldHandlerFault(packet.Handler.Opcode, packet.Payload, player, ex))
                {
                    return;
                }
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

    /// <summary>
    /// Disconnect the session without a recorded reason. A caller that knows why should use <see cref="Kick(string)"/>: an in-session kick
    /// with no reason is reported as <c>session/kick-no-reason</c> (naming the calling method) so no disconnect stays unexplained.
    /// </summary>
    public void Kick() => KickCore(null);

    /// <summary>Disconnect the session and record <paramref name="reason"/> (<see cref="KickReason"/>); the caller logs the details.</summary>
    public void Kick(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        KickCore(reason);
    }

    private void KickCore(string? reason)
    {
        if (Interlocked.CompareExchange(ref _kickRecorded, 1, 0) == 0)
        {
            if (reason is not null)
            {
                Volatile.Write(ref _kickReason, reason);
            }
            else
            {
                ReportKickWithoutReason();
            }
        }

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

    /// <summary>Add one in-world handler run to the opcode's latency histogram. Playerbot sessions are left out: they would drown the players' figures (their cost shows in the tick phases).</summary>
    internal void RecordHandlerLatency(WorldOpcode opcode, long startTimestamp)
    {
        if (!IsPlayerbot)
        {
            PacketHandlerLatency.Record(Latency, opcode, startTimestamp);
        }
    }

    private long _loginRequestedTimestamp;

    /// <summary>The client asked to log in a character (CMSG_PLAYER_LOGIN reached its handler): the clock for the <c>login.to_world</c> latency, stopped by <see cref="TryEnterWorld"/>.</summary>
    internal void MarkLoginRequested() => Interlocked.Exchange(ref _loginRequestedTimestamp, Stopwatch.GetTimestamp());

    private ArcaneCore.Kernel.Diagnostics.LatencyRegistry? _latency;

    /// <summary>The latency registry this session reports to: the one in the service provider, else the shared one (resolved once).</summary>
    internal ArcaneCore.Kernel.Diagnostics.LatencyRegistry Latency => _latency ??= Services.GetService<ArcaneCore.Kernel.Diagnostics.LatencyRegistry>() ?? ArcaneCore.Kernel.Diagnostics.LatencyRegistry.Shared;

    /// <summary>Character screen → loading. Fails if the session is no longer at the character screen or has been superseded.</summary>
    public bool TryBeginLogin()
    {
        lock (_sendLock)
        {
            if (_state != SessionState.CharacterSelect || _kick.IsCancellationRequested || !IsCurrentSession)
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
        Invariant.Assert(World.IsWorldThread, "logout completes on the world thread");
        lock (_sendLock)
        {
            if (_state != SessionState.InWorld)
            {
                return;
            }

            Player = null;
            DiscardQueuedPackets();
            _state = SessionState.CharacterSelect;
            Volatile.Write(ref _leftWorldTick, Environment.TickCount64);
            _heuristicsGm = false; // no player, so no GM mode: the next character's first world packet learns its own
        }

        Send(WorldOpcode.SmsgLogoutComplete, []);
    }

    /// <summary>Loading → in world (world thread). False if the client went away or was superseded meanwhile.</summary>
    public bool TryEnterWorld(Player player)
    {
        Invariant.Assert(World.IsWorldThread, "a player enters the world on the world thread");
        lock (_sendLock)
        {
            if (_state != SessionState.LoggingIn || _kick.IsCancellationRequested || !IsCurrentSession)
            {
                return false;
            }

            // LoggingIn is reached from CharacterSelect only, and both OnLoggedOut and Close clear Player
            // before the session can get back there; a player still set here would be a second character
            // for one session, which the online registry and the save path never expect.
            Invariant.Assert(Player is null, $"session {RemoteEndpoint} enters the world while still holding player {Player?.Guid}");
            Player = player;
            _state = SessionState.InWorld;
            long requested = Interlocked.Exchange(ref _loginRequestedTimestamp, 0);
            if (requested != 0 && !IsPlayerbot)
            {
                Latency.RecordSince(ArcaneCore.Kernel.Diagnostics.LatencyNames.LoginToWorld, requested);
            }

            return true;
        }
    }

    /// <summary>Loading failed: back to the character screen.</summary>
    public void AbortLogin()
    {
        Invariant.Assert(World.IsWorldThread, "a login is abandoned on the world thread");
        lock (_sendLock)
        {
            if (_state == SessionState.LoggingIn)
            {
                _state = SessionState.CharacterSelect;
                Interlocked.Exchange(ref _loginRequestedTimestamp, 0); // an abandoned login is not a login that completed
            }
        }
    }

    // --- read path -----------------------------------------------------------------

    private async Task ReadLoopAsync(CancellationToken token)
    {
        byte[] header = new byte[_crypt.IncomingHeaderLength];

        // Net:Protection:FrameReadTimeout: once the first byte of a header is in, the rest of the
        // header and the payload must follow within the budget (slowloris). One deadline per
        // connection, re-armed per frame: no timer or token source is allocated per packet. Waiting
        // for the first byte is idle time and is not bounded here (World:PreAuthTimeout bounds it
        // before authentication; a retail client may idle at the character screen).
        using var deadline = new ReadDeadline(token, _protection.FrameReadTimeout);
        while (true)
        {
            int read = await _stream.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            deadline.Arm();
            byte[] payload;
            uint rawOpcode;
            try
            {
                await _stream.ReadExactlyAsync(header.AsMemory(1), deadline.Token).ConfigureAwait(false);
                _crypt.DecryptHeader(header);

                // The size is checked against the frame bound before anything is allocated for it.
                if (!WorldFrameCodec.TryParseClientHeader(header, out ushort size, out rawOpcode))
                {
                    _logger.LogWarning("[{Endpoint}] bad packet header (size {Size}, opcode 0x{Opcode:X}); disconnecting",
                        RemoteEndpoint, size, rawOpcode);
                    HeuristicsBadHeader(size, rawOpcode);
                    return;
                }

                int payloadLength = size - 4;
                payload = payloadLength > 0 ? new byte[payloadLength] : [];
                if (payloadLength > 0)
                {
                    await _stream.ReadExactlyAsync(payload, deadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (deadline.Expired)
            {
                if (_guard is not null)
                {
                    _guard.ReportFrameTimeout(RemoteEndpoint);
                }
                else
                {
                    _logger.LogWarning("[{Endpoint}] frame not completed within {Timeout}; disconnecting", RemoteEndpoint, deadline.Timeout);
                }

                return;
            }

            deadline.Disarm();
            WorldOpcode opcode = _protocol.Opcodes.FromWire(rawOpcode);
            ArcaneCore.Kernel.Ops.Metrics.ArcaneMeters.PacketIn(header.Length + payload.Length);
            ArcaneCore.World.Ops.Metrics.WorldPacketMetrics.Table.RecordIn(rawOpcode, header.Length + payload.Length);
            CapturePacket(true, opcode, payload);
            if (!await DispatchFromClientAsync(opcode, payload, token).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Only the controlled outcome the readers raise deliberately (<see cref="MalformedPacket"/>) is
    /// a client fault, and it always disconnects. Anything else a handler throws (an
    /// <see cref="IndexOutOfRangeException"/> from its own tables, a null, a cast) is a server bug: the
    /// player is sent the matching ClientError and the fault is logged with its stack trace and recorded.
    /// Whether the session then goes on depends on the opcode: only the allowlisted read-only ones are
    /// contained (<see cref="IsContainableWorldOpcode"/>); all others, and a fault storm, close the connection.
    /// </summary>
    private static bool IsMalformed(Exception exception) => MalformedPacket.Is(exception);

    private async Task<bool> DispatchAsync(WorldOpcode opcode, byte[] payload)
    {
        try
        {
            HeuristicsClientPacket(opcode, payload);
            if (opcode == WorldOpcode.CmsgPing)
            {
                if (!TryHandlePing(payload)) // vmangos answers pings in WorldSocket, in any state
                {
                    _logger.LogWarning("[{Endpoint}] malformed {Opcode}; disconnecting", RemoteEndpoint, WorldOpcodeNames.GetName(opcode));
                    HeuristicsMalformed(opcode, payload.Length, "CMSG_PING without its sequence number");
                    return false;
                }

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

            if (!IsCurrentSession)
            {
                Kick("superseded by a newer session of the account");
                return false;
            }

            // Net:Protection:World* packet budgets (docs/ops/netguard.md): over a budget the packet is dropped, a flood closes the connection.
            if (_packetLimiter is not null)
            {
                switch (_packetLimiter.OnPacket((ushort)opcode, Clock.Milliseconds()))
                {
                    case OpcodeRateVerdict.Flood:
                        if (_guard is not null)
                        {
                            _guard.ReportPacketFlood(RemoteEndpoint);
                        }
                        else
                        {
                            _logger.LogWarning("[{Endpoint}] packet flood over {Limit} packets per second; disconnecting", RemoteEndpoint, _protection.WorldFloodPacketsPerSecond);
                        }

                        return false;
                    case OpcodeRateVerdict.Dropped:
                        _budgetDropped = true;
                        if (_guard is not null)
                        {
                            _guard.ReportPacketDropped(RemoteEndpoint, WorldOpcodeNames.GetName(opcode));
                        }
                        else if (_packetGate.TryEnter(out int suppressed))
                        {
                            _logger.LogWarning("[{Endpoint}] {Opcode} over the connection's packet budget; dropped ({Suppressed} more dropped since the last line)", RemoteEndpoint, WorldOpcodeNames.GetName(opcode), suppressed);
                        }

                        return true;
                }
            }

            if (!_opcodes.TryGet(opcode, out OpcodeHandler handler))
            {
                HeuristicsUnhandled(opcode, payload.Length);
                ReportUnhandled(opcode, payload.Length);
                return true;
            }

            if (handler.Stripped)
            {
                // A stripped developer opcode: the payload is never read, nothing is queued and nothing is answered (WorldSession.DevOpcodes.cs).
                DropDevOpcode(opcode, payload.Length);
                return true;
            }

            if (handler.World is not null)
            {
                if (_state is SessionState.LoggingIn or SessionState.InWorld)
                {
                    // The read loop is the only producer (a packet-tools injection takes the same
                    // dispatcher gate, WorldSession.PacketTools.cs), so this check-then-add cannot race
                    // another enqueue; the world thread only ever lowers the counters.
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
                    _worldQueue.Enqueue(new QueuedPacket(handler, payload, (uint)Clock.Milliseconds(), _injectDispatch));
                    World.WakeIfManual();
                }
                else
                {
                    HeuristicsWrongState(opcode, "world opcode before the player is logging in or in the world", WrongStateGrace.LeftWorld);
                }

                return true;
            }

            if (!handler.AllowsState(_state))
            {
                _logger.LogDebug("[{Endpoint}] {Opcode} ignored in state {State}",
                    RemoteEndpoint, WorldOpcodeNames.GetName(opcode), _state);
                HeuristicsWrongState(opcode, "session opcode not allowed in this state", WrongStateGrace.None);
                return true;
            }

            await handler.Session!(this, payload).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (IsMalformed(ex))
        {
            _logger.LogWarning(
                "[{Endpoint}] malformed {Opcode} ({PayloadBytes} bytes, {Reason}, state={State}, account={Account}); disconnecting",
                RemoteEndpoint, WorldOpcodeNames.GetName(opcode), payload.Length, ex.Message, _state, AccountName);
            HeuristicsMalformed(opcode, payload.Length, ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or ArcaneCore.Kernel.Diagnostics.InvariantViolationException) && IsContainedSessionFault(opcode))
        {
            // Character create, delete or login threw at the character screen: answer with its failure code
            // (WorldSession.HandlerFaults.cs), counted against the storm limit. Any other session-handler fault
            // still leaves the session.
            return ContainSessionHandlerFault(opcode, ex);
        }
    }

    private void Release(QueuedPacket packet)
    {
        // The bound in DispatchAsync is only as good as this accounting: every dequeue releases exactly
        // what its enqueue added, so neither counter can go below zero. A negative value would turn the
        // queue bound off for this session (the comparison would never trip again).
        int packets = Interlocked.Decrement(ref _queuedPackets);
        long bytes = Interlocked.Add(ref _queuedBytes, -packet.Payload.Length);
        Invariant.Check(packets >= 0 && bytes >= 0, $"world queue accounting went negative ({packets} packets, {bytes} bytes) for {RemoteEndpoint}");
    }

    /// <summary>Drop every queued packet, keeping the count and byte accounting exact.</summary>
    private void DiscardQueuedPackets()
    {
        while (_worldQueue.TryDequeue(out QueuedPacket packet))
        {
            Release(packet);
        }
    }

    /// <summary>CMSG_PING: u32 sequence, u32 latency → SMSG_PONG: u32 sequence (vmangos WorldSocket::HandlePing). False when the sequence is missing.</summary>
    private bool TryHandlePing(byte[] payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryReadUInt32(out uint sequence))
        {
            return false;
        }

        // u32 latency: the client's own measure of the round trip (vmangos WorldSocket::HandlePing keeps it in m_latency).
        // A 1.12 client always sends it; a short packet keeps the previous estimate.
        if (reader.TryReadUInt32(out uint latency))
        {
            RecordLatencySample(latency);
        }

        Span<byte> pong = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(pong, sequence);
        Send(WorldOpcode.SmsgPong, pong);
        return true;
    }

    /// <summary>
    /// A packet whose opcode has no handler is dropped and the connection stays open. It is counted per opcode
    /// (<see cref="UnhandledOpcodeTracker"/>, metric <c>arcanecore.net.opcode.unhandled</c>) and the first occurrence in the process is a
    /// Warning, so a missing or unregistered handler shows up in the log instead of the client feature silently doing nothing.
    /// </summary>
    private void ReportUnhandled(WorldOpcode opcode, int length)
    {
        long count = _unhandled.Record(opcode);
        if (count == 1)
        {
            string label = UnhandledOpcodeTracker.LabelOf(opcode);
            string name = label == UnhandledOpcodeTracker.UnknownLabel ? "an unnamed opcode" : label;
            _logger.LogWarning("[{Endpoint}] client sent {Opcode} (0x{Value:X3}) which has no handler; further occurrences are counted (metric {Metric}, .server wiring)",
                RemoteEndpoint, name, (ushort)opcode, UnhandledOpcodeTracker.MetricName);
        }

        _logger.LogDebug("[{Endpoint}] unhandled {Opcode} ({Length} bytes)", RemoteEndpoint, WorldOpcodeNames.GetName(opcode), length);
    }

    private async Task<bool> HandleAuthSessionAsync(byte[] payload)
    {
        // CMSG_AUTH_SESSION (vmangos WorldSocket::HandleAuthSession, build 5875 layout):
        // u32 build, u32 server id, CString account, u32 client seed, u8[20] digest, addon block.
        // Parsed without throwing or copying; a packet that does not fit is malformed and charged.
        if (!_protocol.Auth.TryParseAuthSession(payload, out AuthSessionRequest request))
        {
            _logger.LogWarning("[{Endpoint}] malformed CMSG_AUTH_SESSION; disconnecting", RemoteEndpoint);
            _guard?.RecordAuthFailure(_address);
            return false;
        }

        // Net:Protection:AuthFailureBurstPerIp: an address whose failure budget is spent is refused
        // before the account lookup (no query for a guesser). AUTH_FAILED, then close.
        if (_guard is not null && !_guard.AllowsAuthAttempt(_address))
        {
            SendAuthResponse(AuthResponseCode.Failed);
            return false;
        }

        uint build = request.Build;
        string account = request.Account;
        uint clientSeed = request.ClientSeed;

        if (!ClientProtocols.TryGet(build, out IClientProtocol protocol))
        {
            SendAuthResponse(AuthResponseCode.VersionMismatch); // a wrong client, not a guess: not charged
            return false;
        }

        IAccountStore accounts = Services.GetRequiredService<IAccountStore>();
        Account? stored = await accounts.FindByUsernameAsync(account).ConfigureAwait(false);
        if (stored?.SessionKey is null)
        {
            _guard?.RecordAuthFailure(_address);
            SendAuthResponse(AuthResponseCode.UnknownAccount);
            return false;
        }

        // The stored K is the 40-byte SRP6 interleave the realm wrote (account.sessionkey); the digest below
        // and the header cipher are both keyed with it. Another length is a damaged or foreign row: refuse the
        // login rather than key the cipher with it (the client would then fail to decrypt every header).
        if (!Invariant.Check(stored.SessionKey.Length == WowSrp6.SessionKeyLength, $"account {stored.Id} holds a session key of {stored.SessionKey.Length} bytes"))
        {
            SendAuthResponse(AuthResponseCode.Failed);
            return false;
        }

        // digest = SHA1(account, u32 0, clientSeed, serverSeed, K) (vmangos WorldSocket::HandleAuthSession)
        byte[] expected = WowAuthDigest.Compute(account, clientSeed, _serverSeed, stored.SessionKey);
        if (!CryptographicOperations.FixedTimeEquals(expected, request.ClientDigest.Span))
        {
            _guard?.RecordAuthFailure(_address);
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
            _guard?.RecordAuthFailure(_address);
            SendAuthResponse(AuthResponseCode.Banned);
            return false;
        }

        // Ban rows and IP bans (live bans): a store error propagates and closes the connection (fail closed).
        // Unlike retail's cached IP list (AccountMgr.cpp:317-327) this reads the rows, so a fresh ban is seen at once.
        IBanStore? bans = Services.GetService<IBanStore>();
        if (bans is not null && await IsBannedAsync(bans, stored.Id, _kick.Token).ConfigureAwait(false))
        {
            _logger.LogInformation("[{Endpoint}] refused world login for banned account or address", RemoteEndpoint);
            _guard?.RecordAuthFailure(_address);
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
            || (bans is not null && await IsBannedAsync(bans, stored.Id, _kick.Token).ConfigureAwait(false)))
        {
            _logger.LogInformation("[{Endpoint}] '{Account}' was banned while authenticating; refusing", RemoteEndpoint, account);
            _guard?.RecordAuthFailure(_address);
            SendAuthResponse(AuthResponseCode.Banned); // still plain: the header cipher is not initialised yet
            return false;
        }

        // Only now does the session become usable: header encryption on and the character screen reachable.
        // It may have been kicked (a live ban event) or superseded by a newer login while it was registered
        // but not yet authenticated: Kick() only cancels the kick token (the state becomes Closed later, in Close()),
        // so the token and claim currency are what must be tested.
        lock (_sendLock)
        {
            if (_state == SessionState.Closed || _kick.IsCancellationRequested || !_registry.IsCurrent(this))
            {
                return false;
            }

            _protocol = protocol;
            _crypt.Initialize(stored.SessionKey);
            _state = SessionState.CharacterSelect;
        }

        _logger.LogInformation("[{Endpoint}] '{Account}' authenticated", RemoteEndpoint, account);

        SendAuthResponse(AuthResponseCode.Ok);
        Send(WorldOpcode.SmsgAddonInfo, AddonInfo.BuildResponse(request.AddonBlock.Span));
        Services.GetService<Warden.WardenFeature>()?.OnAuthenticated(this, request.Build, stored.SessionKey); // vmangos WorldSession::InitWarden
        return true;
    }

    /// <summary>An account ban row in force, or an IP ban on this connection's address (WorldSocket.cpp:339).</summary>
    private async Task<bool> IsBannedAsync(IBanStore bans, int accountId, CancellationToken cancellationToken)
    {
        if (await bans.GetActiveAccountBanAsync(accountId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return true;
        }

        return RemoteAddress is { } address && await bans.GetActiveIpBanAsync(address, cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// SMSG_AUTH_RESPONSE. AUTH_OK carries u32 billing_time, u8 billing_flags, u32 billing_rested after the result
    /// (wow_messages smsg_auth_response.wowm; vmangos World.cpp:324-333; mangos-classic WorldSession::SendAuthOk), all
    /// zero here; every failure code is the bare result byte.
    /// </summary>
    private void SendAuthResponse(AuthResponseCode code) =>
        Send(WorldOpcode.SmsgAuthResponse, _protocol.Auth.AuthResponse(code));

    // --- teardown ------------------------------------------------------------------

    /// <summary>
    /// Wait for the writer to flush what is queued. A client that stopped reading parks the writer
    /// inside WriteAsync forever, so after the drain bound (World:WriterDrainGrace, or
    /// <see cref="WorldSessionOptions.DefaultWriterDrainBound"/> when that is zero) the write is
    /// cancelled and the stream disposed; that releases the socket, the DI scope and the queued frames.
    /// There is no unbounded wait (security finding S2): vmangos does not wait at all, its CloseSocket
    /// shuts the socket down and closes it at once (AsyncSocket_windows.cpp:318-329).
    /// </summary>
    private async Task DrainWriterAsync(Task writer)
    {
        TimeSpan bound = _options.EffectiveWriterDrainBound;
        if (await CompletesWithinAsync(writer, bound).ConfigureAwait(false))
        {
            return;
        }

        _logger.LogWarning("[{Endpoint}] writer did not drain within {Grace}; tearing the connection down", RemoteEndpoint, bound);
        _writerAbort.Cancel();
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // the socket is already gone
        }

        if (!await CompletesWithinAsync(writer, bound).ConfigureAwait(false))
        {
            _logger.LogError("[{Endpoint}] writer ignored cancellation and stream disposal; abandoning it", RemoteEndpoint);
        }
    }

    /// <summary>Await <paramref name="writer"/> for at most <paramref name="bound"/>; its own fault still propagates.</summary>
    private static async Task<bool> CompletesWithinAsync(Task writer, TimeSpan bound)
    {
        try
        {
            await writer.WaitAsync(bound).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) when (!writer.IsCompleted)
        {
            return false;
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
            Volatile.Write(ref _closedAtTick, Environment.TickCount64);
            _packetCapture?.Dispose();
            _packetCapture = null;
            _outbound.Writer.TryComplete();
        }

        _registry.Unregister(this);
        DiscardQueuedPackets(); // a closed session retains nothing
        EndTaps();

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
            Kick("the socket is gone"); // stop reading too
        }
    }

    private readonly record struct QueuedPacket(OpcodeHandler Handler, byte[] Payload, uint ReceivedMs, bool Injected = false);
}

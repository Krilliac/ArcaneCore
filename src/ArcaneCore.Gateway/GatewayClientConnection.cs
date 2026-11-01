using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Cluster.Auxiliary;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Kernel.Net;
using ArcaneCore.Protocol;
using ArcaneCore.Protocol.Versioning;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Net;
using ArcaneCore.World.Net.Versioning;
using ArcaneCore.World.Warden;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Gateway;

/// <summary>
/// One client socket on the gateway (Tier 3a passthrough). Owns exactly what the design gives the gateway: framing and the
/// 1.12 header cipher (one writer, under a lock, in queue order), SMSG_AUTH_CHALLENGE and the CMSG_AUTH_SESSION digest check
/// against the session key the realm stored (the same checks and plain-text refusals as <c>WorldSession.HandleAuthSessionAsync</c>),
/// PING/PONG in any state, the one-connection-per-account claim and the outbound byte bound. After the worker accepts the
/// session every other opcode is forwarded, decrypted, through the <see cref="GatewaySessionLinkClient"/>, and every server
/// packet the worker sends is encrypted and written here.
/// </summary>
public sealed class GatewayClientConnection : IGatewaySessionSink, IGatewayTransferSession
{
    private readonly GatewayServer _server;
    private readonly TcpClient _client;
    private readonly NetGuard _guard;
    private readonly ILogger _logger;
    private readonly IpKey? _address;
    private readonly Lock _sendLock = new();
    private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _kick = new();
    private readonly IWorldHeaderCrypt _crypt = ClientProtocols.PreAuth.CreateHeaderCrypt();
    private IClientProtocol _protocol = ClientProtocols.PreAuth;
    private NetworkStream? _stream;
    private long _outboundBytes;
    private uint _serverSeed;
    private volatile ConnectionState _state = ConnectionState.Connected;
    // True from sending SMSG_NEW_WORLD until the read loop parses the client's MSG_MOVE_WORLDPORT_ACK: bad-header diagnostics only.
    private volatile bool _worldportPending;

    /// <summary>The gateway clock reading when the frame being dispatched was fully read. Written and read by the read loop only.</summary>
    private uint _packetReceivedMs;
    private volatile GatewaySessionLinkClient? _link;
    private long _generation;
    private int _accountId;
    private string _accountName = string.Empty;
    private uint _build;

    // Login redirect: the CMSG_PLAYER_LOGIN forwarded last (kept to re-send to the map owner), whether this connection was moved already (once only:
    // a second redirect would be a routing loop), and the client packets held while the move is in flight (non-null exactly then; guarded by _redirectGate).
    private LoginPacket? _loginPacket;
    private int _redirected;
    private readonly Lock _redirectGate = new();
    private List<HeldPacket>? _redirectHeld;
    // Whole-World handoff: true while the packets of the freeze are held for the handoff (the hold lives in _redirectHeld, guarded by _redirectGate), and whether
    // the character is in the world now (SMSG_LOGIN_VERIFY_WORLD seen, no SMSG_LOGOUT_COMPLETE since): only then does the promoted world have a player to load.
    private bool _handoffHolding;
    // Where the current handoff hold began (guarded by _redirectGate): the link the connection forwarded on, and the worker slot that asked for the hold (null when the hold
    // began inside HandOffAsync). A release keys on these, not on the link the connection forwards on now, which a per-player transfer or a redirect may have changed.
    private GatewaySessionLinkClient? _handoffHoldLink;
    private object? _handoffHoldOwner;
    // True once the source's saves are durable (the handoff reached Flushed, guarded by _redirectGate): from then on the hold survives the loss of the source's link, because the
    // promoted world loads the character from those saves and the held input follows it. Before that the loss of the link ends the session (the character's state is not safe).
    private bool _handoffFlushed;
    private volatile bool _inWorld;

    /// <summary>CMSG_PLAYER_LOGIN was forwarded and the worker has not answered it yet (neither SMSG_LOGIN_VERIFY_WORLD nor SMSG_CHARACTER_LOGIN_FAILED).</summary>
    private volatile bool _loginPending;
    private PendingAuth? _pending;
    private string? _kickReason;
    private GatewayWardenSession? _warden;

    private enum ConnectionState
    {
        Connected,
        Authenticating,
        Forwarding,
        Closed,
    }

    /// <summary>Most client packets held while a login redirect is in flight; a client past it is disconnected.</summary>
    private const int MaxHeldPackets = 256;

    private static readonly TimeSpan RedirectTimeout = TimeSpan.FromSeconds(30);

    private sealed record LoginPacket(byte[] Payload, uint ReceivedMs);

    private sealed record HeldPacket(WorldOpcode Opcode, byte[] Payload, uint ReceivedMs);

    private sealed record PendingAuth(IClientProtocol Protocol, byte[] SessionKey, byte[] AddonBlock, string Account, uint Build, AccountSecurity Security);

    internal GatewayClientConnection(GatewayServer server, TcpClient client, NetGuard guard, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(logger);
        _server = server;
        _client = client;
        _guard = guard;
        _logger = logger;
        RemoteEndpoint = SafeRemoteEndpoint(client);
        _address = IpKey.TryParse(RemoteEndpoint, out IpKey parsed) ? parsed : null;
    }

    /// <summary>The client's own address ("ip:port"); forwarded to the worker for per-IP guards.</summary>
    public string RemoteEndpoint { get; }

    /// <summary>The account once authenticated (0 before).</summary>
    public int AccountId => Volatile.Read(ref _accountId);

    /// <summary>The gateway generation of the forwarded session (0 before the worker accepted it).</summary>
    public long SessionGeneration => Interlocked.Read(ref _generation);

    /// <summary>Why the connection was closed by the gateway, if it was.</summary>
    public string? KickReason => Volatile.Read(ref _kickReason);

    public void Kick(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Interlocked.CompareExchange(ref _kickReason, reason, null);
        try
        {
            _kick.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // torn down
        }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        using TcpClient client = _client;
        client.NoDelay = true;
        _stream = client.GetStream();
        Task writer = Task.Run(RunWriterAsync, CancellationToken.None);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _kick.Token);
        TimeSpan preAuth = _server.Options.PreAuthTimeout;
        using Timer? preAuthTimer = preAuth > TimeSpan.Zero
            ? new Timer(_ => { if (_state == ConnectionState.Connected) { Kick("no authentication within the pre-auth timeout"); } }, null, preAuth, Timeout.InfiniteTimeSpan)
            : null;
        _logger.LogInformation("[{Endpoint}] connected to the gateway", RemoteEndpoint);
        try
        {
            _serverSeed = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
            Span<byte> challenge = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(challenge, _serverSeed);
            Send(WorldOpcode.SmsgAuthChallenge, challenge);
            await ReadLoopAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException or ObjectDisposedException or SocketException)
        {
            // disconnect, kick or shutdown
        }
        finally
        {
            Close();
            await DrainWriterAsync(writer).ConfigureAwait(false);
            _kick.Dispose();
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        NetworkStream stream = _stream!;
        byte[] header = new byte[_crypt.IncomingHeaderLength];
        byte[] cipherHeader = new byte[header.Length]; // the header as received, kept for the bad-header diagnostics
        var trail = new ClientFrameTrail();
        NetProtectionOptions protection = _guard.Options;
        using var deadline = new ReadDeadline(token, protection.FrameReadTimeout);
        while (true)
        {
            int read = await stream.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            deadline.Arm();
            byte[] payload;
            uint rawOpcode;
            bool cipherEngaged;
            try
            {
                await stream.ReadExactlyAsync(header.AsMemory(1), deadline.Token).ConfigureAwait(false);
                header.CopyTo(cipherHeader, 0);
                (byte Index, byte Previous) position = _crypt.ReceivePosition;
                cipherEngaged = _crypt.IsInitialized;
                _crypt.DecryptHeader(header);
                if (!WorldFrameCodec.TryParseClientHeader(header, out ushort size, out rawOpcode))
                {
                    LogBadHeader(size, rawOpcode, cipherHeader, header, position, trail);
                    return;
                }

                payload = size > 4 ? new byte[size - 4] : [];
                if (payload.Length > 0)
                {
                    await stream.ReadExactlyAsync(payload, deadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (deadline.Expired)
            {
                _guard.ReportFrameTimeout(RemoteEndpoint);
                return;
            }

            deadline.Disarm();
            _packetReceivedMs = (uint)Clock.Milliseconds(); // stamped on the frame, forwarded to the worker for the movement anticheat
            ArcaneCore.Kernel.Ops.Metrics.ArcaneMeters.PacketIn(header.Length + payload.Length);
            WorldOpcode opcode = _protocol.Opcodes.FromWire(rawOpcode);
            trail.Record(opcode, header.Length + payload.Length, Clock.Milliseconds(), cipherEngaged);
            if (opcode == WorldOpcode.MsgMoveWorldportAck)
            {
                _worldportPending = false;
            }

            if (!await DispatchAsync(opcode, payload, token).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>One WARN for a rejected client header (see <see cref="BadClientHeader"/>). Read loop only.</summary>
    private void LogBadHeader(ushort size, uint rawOpcode, ReadOnlySpan<byte> cipherHeader, ReadOnlySpan<byte> plainHeader, (byte Index, byte Previous) position, ClientFrameTrail trail)
        => _logger.LogWarning("{Diagnostic}", BadClientHeader.FormatWarning(RemoteEndpoint, size, rawOpcode, cipherHeader, plainHeader, position, trail, Clock.Milliseconds(), _state.ToString(), _worldportPending));

    private async Task<bool> DispatchAsync(WorldOpcode opcode, byte[] payload, CancellationToken token)
    {
        if (opcode == WorldOpcode.CmsgPing)
        {
            // Answered here in any state (vmangos WorldSocket::HandlePing); forwarded once the session is open so the
            // worker still folds the client's latency into the session (the anticheat slack reads it).
            if (payload.Length < 4)
            {
                _logger.LogWarning("[{Endpoint}] malformed CMSG_PING; disconnecting", RemoteEndpoint);
                return false;
            }

            Send(WorldOpcode.SmsgPong, payload.AsSpan(0, 4));
            return _state != ConnectionState.Forwarding || Forward(opcode, payload);
        }

        switch (_state)
        {
            case ConnectionState.Connected:
                if (opcode == WorldOpcode.CmsgAuthSession)
                {
                    return await AuthenticateAsync(payload, token).ConfigureAwait(false);
                }

                _logger.LogWarning("[{Endpoint}] {Opcode} before authentication; disconnecting", RemoteEndpoint, WorldOpcodeNames.GetName(opcode));
                return false;
            case ConnectionState.Forwarding:
                if (opcode == WorldOpcode.CmsgWardenData && Volatile.Read(ref _warden) is { } warden)
                {
                    // The gateway owns this session's Warden: the reply is decrypted and checked here, never forwarded.
                    warden.Handle(payload);
                    return true;
                }

                return Forward(opcode, payload);
            default:
                return false;
        }
    }

    private bool Forward(WorldOpcode opcode, byte[] payload)
    {
        if (opcode == WorldOpcode.CmsgPlayerLogin && payload.Length <= GatewaySessionWire.MaxAdoptPacketBytes)
        {
            Volatile.Write(ref _loginPacket, new LoginPacket(payload, _packetReceivedMs));
            _loginPending = true;
        }

        // A staged cross-worker transfer holds the packets of the freeze; a generation the session left is dropped (GatewayTransferRouter).
        switch (_server.Transfers.Intercept(SessionGeneration, opcode, payload, _packetReceivedMs))
        {
            case GatewayTransferVerdict.Held:
            case GatewayTransferVerdict.Drop:
                return true;
            case GatewayTransferVerdict.Overflow:
                _logger.LogWarning("[{Endpoint}] more input than the transfer buffer holds while the character moved; disconnecting", RemoteEndpoint);
                return false;
        }

        lock (_redirectGate)
        {
            if (_redirectHeld is { } held)
            {
                if (held.Count >= MaxHeldPackets)
                {
                    _logger.LogWarning("[{Endpoint}] more than {Limit} packets while the login redirect ran; disconnecting", RemoteEndpoint, MaxHeldPackets);
                    return false;
                }

                held.Add(new HeldPacket(opcode, payload, _packetReceivedMs));
                return true;
            }
        }

        if (_link is { } link && link.SendClientPacket(SessionGeneration, opcode, payload, _packetReceivedMs))
        {
            GatewayMeters.CountForwarded(_server.Options.NodeId, "to_worker", payload.Length);
            return true;
        }

        _logger.LogInformation("[{Endpoint}] the worker link is gone; disconnecting", RemoteEndpoint);
        return false;
    }

    private async Task<bool> AuthenticateAsync(byte[] payload, CancellationToken token)
    {
        if (!_protocol.Auth.TryParseAuthSession(payload, out AuthSessionRequest request))
        {
            _logger.LogWarning("[{Endpoint}] malformed CMSG_AUTH_SESSION; disconnecting", RemoteEndpoint);
            _guard.RecordAuthFailure(_address);
            return false;
        }

        if (!_guard.AllowsAuthAttempt(_address))
        {
            SendAuthResponse(AuthResponseCode.Failed);
            return false;
        }

        if (_server.Warden is { IsReady: false } unready)
        {
            // Warden is on but unusable here: a client must not get an unscanned session through this gateway.
            _logger.LogWarning("[{Endpoint}] Warden is not ready on this gateway ({Reason}); refusing '{Account}' with AUTH_UNAVAILABLE", RemoteEndpoint, unready.NotReadyReason, request.Account);
            SendAuthResponse(AuthResponseCode.Unavailable);
            return false;
        }

        if (!ClientProtocols.TryGet(request.Build, out IClientProtocol protocol))
        {
            SendAuthResponse(AuthResponseCode.VersionMismatch);
            return false;
        }

        Account? stored;
        bool banned;
        await using (AsyncServiceScope scope = _server.Scopes.CreateAsyncScope())
        {
            stored = await scope.ServiceProvider.GetRequiredService<IAccountStore>().FindByUsernameAsync(request.Account, token).ConfigureAwait(false);
            if (stored?.SessionKey is null)
            {
                _guard.RecordAuthFailure(_address);
                SendAuthResponse(AuthResponseCode.UnknownAccount);
                return false;
            }

            if (!Invariant.Check(stored.SessionKey.Length == WowSrp6.SessionKeyLength, $"account {stored.Id} holds a session key of {stored.SessionKey.Length} bytes"))
            {
                SendAuthResponse(AuthResponseCode.Failed);
                return false;
            }

            byte[] expected = WowAuthDigest.Compute(request.Account, request.ClientSeed, _serverSeed, stored.SessionKey);
            if (!CryptographicOperations.FixedTimeEquals(expected, request.ClientDigest.Span))
            {
                _guard.RecordAuthFailure(_address);
                SendAuthResponse(AuthResponseCode.Failed);
                return false;
            }

            if (stored.Status != AccountStatus.Active)
            {
                _logger.LogInformation("[{Endpoint}] refused gateway login for {Status} account", RemoteEndpoint, stored.Status);
                _guard.RecordAuthFailure(_address);
                SendAuthResponse(AuthResponseCode.Banned);
                return false;
            }

            IBanStore? bans = scope.ServiceProvider.GetService<IBanStore>();
            banned = bans is not null && (await bans.GetActiveAccountBanAsync(stored.Id, token).ConfigureAwait(false) is not null
                || (AccountBanEvaluator.AddressOfEndpoint(RemoteEndpoint) is { } address && await bans.GetActiveIpBanAsync(address, token).ConfigureAwait(false) is not null));
        }

        if (banned)
        {
            _logger.LogInformation("[{Endpoint}] refused gateway login for banned account or address", RemoteEndpoint);
            _guard.RecordAuthFailure(_address);
            SendAuthResponse(AuthResponseCode.Banned);
            return false;
        }

        GatewaySessionLinkClient? link = _server.AcquireLink();
        if (link is null)
        {
            _logger.LogWarning("[{Endpoint}] no worker link; refusing '{Account}' with AUTH_UNAVAILABLE", RemoteEndpoint, request.Account);
            GatewayMeters.CountSessionOpen(_server.Options.NodeId, "no_link");
            SendAuthResponse(AuthResponseCode.Unavailable);
            return false;
        }

        Volatile.Write(ref _accountId, stored.Id);
        _server.Claims.Claim(stored.Id, this);
        if (_kick.IsCancellationRequested)
        {
            return false;
        }

        _link = link;
        _accountName = request.Account;
        _build = request.Build;
        _pending = new PendingAuth(protocol, stored.SessionKey, request.AddonBlock.ToArray(), request.Account, request.Build, stored.Security);
        _state = ConnectionState.Authenticating;
        // The link takes the generation and queues the open under one lock: concurrent logins reach the worker in generation order
        // (it refuses an open not above the last one it saw).
        long openStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        GatewaySessionOpenReply reply = await link.OpenAsync(
            _server.NextGeneration,
            generation =>
            {
                Interlocked.Exchange(ref _generation, generation);
                return new GatewaySessionOpenRequest(generation, stored.Id, request.Account, request.Build, RemoteEndpoint);
            },
            this,
            token).ConfigureAwait(false);
        _pending = null;
        GatewayMeters.RecordLinkLatency(_server.Options.NodeId, link.WorkerEndPoint, System.Diagnostics.Stopwatch.GetElapsedTime(openStarted).TotalMilliseconds);
        GatewayMeters.CountSessionOpen(_server.Options.NodeId, reply.Accepted ? "accepted" : "refused");
        if (!reply.Accepted)
        {
            _logger.LogInformation("[{Endpoint}] worker refused '{Account}': {Code} ({Reason})", RemoteEndpoint, request.Account, reply.RefusalCode, reply.Reason);
            Interlocked.Exchange(ref _generation, 0);
            SendAuthResponse(reply.RefusalCode);
            return false;
        }

        return _state == ConnectionState.Forwarding;
    }

    /// <summary>The worker accepted: engage the cipher and answer the client, before the link delivers anything else of the session.</summary>
    void IGatewaySessionSink.OnOpened(GatewaySessionOpenReply reply)
    {
        if (Volatile.Read(ref _redirected) != 0)
        {
            // The target worker adopted a redirected session: the client was answered and the cipher engaged at the first open.
            return;
        }

        PendingAuth pending = _pending ?? throw new InvalidOperationException("session opened without a pending authentication");
        lock (_sendLock)
        {
            if (_state != ConnectionState.Authenticating)
            {
                return;
            }

            _protocol = pending.Protocol;
            _crypt.Initialize(pending.SessionKey);
            _state = ConnectionState.Forwarding;
        }

        _logger.LogInformation("[{Endpoint}] '{Account}' authenticated; forwarding to the worker (generation {Generation}, epoch {Epoch})",
            RemoteEndpoint, pending.Account, SessionGeneration, reply.OwnerEpoch);
        SendAuthResponse(AuthResponseCode.Ok);
        Send(WorldOpcode.SmsgAddonInfo, AddonInfo.BuildResponse(pending.AddonBlock, _logger, RemoteEndpoint));
        StartWarden(pending);
    }

    /// <summary>Start this session's Warden (when the gateway runs one and the session is scanned); MODULE_USE follows SMSG_ADDON_INFO.</summary>
    private void StartWarden(PendingAuth pending)
    {
        if (_server.Warden is not { } gatewayWarden)
        {
            return;
        }

        GatewayWardenSession? session = gatewayWarden.Begin(pending.Build, pending.Security, pending.Account, pending.SessionKey,
            body => Send(WorldOpcode.SmsgWardenData, body), OnWardenVerdict);
        if (session is null)
        {
            return;
        }

        Volatile.Write(ref _warden, session);
        if (_state == ConnectionState.Closed)
        {
            // The client left while Warden started: Close already ran and saw no session.
            Interlocked.Exchange(ref _warden, null)?.Dispose();
        }
    }

    /// <summary>
    /// A Warden batch called for Kick or Ban: report it to the worker (which applies a ban) and disconnect the client at once, as the
    /// world's own Warden does. The frame is queued before the close, so the worker sees the verdict while the session is still open.
    /// </summary>
    private void OnWardenVerdict(WardenAction action, string reason, uint? checkId)
    {
        long generation = SessionGeneration;
        if (generation != 0 && _link is { } link && _server.Warden is { } gatewayWarden)
        {
            ClusterWardenAction wire = action == WardenAction.Ban ? ClusterWardenAction.Ban : ClusterWardenAction.Kick;
            if (!link.SendWardenVerdict(generation, (ushort)Math.Min(checkId ?? 0u, (uint)ushort.MaxValue), wire, reason, gatewayWarden.Posture))
            {
                _logger.LogWarning("[{Endpoint}] Warden {Action} verdict ({Reason}) could not be sent: the session is closed or the worker link is down",
                    RemoteEndpoint, wire, reason);
            }
        }

        Kick("Warden: " + reason);
    }

    /// <summary>The open request of a session on another worker for this client (cross-worker transfer): the authenticated account, name, build and endpoint.</summary>
    internal GatewaySessionOpenRequest CreateOpenRequest(long generation) => new(generation, AccountId, _accountName, _build, RemoteEndpoint);

    bool IGatewayTransferSession.IsOpen => _state == ConnectionState.Forwarding;

    void IGatewayTransferSession.SendToClient(WorldOpcode opcode, ReadOnlySpan<byte> payload) => Send(opcode, payload);

    bool IGatewayTransferSession.ForwardToWorker(WorldOpcode opcode, ReadOnlySpan<byte> payload, uint receivedMs) =>
        _link is { } link && link.SendClientPacket(SessionGeneration, opcode, payload, receivedMs);

    /// <summary>
    /// Switches this connection to the session the transfer router opened on the target worker and closes the session on the old link. Same shape as the
    /// end of <see cref="AdoptAsync"/>: link and generation change together under the redirect gate; a client that left meanwhile closes the new session.
    /// </summary>
    void IGatewayTransferSession.Repoint(GatewayTransferTarget target)
    {
        GatewaySessionLinkClient next = (target.Link as GatewayLinkClientAdapter)?.Client
            ?? throw new InvalidOperationException("the transfer target link is not a gateway worker link");
        GatewaySessionLinkClient? old;
        long oldGeneration;
        lock (_redirectGate)
        {
            old = _link;
            _link = next;
            oldGeneration = Interlocked.Exchange(ref _generation, target.Generation);
        }

        old?.CloseSession(oldGeneration, ClusterSessionDetachReason.Transferred);
        if (_state == ConnectionState.Closed)
        {
            // The client left while the session moved: Close ran against the old link and saw the old generation.
            long left = Interlocked.Exchange(ref _generation, 0);
            if (left != 0)
            {
                next.CloseSession(left, ClusterSessionDetachReason.ClientDisconnect);
            }
        }
    }

    void IGatewaySessionSink.OnServerPacket(WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        GatewayMeters.CountForwarded(_server.Options.NodeId, "to_client", payload.Length);
        TrackWorldState(opcode);
        Send(opcode, payload);
    }

    /// <summary>Whether the character is in the world, from the worker's own login and logout answers.</summary>
    private void TrackWorldState(WorldOpcode opcode)
    {
        if (opcode == WorldOpcode.SmsgLoginVerifyWorld)
        {
            _inWorld = true;
            _loginPending = false;
        }
        else if (opcode == WorldOpcode.SmsgCharacterLoginFailed)
        {
            _loginPending = false;
        }
        else if (opcode == WorldOpcode.SmsgLogoutComplete)
        {
            _inWorld = false;
        }
    }

    /// <summary>
    /// Whole-World handoff, step 1 (the source froze): start holding this connection's client packets instead of forwarding them to <paramref name="link"/>, so
    /// the input sent during the freeze reaches the promoted world. False when the connection is not forwarding on that link or a hold or redirect is already in place.
    /// </summary>
    internal bool BeginHandoffHold(GatewaySessionLinkClient link, object? owner = null)
    {
        lock (_redirectGate)
        {
            if (!ReferenceEquals(_link, link) || _state != ConnectionState.Forwarding || _redirectHeld is not null)
            {
                return false;
            }

            _redirectHeld = [];
            _handoffHolding = true;
            _handoffFlushed = false;
            _handoffHoldLink = link;
            _handoffHoldOwner = owner;
            return true;
        }
    }

    /// <summary>
    /// Whole-World handoff, after Flushed: the source's saves are durable, so this connection's hold now survives the loss of the source's link (the session on the source
    /// closing with LinkLost no longer disconnects the client; the handoff's reroute moves it to the promoted world, or an abort kicks it when the link is gone). True when the connection holds
    /// for the handoff asked for by <paramref name="owner"/> (the worker slot) and is marked now.
    /// </summary>
    internal bool MarkHandoffFlushed(object owner)
    {
        lock (_redirectGate)
        {
            if (!_handoffHolding || !ReferenceEquals(_handoffHoldOwner, owner))
            {
                return false;
            }

            _handoffFlushed = true;
            return true;
        }
    }

    /// <summary>
    /// The link a flushed hold of <paramref name="owner"/> began on (it may have been lost since), or null when this connection holds no flushed hold of that worker slot.
    /// The reroute of a handoff whose source died moves the connection from this link.
    /// </summary>
    internal GatewaySessionLinkClient? FlushedHoldLinkOf(object owner)
    {
        lock (_redirectGate)
        {
            return _handoffHolding && _handoffFlushed && ReferenceEquals(_handoffHoldOwner, owner) && _state == ConnectionState.Forwarding ? _handoffHoldLink : null;
        }
    }

    /// <summary>
    /// The handoff aborted: replay the held packets to the worker this connection is still on, in order, and forward again. A connection that does not hold is untouched.
    /// With <paramref name="holdLink"/> or <paramref name="holdOwner"/> given, only a hold that began on that link or was asked for by that worker slot is released
    /// (whatever link the connection forwards on now); with both null every hold is released.
    /// </summary>
    internal void EndHandoffHold(GatewaySessionLinkClient? holdLink = null, object? holdOwner = null)
    {
        bool delivered = true;
        bool wasFlushed = false;
        lock (_redirectGate)
        {
            if (!_handoffHolding)
            {
                return;
            }

            if ((holdLink is not null || holdOwner is not null)
                && !(holdLink is not null && ReferenceEquals(_handoffHoldLink, holdLink))
                && !(holdOwner is not null && ReferenceEquals(_handoffHoldOwner, holdOwner)))
            {
                return;
            }

            wasFlushed = _handoffFlushed;
            _handoffHolding = false;
            _handoffFlushed = false;
            _handoffHoldLink = null;
            _handoffHoldOwner = null;
            List<HeldPacket>? held = _redirectHeld;
            _redirectHeld = null;
            if (held is not null)
            {
                long generation = SessionGeneration;
                foreach (HeldPacket packet in held)
                {
                    if (_link is not { } link || !link.SendClientPacket(generation, packet.Opcode, packet.Payload, packet.ReceivedMs))
                    {
                        delivered = false;
                        break;
                    }
                }
            }

            if (wasFlushed && _state == ConnectionState.Forwarding && _link is not { IsConnected: true })
            {
                delivered = false; // a flushed hold survived the loss of its link; the abort leaves nothing to forward to
            }
        }

        if (!delivered)
        {
            Kick("the worker link is gone");
        }
    }

    /// <summary>True while this connection forwards on <paramref name="link"/>.</summary>
    internal bool IsForwardingOn(GatewaySessionLinkClient link) => _state == ConnectionState.Forwarding && ReferenceEquals(_link, link);

    /// <summary>
    /// Whole-World handoff, step 2 (B is promoted and its link is up): re-point this connection from <paramref name="from"/> (the retiring world's link) to
    /// <paramref name="to"/> without disconnecting the client. A character in the world is adopted by B with its buffered CMSG_PLAYER_LOGIN (B loads the save
    /// A captured at the freeze); B's SMSG_LOGIN_VERIFY_WORLD (same map and position as the save: u32 map, x, y, z, o) reaches the client as SMSG_TRANSFER_PENDING
    /// followed by SMSG_NEW_WORLD, the same-map world port the 1.12.1 client accepts (design section 5.5), and the client's MSG_MOVE_WORLDPORT_ACK then reaches B.
    /// A connection still at character select just opens a plain session on B. The packets held since the freeze (and during this call) are replayed to B in order
    /// once it accepted, then the session on A closes. Any failure disconnects the client (the same remedy as a refused login redirect). Returns whether the client moved.
    /// </summary>
    internal async Task<bool> HandOffAsync(GatewaySessionLinkClient from, GatewaySessionLinkClient to, CancellationToken cancellationToken)
    {
        LoginPacket? login = Volatile.Read(ref _loginPacket);
        bool inWorld = _inWorld;

        // A login that A never answered (CMSG_PLAYER_LOGIN sent, no SMSG_LOGIN_VERIFY_WORLD yet) is adopted too, so B answers it; that answer reaches the client
        // as the plain SMSG_LOGIN_VERIFY_WORLD it is waiting for, not as a world port.
        bool adopt = inWorld || _loginPending;
        long oldGeneration;
        lock (_redirectGate)
        {
            if (_state != ConnectionState.Forwarding || !ReferenceEquals(_link, from) || (_redirectHeld is not null && !_handoffHolding))
            {
                return false;
            }

            _redirectHeld ??= [];
            if (!_handoffHolding)
            {
                _handoffHoldLink = from;
                _handoffHoldOwner = null;
                _handoffFlushed = false;
            }

            _handoffHolding = true;
            oldGeneration = SessionGeneration;
        }

        long newGeneration = 0;
        try
        {
            if (adopt && login is null)
            {
                throw new InvalidOperationException("the character is in the world but no login packet was seen");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RedirectTimeout);
            var sink = new HandoffSink(this, TrackWorldState, Send, inWorld);
            GatewaySessionOpenReply reply;
            if (adopt)
            {
                reply = await to.AdoptAsync(
                    _server.NextGeneration,
                    generation =>
                    {
                        newGeneration = generation;
                        return new GatewaySessionAdoptRequest(CreateOpenRequest(generation), (uint)WorldOpcode.CmsgPlayerLogin, login!.Payload, login!.ReceivedMs);
                    },
                    sink,
                    timeout.Token).ConfigureAwait(false);
            }
            else
            {
                reply = await to.OpenAsync(
                    _server.NextGeneration,
                    generation =>
                    {
                        newGeneration = generation;
                        return CreateOpenRequest(generation);
                    },
                    sink,
                    timeout.Token).ConfigureAwait(false);
            }

            GatewayMeters.CountSessionOpen(_server.Options.NodeId, reply.Accepted ? "handoff" : "handoff_refused");
            if (!reply.Accepted)
            {
                _logger.LogInformation("[{Endpoint}] the promoted world refused the handed-off session: {Code} ({Reason})", RemoteEndpoint, reply.RefusalCode, reply.Reason);
                EndHandoff(from, oldGeneration, "the promoted world refused the session");
                return false;
            }

            bool delivered = true;
            lock (_redirectGate)
            {
                _link = to;
                Interlocked.Exchange(ref _generation, newGeneration);
                if (_redirectHeld is { } held)
                {
                    foreach (HeldPacket packet in held)
                    {
                        if (!to.SendClientPacket(newGeneration, packet.Opcode, packet.Payload, packet.ReceivedMs))
                        {
                            delivered = false;
                            break;
                        }
                    }
                }

                _redirectHeld = null;
                _handoffHolding = false;
                _handoffFlushed = false;
                _handoffHoldLink = null;
                _handoffHoldOwner = null;
            }

            from.CloseSession(oldGeneration, ClusterSessionDetachReason.Transferred);
            _logger.LogInformation("[{Endpoint}] session moved to the promoted world (generation {Generation})", RemoteEndpoint, newGeneration);
            if (!delivered)
            {
                Kick("the worker link is gone");
                return false;
            }

            if (_state == ConnectionState.Closed)
            {
                // The client left while the session moved: Close ran against the old link and saw the old generation.
                long left = Interlocked.Exchange(ref _generation, 0);
                if (left != 0)
                {
                    to.CloseSession(left, ClusterSessionDetachReason.ClientDisconnect);
                }

                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not (OperationCanceledException or InvalidOperationException))
            {
                FaultLedger.Shared.Record("cluster.gateway-client", ex);
            }

            if (newGeneration != 0)
            {
                to.CloseSession(newGeneration, ClusterSessionDetachReason.Transferred);
            }

            _logger.LogWarning("[{Endpoint}] world handoff of the session failed ({Error}); closing", RemoteEndpoint, ex.Message);
            EndHandoff(from, oldGeneration, "the world handoff failed");
            return false;
        }
    }

    /// <summary>A handoff move that did not complete: the old session closes, the held packets are dropped and the client is disconnected.</summary>
    private void EndHandoff(GatewaySessionLinkClient from, long oldGeneration, string reason)
    {
        lock (_redirectGate)
        {
            _handoffHolding = false;
            _handoffFlushed = false;
            _handoffHoldLink = null;
            _handoffHoldOwner = null;
        }

        EndRedirect(from, oldGeneration, reason);
    }

    /// <summary>Writes one frame to the client (the connection's encrypted send).</summary>
    internal delegate void ClientFrameWriter(WorldOpcode opcode, ReadOnlySpan<byte> payload);

    /// <summary>
    /// The session on the promoted world, as the client sees it. For a character that was in the world, the first SMSG_LOGIN_VERIFY_WORLD becomes
    /// SMSG_TRANSFER_PENDING (u32 map) + SMSG_NEW_WORLD (the same u32 map and four floats), the pair vmangos sends for a far teleport (Player::ExecuteTeleportFar);
    /// everything else passes unchanged to <paramref name="inner"/> (the connection). <paramref name="trackWorldState"/> and <paramref name="write"/> are the
    /// connection's own state tracker and client writer.
    /// </summary>
    internal sealed class HandoffSink(IGatewaySessionSink inner, Action<WorldOpcode> trackWorldState, ClientFrameWriter write, bool synthesizeWorldPort) : IGatewaySessionSink
    {
        private const int NewWorldPayloadLength = sizeof(uint) + (4 * sizeof(float));

        private bool _synthesized;

        public void OnOpened(GatewaySessionOpenReply reply)
        {
            // The client was answered and the cipher engaged at the first open.
        }

        public void OnServerPacket(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            if (synthesizeWorldPort && !_synthesized && opcode == WorldOpcode.SmsgLoginVerifyWorld && payload.Length == NewWorldPayloadLength)
            {
                _synthesized = true;
                trackWorldState(opcode);
                write(WorldOpcode.SmsgTransferPending, payload[..sizeof(uint)]);
                write(WorldOpcode.SmsgNewWorld, payload);
                return;
            }

            inner.OnServerPacket(opcode, payload);
        }

        public void OnClosed(ClusterSessionDetachReason reason) => inner.OnClosed(reason);

        // The sink stays the session's sink on the promoted world for its whole life: a later login redirect (character select on a map another worker owns)
        // must be resolved by the connection as before the handoff, not refused by the interface default.
        public bool OnRedirect(GatewaySessionRedirect redirect) => inner.OnRedirect(redirect);
    }

    /// <summary>
    /// The login worker found that another worker owns the saved map of the character the client asked for. Resolve that worker through the route table,
    /// hold the client's packets and re-open the session there with the buffered login packet (<see cref="AdoptAsync"/>). False (the connection ends as a
    /// worker close would end it) when no ready worker owns the map, it is the same worker, the connection was moved before, or no login packet was seen.
    /// </summary>
    bool IGatewaySessionSink.OnRedirect(GatewaySessionRedirect redirect)
    {
        GatewaySessionLinkClient? from = _link;
        LoginPacket? login = Volatile.Read(ref _loginPacket);
        GatewaySessionLinkClient? target = _server.FindLinkForMap(redirect.MapId, redirect.InstanceId);
        if (from is null || login is null || target is null || ReferenceEquals(target, from) || redirect.SessionGeneration != SessionGeneration
            || _state != ConnectionState.Forwarding || Interlocked.CompareExchange(ref _redirected, 1, 0) != 0)
        {
            _logger.LogInformation("[{Endpoint}] no worker to redirect to for map {Map} (instance {Instance}); closing", RemoteEndpoint, redirect.MapId, redirect.InstanceId);
            return false;
        }

        lock (_redirectGate)
        {
            _redirectHeld = [];
        }

        _ = Task.Run(() => AdoptAsync(from, target, redirect, login), CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Re-open the session on the map owner and close it on the login worker. The new generation comes from the target link's own allocator lock (opens leave
    /// in generation order), the account, name and build are the ones authenticated here. Only after the target accepted does the login worker's session
    /// close and the held packets follow the login packet; the fenced ownership claim happens on the target only. A refusal, a timeout or a lost link ends the connection.
    /// </summary>
    private async Task AdoptAsync(GatewaySessionLinkClient from, GatewaySessionLinkClient target, GatewaySessionRedirect redirect, LoginPacket login)
    {
        long newGeneration = 0;
        try
        {
            using var timeout = new CancellationTokenSource(RedirectTimeout);
            GatewaySessionOpenReply reply = await target.AdoptAsync(
                _server.NextGeneration,
                generation =>
                {
                    newGeneration = generation;
                    return new GatewaySessionAdoptRequest(
                        new GatewaySessionOpenRequest(generation, AccountId, _accountName, _build, RemoteEndpoint),
                        (uint)WorldOpcode.CmsgPlayerLogin, login.Payload, login.ReceivedMs);
                },
                this,
                timeout.Token).ConfigureAwait(false);
            GatewayMeters.CountSessionOpen(_server.Options.NodeId, reply.Accepted ? "redirected" : "redirect_refused");
            if (!reply.Accepted)
            {
                _logger.LogInformation("[{Endpoint}] the map owner refused the redirected session: {Code} ({Reason})", RemoteEndpoint, reply.RefusalCode, reply.Reason);
                EndRedirect(from, redirect.SessionGeneration, "the map owner refused the redirected session");
                return;
            }

            from.CloseSession(redirect.SessionGeneration, ClusterSessionDetachReason.Transferred);
            bool delivered = true;
            lock (_redirectGate)
            {
                _link = target;
                Interlocked.Exchange(ref _generation, newGeneration);
                if (_redirectHeld is { } held)
                {
                    foreach (HeldPacket packet in held)
                    {
                        if (!target.SendClientPacket(newGeneration, packet.Opcode, packet.Payload, packet.ReceivedMs))
                        {
                            delivered = false;
                            break;
                        }
                    }

                    _redirectHeld = null;
                }
            }

            _logger.LogInformation("[{Endpoint}] session moved to the map owner for map {Map} (generation {Generation})", RemoteEndpoint, redirect.MapId, newGeneration);
            if (!delivered)
            {
                Kick("the worker link is gone");
            }

            if (_state == ConnectionState.Closed)
            {
                // The client left while the session moved: Close ran against the old link and saw the old generation.
                long left = Interlocked.Exchange(ref _generation, 0);
                if (left != 0)
                {
                    target.CloseSession(left, ClusterSessionDetachReason.ClientDisconnect);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not (OperationCanceledException or InvalidOperationException))
            {
                FaultLedger.Shared.Record("cluster.gateway-client", ex);
            }

            _logger.LogWarning("[{Endpoint}] login redirect failed ({Error}); closing", RemoteEndpoint, ex.Message);
            EndRedirect(from, redirect.SessionGeneration, "the login redirect failed");
        }
    }

    /// <summary>A redirect that did not complete: the login worker's session closes, the held packets are dropped and the client is disconnected.</summary>
    private void EndRedirect(GatewaySessionLinkClient from, long oldGeneration, string reason)
    {
        from.CloseSession(oldGeneration, ClusterSessionDetachReason.Transferred);
        lock (_redirectGate)
        {
            _redirectHeld = null;
        }

        Kick(reason);
    }

    void IGatewaySessionSink.OnClosed(ClusterSessionDetachReason reason)
    {
        if (reason == ClusterSessionDetachReason.LinkLost)
        {
            // The source of a whole-World handoff lost its link after Flushed: its saves are durable and the promoted world adopts the character from them, so the client stays
            // connected and the hold goes on. The generation is kept: HandOffAsync closes it on the old link (a no-op there) and replaces it. A lost link before Flushed ends the session.
            lock (_redirectGate)
            {
                if (_handoffHolding && _handoffFlushed && _state == ConnectionState.Forwarding)
                {
                    _logger.LogWarning("[{Endpoint}] the source world's link was lost after the handoff flushed; keeping the client for the promoted world", RemoteEndpoint);
                    return;
                }
            }
        }

        Interlocked.Exchange(ref _generation, 0); // the worker already forgot it: no SessionClose back
        Kick("the worker closed the session (" + reason + ")");
    }

    private void SendAuthResponse(AuthResponseCode code) => Send(WorldOpcode.SmsgAuthResponse, _protocol.Auth.AuthResponse(code));

    /// <summary>Encrypt the header and queue the frame (thread-safe; cipher order equals queue order).</summary>
    private void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > WorldFrameCodec.MaxServerPayload)
        {
            _logger.LogWarning("[{Endpoint}] {Opcode} payload of {Length} bytes exceeds the SMSG size field; dropped", RemoteEndpoint, WorldOpcodeNames.GetName(opcode), payload.Length);
            return;
        }

        if (opcode == WorldOpcode.SmsgNewWorld)
        {
            _worldportPending = true;
        }

        int headerLength = _crypt.OutgoingHeaderLength;
        byte[] frame = new byte[headerLength + payload.Length];
        WorldFrameCodec.WriteServerSize(frame, payload.Length);
        lock (_sendLock)
        {
            if (_state == ConnectionState.Closed)
            {
                return;
            }

            WorldFrameCodec.WriteServerOpcode(frame, _protocol.Opcodes.ToWire(opcode));
            payload.CopyTo(frame.AsSpan(headerLength));
            _crypt.EncryptHeader(frame.AsSpan(0, headerLength));
            if (!_outbound.Writer.TryWrite(frame))
            {
                return;
            }
        }

        ArcaneCore.Kernel.Ops.Metrics.ArcaneMeters.PacketOut(frame.Length);
        if (Interlocked.Add(ref _outboundBytes, frame.Length) > _server.Options.MaxOutboundBytes)
        {
            _logger.LogWarning("[{Endpoint}] outbound queue over {Limit} bytes; disconnecting", RemoteEndpoint, _server.Options.MaxOutboundBytes);
            Kick("outbound queue over the byte limit");
        }
    }

    private void Close()
    {
        lock (_sendLock)
        {
            if (_state == ConnectionState.Closed)
            {
                return;
            }

            _state = ConnectionState.Closed;
            _outbound.Writer.TryComplete();
        }

        Interlocked.Exchange(ref _warden, null)?.Dispose();
        long generation = Interlocked.Exchange(ref _generation, 0);
        if (generation != 0)
        {
            _link?.CloseSession(generation, KickReason is { } reason && reason.StartsWith("superseded", StringComparison.Ordinal)
                ? ClusterSessionDetachReason.SupersededByNewLogin
                : ClusterSessionDetachReason.ClientDisconnect);
        }

        if (AccountId != 0)
        {
            _server.Claims.Release(AccountId, this);
        }

        _logger.LogInformation("[{Endpoint}] gateway connection closed{Reason}", RemoteEndpoint, KickReason is { } why ? " (" + why + ")" : string.Empty);
    }

    private async Task RunWriterAsync()
    {
        NetworkStream stream = _stream!;
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
                            await stream.WriteAsync(buffer.AsMemory(0, used)).ConfigureAwait(false);
                            used = 0;
                        }

                        if (frame.Length > buffer.Length)
                        {
                            await stream.WriteAsync(frame).ConfigureAwait(false);
                            continue;
                        }
                    }

                    frame.CopyTo(buffer, used);
                    used += frame.Length;
                }

                if (used > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, used)).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            Kick("the socket is gone");
        }
    }

    /// <summary>Let the writer flush (a refusal reply) for at most 5 s, then tear the socket down (the world's writer drain bound).</summary>
    private async Task DrainWriterAsync(Task writer)
    {
        try
        {
            await writer.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("[{Endpoint}] writer did not drain; tearing the connection down", RemoteEndpoint);
            _client.Dispose();
            await writer.ConfigureAwait(false);
        }
    }

    private static string SafeRemoteEndpoint(TcpClient client)
    {
        try
        {
            return client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return "unknown";
        }
    }
}

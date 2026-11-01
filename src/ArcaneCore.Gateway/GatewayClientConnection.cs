using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
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
public sealed class GatewayClientConnection : IGatewaySessionSink
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
    private GatewaySessionLinkClient? _link;
    private long _generation;
    private int _accountId;
    private PendingAuth? _pending;
    private string? _kickReason;

    private enum ConnectionState
    {
        Connected,
        Authenticating,
        Forwarding,
        Closed,
    }

    private sealed record PendingAuth(IClientProtocol Protocol, byte[] SessionKey, byte[] AddonBlock, string Account);

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
                return Forward(opcode, payload);
            default:
                return false;
        }
    }

    private bool Forward(WorldOpcode opcode, byte[] payload)
    {
        if (_link is { } link && link.SendClientPacket(SessionGeneration, opcode, payload))
        {
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

        GatewaySessionLinkClient? link = _server.Link;
        if (link is null)
        {
            _logger.LogWarning("[{Endpoint}] no worker link; refusing '{Account}' with AUTH_UNAVAILABLE", RemoteEndpoint, request.Account);
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
        _pending = new PendingAuth(protocol, stored.SessionKey, request.AddonBlock.ToArray(), request.Account);
        _state = ConnectionState.Authenticating;
        // The link takes the generation and queues the open under one lock: concurrent logins reach the worker in generation order
        // (it refuses an open not above the last one it saw).
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
    }

    void IGatewaySessionSink.OnServerPacket(WorldOpcode opcode, ReadOnlySpan<byte> payload) => Send(opcode, payload);

    void IGatewaySessionSink.OnClosed(ClusterSessionDetachReason reason)
    {
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

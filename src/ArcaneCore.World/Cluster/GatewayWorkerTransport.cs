using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Cluster;

/// <summary>
/// Versioned gateway–worker session envelope (<c>docs/CLUSTERING_DESIGN.md</c> §4.2, §10 M15.1).
/// Every proxied packet carries <see cref="SessionGeneration"/>, <see cref="OwnerEpoch"/>, and a monotonic
/// per-session <see cref="Sequence"/> so stale packets after reconnect, transfer, or lease loss are deterministically dropped.
/// </summary>
public readonly record struct ClusterSessionPacket(
    long SessionGeneration,
    int AccountId,
    ulong CharacterGuid,
    long OwnerEpoch,
    long Sequence,
    uint Opcode,
    ReadOnlyMemory<byte> Payload);

/// <summary>Result of attempting to attach a player session to an authoritative worker.</summary>
public readonly record struct ClusterSessionAttachResult(
    bool Accepted,
    long AssignedGeneration,
    long WorkerOwnerEpoch,
    string? RejectionReason);

/// <summary>Reason why a session was detached from a worker.</summary>
public enum ClusterSessionDetachReason : byte
{
    ClientDisconnect = 0,
    Logout = 1,
    SupersededByNewLogin = 2,
    Transferred = 3,
    WorkerDraining = 4,
    OwnerEpochMismatch = 5,

    /// <summary>The worker closed a gateway-backed session (kick, logout to nothing, shutdown): Tier 3a passthrough.</summary>
    WorkerClosed = 6,

    /// <summary>The link carrying the session dropped, or a peer broke the session protocol: Tier 3a passthrough.</summary>
    LinkLost = 7,
}

/// <summary>
/// Transport seam between a session-owning Gateway and an authoritative map Worker (<c>docs/CLUSTERING_DESIGN.md</c> §4).
/// In <see cref="ClusterRole.Combined"/> mode this is backed by <see cref="LocalGatewayWorkerTransport"/> with zero sockets;
/// in split <see cref="ClusterRole.Gateway"/> / <see cref="ClusterRole.Worker"/> mode it runs over framed RPC
/// (<see cref="LoopbackGatewayWorkerClient"/> / <see cref="LoopbackGatewayWorkerHost"/>).
/// </summary>
public interface IGatewayWorkerTransport : IAsyncDisposable
{
    event Action<ClusterSessionPacket>? OutboundPacketReceived;

    event Action<ulong, long, ClusterSessionDetachReason>? SessionDetached;

    ValueTask<ClusterSessionAttachResult> AttachSessionAsync(
        int accountId,
        ulong characterGuid,
        long requestedGeneration,
        long expectedOwnerEpoch,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DetachSessionAsync(
        ulong characterGuid,
        long sessionGeneration,
        ClusterSessionDetachReason reason,
        CancellationToken cancellationToken = default);

    ValueTask<bool> SendToWorkerAsync(
        ClusterSessionPacket packet,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// In-process zero-socket <see cref="IGatewayWorkerTransport"/> that marshals session attach/detach and client opcodes
/// directly onto the authoritative <see cref="WorldRuntime"/> simulation thread while enforcing readiness, drain,
/// <c>SessionGeneration</c>, <c>OwnerEpoch</c>, and monotonic per-session sequence ordering.
/// </summary>
public sealed class LocalGatewayWorkerTransport : IGatewayWorkerTransport
{
    private sealed class AttachedSessionState
    {
        public required int AccountId { get; init; }
        public required ulong CharacterGuid { get; init; }
        public required long SessionGeneration { get; init; }
        public required int GatewayConnectionId { get; init; }
        public long LastInboundSequence;
        public long LastOutboundSequence;
    }

    private readonly WorldRuntime _world;
    private readonly ClusterWorkerReadinessProbe _readiness;
    private readonly Action<LocalGatewayWorkerTransport, ClusterSessionPacket>? _onClientPacket;
    private readonly Dictionary<ulong, AttachedSessionState> _sessions = [];

    public LocalGatewayWorkerTransport(
        WorldRuntime world,
        ClusterWorkerReadinessProbe readiness,
        Action<LocalGatewayWorkerTransport, ClusterSessionPacket>? onClientPacket = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(readiness);
        _world = world;
        _readiness = readiness;
        _onClientPacket = onClientPacket;
    }

    public event Action<ClusterSessionPacket>? OutboundPacketReceived;

    internal event Action<int, ClusterSessionPacket>? OutboundPacketForConnectionReceived;

    public event Action<ulong, long, ClusterSessionDetachReason>? SessionDetached;

    internal event Action<int, ulong, long, ClusterSessionDetachReason>? SessionDetachedForConnection;

    public ValueTask<ClusterSessionAttachResult> AttachSessionAsync(
        int accountId,
        ulong characterGuid,
        long requestedGeneration,
        long expectedOwnerEpoch,
        CancellationToken cancellationToken = default) =>
        AttachSessionCoreAsync( gatewayConnectionId: 0, accountId, characterGuid, requestedGeneration, expectedOwnerEpoch, cancellationToken);

    internal async ValueTask<ClusterSessionAttachResult> AttachSessionCoreAsync(
        int gatewayConnectionId,
        int accountId,
        ulong characterGuid,
        long requestedGeneration,
        long expectedOwnerEpoch,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
        {
            return new ClusterSessionAttachResult(false, 0, _readiness.CurrentOwnerEpoch, "invalid accountId");
        }

        if (characterGuid == 0)
        {
            return new ClusterSessionAttachResult(false, 0, _readiness.CurrentOwnerEpoch, "invalid characterGuid");
        }

        if (requestedGeneration <= 0)
        {
            return new ClusterSessionAttachResult(false, 0, _readiness.CurrentOwnerEpoch, "requestedGeneration must be positive");
        }

        if (!_readiness.IsGameplayReady)
        {
            return new ClusterSessionAttachResult(
                false,
                0,
                _readiness.CurrentOwnerEpoch,
                $"worker is not gameplay-ready (state={_readiness.State})");
        }

        long activeEpoch = _readiness.CurrentOwnerEpoch;
        if (expectedOwnerEpoch != activeEpoch)
        {
            return new ClusterSessionAttachResult(
                false,
                0,
                activeEpoch,
                $"owner epoch mismatch: expected {expectedOwnerEpoch}, worker epoch is {activeEpoch}");
        }

        return await _world.InvokeAsync(() =>
        {
            if (!_readiness.IsGameplayReady)
            {
                return new ClusterSessionAttachResult(
                    false,
                    0,
                    _readiness.CurrentOwnerEpoch,
                    $"worker is not gameplay-ready on world thread (state={_readiness.State})");
            }

            long epochNow = _readiness.CurrentOwnerEpoch;
            if (expectedOwnerEpoch != epochNow)
            {
                return new ClusterSessionAttachResult(
                    false,
                    0,
                    epochNow,
                    $"owner epoch mismatch on world thread: expected {expectedOwnerEpoch}, worker epoch is {epochNow}");
            }

            if (_sessions.TryGetValue(characterGuid, out AttachedSessionState? existing))
            {
                if (requestedGeneration <= existing.SessionGeneration)
                {
                    return new ClusterSessionAttachResult(
                        false,
                        existing.SessionGeneration,
                        epochNow,
                        $"stale session generation {requestedGeneration} (active={existing.SessionGeneration})");
                }

                // Newer generation deterministically supersedes the older session.
                _sessions.Remove(characterGuid);
                NotifySessionDetached(
                    existing.GatewayConnectionId,
                    characterGuid,
                    existing.SessionGeneration,
                    ClusterSessionDetachReason.SupersededByNewLogin);
            }

            var state = new AttachedSessionState
            {
                AccountId = accountId,
                CharacterGuid = characterGuid,
                SessionGeneration = requestedGeneration,
                GatewayConnectionId = gatewayConnectionId,
            };
            _sessions[characterGuid] = state;
            return new ClusterSessionAttachResult(true, requestedGeneration, epochNow, null);
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<bool> DetachSessionAsync(
        ulong characterGuid,
        long sessionGeneration,
        ClusterSessionDetachReason reason,
        CancellationToken cancellationToken = default) =>
        DetachSessionCoreAsync(gatewayConnectionId: null, characterGuid, sessionGeneration, reason, cancellationToken);

    internal async ValueTask<bool> DetachSessionCoreAsync(
        int? gatewayConnectionId,
        ulong characterGuid,
        long sessionGeneration,
        ClusterSessionDetachReason reason,
        CancellationToken cancellationToken = default)
    {
        if (_world.IsStopped)
        {
            return false;
        }

        return await _world.InvokeAsync(() =>
        {
            if (!_sessions.TryGetValue(characterGuid, out AttachedSessionState? existing))
            {
                return false;
            }

            if (existing.SessionGeneration != sessionGeneration)
            {
                return false;
            }

            if (gatewayConnectionId.HasValue && existing.GatewayConnectionId != gatewayConnectionId.Value)
            {
                return false;
            }

            _sessions.Remove(characterGuid);
            NotifySessionDetached(existing.GatewayConnectionId, characterGuid, sessionGeneration, reason);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Detaches all sessions currently owned by <paramref name="gatewayConnectionId"/> (e.g. when a gateway TCP connection drops).
    /// </summary>
    internal async ValueTask DetachAllForConnectionAsync(int gatewayConnectionId, CancellationToken cancellationToken = default)
    {
        if (_world.IsStopped)
        {
            return;
        }

        await _world.InvokeAsync(() =>
        {
            List<AttachedSessionState>? toRemove = null;
            foreach (AttachedSessionState session in _sessions.Values)
            {
                if (session.GatewayConnectionId == gatewayConnectionId)
                {
                    toRemove ??= [];
                    toRemove.Add(session);
                }
            }

            if (toRemove is null)
            {
                return true;
            }

            foreach (AttachedSessionState session in toRemove)
            {
                _sessions.Remove(session.CharacterGuid);
                NotifySessionDetached(
                    session.GatewayConnectionId,
                    session.CharacterGuid,
                    session.SessionGeneration,
                    ClusterSessionDetachReason.ClientDisconnect);
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> SendToWorkerAsync(
        ClusterSessionPacket packet,
        CancellationToken cancellationToken = default)
    {
        if (packet.Payload.Length > WorldSession.MaxClientPacketSize)
        {
            throw new InvalidOperationException(
                $"client packet payload ({packet.Payload.Length} B) exceeds MaxClientPacketSize ({WorldSession.MaxClientPacketSize} B)");
        }

        if (!_readiness.IsLive || _world.IsStopped)
        {
            return false;
        }

        if (packet.OwnerEpoch != _readiness.CurrentOwnerEpoch)
        {
            return false;
        }

        byte[] payloadCopy = packet.Payload.ToArray();
        ClusterSessionPacket safePacket = packet with { Payload = payloadCopy };

        return await _world.InvokeAsync(() =>
        {
            if (!_readiness.IsLive || _world.IsStopped)
            {
                return false;
            }

            if (safePacket.OwnerEpoch != _readiness.CurrentOwnerEpoch)
            {
                return false;
            }

            if (!_sessions.TryGetValue(safePacket.CharacterGuid, out AttachedSessionState? session))
            {
                return false;
            }

            if (session.SessionGeneration != safePacket.SessionGeneration || session.AccountId != safePacket.AccountId)
            {
                return false;
            }

            if (safePacket.Sequence <= session.LastInboundSequence)
            {
                return false;
            }

            session.LastInboundSequence = safePacket.Sequence;
            _onClientPacket?.Invoke(this, safePacket);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Called on the authoritative <see cref="WorldRuntime"/> thread (or via a posted command) to emit an ordered
    /// server packet for an attached session back to its owning gateway.
    /// Returns <see langword="false"/> if the session generation is no longer active.
    /// </summary>
    public bool TrySendToGateway(
        ulong characterGuid,
        long sessionGeneration,
        uint opcode,
        ReadOnlyMemory<byte> payload)
    {
        if (!_world.IsWorldThread)
        {
            throw new InvalidOperationException("TrySendToGateway must be called on the authoritative WorldRuntime thread");
        }

        if (payload.Length > WorldSession.MaxServerPayload)
        {
            throw new InvalidOperationException(
                $"server packet payload ({payload.Length} B) exceeds MaxServerPayload ({WorldSession.MaxServerPayload} B)");
        }

        if (!_readiness.IsLive || _world.IsStopped)
        {
            return false;
        }

        if (!_sessions.TryGetValue(characterGuid, out AttachedSessionState? session))
        {
            return false;
        }

        if (session.SessionGeneration != sessionGeneration)
        {
            return false;
        }

        long seq = ++session.LastOutboundSequence;
        var outbound = new ClusterSessionPacket(
            SessionGeneration: session.SessionGeneration,
            AccountId: session.AccountId,
            CharacterGuid: session.CharacterGuid,
            OwnerEpoch: _readiness.CurrentOwnerEpoch,
            Sequence: seq,
            Opcode: opcode,
            Payload: payload.ToArray());

        OutboundPacketReceived?.Invoke(outbound);
        OutboundPacketForConnectionReceived?.Invoke(session.GatewayConnectionId, outbound);
        return true;
    }

    private void NotifySessionDetached(
        int gatewayConnectionId,
        ulong characterGuid,
        long sessionGeneration,
        ClusterSessionDetachReason reason)
    {
        SessionDetached?.Invoke(characterGuid, sessionGeneration, reason);
        SessionDetachedForConnection?.Invoke(gatewayConnectionId, characterGuid, sessionGeneration, reason);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal enum GatewayWorkerFrameType : byte
{
    HelloRegister = 1,
    HelloAck = 2,
    AttachSessionRequest = 3,
    AttachSessionReply = 4,
    DetachSessionRequest = 5,
    DetachSessionReply = 6,
    ClientSessionPacket = 7,
    ClientSessionPacketAck = 8,
    ServerSessionPacket = 9,
    ServerSessionDetached = 10,

    // Tier 3a passthrough (GatewaySessionLink*.cs): sessions keyed by gateway generation, opened before a character exists.
    SessionOpen = 11,
    SessionOpenReply = 12,
    SessionClientPacket = 13,
    SessionServerPacket = 14,
    SessionClose = 15,
    LinkChallenge = 16,
    LinkProof = 17,
}

/// <summary>
/// Framed TCP loopback host on a Worker node that validates peer <see cref="ClusterNodeManifest"/> admission
/// during handshake and bridges gateway session traffic onto <see cref="LocalGatewayWorkerTransport"/>.
/// </summary>
public sealed class LoopbackGatewayWorkerHost : IAsyncDisposable
{
    public const int MaxFrameBytes = WorldSession.MaxServerPayload + 256;
    private const int OutboundQueueCapacity = 1024;

    private readonly ClusterNodeManifest _workerManifest;
    private readonly LocalGatewayWorkerTransport _localTransport;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, ClientConnection> _connections = new();
    private Task? _acceptTask;
    private int _nextConnectionId;

    private sealed class ClientConnection : IAsyncDisposable
    {
        public required int Id { get; init; }
        public required TcpClient Tcp { get; init; }
        public required NetworkStream Stream { get; init; }
        public required Channel<byte[]> SendChannel { get; init; }
        public Task? ReceiveTask { get; set; }
        public Task? SendTask { get; set; }
        public volatile bool Admitted;

        public async ValueTask DisposeAsync()
        {
            SendChannel.Writer.TryComplete();
            try
            {
                Tcp.Close();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (ex is not (ObjectDisposedException or SocketException or IOException))
                {
                    FaultLedger.Shared.Record("cluster-transport", ex);
                }
            }

            if (SendTask is not null)
            {
                try
                {
                    await SendTask.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (ex is not (OperationCanceledException or ObjectDisposedException or SocketException or IOException))
                    {
                        FaultLedger.Shared.Record("cluster-transport", ex);
                    }
                }
            }

            if (ReceiveTask is not null)
            {
                try
                {
                    await ReceiveTask.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (ex is not (OperationCanceledException or ObjectDisposedException or SocketException or IOException))
                    {
                        FaultLedger.Shared.Record("cluster-transport", ex);
                    }
                }
            }

            Tcp.Dispose();
        }
    }

    public LoopbackGatewayWorkerHost(
        ClusterNodeManifest workerManifest,
        LocalGatewayWorkerTransport localTransport,
        int port = 0)
    {
        ArgumentNullException.ThrowIfNull(workerManifest);
        ArgumentNullException.ThrowIfNull(localTransport);

        _workerManifest = workerManifest;
        _localTransport = localTransport;
        _listener = new TcpListener(IPAddress.Loopback, port);

        _localTransport.OutboundPacketForConnectionReceived += OnWorkerOutboundPacket;
        _localTransport.SessionDetachedForConnection += OnWorkerSessionDetached;
    }

    public IPEndPoint BoundEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public void Start()
    {
        _listener.Start();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("cluster-transport", ex);
                break;
            }

            client.NoDelay = true;
            int id = Interlocked.Increment(ref _nextConnectionId);
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutboundQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

            var conn = new ClientConnection
            {
                Id = id,
                Tcp = client,
                Stream = client.GetStream(),
                SendChannel = channel,
            };

            _connections[id] = conn;
            conn.SendTask = Task.Run(() => SendLoopAsync(conn, ct));
            conn.ReceiveTask = Task.Run(() => ReceiveClientLoopAsync(conn, ct));
        }
    }

    private static async Task SendLoopAsync(ClientConnection conn, CancellationToken ct)
    {
        try
        {
            await foreach (byte[] frame in conn.SendChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await WriteFrameAsync(conn.Stream, frame, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not (OperationCanceledException or ObjectDisposedException or SocketException or IOException))
            {
                FaultLedger.Shared.Record("cluster-transport", ex);
            }
        }
    }

    private async Task ReceiveClientLoopAsync(ClientConnection conn, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? frame = await ReadFrameAsync(conn.Stream, ct).ConfigureAwait(false);
                if (frame is null || frame.Length == 0)
                {
                    break;
                }

                var type = (GatewayWorkerFrameType)frame[0];
                ReadOnlyMemory<byte> body = frame.AsMemory(1);

                if (!conn.Admitted && type != GatewayWorkerFrameType.HelloRegister)
                {
                    break;
                }

                switch (type)
                {
                    case GatewayWorkerFrameType.HelloRegister:
                    {
                        ClusterNodeManifest peer = DeserializeManifest(body.Span);
                        bool admitted = _workerManifest.TryAdmit(peer, out string? reason);
                        conn.Admitted = admitted;
                        byte[] ack = BuildHelloAckFrame(admitted, reason ?? string.Empty);
                        await conn.SendChannel.Writer.WriteAsync(ack, ct).ConfigureAwait(false);
                        if (!admitted)
                        {
                            conn.SendChannel.Writer.TryComplete();
                            return;
                        }

                        break;
                    }

                    case GatewayWorkerFrameType.AttachSessionRequest:
                    {
                        ReadOnlySpan<byte> span = body.Span;
                        if (span.Length < 36)
                        {
                            throw new InvalidDataException($"truncated AttachSessionRequest frame ({span.Length} B)");
                        }

                        long reqId = BinaryPrimitives.ReadInt64LittleEndian(span[..8]);
                        int accountId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8, 4));
                        ulong characterGuid = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(12, 8));
                        long requestedGeneration = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(20, 8));
                        long expectedOwnerEpoch = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(28, 8));

                        ClusterSessionAttachResult res = await _localTransport.AttachSessionCoreAsync(
                            conn.Id,
                            accountId,
                            characterGuid,
                            requestedGeneration,
                            expectedOwnerEpoch,
                            ct).ConfigureAwait(false);

                        byte[] reply = BuildAttachReplyFrame(reqId, res);
                        await conn.SendChannel.Writer.WriteAsync(reply, ct).ConfigureAwait(false);
                        break;
                    }

                    case GatewayWorkerFrameType.DetachSessionRequest:
                    {
                        ReadOnlySpan<byte> span = body.Span;
                        if (span.Length < 25)
                        {
                            throw new InvalidDataException($"truncated DetachSessionRequest frame ({span.Length} B)");
                        }

                        long reqId = BinaryPrimitives.ReadInt64LittleEndian(span[..8]);
                        ulong characterGuid = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8, 8));
                        long sessionGeneration = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(16, 8));
                        var reason = (ClusterSessionDetachReason)span[24];

                        bool detached = await _localTransport.DetachSessionCoreAsync(
                            conn.Id,
                            characterGuid,
                            sessionGeneration,
                            reason,
                            ct).ConfigureAwait(false);

                        byte[] reply = new byte[1 + 8 + 1];
                        reply[0] = (byte)GatewayWorkerFrameType.DetachSessionReply;
                        BinaryPrimitives.WriteInt64LittleEndian(reply.AsSpan(1, 8), reqId);
                        reply[9] = detached ? (byte)1 : (byte)0;
                        await conn.SendChannel.Writer.WriteAsync(reply, ct).ConfigureAwait(false);
                        break;
                    }

                    case GatewayWorkerFrameType.ClientSessionPacket:
                    {
                        ReadOnlySpan<byte> span = body.Span;
                        if (span.Length < 52)
                        {
                            throw new InvalidDataException($"truncated ClientSessionPacket frame ({span.Length} B)");
                        }

                        long reqId = BinaryPrimitives.ReadInt64LittleEndian(span[..8]);
                        ClusterSessionPacket packet = DeserializeSessionPacket(body[8..]);
                        bool accepted = await _localTransport.SendToWorkerAsync(packet, ct).ConfigureAwait(false);

                        byte[] ack = new byte[1 + 8 + 1];
                        ack[0] = (byte)GatewayWorkerFrameType.ClientSessionPacketAck;
                        BinaryPrimitives.WriteInt64LittleEndian(ack.AsSpan(1, 8), reqId);
                        ack[9] = accepted ? (byte)1 : (byte)0;
                        await conn.SendChannel.Writer.WriteAsync(ack, ct).ConfigureAwait(false);
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not (OperationCanceledException or ObjectDisposedException or SocketException or IOException))
            {
                FaultLedger.Shared.Record("cluster-transport", ex);
            }
        }
        finally
        {
            if (_connections.TryRemove(conn.Id, out _))
            {
                await _localTransport.DetachAllForConnectionAsync(conn.Id, CancellationToken.None).ConfigureAwait(false);
                await conn.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void OnWorkerOutboundPacket(int connectionId, ClusterSessionPacket packet)
    {
        if (connectionId <= 0 || !_connections.TryGetValue(connectionId, out ClientConnection? conn) || !conn.Admitted)
        {
            return;
        }

        byte[] frame = SerializeServerPacketFrame(packet);
        conn.SendChannel.Writer.TryWrite(frame);
    }

    private void OnWorkerSessionDetached(
        int connectionId,
        ulong characterGuid,
        long sessionGeneration,
        ClusterSessionDetachReason reason)
    {
        if (connectionId <= 0 || !_connections.TryGetValue(connectionId, out ClientConnection? conn) || !conn.Admitted)
        {
            return;
        }

        byte[] frame = new byte[1 + 8 + 8 + 1];
        frame[0] = (byte)GatewayWorkerFrameType.ServerSessionDetached;
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(1, 8), characterGuid);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(9, 8), sessionGeneration);
        frame[17] = (byte)reason;
        conn.SendChannel.Writer.TryWrite(frame);
    }

    public async ValueTask DisposeAsync()
    {
        _localTransport.OutboundPacketForConnectionReceived -= OnWorkerOutboundPacket;
        _localTransport.SessionDetachedForConnection -= OnWorkerSessionDetached;

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            _listener.Stop();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not (ObjectDisposedException or SocketException))
            {
                FaultLedger.Shared.Record("cluster-transport", ex);
            }
        }

        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (ex is not (OperationCanceledException or ObjectDisposedException or SocketException))
                {
                    FaultLedger.Shared.Record("cluster-transport", ex);
                }
            }
        }

        foreach (KeyValuePair<int, ClientConnection> kvp in _connections)
        {
            if (_connections.TryRemove(kvp.Key, out ClientConnection? conn))
            {
                await conn.DisposeAsync().ConfigureAwait(false);
            }
        }

        _cts.Dispose();
    }

    internal static async ValueTask WriteFrameAsync(NetworkStream stream, byte[] payload, CancellationToken ct)
    {
        if (payload.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException($"frame payload ({payload.Length} B) exceeds MaxFrameBytes ({MaxFrameBytes} B)");
        }

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static async ValueTask<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        int read = await ReadExactlyOrEofAsync(stream, header, ct).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new InvalidOperationException($"invalid gateway–worker frame length: {length}");
        }

        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return body;
    }

    private static async ValueTask<int> ReadExactlyOrEofAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (total == 0)
                {
                    return 0;
                }

                throw new EndOfStreamException("truncated frame header on gateway–worker stream");
            }

            total += n;
        }

        return total;
    }

    internal static byte[] BuildHelloRegisterFrame(ClusterNodeManifest manifest)
    {
        byte[] nodeIdBytes = Encoding.UTF8.GetBytes(manifest.NodeId);
        byte[] contentBytes = Encoding.UTF8.GetBytes(manifest.ContentRevision);
        int total = 1 + 4 + 4 + nodeIdBytes.Length + 16 + 4 + 4 + 4 + 4 + 4 + contentBytes.Length;
        byte[] buf = new byte[total];
        Span<byte> span = buf;
        span[0] = (byte)GatewayWorkerFrameType.HelloRegister;
        int offset = 1;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), manifest.RealmId);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), nodeIdBytes.Length);
        offset += 4;
        nodeIdBytes.CopyTo(span.Slice(offset, nodeIdBytes.Length));
        offset += nodeIdBytes.Length;
        manifest.NodeIncarnation.TryWriteBytes(span.Slice(offset, 16));
        offset += 16;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), (int)manifest.Role);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), manifest.ContractVersion);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), manifest.CharactersSchemaVersion);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), manifest.WorldSchemaVersion);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), contentBytes.Length);
        offset += 4;
        contentBytes.CopyTo(span.Slice(offset, contentBytes.Length));
        return buf;
    }

    internal static ClusterNodeManifest DeserializeManifest(ReadOnlySpan<byte> span)
    {
        int offset = 0;
        int realmId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        int nodeIdLen = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        string nodeId = Encoding.UTF8.GetString(span.Slice(offset, nodeIdLen));
        offset += nodeIdLen;
        var incarnation = new Guid(span.Slice(offset, 16));
        offset += 16;
        var role = (ClusterRole)BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        int contractVer = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        int charSchema = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        int worldSchema = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        int contentLen = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        string contentRev = Encoding.UTF8.GetString(span.Slice(offset, contentLen));
        return new ClusterNodeManifest(
            realmId,
            nodeId,
            incarnation,
            role,
            contractVer,
            charSchema,
            worldSchema,
            contentRev);
    }

    internal static byte[] BuildHelloAckFrame(bool accepted, string reason)
    {
        byte[] reasonBytes = Encoding.UTF8.GetBytes(reason);
        byte[] buf = new byte[1 + 1 + 4 + reasonBytes.Length];
        buf[0] = (byte)GatewayWorkerFrameType.HelloAck;
        buf[1] = accepted ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(2, 4), reasonBytes.Length);
        reasonBytes.CopyTo(buf.AsSpan(6));
        return buf;
    }

    internal static byte[] BuildAttachReplyFrame(long reqId, ClusterSessionAttachResult result)
    {
        byte[] reasonBytes = Encoding.UTF8.GetBytes(result.RejectionReason ?? string.Empty);
        byte[] buf = new byte[1 + 8 + 1 + 8 + 8 + 4 + reasonBytes.Length];
        Span<byte> span = buf;
        span[0] = (byte)GatewayWorkerFrameType.AttachSessionReply;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(1, 8), reqId);
        span[9] = result.Accepted ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(10, 8), result.AssignedGeneration);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(18, 8), result.WorkerOwnerEpoch);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(26, 4), reasonBytes.Length);
        reasonBytes.CopyTo(span[30..]);
        return buf;
    }

    internal static byte[] SerializeClientPacketFrame(long reqId, ClusterSessionPacket packet)
    {
        ReadOnlySpan<byte> payload = packet.Payload.Span;
        byte[] buf = new byte[1 + 8 + 44 + payload.Length];
        Span<byte> span = buf;
        span[0] = (byte)GatewayWorkerFrameType.ClientSessionPacket;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(1, 8), reqId);
        WriteSessionPacketBody(span[9..], packet);
        return buf;
    }

    internal static byte[] SerializeServerPacketFrame(ClusterSessionPacket packet)
    {
        ReadOnlySpan<byte> payload = packet.Payload.Span;
        byte[] buf = new byte[1 + 44 + payload.Length];
        Span<byte> span = buf;
        span[0] = (byte)GatewayWorkerFrameType.ServerSessionPacket;
        WriteSessionPacketBody(span[1..], packet);
        return buf;
    }

    internal static void WriteSessionPacketBody(Span<byte> span, ClusterSessionPacket packet)
    {
        BinaryPrimitives.WriteInt64LittleEndian(span[..8], packet.SessionGeneration);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8, 4), packet.AccountId);
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(12, 8), packet.CharacterGuid);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(20, 8), packet.OwnerEpoch);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(28, 8), packet.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(36, 4), packet.Opcode);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(40, 4), packet.Payload.Length);
        packet.Payload.Span.CopyTo(span[44..]);
    }

    /// <summary>Bytes of the envelope header that precedes the payload (<see cref="WriteSessionPacketBody"/>).</summary>
    internal const int SessionPacketHeaderBytes = 44;

    internal static ClusterSessionPacket DeserializeSessionPacket(ReadOnlyMemory<byte> memory)
    {
        ReadOnlySpan<byte> span = memory.Span;
        if (span.Length < SessionPacketHeaderBytes)
        {
            throw new InvalidDataException($"truncated session packet envelope ({span.Length} B)");
        }

        long gen = BinaryPrimitives.ReadInt64LittleEndian(span[..8]);
        int accountId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8, 4));
        ulong guid = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(12, 8));
        long epoch = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(20, 8));
        long seq = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(28, 8));
        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(36, 4));
        int len = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(40, 4));
        if (len < 0 || len > span.Length - SessionPacketHeaderBytes)
        {
            throw new InvalidDataException($"session packet payload length {len} does not fit the {span.Length} B envelope");
        }

        ReadOnlyMemory<byte> payload = memory.Slice(SessionPacketHeaderBytes, len);
        return new ClusterSessionPacket(gen, accountId, guid, epoch, seq, opcode, payload);
    }
}

/// <summary>
/// Framed TCP loopback client on a Gateway node that connects to a <see cref="LoopbackGatewayWorkerHost"/>.
/// Starts its background receive loop BEFORE sending registration and awaiting the handshake reply
/// (<c>docs/CLUSTERING_DESIGN.md</c> §2.3, §4.4) so it can never deadlock on startup.
/// </summary>
public sealed class LoopbackGatewayWorkerClient : IGatewayWorkerTransport
{
    private readonly ClusterNodeManifest _gatewayManifest;
    private readonly IPEndPoint _workerEndPoint;
    private readonly TcpClient _tcp = new() { NoDelay = true };
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _receiveLoopStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<(bool Accepted, string Reason)> _helloAckTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<ClusterSessionAttachResult>> _pendingAttaches = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> _pendingDetaches = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> _pendingPacketAcks = new();

    private NetworkStream? _stream;
    private Task? _receiveTask;
    private long _nextRequestId;
    private volatile bool _isReceiveLoopRunning;
    private volatile bool _receiveLoopStartedBeforeRegisterSent;

    public LoopbackGatewayWorkerClient(ClusterNodeManifest gatewayManifest, IPEndPoint workerEndPoint)
    {
        ArgumentNullException.ThrowIfNull(gatewayManifest);
        ArgumentNullException.ThrowIfNull(workerEndPoint);
        _gatewayManifest = gatewayManifest;
        _workerEndPoint = workerEndPoint;
    }

    public event Action<ClusterSessionPacket>? OutboundPacketReceived;

    public event Action<ulong, long, ClusterSessionDetachReason>? SessionDetached;

    /// <summary>True while the client's background receive loop is actively running.</summary>
    public bool IsReceiveLoopRunning => _isReceiveLoopRunning;

    /// <summary>
    /// Diagnostic proof for <c>docs/CLUSTERING_DESIGN.md</c> §4.4: verifies that the client receive loop was
    /// started and actively awaiting frames BEFORE the registration frame was sent and awaited.
    /// </summary>
    public bool ReceiveLoopStartedBeforeRegisterSent => _receiveLoopStartedBeforeRegisterSent;

    public async Task ConnectAndRegisterAsync(CancellationToken cancellationToken = default)
    {
        await _tcp.ConnectAsync(_workerEndPoint.Address, _workerEndPoint.Port, cancellationToken).ConfigureAwait(false);
        _stream = _tcp.GetStream();

        // CRITICAL INVARIANT (CLUSTERING_DESIGN.md:27,232):
        // Start the receive loop BEFORE sending registration and awaiting the registration reply.
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        await _receiveLoopStarted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        _receiveLoopStartedBeforeRegisterSent = _isReceiveLoopRunning;

        byte[] helloFrame = LoopbackGatewayWorkerHost.BuildHelloRegisterFrame(_gatewayManifest);
        await SendFrameAsync(helloFrame, cancellationToken).ConfigureAwait(false);

        (bool accepted, string reason) = await _helloAckTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!accepted)
        {
            throw new InvalidOperationException($"worker rejected gateway registration: {reason}");
        }
    }

    public async ValueTask<ClusterSessionAttachResult> AttachSessionAsync(
        int accountId,
        ulong characterGuid,
        long requestedGeneration,
        long expectedOwnerEpoch,
        CancellationToken cancellationToken = default)
    {
        long reqId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<ClusterSessionAttachResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAttaches[reqId] = tcs;

        try
        {
            byte[] frame = new byte[1 + 36];
            Span<byte> span = frame;
            span[0] = (byte)GatewayWorkerFrameType.AttachSessionRequest;
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(1, 8), reqId);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(9, 4), accountId);
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(13, 8), characterGuid);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(21, 8), requestedGeneration);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(29, 8), expectedOwnerEpoch);

            await SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingAttaches.TryRemove(reqId, out _);
        }
    }

    public async ValueTask<bool> DetachSessionAsync(
        ulong characterGuid,
        long sessionGeneration,
        ClusterSessionDetachReason reason,
        CancellationToken cancellationToken = default)
    {
        long reqId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingDetaches[reqId] = tcs;

        try
        {
            byte[] frame = new byte[1 + 8 + 8 + 8 + 1];
            Span<byte> span = frame;
            span[0] = (byte)GatewayWorkerFrameType.DetachSessionRequest;
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(1, 8), reqId);
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(9, 8), characterGuid);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(17, 8), sessionGeneration);
            span[25] = (byte)reason;

            await SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingDetaches.TryRemove(reqId, out _);
        }
    }

    public async ValueTask<bool> SendToWorkerAsync(
        ClusterSessionPacket packet,
        CancellationToken cancellationToken = default)
    {
        if (packet.Payload.Length > WorldSession.MaxClientPacketSize)
        {
            throw new InvalidOperationException(
                $"client packet payload ({packet.Payload.Length} B) exceeds MaxClientPacketSize ({WorldSession.MaxClientPacketSize} B)");
        }

        long reqId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPacketAcks[reqId] = tcs;

        try
        {
            byte[] frame = LoopbackGatewayWorkerHost.SerializeClientPacketFrame(reqId, packet);
            await SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingPacketAcks.TryRemove(reqId, out _);
        }
    }

    private async ValueTask SendFrameAsync(byte[] frame, CancellationToken ct)
    {
        NetworkStream? stream = _stream ?? throw new InvalidOperationException("LoopbackGatewayWorkerClient is not connected");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoopbackGatewayWorkerHost.WriteFrameAsync(stream, frame, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        _isReceiveLoopRunning = true;
        _receiveLoopStarted.TrySetResult();

        try
        {
            if (_stream is not { } stream)
            {
                Invariant.Check(false, "LoopbackGatewayWorkerClient.ReceiveLoopAsync started without an active NetworkStream.");
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                byte[]? frame = await LoopbackGatewayWorkerHost.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                if (frame is null || frame.Length == 0)
                {
                    break;
                }

                var type = (GatewayWorkerFrameType)frame[0];
                ReadOnlyMemory<byte> body = frame.AsMemory(1);

                switch (type)
                {
                    case GatewayWorkerFrameType.HelloAck:
                    {
                        ReadOnlySpan<byte> span = body.Span;
                        if (span.Length < 5)
                        {
                            throw new InvalidDataException($"truncated HelloAck frame ({span.Length} B)");
                        }

                        bool accepted = span[0] != 0;
                        int len = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(1, 4));
                        if (len < 0 || span.Length < 5 + len)
                        {
                            throw new InvalidDataException($"invalid HelloAck reason length ({len})");
                        }

                        string reason = len > 0 ? Encoding.UTF8.GetString(span.Slice(5, len)) : string.Empty;
                        _helloAckTcs.TrySetResult((accepted, reason));
                        break;
                    }

                    case GatewayWorkerFrameType.AttachSessionReply:
                    {
                        ReadOnlySpan<byte> span = body.Span;
                        if (span.Length < 29)
                        {
                            throw new InvalidDataException($"truncated AttachSessionReply frame ({span.Length} B)");
                        }

                        long reqId = BinaryPrimitives.ReadInt64LittleEndian(span[..8]);
                        bool accepted = span[8] != 0;
                        long assignedGen = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(9, 8));
                        long workerEpoch = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(17, 8));
                        int reasonLen = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(25, 4));
                        if (reasonLen < 0 || span.Length < 29 + reasonLen)
                        {
                            throw new InvalidDataException($"invalid AttachSessionReply reason length ({reasonLen})");
                        }

                        string? reason = reasonLen > 0 ? Encoding.UTF8.GetString(span.Slice(29, reasonLen)) : null;

                        if (_pendingAttaches.TryGetValue(reqId, out TaskCompletionSource<ClusterSessionAttachResult>? tcs))
                        {
                            tcs.TrySetResult(new ClusterSessionAttachResult(accepted, assignedGen, workerEpoch, reason));
                        }

                        break;
                    }

                    case GatewayWorkerFrameType.DetachSessionReply:
                    {
                        ReadOnlySpan<byte> span = body.Span;
                        if (span.Length < 9)
                        {
                            throw new InvalidDataException($"truncated DetachSessionReply frame ({span.Length} B)");
                        }

                        long reqId = BinaryPrimitives.ReadInt64LittleEndian(span[..8]);
                        bool detached = span[8] != 0;
                        if (_pendingDetaches.TryGetValue(reqId, out TaskCompletionSource<bool>? tcs))
                        {
                            tcs.TrySetResult(detached);
                        }

                        break;
                    }

                    case GatewayWorkerFrameType.ClientSessionPacketAck:
                    {
                        ReadOnlySpan<byte> span = body.Span;
                        if (span.Length < 9)
                        {
                            throw new InvalidDataException($"truncated ClientSessionPacketAck frame ({span.Length} B)");
                        }

                        long reqId = BinaryPrimitives.ReadInt64LittleEndian(span[..8]);
                        bool accepted = span[8] != 0;
                        if (_pendingPacketAcks.TryGetValue(reqId, out TaskCompletionSource<bool>? tcs))
                        {
                            tcs.TrySetResult(accepted);
                        }

                        break;
                    }

                    case GatewayWorkerFrameType.ServerSessionPacket:
                    {
                        ClusterSessionPacket packet = LoopbackGatewayWorkerHost.DeserializeSessionPacket(body);
                        OutboundPacketReceived?.Invoke(packet);
                        break;
                    }

                    case GatewayWorkerFrameType.ServerSessionDetached:
                    {
                        ReadOnlySpan<byte> span = body.Span;
                        if (span.Length < 17)
                        {
                            throw new InvalidDataException($"truncated ServerSessionDetached frame ({span.Length} B)");
                        }

                        ulong characterGuid = BinaryPrimitives.ReadUInt64LittleEndian(span[..8]);
                        long sessionGeneration = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(8, 8));
                        var reason = (ClusterSessionDetachReason)span[16];
                        SessionDetached?.Invoke(characterGuid, sessionGeneration, reason);
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not (OperationCanceledException or ObjectDisposedException or SocketException or IOException))
            {
                FaultLedger.Shared.Record("cluster-transport", ex);
            }

            _helloAckTcs.TrySetException(ex);
        }
        finally
        {
            _isReceiveLoopRunning = false;
            var closedEx = new IOException("gateway–worker loopback connection closed");
            _helloAckTcs.TrySetException(closedEx);
            foreach (TaskCompletionSource<ClusterSessionAttachResult> tcs in _pendingAttaches.Values)
            {
                tcs.TrySetException(closedEx);
            }

            foreach (TaskCompletionSource<bool> tcs in _pendingDetaches.Values)
            {
                tcs.TrySetException(closedEx);
            }

            foreach (TaskCompletionSource<bool> tcs in _pendingPacketAcks.Values)
            {
                tcs.TrySetException(closedEx);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            _tcp.Close();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not (ObjectDisposedException or SocketException or IOException))
            {
                FaultLedger.Shared.Record("cluster-transport", ex);
            }
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (ex is not (OperationCanceledException or ObjectDisposedException or SocketException or IOException))
                {
                    FaultLedger.Shared.Record("cluster-transport", ex);
                }
            }
        }

        _tcp.Dispose();
        _writeLock.Dispose();
        _cts.Dispose();
    }
}

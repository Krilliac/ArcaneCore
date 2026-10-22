using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>The gateway side of one forwarded client: what the link delivers to it. Every call comes from the link's receive loop, in order.</summary>
public interface IGatewaySessionSink
{
    /// <summary>
    /// The worker accepted the session. The gateway engages the header cipher and queues SMSG_AUTH_RESPONSE here, before
    /// returning: the next server packet of the session is delivered only after this call returns.
    /// </summary>
    void OnOpened(GatewaySessionOpenReply reply);

    /// <summary>One server packet, in the worker's sequence order. The payload is only valid during the call.</summary>
    void OnServerPacket(WorldOpcode opcode, ReadOnlySpan<byte> payload);

    /// <summary>The worker closed the session, or the link was lost. Called at most once, never before <see cref="OnOpened"/>.</summary>
    void OnClosed(ClusterSessionDetachReason reason);
}

/// <summary>
/// The gateway end of the Tier 3a session link: connects to a <see cref="GatewaySessionLinkHost"/>, answers its challenge
/// with the link secret's HMAC, registers with a <see cref="ClusterRole.Gateway"/> manifest, then opens sessions and
/// forwards client opcodes in the <see cref="ClusterSessionPacket"/> envelope (this gateway's generation, the owner epoch the
/// worker returned at open, an ascending sequence). Server packets whose generation, account or epoch do not match, or whose
/// sequence is not higher than the last one delivered, are dropped and counted (<see cref="DroppedServerPackets"/>).
/// </summary>
public sealed class GatewaySessionLinkClient : IAsyncDisposable
{
    private readonly ClusterNodeManifest _manifest;
    private readonly IPEndPoint _worker;
    private readonly string _linkSecret;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, ClientSession> _sessions = new();
    private readonly ConcurrentDictionary<long, ClientSession> _pendingOpens = new();
    private readonly TaskCompletionSource<(bool Accepted, string Reason)> _helloAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly LogGate _dropGate = new(TimeSpan.FromSeconds(10));
    private readonly Lock _openGate = new();
    private GatewayLinkChannel? _channel;
    private Task? _receive;
    private long _nextRequestId;
    private long _droppedServerPackets;
    private volatile bool _registered;

    public GatewaySessionLinkClient(ClusterNodeManifest gatewayManifest, IPEndPoint workerEndPoint, string linkSecret, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(gatewayManifest);
        ArgumentNullException.ThrowIfNull(workerEndPoint);
        ArgumentNullException.ThrowIfNull(linkSecret);
        GatewaySessionLinkHost.ThrowIfWeakSecret(linkSecret, "Gateway:LinkSecret");
        if (gatewayManifest.Role != ClusterRole.Gateway)
        {
            throw new ArgumentException("the gateway end of a link registers with the Gateway role", nameof(gatewayManifest));
        }

        _manifest = gatewayManifest;
        _worker = workerEndPoint;
        _linkSecret = linkSecret;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>True from a successful <see cref="ConnectAsync"/> until the link ends.</summary>
    public bool IsConnected => _registered && _channel is { IsOpen: true };

    /// <summary>Completes when the link has ended (after every session was told).</summary>
    public Task Ended => _ended.Task;

    /// <summary>Server packets dropped by the envelope rules.</summary>
    public long DroppedServerPackets => Interlocked.Read(ref _droppedServerPackets);

    /// <summary>Open sessions on this link.</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>Connect, start the receive loop, prove the link secret and register; throws when the worker refuses.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null)
        {
            throw new InvalidOperationException("a link client connects once; create a new one to reconnect");
        }

        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(_worker, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _channel = new GatewayLinkChannel(tcp);
        // The receive loop runs before anything is awaited from the worker (CLUSTERING_DESIGN.md §4.4): the challenge,
        // the proof and the registration are all answered from it.
        _receive = Task.Run(ReceiveLoopAsync, CancellationToken.None);
        (bool accepted, string reason) = await _helloAck.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!accepted)
        {
            throw new InvalidOperationException($"worker refused the gateway link: {reason}");
        }

        _registered = true;
    }

    /// <summary>
    /// Ask the worker to open a session whose generation the caller already chose. Accepted: <see cref="IGatewaySessionSink.OnOpened"/>
    /// has run (on the receive loop) before this returns. Refused: the sink is never called and the reply carries the client's refusal
    /// code. The worker refuses an open whose generation is not above every one before it on the link, so concurrent callers must use
    /// the overload that allocates the generation (<see cref="OpenAsync(Func{long}, Func{long, GatewaySessionOpenRequest}, IGatewaySessionSink, CancellationToken)"/>).
    /// </summary>
    public Task<GatewaySessionOpenReply> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OpenAsync(() => request.SessionGeneration, _ => request, sink, cancellationToken);
    }

    /// <summary>
    /// Ask the worker to open a session, taking its generation from <paramref name="nextGeneration"/> (ascending) and building the
    /// request with <paramref name="requestFor"/>. Taking the generation and queueing the SessionOpen happen under one lock, so the
    /// opens leave on the link in generation order however many clients log in at once: the worker refuses an open whose generation
    /// is not above the last one it saw. Both callbacks run under that lock: keep them short and non-blocking.
    /// </summary>
    public async Task<GatewaySessionOpenReply> OpenAsync(
        Func<long> nextGeneration, Func<long, GatewaySessionOpenRequest> requestFor, IGatewaySessionSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nextGeneration);
        ArgumentNullException.ThrowIfNull(requestFor);
        ArgumentNullException.ThrowIfNull(sink);
        GatewayLinkChannel channel = _channel ?? throw new InvalidOperationException("not connected");
        GatewaySessionOpenRequest request;
        ClientSession session;
        long requestId;
        lock (_openGate)
        {
            long generation = nextGeneration();
            request = requestFor(generation);
            if (request.SessionGeneration != generation)
            {
                throw new InvalidOperationException($"the open request carries generation {request.SessionGeneration}, not the allocated {generation}");
            }

            requestId = Interlocked.Increment(ref _nextRequestId);
            session = new ClientSession(generation, request.AccountId, sink, requestId);
            if (!_sessions.TryAdd(generation, session))
            {
                throw new InvalidOperationException($"session generation {generation} is already open on this link");
            }

            _pendingOpens[requestId] = session;
            if (!channel.TryEnqueue(GatewaySessionWire.EncodeOpen(requestId, request)))
            {
                _pendingOpens.TryRemove(requestId, out _);
                _sessions.TryRemove(generation, out _);
                return GatewaySessionOpenReply.Refuse(AuthResponseCode.Unavailable, "the worker link is down");
            }
        }

        try
        {
            return await session.Opened.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pendingOpens.TryRemove(requestId, out _);
            CloseSession(request.SessionGeneration, ClusterSessionDetachReason.ClientDisconnect);
            throw;
        }
    }

    /// <summary>Forward one client opcode. False when the session is not open or the link is down.</summary>
    public bool SendClientPacket(long sessionGeneration, WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        if (_channel is not { } channel || !_sessions.TryGetValue(sessionGeneration, out ClientSession? session))
        {
            return false;
        }

        lock (session.Gate)
        {
            if (!session.IsOpen)
            {
                return false;
            }

            byte[] frame = GatewaySessionWire.EncodePacket(GatewayWorkerFrameType.SessionClientPacket, session.Generation, session.AccountId, 0,
                session.Epoch, ++session.OutboundSequence, (uint)opcode, payload);
            return channel.TryEnqueue(frame);
        }
    }

    /// <summary>Test seam: send an envelope exactly as given (sequence, epoch, account), bypassing the session's own counters.</summary>
    internal bool SendRawClientPacket(ClusterSessionPacket packet) =>
        _channel is { } channel && channel.TryEnqueue(GatewaySessionWire.EncodePacket(GatewayWorkerFrameType.SessionClientPacket, packet));

    /// <summary>The client went away (or was superseded): tell the worker. The sink is not called.</summary>
    public void CloseSession(long sessionGeneration, ClusterSessionDetachReason reason)
    {
        if (!_sessions.TryRemove(sessionGeneration, out ClientSession? session))
        {
            return;
        }

        lock (session.Gate)
        {
            session.IsClosed = true;
        }

        _channel?.TryEnqueue(GatewaySessionWire.EncodeClose(session.Generation, session.AccountId, reason));
    }

    private async Task ReceiveLoopAsync()
    {
        GatewayLinkChannel channel = _channel!;
        try
        {
            while (channel.IsOpen)
            {
                byte[]? frame = await channel.ReadFrameAsync(channel.Closed).ConfigureAwait(false);
                if (frame is null || frame.Length == 0)
                {
                    break;
                }

                var type = (GatewayWorkerFrameType)frame[0];
                ReadOnlyMemory<byte> body = frame.AsMemory(1);
                switch (type)
                {
                    case GatewayWorkerFrameType.LinkChallenge:
                        byte[] nonce = GatewaySessionWire.DecodeNonceFrame(body.Span, "LinkChallenge");
                        channel.TryEnqueue(GatewaySessionWire.EncodeNonceFrame(GatewayWorkerFrameType.LinkProof, GatewaySessionWire.ComputeProof(_linkSecret, nonce)));
                        channel.TryEnqueue(LoopbackGatewayWorkerHost.BuildHelloRegisterFrame(_manifest));
                        break;
                    case GatewayWorkerFrameType.HelloAck:
                        _helloAck.TrySetResult(DecodeHelloAck(body.Span));
                        break;
                    case GatewayWorkerFrameType.SessionOpenReply:
                        OnOpenReply(body.Span);
                        break;
                    case GatewayWorkerFrameType.SessionServerPacket:
                        OnServerPacket(body);
                        break;
                    case GatewayWorkerFrameType.SessionClose:
                        (long generation, int accountId, ClusterSessionDetachReason reason) = GatewaySessionWire.DecodeClose(body.Span);
                        if (_sessions.TryGetValue(generation, out ClientSession? closing) && closing.AccountId == accountId
                            && _sessions.TryRemove(new KeyValuePair<long, ClientSession>(generation, closing)))
                        {
                            NotifyClosed(closing, reason);
                        }

                        break;
                    default:
                        throw new InvalidDataException($"unexpected frame {type} from a worker");
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is InvalidDataException or InvalidOperationException)
            {
                _logger.LogWarning("Worker link to {Worker} broke the protocol ({Message}); closing", _worker, ex.Message);
            }
            else if (ex is not (OperationCanceledException or ObjectDisposedException or SocketException or IOException or EndOfStreamException))
            {
                FaultLedger.Shared.Record("cluster.transport", ex);
            }
        }
        finally
        {
            channel.Close();
            _registered = false;
            _helloAck.TrySetResult((false, "the worker closed the link"));
            foreach (ClientSession pending in _pendingOpens.Values)
            {
                pending.Opened.TrySetResult(GatewaySessionOpenReply.Refuse(AuthResponseCode.Unavailable, "the worker link closed"));
            }

            _pendingOpens.Clear();
            foreach (KeyValuePair<long, ClientSession> entry in _sessions)
            {
                if (_sessions.TryRemove(entry))
                {
                    NotifyClosed(entry.Value, ClusterSessionDetachReason.LinkLost);
                }
            }

            _ended.TrySetResult();
        }
    }

    private void OnOpenReply(ReadOnlySpan<byte> body)
    {
        (long requestId, GatewaySessionOpenReply reply) = GatewaySessionWire.DecodeOpenReply(body);
        if (!_pendingOpens.TryRemove(requestId, out ClientSession? session))
        {
            return;
        }

        if (!reply.Accepted)
        {
            _sessions.TryRemove(new KeyValuePair<long, ClientSession>(session.Generation, session));
            session.Opened.TrySetResult(reply);
            return;
        }

        bool closed;
        lock (session.Gate)
        {
            closed = session.IsClosed;
            session.Epoch = reply.OwnerEpoch;
            session.IsOpen = !closed;
        }

        if (!closed)
        {
            try
            {
                session.Sink.OnOpened(reply);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("cluster.gateway-client", ex);
                AnomalyLedger.Shared.Report("cluster", "gateway-fault", $"session-{session.Generation}", $"phase=open error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
                _logger.LogFault("cluster.transport", ex, "Gateway session {Generation}: the open handler failed", session.Generation);
                CloseSession(session.Generation, ClusterSessionDetachReason.LinkLost);
            }
        }

        session.Opened.TrySetResult(reply);
    }

    private void OnServerPacket(ReadOnlyMemory<byte> body)
    {
        ClusterSessionPacket packet = GatewaySessionWire.DecodePacket(body);
        if (_sessions.TryGetValue(packet.SessionGeneration, out ClientSession? session))
        {
            bool accept;
            lock (session.Gate)
            {
                accept = session.IsOpen && !session.IsClosed && packet.AccountId == session.AccountId && packet.OwnerEpoch == session.Epoch
                    && packet.Sequence > session.LastInboundSequence;
                if (accept)
                {
                    session.LastInboundSequence = packet.Sequence;
                }
            }

            if (accept)
            {
                session.Sink.OnServerPacket((WorldOpcode)packet.Opcode, packet.Payload.Span);
                return;
            }
        }

        long dropped = Interlocked.Increment(ref _droppedServerPackets);
        if (_dropGate.TryEnter(out int suppressed))
        {
            _logger.LogWarning(
                "Worker link to {Worker}: dropped server packet (generation {Generation}, account {Account}, epoch {Epoch}, sequence {Sequence}); {Dropped} dropped in all, {Suppressed} lines suppressed",
                _worker, packet.SessionGeneration, packet.AccountId, packet.OwnerEpoch, packet.Sequence, dropped, suppressed);
        }
    }

    private void NotifyClosed(ClientSession session, ClusterSessionDetachReason reason)
    {
        bool wasOpen;
        lock (session.Gate)
        {
            wasOpen = session.IsOpen && !session.IsClosed;
            session.IsClosed = true;
        }

        if (wasOpen)
        {
            try
            {
                session.Sink.OnClosed(reason);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("cluster.gateway-client", ex);
                AnomalyLedger.Shared.Report("cluster", "gateway-fault", $"session-{session.Generation}", $"phase=close error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Warning);
                _logger.LogFault("cluster.transport", ex, "Gateway session {Generation}: the close handler failed", session.Generation);
            }
        }
    }

    private static (bool Accepted, string Reason) DecodeHelloAck(ReadOnlySpan<byte> span)
    {
        if (span.Length < 5)
        {
            throw new InvalidDataException($"truncated HelloAck frame ({span.Length} B)");
        }

        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span.Slice(1, 4));
        if (length < 0 || length > span.Length - 5)
        {
            throw new InvalidDataException($"invalid HelloAck reason length ({length})");
        }

        return (span[0] != 0, Encoding.UTF8.GetString(span.Slice(5, length)));
    }

    public async ValueTask DisposeAsync()
    {
        _channel?.Close();
        if (_receive is not null)
        {
            await _receive.ConfigureAwait(false);
        }

        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ClientSession(long generation, int accountId, IGatewaySessionSink sink, long requestId)
    {
        public Lock Gate { get; } = new();
        public long Generation { get; } = generation;
        public int AccountId { get; } = accountId;
        public IGatewaySessionSink Sink { get; } = sink;
        public long RequestId { get; } = requestId;
        public TaskCompletionSource<GatewaySessionOpenReply> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Epoch;
        public long OutboundSequence;
        public long LastInboundSequence;
        public bool IsOpen;
        public bool IsClosed;
    }
}

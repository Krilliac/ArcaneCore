using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Auxiliary;
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

    /// <summary>
    /// The worker asked to move the session to the owner of a map (login redirect). Return true to take it over: the link then keeps the session
    /// registered but silent (its packets and its close are no longer delivered to this sink) and the sink must end it with
    /// <see cref="GatewaySessionLinkClient.CloseSession"/> once the target worker has taken the session. Return false to refuse: the link then
    /// closes the session as <see cref="OnClosed"/> with <see cref="ClusterSessionDetachReason.WorkerClosed"/>. Called from the link's receive loop; must not block.
    /// The default refuses.
    /// </summary>
    bool OnRedirect(GatewaySessionRedirect redirect) => false;
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
    private readonly TaskCompletionSource<(bool Accepted, string Reason, string OwnedMaps, bool AllowInstanceMaps)> _helloAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly LogGate _dropGate = new(TimeSpan.FromSeconds(10));
    private readonly Lock _openGate = new();
    private GatewayLinkChannel? _channel;
    private Task? _receive;
    private long _nextRequestId;
    private long _droppedServerPackets;
    private volatile bool _registered;
    private volatile string _workerOwnedMaps = string.Empty;
    private volatile bool _workerAllowsInstanceMaps = true;
    private readonly GatewayWardenPosture? _wardenPosture;
    private readonly string? _slotNode;

    /// <param name="wardenPosture">The gateway's Warden posture, sent to the worker right after admission and before any session opens. A worker that
    /// enforces a Warden posture refuses sessions on a link that sent none or a different one.</param>
    /// <param name="slotNode">The slot this link serves, sent to the worker right after admission (the LinkSlot frame): every link-up tells the process behind it the name it answers transfers under, so a promoted
    /// process relaunched with its own node id gets it back. Null on a link that does not reach a slot's active process (a standby's control link, a test's).</param>
    public GatewaySessionLinkClient(
        ClusterNodeManifest gatewayManifest, IPEndPoint workerEndPoint, string linkSecret, ILogger? logger = null, GatewayWardenPosture? wardenPosture = null, string? slotNode = null)
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
        _wardenPosture = wardenPosture;
        _slotNode = slotNode;
    }

    /// <summary>The worker endpoint this link connects to.</summary>
    public IPEndPoint WorkerEndPoint => _worker;

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
        (bool accepted, string reason, string ownedMaps, bool allowInstanceMaps) = await _helloAck.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!accepted)
        {
            throw new InvalidOperationException($"worker refused the gateway link: {reason}");
        }

        // The clock reading goes first: the worker derives the offset it translates every client packet's receive stamp with.
        if (!_channel.TryEnqueue(GatewaySessionWire.EncodeLinkClock((uint)Clock.Milliseconds())))
        {
            throw new InvalidOperationException("the worker link closed before the link clock could be sent");
        }

        if (_wardenPosture is { } posture && !_channel.TryEnqueue(GatewaySessionWire.EncodeWardenPosture(posture)))
        {
            throw new InvalidOperationException("the worker link closed before the Warden posture could be sent");
        }

        // Before anything can use the name: the frames of one link arrive in order, so no control request of this link can overtake it.
        if (_slotNode is { } slot && !_channel.TryEnqueue(GatewaySessionWire.EncodeLinkSlot(slot)))
        {
            throw new InvalidOperationException("the worker link closed before the slot could be named");
        }

        _workerOwnedMaps = ownedMaps;
        _workerAllowsInstanceMaps = allowInstanceMaps;
        _registered = true;
    }

    /// <summary>The map list the worker announced in its HelloAck (<c>Cluster:Worker:OwnedMaps</c> text such as <c>0,1,30-40</c>); empty (also before registration, or from a worker that sent none) means it owns every map.</summary>
    public string WorkerOwnedMaps => _workerOwnedMaps;

    /// <summary>Whether the worker announced that it also runs instance copies of maps outside its list (true until it says otherwise).</summary>
    public bool WorkerAllowsInstanceMaps => _workerAllowsInstanceMaps;

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
    public Task<GatewaySessionOpenReply> OpenAsync(
        Func<long> nextGeneration, Func<long, GatewaySessionOpenRequest> requestFor, IGatewaySessionSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestFor);
        return OpenCoreAsync(nextGeneration, generation =>
        {
            GatewaySessionOpenRequest request = requestFor(generation);
            return (request, new Func<long, byte[]>(requestId => GatewaySessionWire.EncodeOpen(requestId, request)));
        }, sink, cancellationToken);
    }

    /// <summary>
    /// The second half of a login redirect: ask this worker (the map owner) to open the session the gateway moved off the login worker, with the
    /// buffered CMSG_PLAYER_LOGIN. Generation allocation, ordering, the reply and the sink behave exactly as in
    /// <see cref="OpenAsync(Func{long}, Func{long, GatewaySessionOpenRequest}, IGatewaySessionSink, CancellationToken)"/>; the worker delivers the login packet to the new session itself,
    /// so the client sees the login answers only after the reply. <paramref name="requestFor"/> runs under the same lock as there.
    /// </summary>
    public Task<GatewaySessionOpenReply> AdoptAsync(
        Func<long> nextGeneration, Func<long, GatewaySessionAdoptRequest> requestFor, IGatewaySessionSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestFor);
        return OpenCoreAsync(nextGeneration, generation =>
        {
            GatewaySessionAdoptRequest adopt = requestFor(generation);
            return (adopt.Open, new Func<long, byte[]>(requestId => GatewaySessionWire.EncodeAdopt(requestId, adopt)));
        }, sink, cancellationToken);
    }

    private async Task<GatewaySessionOpenReply> OpenCoreAsync(
        Func<long> nextGeneration, Func<long, (GatewaySessionOpenRequest Request, Func<long, byte[]> Frame)> build, IGatewaySessionSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nextGeneration);
        ArgumentNullException.ThrowIfNull(sink);
        GatewayLinkChannel channel = _channel ?? throw new InvalidOperationException("not connected");
        GatewaySessionOpenRequest request;
        ClientSession session;
        long requestId;
        lock (_openGate)
        {
            long generation = nextGeneration();
            Func<long, byte[]> frame;
            (request, frame) = build(generation);
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
            if (!channel.TryEnqueue(frame(requestId)))
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

    /// <summary>
    /// Forward one client opcode. False when the session is not open or the link is down. <paramref name="receivedMs"/> is the gateway
    /// clock reading (<see cref="Clock.Milliseconds"/>, low 32 bits) when the client packet arrived; null stamps the moment of this call.
    /// </summary>
    public bool SendClientPacket(long sessionGeneration, WorldOpcode opcode, ReadOnlySpan<byte> payload, uint? receivedMs = null)
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

            byte[] frame = GatewaySessionWire.EncodeClientPacket(session.Generation, session.AccountId, 0,
                session.Epoch, ++session.OutboundSequence, (uint)opcode, payload, receivedMs ?? (uint)Clock.Milliseconds());
            return channel.TryEnqueue(frame);
        }
    }

    /// <summary>
    /// Tell the worker of a Warden verdict about an open session (the gateway runs Warden; the worker applies the penalty). The frame
    /// carries the session's own generation, account and owner epoch, and the account id as the verdict key (the gateway has no
    /// character). False when the session is not open or the link is down; the frame follows every packet already queued.
    /// </summary>
    public bool SendWardenVerdict(long sessionGeneration, ushort checkId, ClusterWardenAction action, string reason, GatewayWardenPosture posture)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(posture);
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

            var verdict = new ClusterWardenVerdict(session.Generation, session.Epoch, session.AccountId, (ulong)session.AccountId, reason, action, checkId);
            return channel.TryEnqueue(GatewaySessionWire.EncodeWardenVerdict(verdict, posture));
        }
    }

    /// <summary>Test seam: send an envelope exactly as given (sequence, epoch, account), bypassing the session's own counters.</summary>
    internal bool SendRawClientPacket(ClusterSessionPacket packet) =>
        _channel is { } channel && channel.TryEnqueue(GatewaySessionWire.EncodeClientPacket(packet, (uint)Clock.Milliseconds()));

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
        byte[]? listenerNonce = null;
        byte[]? connectorNonce = null;
        bool secured = false;
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
                if (!secured && type is not (GatewayWorkerFrameType.LinkChallenge or GatewayWorkerFrameType.LinkProof or GatewayWorkerFrameType.HelloAck))
                {
                    throw new InvalidDataException($"frame {type} from a worker that has not proved the link secret");
                }

                switch (type)
                {
                    case GatewayWorkerFrameType.LinkChallenge:
                        if (listenerNonce is not null)
                        {
                            throw new InvalidDataException("a second LinkChallenge frame");
                        }

                        // Answer with the proof and our own nonce; the registration waits until the worker has proved itself back.
                        listenerNonce = GatewaySessionWire.DecodeNonceFrame(body.Span, "LinkChallenge");
                        connectorNonce = RandomNumberGenerator.GetBytes(GatewaySessionWire.NonceBytes);
                        channel.TryEnqueue(LinkAuth.Frame((byte)GatewayWorkerFrameType.LinkProof, GatewaySessionWire.ComputeProof(_linkSecret, listenerNonce), connectorNonce));
                        break;
                    case GatewayWorkerFrameType.LinkProof:
                        if (listenerNonce is null || connectorNonce is null || secured)
                        {
                            throw new InvalidDataException("unexpected LinkProof frame from a worker");
                        }

                        byte[] serverProof = GatewaySessionWire.DecodeNonceFrame(body.Span, "LinkProof");
                        if (!CryptographicOperations.FixedTimeEquals(serverProof, LinkAuth.ComputeServerProof(_linkSecret, listenerNonce, connectorNonce)))
                        {
                            throw new InvalidDataException("the worker failed the link proof (its link secret differs, or it is not the worker)");
                        }

                        LinkKeys keys = LinkAuth.DeriveKeys(_linkSecret, listenerNonce, connectorNonce);
                        channel.ActivateReceiveKey(keys.ServerToClient);
                        channel.EnqueueSendKey(keys.ClientToServer);
                        secured = true;
                        channel.TryEnqueue(LoopbackGatewayWorkerHost.BuildHelloRegisterFrame(_manifest));
                        break;
                    case GatewayWorkerFrameType.HelloAck:
                        var ack = DecodeHelloAck(body.Span);
                        if (ack.Accepted && !secured)
                        {
                            throw new InvalidDataException("HelloAck accepted before the worker proved the link secret");
                        }

                        _helloAck.TrySetResult(ack);
                        break;
                    case GatewayWorkerFrameType.SessionOpenReply:
                        OnOpenReply(body.Span);
                        break;
                    case GatewayWorkerFrameType.SessionServerPacket:
                        OnServerPacket(body);
                        break;
                    case GatewayWorkerFrameType.SessionRedirect:
                        OnRedirect(GatewaySessionWire.DecodeRedirect(body.Span));
                        break;
                    case GatewayWorkerFrameType.WorkerStatus:
                        OnWorkerStatus(GatewaySessionWire.DecodeWorkerStatus(body.Span));
                        break;
                    case GatewayWorkerFrameType.TransferStage:
                    case GatewayWorkerFrameType.TransferCancel:
                    case GatewayWorkerFrameType.TransferCommit:
                        OnTransferControl(GatewayTransferWire.DecodeControl(type, body.Span));
                        break;
                    case GatewayWorkerFrameType.ControlReply:
                        OnControlReply(ClusterControlWire.DecodeReply(body.Span));
                        break;
                    case GatewayWorkerFrameType.ControlSubmit:
                        OnControlSubmit(ClusterControlWire.DecodeSubmit(body.Span));
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
            _helloAck.TrySetResult((false, "the worker closed the link", string.Empty, true));
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

    /// <summary>
    /// Raised on the link's receive loop for every WorkerStatus frame (realm liveness, docs/ops/realm-liveness.md section 7). Handlers must not block and
    /// must not throw; one that throws is logged and the link goes on. Set before <see cref="ConnectAsync"/> so the first report is not missed.
    /// </summary>
    public Action<WorkerStatusReport>? WorkerStatusReceived { get; set; }

    /// <summary>
    /// Raised on the link's receive loop for every cross-worker transfer control frame (<see cref="GatewayTransferWire"/>). The handler must not block and
    /// must not throw; it answers with <see cref="SendTransferAck"/>. Without a handler the frame is ignored (the worker's coordinator then times out).
    /// </summary>
    public Action<GatewayTransferControl>? TransferControlReceived { get; set; }

    /// <summary>
    /// Raised on the link's receive loop for every <see cref="GatewayWorkerFrameType.ControlReply"/> frame (the worker's answer to a <see cref="SendControlRequest"/>).
    /// The handler must not block and must not throw. Without a handler the reply is ignored.
    /// </summary>
    public Action<ControlReply>? ControlReplyReceived { get; set; }

    /// <summary>Sends one control request to the worker (contract 5). False when the link is down. The reply arrives on <see cref="ControlReplyReceived"/>.</summary>
    public bool SendControlRequest(ControlRequest request) =>
        _channel is { } channel && channel.TryEnqueue(ClusterControlWire.EncodeRequest(request));

    /// <summary>
    /// Raised on the link's receive loop for every <see cref="GatewayWorkerFrameType.ControlSubmit"/> frame (a worker asks the gateway to start an operation, design section 2.3). An event so that the
    /// operator-restart handler (body kind 1) and the transfer handler (body kind 2) can both listen: each answers with <see cref="SendControlSubmitReply"/> only for the body kind it owns.
    /// Handlers must not block and must not throw; one that throws is logged and answered with a refusal. With no handler at all the worker is answered with a refusal here,
    /// so its request never waits for a reply that cannot come.
    /// </summary>
    public event Action<ControlSubmit>? ControlSubmitReceived;

    /// <summary>Answers a <see cref="ControlSubmit"/> to the worker. False when the link is down.</summary>
    public bool SendControlSubmitReply(ControlSubmitReply reply) =>
        _channel is { } channel && channel.TryEnqueue(ClusterControlWire.EncodeSubmitReply(reply));

    /// <summary>Tells the worker the outcome of an operation it submitted, or text for an operator (contract 5). False when the link is down.</summary>
    public bool SendControlNotice(ControlNotice notice) =>
        _channel is { } channel && channel.TryEnqueue(ClusterControlWire.EncodeNotice(notice));

    /// <summary>Whether the client session with this generation is open on this link.</summary>
    public bool HasSession(long sessionGeneration) => _sessions.ContainsKey(sessionGeneration);

    private void OnControlSubmit(ControlSubmit submit)
    {
        if (ControlSubmitReceived is not { } handler)
        {
            SendControlSubmitReply(new ControlSubmitReply(submit.RequestId, false, Guid.Empty, "this gateway link takes no control submits (its handoff and transfer hosting is off)", ControlReasonCode.NotHosted));
            return;
        }

        try
        {
            handler(submit);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("cluster.transport", ex, "Worker link {Worker}: the control submit handler failed", _worker);
            SendControlSubmitReply(new ControlSubmitReply(submit.RequestId, false, Guid.Empty, "the gateway failed to take the request", ControlReasonCode.GatewayFailure));
        }
    }

    private void OnControlReply(ControlReply reply)
    {
        try
        {
            ControlReplyReceived?.Invoke(reply);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("cluster.transport", ex, "Worker link {Worker}: the control reply handler failed", _worker);
        }
    }

    /// <summary>Answers a transfer control frame to the worker. False when the link is down.</summary>
    public bool SendTransferAck(GatewayTransferAck ack) =>
        _channel is { } channel && channel.TryEnqueue(GatewayTransferWire.EncodeAck(ack));

    private void OnTransferControl(GatewayTransferControl control)
    {
        try
        {
            TransferControlReceived?.Invoke(control);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("cluster.transport", ex, "Worker link {Worker}: the transfer control handler failed", _worker);
        }
    }

    private void OnWorkerStatus(WorkerStatusReport report)
    {
        try
        {
            WorkerStatusReceived?.Invoke(report);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("cluster.transport", ex, "Worker link {Worker}: the worker status handler failed", _worker);
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

    /// <summary>
    /// The worker asks to move an open session to another worker. A session that is not open, or whose account does not match, is ignored (counted as a
    /// dropped packet would be). The sink decides: when it takes the redirect the session turns silent and stays registered until the sink closes it;
    /// otherwise it is closed here as the worker closing it.
    /// </summary>
    private void OnRedirect(GatewaySessionRedirect redirect)
    {
        if (!_sessions.TryGetValue(redirect.SessionGeneration, out ClientSession? session) || session.AccountId != redirect.AccountId)
        {
            _logger.LogWarning("Worker link to {Worker}: redirect for unknown session (generation {Generation}, account {Account}); ignored", _worker, redirect.SessionGeneration, redirect.AccountId);
            return;
        }

        bool taken = false;
        lock (session.Gate)
        {
            if (!session.IsOpen || session.IsClosed || session.Redirected)
            {
                return;
            }

            session.Redirected = true;
        }

        try
        {
            taken = session.Sink.OnRedirect(redirect);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.gateway-client", ex);
            AnomalyLedger.Shared.Report("cluster", "gateway-fault", $"session-{session.Generation}", $"phase=redirect error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
            _logger.LogFault("cluster.transport", ex, "Gateway session {Generation}: the redirect handler failed", session.Generation);
        }

        if (!taken)
        {
            lock (session.Gate)
            {
                session.Redirected = false;
            }

            if (_sessions.TryRemove(new KeyValuePair<long, ClientSession>(session.Generation, session)))
            {
                // The worker keeps a session that asked for a redirect open (LinkSession.TryRedirect), so a refused redirect must tell it to close
                // the session; otherwise its LinkSession and WorldSession stay at the character screen until the link drops.
                _channel?.TryEnqueue(GatewaySessionWire.EncodeClose(session.Generation, session.AccountId, ClusterSessionDetachReason.ClientDisconnect));
                NotifyClosed(session, ClusterSessionDetachReason.WorkerClosed);
            }
        }
    }

    private void OnServerPacket(ReadOnlyMemory<byte> body)
    {
        ClusterSessionPacket packet = GatewaySessionWire.DecodePacket(body);
        if (_sessions.TryGetValue(packet.SessionGeneration, out ClientSession? session))
        {
            bool accept;
            lock (session.Gate)
            {
                accept = session.IsOpen && !session.IsClosed && !session.Redirected && packet.AccountId == session.AccountId && packet.OwnerEpoch == session.Epoch
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
            // A redirected session belongs to the sink's hand-over now: its end (the worker's close, a lost link) is not the client's end.
            wasOpen = session.IsOpen && !session.IsClosed && !session.Redirected;
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

    private static (bool Accepted, string Reason, string OwnedMaps, bool AllowInstanceMaps) DecodeHelloAck(ReadOnlySpan<byte> span)
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

        string reason = Encoding.UTF8.GetString(span.Slice(5, length));

        // The tail appended by the map routing change; an ack from an older worker ends after the reason and reads as "owns every map".
        string ownedMaps = string.Empty;
        bool allowInstanceMaps = true;
        int offset = 5 + length;
        if (offset < span.Length)
        {
            if (span.Length - offset < 5)
            {
                throw new InvalidDataException($"truncated HelloAck map tail ({span.Length - offset} B)");
            }

            int ownedLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
            offset += 4;
            if (ownedLength < 0 || ownedLength > span.Length - offset - 1)
            {
                throw new InvalidDataException($"invalid HelloAck owned map length ({ownedLength})");
            }

            ownedMaps = Encoding.UTF8.GetString(span.Slice(offset, ownedLength));
            offset += ownedLength;
            allowInstanceMaps = span[offset] != 0;
        }

        return (span[0] != 0, reason, ownedMaps, allowInstanceMaps);
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

        /// <summary>The worker asked to move the session and the sink took it over; the session is silent until the sink closes it.</summary>
        public bool Redirected;
    }
}

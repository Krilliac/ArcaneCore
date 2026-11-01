using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Auxiliary;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Kernel.Net;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Auxiliary;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>Where a worker-side session sends its server packets, and how it tells the gateway it closed.</summary>
public interface IGatewaySessionOutput : IRemoteSessionOutput
{
    /// <summary>The session ended on the worker: send SessionClose to the gateway (once; later calls do nothing).</summary>
    void Close(ClusterSessionDetachReason reason);
}

/// <summary>A worker-side session opened by <see cref="IGatewaySessionAcceptor"/>.</summary>
public interface IGatewayRemoteSession
{
    /// <summary>Called once, after the open reply is queued to the gateway (anything sent from here on follows it).</summary>
    void Start();

    /// <summary>One client opcode, in sequence order, from the link's receive loop. Must not block.</summary>
    void Deliver(WorldOpcode opcode, byte[] payload);

    /// <summary>
    /// <see cref="Deliver(WorldOpcode, byte[])"/> with the time the gateway received the client packet, translated to this worker's
    /// monotonic millisecond clock (<see cref="ArcaneCore.Kernel.Logging.Clock.Milliseconds"/>, low 32 bits). The movement anticheat judges
    /// the packet at that time instead of the time it came off the link. The default ignores the time.
    /// </summary>
    void Deliver(WorldOpcode opcode, byte[] payload, uint receivedMs) => Deliver(opcode, payload);

    /// <summary>The gateway closed the session or the link was lost. Must not block.</summary>
    void CloseFromGateway(ClusterSessionDetachReason reason);
}

/// <summary>What the link host knows about the gateway side when it hands a control request to its handler.</summary>
/// <param name="AdmittedGateways">How many distinct gateways (node and incarnation) have an open admitted link to this worker; the whole-World handoff is only defined for one.</param>
public readonly record struct GatewayControlContext(int AdmittedGateways);

/// <summary>
/// Executes the unified control requests of an admitted gateway (<see cref="ControlRequest"/>, contract 5; design _wf/handoff-control-DESIGN.md section 2) and
/// returns the reply the link host sends back on the same link. Implemented by <c>WorkerControlService</c>. Called off the link's receive loop, concurrently for
/// concurrent requests; an exception it throws is answered as <see cref="ControlStatus.Failed"/>.
/// </summary>
public interface IGatewayControlHandler
{
    /// <summary>Runs one verb. <paramref name="cancellationToken"/> is cancelled when the link host stops.</summary>
    Task<ControlReply> HandleAsync(ControlRequest request, GatewayControlContext context, CancellationToken cancellationToken);
}

/// <summary>The acceptor's answer: <see cref="AuthResponseCode.Ok"/> with a session, or a refusal code for the client.</summary>
public readonly record struct GatewaySessionAcceptResult(AuthResponseCode Code, string? Reason, IGatewayRemoteSession? Session)
{
    public static GatewaySessionAcceptResult Accepted(IGatewayRemoteSession session) => new(AuthResponseCode.Ok, null, session);

    public static GatewaySessionAcceptResult Refused(AuthResponseCode code, string reason) => new(code, reason, null);
}

/// <summary>Creates the worker-side session of a client the gateway authenticated (World: <see cref="WorldGatewaySessionAcceptor"/>).</summary>
public interface IGatewaySessionAcceptor
{
    ValueTask<GatewaySessionAcceptResult> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="OpenAsync"/> for a session the gateway moved here from the login worker (SessionAdopt): the same authentication, and the
    /// buffered CMSG_PLAYER_LOGIN is delivered to the new session before it starts. The default refuses (an acceptor that does not take adopted sessions).
    /// </summary>
    ValueTask<GatewaySessionAcceptResult> OpenAdoptedAsync(GatewaySessionAdoptRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken)
        => ValueTask.FromResult(GatewaySessionAcceptResult.Refused(AuthResponseCode.Unavailable, "the worker does not adopt sessions"));
}

/// <summary>
/// The worker end of the Tier 3a session link (<c>docs/design/hot-reload-tiers.md</c> §5.3): listens for gateways, runs the
/// link handshake (a challenge answered with an HMAC of the shared link secret, then the <see cref="ClusterNodeManifest"/>
/// admission of a <see cref="ClusterRole.Gateway"/> peer) and multiplexes client sessions over each link.
/// <para>
/// Envelope rules, per session: a SessionOpen generation must be higher than every generation opened before on that link
/// (a replayed or reordered open is refused); a client packet whose account, generation or owner epoch does not match the
/// open session, or whose sequence is not higher than the last one delivered, is dropped and counted
/// (<see cref="DroppedClientPackets"/>), never delivered. Server packets leave with their own ascending sequence and the
/// current owner epoch. A session's packets sent before its open reply is queued wait and follow the reply, so the client
/// can never see a world packet before SMSG_AUTH_RESPONSE.
/// </para>
/// </summary>
public sealed class GatewaySessionLinkHost : IAsyncDisposable
{
    /// <summary>
    /// The shortest link secret either end accepts, on every bind address. An empty or short secret makes the link proof
    /// computable by any local process, and an admitted gateway opens a session for any account it names.
    /// </summary>
    public const int MinLinkSecretLength = 16;

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RejectFlushBound = TimeSpan.FromSeconds(2);

    private readonly ClusterNodeManifest _workerManifest;
    private readonly IGatewaySessionAcceptor _acceptor;
    private readonly Func<long> _ownerEpoch;
    private readonly string _linkSecret;
    private readonly ILogger _logger;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, Link> _links = new();
    private readonly GatewayWardenManifestVerifier? _wardenVerifier;
    private readonly Action<long, ClusterWardenVerdict>? _wardenVerdictAccepted;
    private readonly Func<WorkerStatusReport>? _workerStatus;
    private readonly Lock _sourceGate = new();
    private readonly Dictionary<ClusterWardenVerdictSource, int> _openLinksBySource = [];
    // Newest incarnation admitted per node id. A gateway process keeps one incarnation for life and reconnects with it, so
    // an incarnation is only superseded (and retirable) once a different incarnation of its node id has been admitted.
    private readonly Dictionary<string, Guid> _newestIncarnationByNode = [];
    // Incarnations that lost newest status. Incarnation ids are unordered GUIDs, so supersession is recorded explicitly and is
    // monotonic: a superseded incarnation that reconnects never becomes newest again and never supersedes the live one.
    private readonly HashSet<ClusterWardenVerdictSource> _supersededSources = [];
    private readonly LogGate _dropGate = new(TimeSpan.FromSeconds(10));
    // Control requests being answered; DisposeAsync waits for them (each ends at once: the handler's token is the host's stop token).
    private readonly ConcurrentDictionary<Task, int> _controlRuns = new();
    private static readonly TimeSpan ControlStopWait = TimeSpan.FromSeconds(5);
    private Task? _acceptTask;
    private int _nextLinkId;
    private long _droppedClientPackets;
    private long _acceptedWardenVerdicts;
    private long _refusedWardenVerdicts;
    private string? _lastWardenRefusal;

    public GatewaySessionLinkHost(
        ClusterNodeManifest workerManifest,
        IGatewaySessionAcceptor acceptor,
        Func<long> ownerEpoch,
        string linkSecret,
        IPEndPoint bindEndPoint,
        ILogger? logger = null,
        GatewayWardenManifestVerifier? wardenVerifier = null,
        Action<long, ClusterWardenVerdict>? wardenVerdictAccepted = null,
        Func<WorkerStatusReport>? workerStatus = null)
    {
        ArgumentNullException.ThrowIfNull(workerManifest);
        ArgumentNullException.ThrowIfNull(acceptor);
        ArgumentNullException.ThrowIfNull(ownerEpoch);
        ArgumentNullException.ThrowIfNull(linkSecret);
        ArgumentNullException.ThrowIfNull(bindEndPoint);
        ThrowIfWeakSecret(linkSecret, "Cluster:Worker:LinkSecret");

        _workerManifest = workerManifest;
        _acceptor = acceptor;
        _ownerEpoch = ownerEpoch;
        _linkSecret = linkSecret;
        _logger = logger ?? NullLogger.Instance;
        _listener = new TcpListener(bindEndPoint);
        _wardenVerifier = wardenVerifier;
        _wardenVerdictAccepted = wardenVerdictAccepted;
        _workerStatus = workerStatus;
    }

    /// <summary>
    /// Send the worker's current liveness report (the <c>workerStatus</c> constructor argument) to every admitted gateway link; the gateway's realm
    /// liveness writer reads it. Returns the number of links it was queued on (0 without a provider). Never throws for a failed provider or link
    /// (logged; the next call tries again). Thread-safe: the frame is queued, not written on the caller's thread.
    /// </summary>
    public int BroadcastWorkerStatus()
    {
        int sent = 0;
        foreach (Link link in _links.Values)
        {
            if (link.Admitted && TrySendWorkerStatus(link))
            {
                sent++;
            }
        }

        return sent;
    }

    private bool TrySendWorkerStatus(Link link)
    {
        if (_workerStatus is not { } provider)
        {
            return false;
        }

        try
        {
            return link.Channel.TryEnqueue(GatewaySessionWire.EncodeWorkerStatus(provider()));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Gateway link from {Remote}: the worker status could not be sent ({Message})", link.Remote, ex.Message);
            return false;
        }
    }

    public IPEndPoint BoundEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>
    /// Refuse a link secret shorter than <see cref="MinLinkSecretLength"/> (loopback included: any local process can reach a
    /// loopback port). <paramref name="key"/> names the setting in the message.
    /// </summary>
    public static void ThrowIfWeakSecret(string? linkSecret, string key)
    {
        if (string.IsNullOrEmpty(linkSecret) || linkSecret.Length < MinLinkSecretLength)
        {
            throw new ArgumentException(
                $"the gateway link needs a secret of at least {MinLinkSecretLength} characters ({key}, the same on both ends); it is {(string.IsNullOrEmpty(linkSecret) ? "empty" : "too short")}",
                nameof(linkSecret));
        }
    }

    /// <summary>Client packets dropped by the envelope rules (stale sequence, wrong generation, account or epoch, unknown session).</summary>
    public long DroppedClientPackets => Interlocked.Read(ref _droppedClientPackets);

    /// <summary>Warden verdict frames of a gateway that passed every check and were handed to the verdict callback.</summary>
    public long AcceptedWardenVerdicts => Interlocked.Read(ref _acceptedWardenVerdicts);

    /// <summary>Warden verdict frames refused (unknown session, mismatched posture, stale epoch or generation, replay, no verifier).</summary>
    public long RefusedWardenVerdicts => Interlocked.Read(ref _refusedWardenVerdicts);

    /// <summary>Why the newest refused Warden verdict was refused (null when none was).</summary>
    public string? LastWardenRefusal => Volatile.Read(ref _lastWardenRefusal);

    /// <summary>Open sessions over every link.</summary>
    public int SessionCount => _links.Values.Sum(link => link.Sessions.Count);

    /// <summary>Admitted gateway links.</summary>
    public int AdmittedLinkCount => _links.Values.Count(link => link.Admitted);

    /// <summary>Distinct gateways (node id and incarnation) with at least one open admitted link: a gateway that holds several links counts once.</summary>
    public int AdmittedGatewayCount
    {
        get
        {
            lock (_sourceGate)
            {
                return _openLinksBySource.Count;
            }
        }
    }

    /// <summary>
    /// Executes the <see cref="ControlRequest"/> frames of an admitted gateway. Set once, before <see cref="Start"/>. With none set (the worker's opt-in control keys are off)
    /// every request is answered <see cref="ControlStatus.Disabled"/> and the link stays up: a gateway on contract 5 may ask, and an old worker closes the link instead.
    /// </summary>
    public IGatewayControlHandler? ControlHandler { get; set; }

    /// <summary>Test seam: awaited before every accept; an exception it throws behaves like a failed accept.</summary>
    internal Func<CancellationToken, ValueTask>? BeforeAcceptForTests { get; set; }

    public void Start()
    {
        _listener.Start();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_stop.Token));
    }

    // The shared accept loop: a transient accept failure (connection reset, fd exhaustion) is logged (throttled), backed off
    // and retried; only cancellation (DisposeAsync) ends it, so a gateway can always reconnect.
    private Task AcceptLoopAsync(CancellationToken ct) =>
        AcceptLoop.RunAsync(
            async token =>
            {
                if (BeforeAcceptForTests is { } before)
                {
                    await before(token).ConfigureAwait(false);
                }

                return await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            },
            client =>
            {
                int id = Interlocked.Increment(ref _nextLinkId);
                var link = new Link(id, new GatewayLinkChannel(client), client.Client.RemoteEndPoint?.ToString() ?? "unknown");
                _links[id] = link;
                link.Receive = Task.Run(() => RunLinkAsync(link, ct), CancellationToken.None);
            },
            _logger,
            ct);

    /// <summary>Answers a failed handshake step with a plain HelloAck refusal (the peer is not authenticated, so nothing secret goes in it) and closes the link.</summary>
    private async Task RefuseLinkAsync(Link link, string reason)
    {
        _logger.LogWarning("Gateway link from {Remote} refused: {Reason}", link.Remote, reason);
        link.Channel.TryEnqueue(LoopbackGatewayWorkerHost.BuildHelloAckFrame(false, reason, _workerManifest.OwnedMaps, _workerManifest.AllowInstanceMaps));
        await link.Channel.FlushAndCloseAsync(RejectFlushBound).ConfigureAwait(false);
    }

    private async Task RunLinkAsync(Link link, CancellationToken ct)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(GatewaySessionWire.NonceBytes);
        bool proven = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, link.Channel.Closed);
        using var handshake = new CancellationTokenSource(HandshakeTimeout);
        using CancellationTokenRegistration expire = handshake.Token.Register(() =>
        {
            if (!link.Admitted)
            {
                link.Channel.Close();
            }
        });
        try
        {
            link.Channel.TryEnqueue(GatewaySessionWire.EncodeNonceFrame(GatewayWorkerFrameType.LinkChallenge, nonce));
            while (!linked.IsCancellationRequested)
            {
                byte[]? frame = await link.Channel.ReadFrameAsync(linked.Token).ConfigureAwait(false);
                if (frame is null || frame.Length == 0)
                {
                    break;
                }

                var type = (GatewayWorkerFrameType)frame[0];
                ReadOnlyMemory<byte> body = frame.AsMemory(1);
                if (!link.Admitted)
                {
                    switch (type)
                    {
                        case GatewayWorkerFrameType.LinkProof:
                            if (proven)
                            {
                                throw new InvalidDataException("a second LinkProof frame");
                            }

                            if (!LinkAuth.TrySplitClientProof(body.Span, out byte[] proof, out byte[] connectorNonce, out bool legacyProof))
                            {
                                if (legacyProof)
                                {
                                    // A pre-v3 gateway sends the bare 32-byte proof: it can neither verify this worker nor MAC its frames.
                                    await RefuseLinkAsync(link, "gateway link protocol is older than this worker (v3 adds mutual proof and frame MACs); run the same build on both ends").ConfigureAwait(false);
                                    return;
                                }

                                throw new InvalidDataException($"LinkProof frame of {body.Length} B (expected {LinkAuth.ClientProofBodyBytes})");
                            }

                            if (!CryptographicOperations.FixedTimeEquals(proof, GatewaySessionWire.ComputeProof(_linkSecret, nonce)))
                            {
                                await RefuseLinkAsync(link, "link proof missing or wrong (Cluster:Worker:LinkSecret)").ConfigureAwait(false);
                                return;
                            }

                            proven = true;
                            // The worker proves itself back (bound to both nonces), then both directions switch to MAC-protected frames:
                            // the proof frame is the last one sent in the clear, and everything the gateway sends from now on must carry a tag.
                            LinkKeys keys = LinkAuth.DeriveKeys(_linkSecret, nonce, connectorNonce);
                            link.Channel.TryEnqueue(GatewaySessionWire.EncodeNonceFrame(
                                GatewayWorkerFrameType.LinkProof, LinkAuth.ComputeServerProof(_linkSecret, nonce, connectorNonce)));
                            link.Channel.EnqueueSendKey(keys.ServerToClient);
                            link.Channel.ActivateReceiveKey(keys.ClientToServer);
                            continue;
                        case GatewayWorkerFrameType.HelloRegister:
                            ClusterNodeManifest peer;
                            try
                            {
                                peer = LoopbackGatewayWorkerHost.DeserializeManifest(body.Span);
                            }
                            catch (ArgumentException ex)
                            {
                                FaultLedger.Shared.Record("cluster.gateway-host", ex);
                                AnomalyLedger.Shared.Report("cluster", "gateway-fault", link.Remote, $"phase=hello-register error={ex.Message}", AnomalySeverity.Warning);
                                throw new InvalidDataException("malformed HelloRegister manifest", ex);
                            }

                            string? reason = null;
                            bool admitted = proven
                                && peer.Role == ClusterRole.Gateway
                                && _workerManifest.TryAdmit(peer, out reason);
                            reason = !proven ? "link proof missing or wrong (Cluster:Worker:LinkSecret)"
                                : peer.Role != ClusterRole.Gateway ? $"peer role {peer.Role} is not Gateway"
                                : reason;
                            link.Channel.TryEnqueue(LoopbackGatewayWorkerHost.BuildHelloAckFrame(admitted, reason ?? string.Empty, _workerManifest.OwnedMaps, _workerManifest.AllowInstanceMaps));
                            if (!admitted)
                            {
                                _logger.LogWarning("Gateway link from {Remote} refused: {Reason}", link.Remote, reason);
                                await link.Channel.FlushAndCloseAsync(RejectFlushBound).ConfigureAwait(false);
                                return;
                            }

                            var source = new ClusterWardenVerdictSource(peer.NodeId, peer.NodeIncarnation);
                            lock (_sourceGate)
                            {
                                _openLinksBySource[source] = _openLinksBySource.GetValueOrDefault(source) + 1;
                                link.Source = source;
                                if (!_supersededSources.Contains(source))
                                {
                                    if (_newestIncarnationByNode.TryGetValue(source.NodeId, out Guid previous) && previous != source.NodeIncarnation)
                                    {
                                        // The previous newest is now superseded for good: retire it at once if it has no open
                                        // link, else ReleaseSource retires it when its last link goes.
                                        var superseded = new ClusterWardenVerdictSource(source.NodeId, previous);
                                        _supersededSources.Add(superseded);
                                        if (!_openLinksBySource.ContainsKey(superseded))
                                        {
                                            _wardenVerifier?.RetireSource(superseded);
                                        }
                                    }

                                    _newestIncarnationByNode[source.NodeId] = source.NodeIncarnation;
                                }
                            }

                            link.Admitted = true;
                            _logger.LogInformation("Gateway link from {Remote} admitted (node {Node}, incarnation {Incarnation})",
                                link.Remote, peer.NodeId, peer.NodeIncarnation);
                            TrySendWorkerStatus(link); // the gateway knows this worker's state at once, not after the next periodic report
                            continue;
                        default:
                            _logger.LogWarning("Gateway link from {Remote}: frame {Type} before admission; closing", link.Remote, type);
                            return;
                    }
                }

                switch (type)
                {
                    case GatewayWorkerFrameType.LinkClock:
                        if (link.ClockOffset is not null)
                        {
                            throw new InvalidDataException("a second LinkClock frame");
                        }

                        link.ClockOffset = MovementClock.Estimate(GatewaySessionWire.DecodeLinkClock(body.Span), (uint)Clock.Milliseconds());
                        break;
                    case GatewayWorkerFrameType.WardenPosture:
                        OnWardenPosture(link, body.Span);
                        break;
                    case GatewayWorkerFrameType.SessionOpen:
                        OnOpen(link, body.Span, ct);
                        break;
                    case GatewayWorkerFrameType.SessionAdopt:
                        OnAdopt(link, body.Span, ct);
                        break;
                    case GatewayWorkerFrameType.SessionClientPacket:
                        OnClientPacket(link, body);
                        break;
                    case GatewayWorkerFrameType.WardenVerdict:
                        OnWardenVerdict(link, body.Span);
                        break;
                    case GatewayWorkerFrameType.TransferAck:
                        OnTransferAck(GatewayTransferWire.DecodeAck(body.Span));
                        break;
                    case GatewayWorkerFrameType.ControlRequest:
                        OnControlRequest(link, ClusterControlWire.DecodeRequest(body.Span));
                        break;
                    case GatewayWorkerFrameType.SessionClose:
                        (long generation, int accountId, ClusterSessionDetachReason reason) = GatewaySessionWire.DecodeClose(body.Span);
                        if (link.Sessions.TryGetValue(generation, out LinkSession? closing) && closing.AccountId == accountId)
                        {
                            link.Sessions.TryRemove(generation, out _);
                            closing.CloseFromLink(reason);
                        }

                        break;
                    default:
                        throw new InvalidDataException($"unexpected frame {type} from a gateway");
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is InvalidDataException or InvalidOperationException)
            {
                _logger.LogWarning("Gateway link from {Remote} broke the protocol ({Message}); closing", link.Remote, ex.Message);
            }
            else if (ex is not (OperationCanceledException or ObjectDisposedException or SocketException or IOException or EndOfStreamException))
            {
                FaultLedger.Shared.Record("cluster.transport", ex);
            }
        }
        finally
        {
            link.Channel.Close();
            _links.TryRemove(link.Id, out _);
            ReleaseSource(link);
            foreach (LinkSession session in link.Sessions.Values)
            {
                session.CloseFromLink(ClusterSessionDetachReason.LinkLost);
            }

            link.Sessions.Clear();
            if (link.Admitted)
            {
                _logger.LogInformation("Gateway link from {Remote} closed", link.Remote);
            }

            await link.Channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Raised on a link's receive loop for every TransferAck frame (the gateway's answer to a transfer control frame). The handler must not block and
    /// must not throw. Set once, before the first transfer.
    /// </summary>
    public Action<GatewayTransferAck>? TransferAckReceived { get; set; }

    private void OnTransferAck(GatewayTransferAck ack)
    {
        try
        {
            TransferAckReceived?.Invoke(ack);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("cluster.transport", ex, "The transfer ack handler failed");
        }
    }

    /// <summary>
    /// Sends a transfer control frame on the admitted link that carries the session <paramref name="sessionGeneration"/>. False when no admitted link has
    /// that session (the gateway restarted or the client left) or the link is down.
    /// </summary>
    public bool TrySendTransferControl(long sessionGeneration, GatewayTransferControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        foreach (Link link in _links.Values)
        {
            if (link.Admitted && link.Sessions.ContainsKey(sessionGeneration))
            {
                return link.Channel.TryEnqueue(GatewayTransferWire.Encode(control));
            }
        }

        return false;
    }

    /// <summary>
    /// A control request of an admitted gateway: answered on the same link, off the receive loop (a verb may take a minute: a freeze, a flush, a promotion). The decoder
    /// already refused malformed bytes (an <see cref="InvalidDataException"/> closes the link). Never throws.
    /// </summary>
    private void OnControlRequest(Link link, ControlRequest request)
    {
        CancellationToken stopping = _stop.Token;
        Task run = Task.Run(() => AnswerControlAsync(link, request, stopping), CancellationToken.None);
        _controlRuns[run] = 0;
        _ = run.ContinueWith(
            static (done, state) => ((ConcurrentDictionary<Task, int>)state!).TryRemove(done, out _),
            _controlRuns, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task AnswerControlAsync(Link link, ControlRequest request, CancellationToken stopping)
    {
        ControlReply reply;
        if (ControlHandler is not { } handler)
        {
            reply = ControlFailure(request, ControlStatus.Disabled,
                "this worker has no control service (Cluster:Worker:Handoff:Enabled and Cluster:Worker:Transfers:Enabled are off)");
        }
        else
        {
            try
            {
                reply = await handler.HandleAsync(request, new GatewayControlContext(AdmittedGatewayCount), stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                reply = ControlFailure(request, ControlStatus.Failed, "the worker is stopping");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogFault("cluster.control", ex, "Control request {Verb} of operation {Operation} failed", request.Verb, request.OperationId);
                reply = ControlFailure(request, ControlStatus.Failed, ex.Message);
            }
        }

        byte[] frame;
        try
        {
            frame = ClusterControlWire.EncodeReply(reply);
        }
        catch (ArgumentException ex)
        {
            // A handler that built a reply the wire cannot carry (a payload of the wrong type, too many maps, a bundle over the limit): the gateway still gets an answer.
            _logger.LogFault("cluster.control", ex, "The reply to control request {Verb} of operation {Operation} could not be encoded", request.Verb, request.OperationId);
            frame = ClusterControlWire.EncodeReply(ControlFailure(request, ControlStatus.Failed, "the reply could not be encoded: " + ex.Message));
        }

        link.Channel.TryEnqueue(frame);
    }

    private static ControlReply ControlFailure(ControlRequest request, ControlStatus status, string reason) =>
        new(request.RequestId, request.Kind, request.OperationId, request.Verb, status, reason, ControlNoPayload.Instance);

    /// <summary>Open admitted links of one (node, incarnation); 0 when none. For tests and diagnostics.</summary>
    internal int OpenLinkCount(ClusterWardenVerdictSource source)
    {
        lock (_sourceGate)
        {
            return _openLinksBySource.GetValueOrDefault(source);
        }
    }

    /// <summary>Ids of the currently admitted links, for routing a verdict to <see cref="TryAcceptWardenVerdict"/>.</summary>
    internal int[] AdmittedLinkIds() => [.. _links.Values.Where(link => link.Admitted).Select(link => link.Id)];

    /// <summary>
    /// Routes a Warden verdict that arrived on an admitted link through the verifier, keyed by the link's own peer identity
    /// (node id and incarnation from the admitted manifest, never from the verdict body). A link that is unknown, closed or
    /// not admitted, or a host built without a verifier, refuses the verdict. Thread-safe; callable from any thread.
    /// </summary>
    internal bool TryAcceptWardenVerdict(int linkId, ClusterWardenVerdict? verdict, long activeSessionGeneration, long activeOwnerEpoch, out string? discardReason)
    {
        if (_wardenVerifier is null)
        {
            discardReason = "no Warden verdict verifier is attached to this link host";
            return false;
        }

        ClusterWardenVerdictSource source;
        lock (_sourceGate)
        {
            if (!_links.TryGetValue(linkId, out Link? link) || link.Source is not { } linkSource)
            {
                discardReason = "Warden verdict from a link that is not an open admitted gateway link";
                return false;
            }

            source = linkSource;
        }

        return _wardenVerifier.TryAcceptVerdict(verdict, source, activeSessionGeneration, activeOwnerEpoch, out discardReason);
    }

    /// <summary>
    /// Drops this link's hold on its (node, incarnation). The verifier retires the source only when the last open link of that
    /// exact incarnation is gone AND a different incarnation of the same node id has been admitted (see admission), so a live
    /// gateway that drops its only link and reconnects with the same incarnation keeps its verdicts, and a newer incarnation
    /// never retires an older one that still has links.
    /// Runs once per link from the receive loop's finally block (every close path: clean, protocol fault, cancellation, dispose).
    /// </summary>
    private void ReleaseSource(Link link)
    {
        lock (_sourceGate)
        {
            if (link.Source is not { } source)
            {
                return;
            }

            link.Source = null;
            int remaining = _openLinksBySource.GetValueOrDefault(source) - 1;
            if (remaining > 0)
            {
                _openLinksBySource[source] = remaining;
                return;
            }

            _openLinksBySource.Remove(source);
            if (_supersededSources.Contains(source))
            {
                _wardenVerifier?.RetireSource(source);
            }
        }
    }

    /// <summary>
    /// A Warden verdict frame of an admitted gateway link. It must name an open session of this link (generation and account), carry the
    /// gateway's Warden posture matching this worker's expected one, and pass the verifier's epoch, generation and replay rules, which
    /// key it by the link's own peer identity. Only then is the verdict callback called (on the link's receive loop: it must not block).
    /// </summary>
    private void OnWardenVerdict(Link link, ReadOnlySpan<byte> body)
    {
        (ClusterWardenVerdict verdict, GatewayWardenPosture posture) = GatewaySessionWire.DecodeWardenVerdict(body);
        if (!link.Sessions.TryGetValue(verdict.SessionGeneration, out LinkSession? session) || session.AccountId != verdict.AccountId)
        {
            RefuseWardenVerdict(link, verdict, "Warden verdict for a session that is not open on this link");
            return;
        }

        if (_wardenVerifier is null)
        {
            RefuseWardenVerdict(link, verdict, "no Warden verdict verifier is attached to this link host");
            return;
        }

        if (!_wardenVerifier.VerifyPeerPosture(posture, out string? mismatch))
        {
            RefuseWardenVerdict(link, verdict, mismatch ?? "Warden posture mismatch");
            return;
        }

        if (!TryAcceptWardenVerdict(link.Id, verdict, session.Generation, _ownerEpoch(), out string? discard))
        {
            RefuseWardenVerdict(link, verdict, discard ?? "refused");
            return;
        }

        Interlocked.Increment(ref _acceptedWardenVerdicts);
        try
        {
            _wardenVerdictAccepted?.Invoke(session.Generation, verdict);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("cluster.warden-verdict", ex, "Applying the Warden verdict of generation {Generation} failed", verdict.SessionGeneration);
        }
    }

    /// <summary>
    /// The gateway's Warden posture. With a verifier attached it must match this worker's expected posture; a mismatch closes the link
    /// (protocol break, logged) so the gateway cannot carry sessions. Without a verifier the frame is ignored.
    /// </summary>
    private void OnWardenPosture(Link link, ReadOnlySpan<byte> body)
    {
        GatewayWardenPosture posture = GatewaySessionWire.DecodeWardenPosture(body);
        if (_wardenVerifier is null)
        {
            return;
        }

        if (!_wardenVerifier.VerifyPeerPosture(posture, out string? mismatch))
        {
            Volatile.Write(ref _lastWardenRefusal, mismatch);
            throw new InvalidDataException("gateway Warden posture refused: " + (mismatch ?? "mismatch"));
        }

        link.PostureProven = true;
    }

    private void RefuseWardenVerdict(Link link, ClusterWardenVerdict verdict, string reason)
    {
        Interlocked.Increment(ref _refusedWardenVerdicts);
        Volatile.Write(ref _lastWardenRefusal, reason);
        _logger.LogWarning("Gateway link from {Remote}: refused a Warden verdict (generation {Generation}, account {Account}, check {Check}): {Reason}",
            link.Remote, verdict.SessionGeneration, verdict.AccountId, verdict.CheckId, reason);
    }

    private void OnOpen(Link link, ReadOnlySpan<byte> body, CancellationToken ct)
    {
        (long requestId, GatewaySessionOpenRequest request) = GatewaySessionWire.DecodeOpen(body);
        BeginOpen(link, requestId, request, null, ct);
    }

    /// <summary>A session moved here by the gateway after a login redirect: the open rules of <see cref="OnOpen"/> apply unchanged (generation order, Warden posture).</summary>
    private void OnAdopt(Link link, ReadOnlySpan<byte> body, CancellationToken ct)
    {
        (long requestId, GatewaySessionAdoptRequest adopt) = GatewaySessionWire.DecodeAdopt(body);
        BeginOpen(link, requestId, adopt.Open, adopt, ct);
    }

    private void BeginOpen(Link link, long requestId, GatewaySessionOpenRequest request, GatewaySessionAdoptRequest? adopt, CancellationToken ct)
    {
        if (request.SessionGeneration <= link.HighestGeneration || request.AccountId <= 0)
        {
            link.Channel.TryEnqueue(GatewaySessionWire.EncodeOpenReply(requestId, GatewaySessionOpenReply.Refuse(
                AuthResponseCode.Failed, $"stale or invalid session generation {request.SessionGeneration} (highest {link.HighestGeneration})")));
            return;
        }

        if (_wardenVerifier is not null && !link.PostureProven)
        {
            // The world skips its own Warden for gateway-backed sessions, so no session may open on a link whose gateway has not proven
            // the same Warden posture (enabled flag, module digest, options, revision).
            link.Channel.TryEnqueue(GatewaySessionWire.EncodeOpenReply(requestId, GatewaySessionOpenReply.Refuse(
                AuthResponseCode.Unavailable, "the gateway has not proven a matching Warden posture")));
            return;
        }

        link.HighestGeneration = request.SessionGeneration;
        var session = new LinkSession(this, link, request.SessionGeneration, request.AccountId);
        link.Sessions[request.SessionGeneration] = session;
        _ = OpenAsync(link, session, requestId, request, adopt, ct);
    }

    private async Task OpenAsync(Link link, LinkSession session, long requestId, GatewaySessionOpenRequest request, GatewaySessionAdoptRequest? adopt, CancellationToken ct)
    {
        GatewaySessionAcceptResult result;
        try
        {
            result = adopt is null
                ? await _acceptor.OpenAsync(request, session, ct).ConfigureAwait(false)
                : await _acceptor.OpenAdoptedAsync(adopt, session, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The host is stopping: cancellation is not a fault. The open is refused so the gateway does not wait for a reply.
            result = GatewaySessionAcceptResult.Refused(AuthResponseCode.Failed, "the worker is shutting down");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AnomalyLedger.Shared.Report("cluster", "gateway-fault", $"account-{request.AccountId}", $"phase=session-open error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
            // LogFault records the fault ledger entry itself.
            _logger.LogFault("cluster.transport", ex, "Gateway session open for account {Account} failed", request.AccountId);
            result = GatewaySessionAcceptResult.Refused(AuthResponseCode.Failed, "the worker failed to open the session");
        }

        if (result.Code != AuthResponseCode.Ok || result.Session is null)
        {
            link.Sessions.TryRemove(session.Generation, out _);
            session.Refuse(requestId, result.Code == AuthResponseCode.Ok ? AuthResponseCode.Failed : result.Code, result.Reason ?? "refused");
            return;
        }

        if (!session.Accept(requestId, result.Session, _ownerEpoch()))
        {
            // The gateway closed the client (or the link dropped) while the open ran: nothing was started.
            result.Session.CloseFromGateway(ClusterSessionDetachReason.ClientDisconnect);
            return;
        }

        result.Session.Start();
    }

    private void OnClientPacket(Link link, ReadOnlyMemory<byte> body)
    {
        ClusterSessionPacket packet = GatewaySessionWire.DecodeClientPacket(body, out uint gatewayReceivedMs);
        if (!link.Sessions.TryGetValue(packet.SessionGeneration, out LinkSession? session)
            || !session.TryAcceptInbound(packet, _ownerEpoch(), out IGatewayRemoteSession? target))
        {
            long dropped = Interlocked.Increment(ref _droppedClientPackets);
            if (_dropGate.TryEnter(out int suppressed))
            {
                _logger.LogWarning(
                    "Gateway link from {Remote}: dropped client packet (generation {Generation}, account {Account}, epoch {Epoch}, sequence {Sequence}); {Dropped} dropped in all, {Suppressed} lines suppressed",
                    link.Remote, packet.SessionGeneration, packet.AccountId, packet.OwnerEpoch, packet.Sequence, dropped, suppressed);
            }

            return;
        }

        // The gateway's stamp, moved to this worker's clock. Without a LinkClock frame (a gateway that skipped it) the worker keeps its own
        // arrival time: the target's default Deliver ignores the stamp.
        uint now = (uint)Clock.Milliseconds();
        if (link.ClockOffset is { } clock)
        {
            target!.Deliver((WorldOpcode)packet.Opcode, packet.Payload.ToArray(), clock.ToWorld(gatewayReceivedMs, now));
        }
        else
        {
            target!.Deliver((WorldOpcode)packet.Opcode, packet.Payload.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // already stopped
        }

        if (_acceptTask is not null)
        {
            await _acceptTask.ConfigureAwait(false);
        }

        foreach (Link link in _links.Values)
        {
            link.Channel.Close();
        }

        foreach (Link link in _links.Values)
        {
            if (link.Receive is { } receive)
            {
                await receive.ConfigureAwait(false);
            }
        }

        // Each answer ends at once when its handler honours the token cancelled above, and AnswerControlAsync never throws. A handler that ignores the token must not hang the stop: the wait is bounded.
        try
        {
            await Task.WhenAll(_controlRuns.Keys.ToArray()).WaitAsync(ControlStopWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Worker link host: {Count} control request(s) still running {Seconds:0.###} s after stop; not waiting for them", _controlRuns.Count, ControlStopWait.TotalSeconds);
        }

        _stop.Dispose();
    }

    private sealed class Link(int id, GatewayLinkChannel channel, string remote)
    {
        public int Id { get; } = id;
        public GatewayLinkChannel Channel { get; } = channel;
        public string Remote { get; } = remote;
        public ConcurrentDictionary<long, LinkSession> Sessions { get; } = new();
        public volatile bool Admitted;

        /// <summary>The gateway sent a WardenPosture frame that matched this worker's verifier. Receive loop only.</summary>
        public bool PostureProven;

        /// <summary>The gateway-to-worker clock offset from the LinkClock frame; null until it arrives. Receive loop only.</summary>
        public MovementClock? ClockOffset;

        /// <summary>Set under the host's source gate at admission, cleared by ReleaseSource; null before admission.</summary>
        public ClusterWardenVerdictSource? Source;
        public long HighestGeneration;
        public Task? Receive;
    }

    /// <summary>One session on a link: the envelope state and the output the worker-side session writes to.</summary>
    private sealed class LinkSession(GatewaySessionLinkHost host, Link link, long generation, int accountId) : IGatewaySessionOutput
    {
        private const int MaxPendingBeforeOpen = 1024;
        private readonly Lock _gate = new();
        private List<byte[]>? _pending = [];
        private IGatewayRemoteSession? _session;
        private bool _closed;
        private long _outboundSequence;
        private long _lastInboundSequence;

        public long Generation { get; } = generation;

        public int AccountId { get; } = accountId;

        public bool TrySend(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return false;
                }

                byte[] frame = GatewaySessionWire.EncodePacket(GatewayWorkerFrameType.SessionServerPacket, Generation, AccountId, 0,
                    host._ownerEpoch(), ++_outboundSequence, (uint)opcode, payload);
                if (_pending is { } pending)
                {
                    if (pending.Count >= MaxPendingBeforeOpen)
                    {
                        return false;
                    }

                    pending.Add(frame);
                    return true;
                }

                return link.Channel.TryEnqueue(frame);
            }
        }

        /// <summary>Queue the accepted reply, then whatever the session sent while it was being opened. False if already closed.</summary>
        public bool Accept(long requestId, IGatewayRemoteSession session, long ownerEpoch)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return false;
                }

                _session = session;
                link.Channel.TryEnqueue(GatewaySessionWire.EncodeOpenReply(requestId, GatewaySessionOpenReply.Accept(ownerEpoch)));
                foreach (byte[] frame in _pending!)
                {
                    link.Channel.TryEnqueue(frame);
                }

                _pending = null;
                return true;
            }
        }

        public void Refuse(long requestId, AuthResponseCode code, string reason)
        {
            lock (_gate)
            {
                _closed = true;
                _pending = null;
                link.Channel.TryEnqueue(GatewaySessionWire.EncodeOpenReply(requestId, GatewaySessionOpenReply.Refuse(code, reason)));
            }
        }

        /// <summary>
        /// Ask the gateway to move this session to the owner of <paramref name="mapId"/> (login redirect): queues SessionRedirect after every packet
        /// already sent. False when the session is closed or its open reply is not queued yet; the session itself stays open until the gateway closes it.
        /// </summary>
        public bool TryRedirect(uint mapId, uint instanceId)
        {
            lock (_gate)
            {
                if (_closed || _pending is not null)
                {
                    return false;
                }

                return link.Channel.TryEnqueue(GatewaySessionWire.EncodeRedirect(new GatewaySessionRedirect(Generation, AccountId, mapId, instanceId)));
            }
        }

        public bool TryAcceptInbound(ClusterSessionPacket packet, long ownerEpoch, out IGatewayRemoteSession? target)
        {
            lock (_gate)
            {
                target = _session;
                if (_closed || target is null || packet.AccountId != AccountId || packet.OwnerEpoch != ownerEpoch
                    || packet.Sequence <= _lastInboundSequence)
                {
                    return false;
                }

                _lastInboundSequence = packet.Sequence;
                return true;
            }
        }

        /// <summary>The worker-side session ended: tell the gateway.</summary>
        public void Close(ClusterSessionDetachReason reason)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                _pending = null;
                link.Sessions.TryRemove(new KeyValuePair<long, LinkSession>(Generation, this));
                link.Channel.TryEnqueue(GatewaySessionWire.EncodeClose(Generation, AccountId, reason));
            }
        }

        /// <summary>The gateway closed the session, or the link is gone: no frame back.</summary>
        public void CloseFromLink(ClusterSessionDetachReason reason)
        {
            IGatewayRemoteSession? session;
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                _pending = null;
                session = _session;
            }

            session?.CloseFromGateway(reason);
        }
    }
}

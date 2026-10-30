using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Auxiliary;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Logging;
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

    /// <summary>The gateway closed the session or the link was lost. Must not block.</summary>
    void CloseFromGateway(ClusterSessionDetachReason reason);
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
    private readonly Lock _sourceGate = new();
    private readonly Dictionary<ClusterWardenVerdictSource, int> _openLinksBySource = [];
    // Newest incarnation admitted per node id. A gateway process keeps one incarnation for life and reconnects with it, so
    // an incarnation is only superseded (and retirable) once a different incarnation of its node id has been admitted.
    private readonly Dictionary<string, Guid> _newestIncarnationByNode = [];
    private readonly LogGate _dropGate = new(TimeSpan.FromSeconds(10));
    private Task? _acceptTask;
    private int _nextLinkId;
    private long _droppedClientPackets;

    public GatewaySessionLinkHost(
        ClusterNodeManifest workerManifest,
        IGatewaySessionAcceptor acceptor,
        Func<long> ownerEpoch,
        string linkSecret,
        IPEndPoint bindEndPoint,
        ILogger? logger = null,
        GatewayWardenManifestVerifier? wardenVerifier = null)
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

    /// <summary>Open sessions over every link.</summary>
    public int SessionCount => _links.Values.Sum(link => link.Sessions.Count);

    /// <summary>Admitted gateway links.</summary>
    public int AdmittedLinkCount => _links.Values.Count(link => link.Admitted);

    public void Start()
    {
        _listener.Start();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_stop.Token));
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
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                || (ex is SocketException && ct.IsCancellationRequested))
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("cluster.transport", ex);
                break;
            }

            int id = Interlocked.Increment(ref _nextLinkId);
            var link = new Link(id, new GatewayLinkChannel(client), client.Client.RemoteEndPoint?.ToString() ?? "unknown");
            _links[id] = link;
            link.Receive = Task.Run(() => RunLinkAsync(link, ct), CancellationToken.None);
        }
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
                            byte[] proof = GatewaySessionWire.DecodeNonceFrame(body.Span, "LinkProof");
                            proven = CryptographicOperations.FixedTimeEquals(proof, GatewaySessionWire.ComputeProof(_linkSecret, nonce));
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
                            link.Channel.TryEnqueue(LoopbackGatewayWorkerHost.BuildHelloAckFrame(admitted, reason ?? string.Empty));
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
                                if (_newestIncarnationByNode.TryGetValue(source.NodeId, out Guid previous) && previous != source.NodeIncarnation)
                                {
                                    // The previous incarnation is now superseded: retire it at once if it has no open link,
                                    // else ReleaseSource retires it when its last link goes.
                                    var superseded = new ClusterWardenVerdictSource(source.NodeId, previous);
                                    if (!_openLinksBySource.ContainsKey(superseded))
                                    {
                                        _wardenVerifier?.RetireSource(superseded);
                                    }
                                }

                                _newestIncarnationByNode[source.NodeId] = source.NodeIncarnation;
                            }

                            link.Admitted = true;
                            _logger.LogInformation("Gateway link from {Remote} admitted (node {Node}, incarnation {Incarnation})",
                                link.Remote, peer.NodeId, peer.NodeIncarnation);
                            continue;
                        default:
                            _logger.LogWarning("Gateway link from {Remote}: frame {Type} before admission; closing", link.Remote, type);
                            return;
                    }
                }

                switch (type)
                {
                    case GatewayWorkerFrameType.SessionOpen:
                        OnOpen(link, body.Span, ct);
                        break;
                    case GatewayWorkerFrameType.SessionClientPacket:
                        OnClientPacket(link, body);
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
            if (_newestIncarnationByNode.TryGetValue(source.NodeId, out Guid newest) && newest != source.NodeIncarnation)
            {
                _wardenVerifier?.RetireSource(source);
            }
        }
    }

    private void OnOpen(Link link, ReadOnlySpan<byte> body, CancellationToken ct)
    {
        (long requestId, GatewaySessionOpenRequest request) = GatewaySessionWire.DecodeOpen(body);
        if (request.SessionGeneration <= link.HighestGeneration || request.AccountId <= 0)
        {
            link.Channel.TryEnqueue(GatewaySessionWire.EncodeOpenReply(requestId, GatewaySessionOpenReply.Refuse(
                AuthResponseCode.Failed, $"stale or invalid session generation {request.SessionGeneration} (highest {link.HighestGeneration})")));
            return;
        }

        link.HighestGeneration = request.SessionGeneration;
        var session = new LinkSession(this, link, request.SessionGeneration, request.AccountId);
        link.Sessions[request.SessionGeneration] = session;
        _ = OpenAsync(link, session, requestId, request, ct);
    }

    private async Task OpenAsync(Link link, LinkSession session, long requestId, GatewaySessionOpenRequest request, CancellationToken ct)
    {
        GatewaySessionAcceptResult result;
        try
        {
            result = await _acceptor.OpenAsync(request, session, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.gateway-host", ex);
            AnomalyLedger.Shared.Report("cluster", "gateway-fault", $"account-{request.AccountId}", $"phase=session-open error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
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
        ClusterSessionPacket packet = GatewaySessionWire.DecodePacket(body);
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

        target!.Deliver((WorldOpcode)packet.Opcode, packet.Payload.ToArray());
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

        _stop.Dispose();
    }

    private sealed class Link(int id, GatewayLinkChannel channel, string remote)
    {
        public int Id { get; } = id;
        public GatewayLinkChannel Channel { get; } = channel;
        public string Remote { get; } = remote;
        public ConcurrentDictionary<long, LinkSession> Sessions { get; } = new();
        public volatile bool Admitted;

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

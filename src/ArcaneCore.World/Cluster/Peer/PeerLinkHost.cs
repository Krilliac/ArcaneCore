using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Peer;

/// <summary>Which realm authority leases this node holds right now (read side only; nothing here takes or changes a lease).</summary>
public interface IPeerLeaseSource
{
    /// <summary>This node's id (<c>Cluster:Worker:NodeId</c>), the owner id of its leases.</summary>
    string NodeId { get; }

    /// <summary>The leases held now, one token per scope; empty when the node holds none. Thread-safe.</summary>
    IReadOnlyList<PeerLeaseToken> HeldLeases();
}

/// <summary>
/// <see cref="IPeerLeaseSource"/> over the node's <see cref="RealmAuthorityLeaseKeeper"/>, read through its existing <see cref="RealmAuthorityLeaseKeeper.TokenFor"/> and
/// <see cref="RealmAuthorityLeaseKeeper.NodeId"/>. Thread-safe (the keeper guards its own state).
/// </summary>
public sealed class KeeperPeerLeaseSource(RealmAuthorityLeaseKeeper keeper) : IPeerLeaseSource
{
    private static readonly string[] Scopes = [RealmAuthorityScopes.Social, RealmAuthorityScopes.Economy, RealmAuthorityScopes.Instances];

    public string NodeId => keeper.NodeId;

    public IReadOnlyList<PeerLeaseToken> HeldLeases()
    {
        var held = new List<PeerLeaseToken>(Scopes.Length);
        foreach (string scope in Scopes)
        {
            if (keeper.TokenFor(scope) is { } token)
            {
                held.Add(new PeerLeaseToken(scope, token));
            }
        }

        return held;
    }
}

/// <summary>
/// The listening end of the peer link: runs on the node that holds the Social or Economy lease (<see cref="HoldsPeerScope"/>). Every accepted connection gets a random
/// challenge and must answer with the HMAC of it under the shared secret (read from the environment by the caller, see
/// <see cref="PeerLinkOptions.SecretEnvironmentVariable"/>), then say hello with a <see cref="ClusterNodeManifest"/> that <see cref="ClusterNodeManifest.TryAdmit"/> accepts
/// (the same realm, contract, schema and content checks as the gateway link) and the Worker role. Only then are Ping frames answered, by echoing the payload.
/// <para>Idempotency: a Ping is keyed by (peer incarnation, idempotency id); a repeat of a key already served is answered from a bounded cache
/// (<see cref="IdempotencyCacheSize"/> entries, oldest evicted first) without counting as a new execution, so a client that retries after a lost link does not
/// repeat an effect. Pending output per link is bounded by <see cref="GatewayLinkChannel.MaxQueuedBytes"/> (a link over it is closed).</para>
/// <para>Thread affinity: none. One receive task per link on the thread pool; counters are interlocked; the idempotency cache has its own lock.</para>
/// </summary>
public sealed class PeerLinkHost : IAsyncDisposable
{
    /// <summary>Most (incarnation, idempotency id) answers remembered.</summary>
    public const int IdempotencyCacheSize = 1024;

    private static readonly TimeSpan RejectFlushBound = TimeSpan.FromSeconds(1);

    private sealed class Link(int id, GatewayLinkChannel channel, string remote)
    {
        public int Id { get; } = id;
        public GatewayLinkChannel Channel { get; } = channel;
        public string Remote { get; } = remote;
        public volatile bool Admitted;
        public Guid PeerIncarnation;
        public Task Receive = Task.CompletedTask;
    }

    private readonly PeerLinkOptions _options;
    private readonly string _secret;
    private readonly ClusterNodeManifest _manifest;
    private readonly IPeerLeaseSource _leases;
    private readonly ILogger _logger;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, Link> _links = new();
    private readonly Lock _cacheGate = new();
    private readonly Dictionary<(Guid Incarnation, long Id), byte[]> _answers = [];
    private readonly Queue<(Guid Incarnation, long Id)> _answerOrder = new();
    private Task _acceptTask = Task.CompletedTask;
    private int _nextLinkId;
    private long _refusedLinks;
    private long _echoExecutions;
    private long _duplicateRequests;
    private string? _lastRefusal;
    private int _started;
    private int _disposed;

    /// <param name="options">Bind address and port, handshake timeout.</param>
    /// <param name="secret">The shared peer secret, at least <see cref="PeerLinkOptions.MinSecretLength"/> characters.</param>
    /// <param name="manifest">This node's manifest, compared with every peer's.</param>
    /// <param name="leases">Where the held lease tokens for HelloAck come from.</param>
    /// <param name="logger">Optional logger.</param>
    public PeerLinkHost(PeerLinkOptions options, string secret, ClusterNodeManifest manifest, IPeerLeaseSource leases, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(leases);
        if (secret is null || secret.Length < PeerLinkOptions.MinSecretLength)
        {
            throw new ArgumentException($"the peer link secret must be at least {PeerLinkOptions.MinSecretLength} characters", nameof(secret));
        }

        if (!IPAddress.TryParse(options.BindAddress, out IPAddress? address))
        {
            throw new ArgumentException($"Cluster:Peer:BindAddress '{options.BindAddress}' is not an IP address", nameof(options));
        }

        _options = options;
        _secret = secret;
        _manifest = manifest;
        _leases = leases;
        _logger = logger ?? NullLogger.Instance;
        _listener = new TcpListener(address, options.Port);
    }

    /// <summary>True when <paramref name="leases"/> holds the Social or the Economy lease: the node that should run a <see cref="PeerLinkHost"/>.</summary>
    public static bool HoldsPeerScope(IPeerLeaseSource leases)
    {
        ArgumentNullException.ThrowIfNull(leases);
        foreach (PeerLeaseToken lease in leases.HeldLeases())
        {
            if (lease.Scope is RealmAuthorityScopes.Social or RealmAuthorityScopes.Economy)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The address and port the listener is bound to (the real port when Port is 0). Valid after <see cref="Start"/>.</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>Links that completed the handshake and are still open.</summary>
    public int AdmittedLinkCount => _links.Values.Count(link => link.Admitted);

    /// <summary>Links refused at the handshake (wrong secret, manifest mismatch, protocol version, role).</summary>
    public long RefusedLinkCount => Interlocked.Read(ref _refusedLinks);

    /// <summary>Why the newest refused link was refused (null when none was).</summary>
    public string? LastRefusal => Volatile.Read(ref _lastRefusal);

    /// <summary>Pings executed (a repeat of an idempotency id already served is not counted).</summary>
    public long EchoExecutions => Interlocked.Read(ref _echoExecutions);

    /// <summary>Pings answered from the idempotency cache.</summary>
    public long DuplicateRequests => Interlocked.Read(ref _duplicateRequests);

    /// <summary>Test seam: when it returns true for a request id the Ping is executed but its Pong is withheld (an unanswered request).</summary>
    internal Func<long, bool>? SuppressReplyForTests { get; set; }

    /// <summary>
    /// What answers a request: given the request payload, returns the answer payload (the presence protocol's <c>PresenceHolderService.Handle</c>). Null (the default) echoes the
    /// payload. Set before <see cref="Start"/>; called on the link's receive task, so it must be thread-safe and must not throw for bad input. The answer is cached per
    /// (peer incarnation, idempotency id) like an echo, so a retried request is not executed twice.
    /// </summary>
    public Func<ReadOnlyMemory<byte>, byte[]>? PayloadHandler { get; set; }

    /// <summary>Binds the port and starts accepting. Call once.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("the peer link host is already started");
        }

        _listener.Start();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>Test seam: closes every open link without stopping the listener (a dropped connection).</summary>
    internal void DropLinksForTests()
    {
        foreach (Link link in _links.Values)
        {
            link.Channel.Close();
        }
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
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning("Peer link accept failed ({Message}); retrying", ex.Message);
                try
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            int id = Interlocked.Increment(ref _nextLinkId);
            var link = new Link(id, new GatewayLinkChannel(client), client.Client.RemoteEndPoint?.ToString() ?? "unknown");
            _links[id] = link;
            link.Receive = Task.Run(() => RunLinkAsync(link, ct), CancellationToken.None);
        }
    }

    private void Refuse(string reason)
    {
        Interlocked.Increment(ref _refusedLinks);
        Volatile.Write(ref _lastRefusal, reason);
    }

    /// <summary>Refuses a link at a handshake step before the peer is authenticated: counts it, answers with a plain HelloAck refusal and closes.</summary>
    private async Task RefuseLinkAsync(Link link, string reason)
    {
        Refuse(reason);
        _logger.LogWarning("Peer link from {Remote} refused: {Reason}", link.Remote, reason);
        link.Channel.TryEnqueue(PeerLinkWire.EncodeHelloAck(false, Clamp(reason), null));
        await link.Channel.FlushAndCloseAsync(RejectFlushBound).ConfigureAwait(false);
    }

    private async Task RunLinkAsync(Link link, CancellationToken ct)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(GatewaySessionWire.NonceBytes);
        bool proven = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, link.Channel.Closed);
        using var handshake = new CancellationTokenSource(_options.HandshakeTimeout);
        using CancellationTokenRegistration expire = handshake.Token.Register(() =>
        {
            if (!link.Admitted)
            {
                link.Channel.Close();
            }
        });
        try
        {
            link.Channel.TryEnqueue(PeerLinkWire.EncodeNonce(RealmServiceFrameType.Challenge, nonce));
            while (!linked.IsCancellationRequested)
            {
                byte[]? frame = await link.Channel.ReadFrameAsync(linked.Token).ConfigureAwait(false);
                if (frame is null || frame.Length == 0)
                {
                    break;
                }

                var type = (RealmServiceFrameType)frame[0];
                ReadOnlyMemory<byte> body = frame.AsMemory(1);
                if (!link.Admitted)
                {
                    switch (type)
                    {
                        case RealmServiceFrameType.Proof:
                            if (proven)
                            {
                                throw new InvalidDataException("a second Proof frame");
                            }

                            if (!LinkAuth.TrySplitClientProof(body.Span, out byte[] proof, out byte[] connectorNonce, out bool legacyProof))
                            {
                                if (legacyProof)
                                {
                                    await RefuseLinkAsync(link, $"peer link protocol is older than local v{RealmServiceProtocol.Version} (it lacks mutual proof and frame MACs)").ConfigureAwait(false);
                                    return;
                                }

                                throw new InvalidDataException($"Proof frame of {body.Length} B (expected {LinkAuth.ClientProofBodyBytes})");
                            }

                            if (!CryptographicOperations.FixedTimeEquals(proof, GatewaySessionWire.ComputeProof(_secret, nonce)))
                            {
                                await RefuseLinkAsync(link, "peer link proof missing or wrong (the peer link secret differs)").ConfigureAwait(false);
                                return;
                            }

                            proven = true;
                            // The holder proves itself back, then both directions carry a frame MAC (the proof frame is the last one in the clear).
                            LinkKeys keys = LinkAuth.DeriveKeys(_secret, nonce, connectorNonce);
                            link.Channel.TryEnqueue(PeerLinkWire.EncodeNonce(RealmServiceFrameType.Proof, LinkAuth.ComputeServerProof(_secret, nonce, connectorNonce)));
                            link.Channel.EnqueueSendKey(keys.ServerToClient);
                            link.Channel.ActivateReceiveKey(keys.ClientToServer);
                            continue;
                        case RealmServiceFrameType.Hello:
                            if (!await AdmitAsync(link, body, proven).ConfigureAwait(false))
                            {
                                return;
                            }

                            continue;
                        default:
                            _logger.LogWarning("Peer link from {Remote}: frame {Type} before admission; closing", link.Remote, type);
                            return;
                    }
                }

                switch (type)
                {
                    case RealmServiceFrameType.Ping:
                        OnPing(link, body);
                        break;
                    default:
                        throw new InvalidDataException($"unexpected frame {type} from a peer");
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is InvalidDataException or InvalidOperationException)
            {
                _logger.LogWarning("Peer link from {Remote} broke the protocol ({Message}); closing", link.Remote, ex.Message);
            }
            else if (ex is not (OperationCanceledException or ObjectDisposedException or SocketException or IOException or EndOfStreamException))
            {
                FaultLedger.Shared.Record("cluster.peer-link", ex);
            }
        }
        finally
        {
            _links.TryRemove(link.Id, out _);
            await link.Channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Answers a Hello: admits the link or refuses it and closes. False when the link was refused.</summary>
    private async Task<bool> AdmitAsync(Link link, ReadOnlyMemory<byte> body, bool proven)
    {
        PeerHelloInfo peer = PeerLinkWire.DecodeHello(body);
        string? reason = null;
        bool admitted = proven
            && peer.ProtocolVersion == RealmServiceProtocol.Version
            && peer.Manifest.Role == ClusterRole.Worker
            && _manifest.TryAdmit(peer.Manifest, out reason);
        if (!admitted)
        {
            reason = !proven ? "peer link proof missing or wrong (the peer link secret differs)"
                : peer.ProtocolVersion != RealmServiceProtocol.Version ? $"peer link protocol v{peer.ProtocolVersion} != local v{RealmServiceProtocol.Version}"
                : peer.Manifest.Role != ClusterRole.Worker ? $"peer role {peer.Manifest.Role} is not Worker"
                : reason ?? "refused";
            Refuse(reason);
            _logger.LogWarning("Peer link from {Remote} refused: {Reason}", link.Remote, reason);
            link.Channel.TryEnqueue(PeerLinkWire.EncodeHelloAck(false, Clamp(reason), null));
            await link.Channel.FlushAndCloseAsync(RejectFlushBound).ConfigureAwait(false);
            return false;
        }

        link.PeerIncarnation = peer.Manifest.NodeIncarnation;
        var self = new PeerHelloInfo(RealmServiceProtocol.Version, _manifest, _leases.HeldLeases());
        link.Channel.TryEnqueue(PeerLinkWire.EncodeHelloAck(true, string.Empty, self));
        link.Admitted = true;
        _logger.LogInformation("Peer link from {Remote} admitted (node {Node}, incarnation {Incarnation})", link.Remote, peer.Manifest.NodeId, peer.Manifest.NodeIncarnation);
        return true;
    }

    private static string Clamp(string reason)
    {
        // Text is limited to MaxTextBytes UTF-8 bytes; a reason is ASCII in practice, but clamp by characters conservatively.
        return reason.Length <= PeerLinkWire.MaxTextBytes / 4 ? reason : reason[..(PeerLinkWire.MaxTextBytes / 4)];
    }

    private void OnPing(Link link, ReadOnlyMemory<byte> body)
    {
        (long requestId, long idempotencyId, byte[] payload) = PeerLinkWire.DecodeEcho(body, "Ping");
        var key = (link.PeerIncarnation, idempotencyId);
        byte[] answer;
        bool duplicate;
        lock (_cacheGate)
        {
            duplicate = _answers.TryGetValue(key, out byte[]? cached);
            if (duplicate)
            {
                answer = cached!;
            }
            else
            {
                answer = PayloadHandler is { } handler ? handler(payload) : payload;
                _answers[key] = answer;
                _answerOrder.Enqueue(key);
                while (_answerOrder.Count > IdempotencyCacheSize)
                {
                    _answers.Remove(_answerOrder.Dequeue());
                }
            }
        }

        if (duplicate)
        {
            Interlocked.Increment(ref _duplicateRequests);
        }
        else
        {
            Interlocked.Increment(ref _echoExecutions);
        }

        if (SuppressReplyForTests is { } suppress && suppress(requestId))
        {
            return;
        }

        link.Channel.TryEnqueue(PeerLinkWire.EncodeEcho(RealmServiceFrameType.Pong, requestId, idempotencyId, answer));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        await _acceptTask.ConfigureAwait(false);
        Link[] links = [.. _links.Values];
        foreach (Link link in links)
        {
            link.Channel.Close();
        }

        foreach (Link link in links)
        {
            await link.Receive.ConfigureAwait(false);
        }

        _stop.Dispose();
    }
}

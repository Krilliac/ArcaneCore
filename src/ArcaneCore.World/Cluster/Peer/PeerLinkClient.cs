using System.Collections.Concurrent;
using System.Net.Sockets;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Cluster.Gateway;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Peer;

/// <summary>Where the holder of a realm authority lease can be dialled: its lease (owner, incarnation, token) and its peer endpoint from <c>Cluster:Peer:Nodes</c>.</summary>
public sealed record PeerHolder(string Scope, string NodeId, Guid Incarnation, long FencingToken, string Host, int Port);

/// <summary>
/// Finds the node that holds the Social lease (else the Economy lease) in the lease store and maps its node id to a peer endpoint through
/// <see cref="PeerLinkOptions.Nodes"/>. A holder with no (or a malformed) endpoint entry is skipped, so it is not dialled. Reads only
/// (<see cref="IRealmAuthorityLeaseStore.GetCurrent"/>); the store call is synchronous and may touch a database, so callers run it off the world thread.
/// Thread-safe when the store is.
/// </summary>
public sealed class PeerHolderLocator(IRealmAuthorityLeaseStore store, PeerLinkOptions options, TimeProvider? time = null)
{
    private static readonly string[] Scopes = [RealmAuthorityScopes.Social, RealmAuthorityScopes.Economy];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The current dialable holder, or null when no lease is held or the holder has no endpoint entry.</summary>
    public PeerHolder? Locate()
    {
        DateTimeOffset now = _time.GetUtcNow();
        foreach (string scope in Scopes)
        {
            if (store.GetCurrent(scope, now) is { } lease
                && options.Nodes.TryGetValue(lease.OwnerNodeId, out string? endpoint)
                && PeerLinkOptions.TryParseEndpoint(endpoint, out string host, out int port))
            {
                return new PeerHolder(scope, lease.OwnerNodeId, lease.OwnerIncarnation, lease.FencingToken, host, port);
            }
        }

        return null;
    }
}

/// <summary>
/// The dialling end of the peer link: keeps one link to the current lease holder (the locate delegate, normally <see cref="PeerHolderLocator.Locate"/>),
/// redialling with exponential backoff (<see cref="PeerLinkOptions.ReconnectInitialMs"/> doubling to <see cref="PeerLinkOptions.ReconnectMaxMs"/>; a refused or failed
/// dial doubles it, an admitted link resets it). While linked it re-reads the holder every <see cref="PeerLinkOptions.HolderPollMs"/>; when the holder's node or
/// incarnation changed it drops the link and dials the new holder at once. It never dials itself (a node that holds the lease is the holder).
/// <para>Requests: <see cref="EchoAsync"/> sends a Ping with a fresh request id and an idempotency id and waits for the matching Pong; no answer within the timeout
/// throws <see cref="TimeoutException"/>; a link lost meanwhile fails it with <see cref="IOException"/> (retry with the same idempotency id and the holder
/// will not execute it twice). At most <see cref="PeerLinkOptions.MaxPendingRequests"/> are in flight; the link's send queue is byte-bounded by <see cref="GatewayLinkChannel.MaxQueuedBytes"/>.</para>
/// <para>Thread affinity: none. One background task runs the dial/receive loop; <see cref="EchoAsync"/> may be called from any thread. State fields are volatile or interlocked.</para>
/// </summary>
public sealed class PeerLinkClient : IAsyncDisposable
{
    private enum LinkEnd
    {
        Failed,
        Refused,
        Dropped,
        HolderChanged,
    }

    private readonly PeerLinkOptions _options;
    private readonly string _secret;
    private readonly ClusterNodeManifest _manifest;
    private readonly IPeerLeaseSource _leases;
    private readonly Func<PeerHolder?> _locate;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<byte[]>> _pending = new();
    private GatewayLinkChannel? _channel;
    private PeerHelloInfo? _peer;
    private Task _loop = Task.CompletedTask;
    private long _nextRequestId;
    private long _nextIdempotencyId;
    private int _connectCount;
    private string? _lastRefusal;
    private string? _lastError;
    private int _started;
    private int _disposed;

    /// <param name="options">Reconnect, poll, handshake and request settings.</param>
    /// <param name="secret">The shared peer secret, at least <see cref="PeerLinkOptions.MinSecretLength"/> characters.</param>
    /// <param name="manifest">This node's manifest (role Worker), sent in Hello.</param>
    /// <param name="leases">The leases this node holds, sent in Hello.</param>
    /// <param name="locate">Finds the current holder, or null when there is none to dial.</param>
    /// <param name="logger">Optional logger.</param>
    public PeerLinkClient(PeerLinkOptions options, string secret, ClusterNodeManifest manifest, IPeerLeaseSource leases, Func<PeerHolder?> locate, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(leases);
        ArgumentNullException.ThrowIfNull(locate);
        if (secret is null || secret.Length < PeerLinkOptions.MinSecretLength)
        {
            throw new ArgumentException($"the peer link secret must be at least {PeerLinkOptions.MinSecretLength} characters", nameof(secret));
        }

        _options = options;
        _secret = secret;
        _manifest = manifest;
        _leases = leases;
        _locate = locate;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>True while a link to a holder is admitted and open.</summary>
    public bool IsConnected => Volatile.Read(ref _channel) is { IsOpen: true } && Volatile.Read(ref _peer) is not null;

    /// <summary>What the holder said in HelloAck (its node, incarnation and held lease tokens); null while not connected.</summary>
    public PeerHelloInfo? ConnectedPeer => Volatile.Read(ref _peer);

    /// <summary>How many links were admitted so far (a redial after a drop raises it).</summary>
    public int ConnectCount => Volatile.Read(ref _connectCount);

    /// <summary>Why the newest dial was refused by a holder (null when none was).</summary>
    public string? LastRefusal => Volatile.Read(ref _lastRefusal);

    /// <summary>The newest failure that was not a refusal (null when none).</summary>
    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>Requests in flight.</summary>
    public int PendingRequestCount => _pending.Count;

    /// <summary>Starts the dial loop. Call once.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("the peer link client is already started");
        }

        _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>
    /// Sends <paramref name="payload"/> to the holder and returns what it echoes. <paramref name="idempotencyId"/> null takes a fresh id; pass the id of an earlier
    /// attempt to retry it. Throws <see cref="InvalidOperationException"/> when not connected or too many requests are pending, <see cref="TimeoutException"/> when
    /// no answer came within <paramref name="timeout"/> (default <see cref="PeerLinkOptions.RequestTimeout"/>), <see cref="IOException"/> when the link was lost.
    /// </summary>
    public async Task<byte[]> EchoAsync(ReadOnlyMemory<byte> payload, long? idempotencyId = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (payload.Length > PeerLinkWire.MaxPayloadBytes)
        {
            throw new ArgumentException($"echo payload of {payload.Length} B exceeds {PeerLinkWire.MaxPayloadBytes} B", nameof(payload));
        }

        GatewayLinkChannel channel = Volatile.Read(ref _channel) ?? throw new InvalidOperationException("the peer link is not connected");
        if (_pending.Count >= _options.MaxPendingRequests)
        {
            throw new InvalidOperationException($"{_options.MaxPendingRequests} peer requests are already pending");
        }

        long requestId = Interlocked.Increment(ref _nextRequestId);
        long idempotency = idempotencyId ?? Interlocked.Increment(ref _nextIdempotencyId);
        var answer = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = answer;
        try
        {
            if (!channel.TryEnqueue(PeerLinkWire.EncodeEcho(RealmServiceFrameType.Ping, requestId, idempotency, payload.Span)))
            {
                throw new IOException("the peer link is closed or its send queue is full");
            }

            return await answer.Task.WaitAsync(timeout ?? _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private PeerHolder? SafeLocate()
    {
        try
        {
            return _locate();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Volatile.Write(ref _lastError, $"locating the lease holder failed: {ex.Message}");
            _logger.LogWarning("Locating the peer link holder failed ({Message})", ex.Message);
            return null;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        TimeSpan backoff = _options.ReconnectInitial;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                PeerHolder? holder = await Task.Run(SafeLocate, CancellationToken.None).ConfigureAwait(false);
                if (holder is null || string.Equals(holder.NodeId, _manifest.NodeId, StringComparison.Ordinal))
                {
                    await Task.Delay(_options.HolderPoll, ct).ConfigureAwait(false);
                    continue;
                }

                LinkEnd end;
                try
                {
                    end = await RunLinkAsync(holder, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    end = LinkEnd.Failed;
                    Volatile.Write(ref _lastError, $"{ex.GetType().Name}: {ex.Message}");
                    if (ex is not (OperationCanceledException or SocketException or IOException or ObjectDisposedException or InvalidDataException))
                    {
                        FaultLedger.Shared.Record("cluster.peer-link", ex);
                    }
                }

                switch (end)
                {
                    case LinkEnd.HolderChanged:
                        backoff = _options.ReconnectInitial;
                        continue;
                    case LinkEnd.Dropped:
                        backoff = _options.ReconnectInitial;
                        await Task.Delay(backoff, ct).ConfigureAwait(false);
                        break;
                    default:
                        await Task.Delay(backoff, ct).ConfigureAwait(false);
                        backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.ReconnectMax.Ticks));
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // disposed
        }
    }

    private async Task<LinkEnd> RunLinkAsync(PeerHolder holder, CancellationToken ct)
    {
        var tcp = new TcpClient();
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(_options.HandshakeTimeout);
            try
            {
                await tcp.ConnectAsync(holder.Host, holder.Port, connect.Token).ConfigureAwait(false);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        var channel = new GatewayLinkChannel(tcp);
        bool holderChanged = false;
        bool admitted = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, channel.Closed);
        Task? watcher = null;
        try
        {
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
            {
                handshake.CancelAfter(_options.HandshakeTimeout);
                byte[]? challenge = await channel.ReadFrameAsync(handshake.Token).ConfigureAwait(false);
                if (challenge is not { Length: > 0 } || (RealmServiceFrameType)challenge[0] != RealmServiceFrameType.Challenge)
                {
                    throw new InvalidDataException("the peer did not open with a Challenge frame");
                }

                byte[] nonce = PeerLinkWire.DecodeNonce(challenge.AsSpan(1), "Challenge");
                channel.TryEnqueue(PeerLinkWire.EncodeNonce(RealmServiceFrameType.Proof, GatewaySessionWire.ComputeProof(_secret, nonce)));
                channel.TryEnqueue(PeerLinkWire.EncodeHello(new PeerHelloInfo(RealmServiceProtocol.Version, _manifest, _leases.HeldLeases())));

                byte[]? ackFrame = await channel.ReadFrameAsync(handshake.Token).ConfigureAwait(false);
                if (ackFrame is not { Length: > 0 })
                {
                    throw new IOException("the peer closed the link during the handshake");
                }

                if ((RealmServiceFrameType)ackFrame[0] != RealmServiceFrameType.HelloAck)
                {
                    throw new InvalidDataException($"expected HelloAck, got frame type {ackFrame[0]}");
                }

                (bool accepted, string reason, PeerHelloInfo? info) = PeerLinkWire.DecodeHelloAck(ackFrame.AsMemory(1));
                if (!accepted || info is null)
                {
                    Volatile.Write(ref _lastRefusal, reason);
                    _logger.LogWarning("Peer link to {Node} refused: {Reason}", holder.NodeId, reason);
                    return LinkEnd.Refused;
                }

                if (info.ProtocolVersion != RealmServiceProtocol.Version)
                {
                    throw new InvalidDataException($"holder speaks peer link protocol v{info.ProtocolVersion}, local is v{RealmServiceProtocol.Version}");
                }

                Volatile.Write(ref _peer, info);
                Volatile.Write(ref _channel, channel);
                Interlocked.Increment(ref _connectCount);
                admitted = true;
            }

            _logger.LogInformation("Peer link to {Node} ({Host}:{Port}) admitted", holder.NodeId, holder.Host, holder.Port);
            watcher = Task.Run(async () =>
            {
                while (!linked.IsCancellationRequested)
                {
                    await Task.Delay(_options.HolderPoll, linked.Token).ConfigureAwait(false);
                    PeerHolder? now = SafeLocate();
                    if (now is not null
                        && (!string.Equals(now.NodeId, holder.NodeId, StringComparison.Ordinal) || now.Incarnation != holder.Incarnation))
                    {
                        holderChanged = true;
                        channel.Close();
                        return;
                    }
                }
            }, CancellationToken.None);

            while (!linked.IsCancellationRequested)
            {
                byte[]? frame = await channel.ReadFrameAsync(linked.Token).ConfigureAwait(false);
                if (frame is null || frame.Length == 0)
                {
                    break;
                }

                var type = (RealmServiceFrameType)frame[0];
                if (type != RealmServiceFrameType.Pong)
                {
                    throw new InvalidDataException($"unexpected frame {type} from the holder");
                }

                (long requestId, _, byte[] payload) = PeerLinkWire.DecodeEcho(frame.AsMemory(1), "Pong");
                if (_pending.TryGetValue(requestId, out TaskCompletionSource<byte[]>? waiting))
                {
                    waiting.TrySetResult(payload);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // the link closed under us (holder change, handshake timeout): classified below
            if (!admitted)
            {
                throw new TimeoutException("the peer link handshake did not finish in time");
            }
        }
        finally
        {
            Volatile.Write(ref _channel, null);
            Volatile.Write(ref _peer, null);
            channel.Close();
            await linked.CancelAsync().ConfigureAwait(false);
            if (watcher is not null)
            {
                try
                {
                    await watcher.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // the link ended first
                }
            }

            foreach (KeyValuePair<long, TaskCompletionSource<byte[]>> request in _pending)
            {
                request.Value.TrySetException(new IOException("the peer link was lost before the request was answered"));
            }

            await channel.DisposeAsync().ConfigureAwait(false);
        }

        return holderChanged ? LinkEnd.HolderChanged : admitted ? LinkEnd.Dropped : LinkEnd.Failed;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopped
        }

        foreach (KeyValuePair<long, TaskCompletionSource<byte[]>> request in _pending)
        {
            request.Value.TrySetException(new ObjectDisposedException(nameof(PeerLinkClient)));
        }

        _stop.Dispose();
    }
}

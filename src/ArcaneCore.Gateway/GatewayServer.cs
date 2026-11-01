using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Net;
using ArcaneCore.World.Cluster.Gateway;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Gateway;

/// <summary>
/// The gateway daemon's loops: the client listener (admission through a <see cref="NetGuard"/> built from
/// <c>Net:Protection</c>, one <see cref="GatewayClientConnection"/> per client) and one link supervisor per configured worker
/// (<see cref="GatewayWorkerLink"/>: connect, and reconnect with its own backoff when the link is lost or refused). A lost link drops
/// only the clients of that worker; a client that authenticates is placed on the ready link with the fewest sessions, and is answered
/// AUTH_UNAVAILABLE when no link is ready.
/// </summary>
public sealed class GatewayServer : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILoggerFactory _loggers;
    private readonly IOptions<NetProtectionOptions>? _protection;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<GatewayClientConnection, Task> _connections = new();
    private readonly Guid _incarnation = Guid.NewGuid();
    private readonly GatewayRealmLiveness? _realmLiveness;
    private TcpListener? _listener;
    private volatile GatewayWorkerLink[] _workers = [];
    private volatile GatewayRouteTable _routes = new([]);
    private volatile IPEndPoint? _worker;
    private long _nextGeneration;
    private int _nextPick;

    public GatewayServer(
        IServiceScopeFactory scopes,
        IOptions<GatewayOptions> options,
        ILoggerFactory loggers,
        IOptions<NetProtectionOptions>? protection = null,
        GatewayWarden? warden = null,
        GatewayRealmLiveness? realmLiveness = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Value);
        ArgumentNullException.ThrowIfNull(loggers);
        _scopes = scopes;
        _options = options;
        _loggers = loggers;
        _protection = protection;
        Warden = warden;
        _realmLiveness = realmLiveness;
        _logger = loggers.CreateLogger<GatewayServer>();
        Transfers = new GatewayTransferRouter(new GatewayServerTransferEnvironment(this, () => Transfers));
    }

    /// <summary>The gateway side of cross-worker player transfers: buffers, replays and re-points sessions on the worker coordinator's control frames.</summary>
    public GatewayTransferRouter Transfers { get; }

    /// <summary>The connection whose current session generation is <paramref name="generation"/>, or null.</summary>
    internal GatewayClientConnection? FindConnection(long generation)
    {
        foreach (GatewayClientConnection connection in _connections.Keys)
        {
            if (connection.SessionGeneration == generation)
            {
                return connection;
            }
        }

        return null;
    }

    /// <summary>The registered link of the worker with node id <paramref name="nodeId"/>, or null when it has none at this moment.</summary>
    internal GatewaySessionLinkClient? FindLinkByNode(string nodeId)
    {
        foreach (GatewayWorkerLink worker in _workers)
        {
            if (string.Equals(worker.NodeId, nodeId, StringComparison.Ordinal))
            {
                return worker.Link;
            }
        }

        return null;
    }

    /// <summary>The gateway's Warden (null when none was registered: Warden stays on the world, as in a hand-built test server).</summary>
    public GatewayWarden? Warden { get; }

    /// <summary>
    /// False when Warden is enabled on this gateway but unusable (invalid options, module missing or failing its digest check): the gateway
    /// then answers every login with AUTH_UNAVAILABLE. True when Warden is off or usable.
    /// </summary>
    public bool IsWardenReady => Warden is null || Warden.IsReady;

    internal GatewayOptions Options => _options.Value;

    internal IServiceScopeFactory Scopes => _scopes;

    internal ILoggerFactory Loggers => _loggers;

    /// <summary>The one-connection-per-account claim of this gateway.</summary>
    public GatewayAccountClaims Claims { get; } = new();

    /// <summary>The listener's guard (null before start).</summary>
    public NetGuard? Guard { get; private set; }

    /// <summary>The bound client endpoint (after start).</summary>
    public IPEndPoint ListenEndPoint => (IPEndPoint)(_listener ?? throw new InvalidOperationException("not started")).LocalEndpoint;

    /// <summary>The map-to-worker table (fed by link up and down from each worker's announced maps, with <c>Gateway:Workers:N:Maps</c> as the fallback). Replaced at start.</summary>
    public GatewayRouteTable Routes => _routes;

    /// <summary>The worker links, in configuration order (empty before start).</summary>
    public IReadOnlyList<GatewayWorkerLink> Workers => _workers;

    /// <summary>The state of every worker link (node, endpoint, maps, status, sessions, failures, reconnects, last error).</summary>
    public IReadOnlyList<GatewayWorkerLinkInfo> LinkStates()
    {
        GatewayWorkerLink[] workers = _workers;
        var states = new GatewayWorkerLinkInfo[workers.Length];
        for (int index = 0; index < workers.Length; index++)
        {
            states[index] = workers[index].Snapshot();
        }

        return states;
    }

    /// <summary>The first ready worker link (configuration order), or null while none is ready. With one worker this is the worker link.</summary>
    public GatewaySessionLinkClient? Link
    {
        get
        {
            foreach (GatewayWorkerLink worker in _workers)
            {
                if (worker.Link is { } link)
                {
                    return link;
                }
            }

            return null;
        }
    }

    /// <summary>Links that are ready to take sessions.</summary>
    internal int ReadyLinkCount => _workers.Count(worker => worker.Link is not null);

    /// <summary>Sessions open across every ready link.</summary>
    internal int TotalSessions => _workers.Sum(worker => worker.Link?.SessionCount ?? 0);

    /// <summary>The first worker's endpoint: Gateway:Workers[0] (or WorkerAddress/WorkerPort) until <see cref="RetargetWorkerAsync"/> moves it.</summary>
    public IPEndPoint WorkerEndPoint => _workers is { Length: > 0 } workers ? workers[0].EndPoint : _worker ?? ConfiguredWorker();

    /// <summary>
    /// Move the first worker's link to <paramref name="worker"/> (the reroute of a whole-World handoff, Tier 3b): the link to the old worker is closed,
    /// which disconnects its clients (they log in again through the new link; a seamless per-session reroute is not built), and the link supervisor
    /// connects to the new worker with the same secret and manifest. Other workers' links are not touched. Completes when the new link is registered.
    /// In-process only: nothing remote can call it. Idempotent.
    /// </summary>
    public async Task RetargetWorkerAsync(IPEndPoint worker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);
        _worker = worker;
        _logger.LogInformation("Worker link retargeted to {Worker}", worker);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GatewayWorkerLink[] workers = _workers;
            if (workers.Length > 0)
            {
                GatewayWorkerLink primary = workers[0];
                primary.Retarget(worker);
                if (primary.Link is not null && Equals(primary.ConnectedEndPoint, worker))
                {
                    return;
                }
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Open client connections.</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>Completes when any worker link is registered (a current one, or the next).</summary>
    public Task WaitForLinkAsync(CancellationToken cancellationToken)
    {
        GatewayWorkerLink[] workers = _workers;
        if (workers.Length == 0)
        {
            return new TaskCompletionSource().Task.WaitAsync(cancellationToken);
        }

        return Task.WhenAny(workers.Select(worker => worker.WaitForLinkAsync(cancellationToken))).Unwrap();
    }

    /// <summary>Completes when every worker link is registered (each link's current registration, or its next).</summary>
    public Task WaitForAllLinksAsync(CancellationToken cancellationToken)
    {
        GatewayWorkerLink[] workers = _workers;
        if (workers.Length == 0)
        {
            return new TaskCompletionSource().Task.WaitAsync(cancellationToken);
        }

        return Task.WhenAll(workers.Select(worker => worker.WaitForLinkAsync(cancellationToken)));
    }

    /// <summary>
    /// The next session generation: unique and ascending for this process. The link client calls it and queues the SessionOpen
    /// under one lock (<see cref="GatewaySessionLinkClient.OpenAsync(Func{long}, Func{long, GatewaySessionOpenRequest}, IGatewaySessionSink, CancellationToken)"/>),
    /// so every SessionOpen on a link is newer than the last even when clients log in at once.
    /// </summary>
    internal long NextGeneration() => Interlocked.Increment(ref _nextGeneration);

    /// <summary>
    /// The ready link a new session is placed on: the one with the fewest open sessions, ties broken round robin so a burst of logins spreads.
    /// Null when no link is ready.
    /// </summary>
    internal GatewaySessionLinkClient? AcquireLink()
    {
        GatewayWorkerLink[] workers = _workers;
        if (workers.Length == 0)
        {
            return null;
        }

        if (_routes.TryGetLoginWorker(out string loginNode))
        {
            foreach (GatewayWorkerLink worker in workers)
            {
                if (string.Equals(worker.NodeId, loginNode, StringComparison.Ordinal) && worker.Link is { } login)
                {
                    return login;
                }
            }
        }

        int start = (int)((uint)Interlocked.Increment(ref _nextPick) % (uint)workers.Length);
        GatewaySessionLinkClient? best = null;
        int bestSessions = int.MaxValue;
        for (int offset = 0; offset < workers.Length; offset++)
        {
            GatewaySessionLinkClient? link = workers[(start + offset) % workers.Length].Link;
            if (link is null)
            {
                continue;
            }

            int sessions = link.SessionCount;
            if (sessions < bestSessions)
            {
                best = link;
                bestSessions = sessions;
            }
        }

        return best;
    }

    /// <summary>
    /// The registered link of the worker that owns the map copy, resolved through the <see cref="Routes"/> table (login redirect). Null when no ready
    /// worker owns it or its link is not registered at this moment.
    /// </summary>
    internal GatewaySessionLinkClient? FindLinkForMap(uint mapId, uint instanceId)
    {
        if (!_routes.TryResolve(mapId, instanceId, out string nodeId))
        {
            return null;
        }

        foreach (GatewayWorkerLink worker in _workers)
        {
            if (string.Equals(worker.NodeId, nodeId, StringComparison.Ordinal))
            {
                return worker.Link;
            }
        }

        return null;
    }

    private IPEndPoint ConfiguredWorker()
    {
        GatewayWorkerEntry entry = _options.Value.EffectiveWorkers()[0];
        return new(IPAddress.Parse(entry.Address), entry.Port);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        GatewayOptions config = _options.Value;
        GatewaySessionLinkHost.ThrowIfWeakSecret(config.LinkSecret, "Gateway:LinkSecret"); // fail at start, not in the link loop
        ArgumentException.ThrowIfNullOrWhiteSpace(config.BindAddress);
        ArgumentOutOfRangeException.ThrowIfLessThan(config.Port, IPEndPoint.MinPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(config.Port, IPEndPoint.MaxPort);
        if (config.Workers.Count > 0)
        {
            IReadOnlyList<string> workerProblems = config.ValidateWorkers();
            if (workerProblems.Count > 0)
            {
                throw new ArgumentException(string.Join(" ", workerProblems), nameof(config));
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(config.WorkerAddress);
            ArgumentOutOfRangeException.ThrowIfLessThan(config.WorkerPort, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(config.WorkerPort, IPEndPoint.MaxPort);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(config.MaxConnections);
        ArgumentOutOfRangeException.ThrowIfNegative(config.MaxConnectionsPerIp);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(config.MaxOutboundBytes);
        IReadOnlyList<GatewayWorkerEntry> entries = config.EffectiveWorkers();
        var routes = new GatewayRouteTable(entries, config.LoginWorker);
        var workers = new GatewayWorkerLink[entries.Count];
        for (int index = 0; index < entries.Count; index++)
        {
            GatewayWorkerEntry entry = entries[index];
            IPEndPoint endpoint = index == 0 && _worker is { } retargeted ? retargeted : new IPEndPoint(IPAddress.Parse(entry.Address), entry.Port);
            workers[index] = new GatewayWorkerLink(entry, endpoint, config, _incarnation, _loggers, Warden, _logger, routes, _realmLiveness, Transfers);
        }

        var listener = new TcpListener(IPAddress.Parse(config.BindAddress), config.Port);
        listener.Start();
        _listener = listener;
        _routes = routes;
        _workers = workers;
        GatewayMeters.Attach(this);
        Guard = new NetGuard(_protection?.Value ?? new NetProtectionOptions(), () => config.MaxConnections, () => config.MaxConnectionsPerIp,
            _logger, bindAddress: IPAddress.Parse(config.BindAddress));
        _logger.LogInformation("Gateway listening on {Endpoint}; {Count} worker link(s): {Workers}", listener.LocalEndpoint, workers.Length,
            string.Join(", ", workers.Select(worker => $"{worker.NodeId}={worker.EndPoint}")));
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TcpListener listener = _listener ?? throw new InvalidOperationException("the gateway listener was not started");
        NetGuard guard = Guard!;
        GatewayWorkerLink[] workers = _workers;
        Task linkLoops = Task.WhenAll(workers.Select(worker => Task.Run(() => worker.RunAsync(stoppingToken), CancellationToken.None)));
        try
        {
            await AcceptLoop.RunAsync(
                ct => listener.AcceptTcpClientAsync(ct),
                client =>
                {
                    IDisposable? lease = null;
                    try
                    {
                        if (client.Client.RemoteEndPoint is IPEndPoint remote)
                        {
                            lease = guard.TryAdmit(remote.Address);
                            if (lease is null)
                            {
                                client.Dispose();
                                return;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                        client.Dispose();
                        return;
                    }

                    var connection = new GatewayClientConnection(this, client, guard, _loggers.CreateLogger<GatewayClientConnection>());
                    _connections[connection] = RunConnectionAsync(connection, lease, stoppingToken);
                },
                _logger,
                stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        finally
        {
            listener.Stop();
            await Task.WhenAll(_connections.Values).ConfigureAwait(false);
            await linkLoops.ConfigureAwait(false);
            foreach (GatewayWorkerLink worker in workers)
            {
                await worker.DisposeLinkAsync().ConfigureAwait(false);
            }

            GatewayMeters.Detach(this);
            _logger.LogInformation("Gateway stopped");
        }
    }

    public override void Dispose()
    {
        GatewayMeters.Detach(this);
        base.Dispose();
    }

    private async Task RunConnectionAsync(GatewayClientConnection connection, IDisposable? lease, CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await connection.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("gateway.session", ex, "[{Endpoint}] gateway connection error", connection.RemoteEndpoint);
        }
        finally
        {
            lease?.Dispose();
            _connections.TryRemove(connection, out _);
        }
    }
}

/// <summary>Where a worker link is in its life.</summary>
public enum GatewayWorkerLinkStatus
{
    /// <summary>Connecting (the first attempt, or the next after a backoff).</summary>
    Connecting,

    /// <summary>Registered with the worker and taking sessions.</summary>
    Ready,

    /// <summary>The last attempt failed or the link was lost; waiting out the backoff.</summary>
    Backoff,
}

/// <summary>A point-in-time view of one worker link.</summary>
public sealed record GatewayWorkerLinkInfo(
    string NodeId,
    IPEndPoint EndPoint,
    IReadOnlyList<int> Maps,
    GatewayWorkerLinkStatus Status,
    int Sessions,
    long Failures,
    long Reconnects,
    string? LastError);

/// <summary>
/// The supervisor of one worker link: it connects a <see cref="GatewaySessionLinkClient"/> to its worker, keeps it registered, and after a lost or
/// refused link reconnects with its own backoff (first <see cref="GatewayOptions.LinkRetryDelay"/>, doubling up to 30 s). Links are independent: one
/// worker going away drops only its sessions. Thread safety: <see cref="RunAsync"/> runs on one task and writes the state; every reader
/// (admission, metrics, <see cref="GatewayServer.LinkStates"/>) reads volatile fields.
/// </summary>
public sealed class GatewayWorkerLink
{
    private static readonly TimeSpan MaxLinkRetryDelay = TimeSpan.FromSeconds(30);

    private readonly GatewayWorkerEntry _entry;
    private readonly GatewayOptions _config;
    private readonly Guid _incarnation;
    private readonly ILoggerFactory _loggers;
    private readonly GatewayWarden? _warden;
    private readonly ILogger _logger;
    private readonly GatewayRouteTable _routes;
    private readonly GatewayRealmLiveness? _realmLiveness;
    private readonly GatewayTransferRouter? _transfers;
    private readonly int[] _maps;
    private volatile IPEndPoint _target;
    private volatile IPEndPoint? _connected;
    private volatile GatewaySessionLinkClient? _link;
    private volatile string? _lastError;
    private int _status;
    private long _failures;
    private long _reconnects;
    private TaskCompletionSource _linkUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _retarget;

    internal GatewayWorkerLink(
        GatewayWorkerEntry entry,
        IPEndPoint endpoint,
        GatewayOptions config,
        Guid incarnation,
        ILoggerFactory loggers,
        GatewayWarden? warden,
        ILogger logger,
        GatewayRouteTable routes,
        GatewayRealmLiveness? realmLiveness = null,
        GatewayTransferRouter? transfers = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        _transfers = transfers;
        _routes = routes;
        _realmLiveness = realmLiveness;
        _entry = entry;
        _config = config;
        _incarnation = incarnation;
        _loggers = loggers;
        _warden = warden;
        _logger = logger;
        _target = endpoint;
        _maps = [.. entry.Maps];
    }

    /// <summary>The worker's node id (Gateway:Workers:N:NodeId).</summary>
    public string NodeId => _entry.NodeId;

    /// <summary>The endpoint the supervisor connects to (a retarget moves it).</summary>
    public IPEndPoint EndPoint => _target;

    /// <summary>The endpoint of the registered link, or null while none is registered.</summary>
    public IPEndPoint? ConnectedEndPoint => _connected;

    /// <summary>Map ids this worker owns (empty: not map-restricted).</summary>
    public IReadOnlyList<int> Maps => _maps;

    /// <summary>Where the link is in its life.</summary>
    public GatewayWorkerLinkStatus Status => (GatewayWorkerLinkStatus)Volatile.Read(ref _status);

    /// <summary>The registered link, or null while it is down.</summary>
    public GatewaySessionLinkClient? Link => _link is { IsConnected: true } link ? link : null;

    /// <summary>Completes when this link is registered (the current registration, or the next).</summary>
    public Task WaitForLinkAsync(CancellationToken cancellationToken) => Volatile.Read(ref _linkUp).Task.WaitAsync(cancellationToken);

    /// <summary>A point-in-time view of this link.</summary>
    public GatewayWorkerLinkInfo Snapshot() => new(
        NodeId, EndPoint, _maps, Status, Link?.SessionCount ?? 0, Interlocked.Read(ref _failures), Interlocked.Read(ref _reconnects), _lastError);

    /// <summary>Point the link at <paramref name="worker"/>; a registered link to another endpoint is closed so the supervisor reconnects.</summary>
    internal void Retarget(IPEndPoint worker)
    {
        _target = worker;
        if (_link is not null && !Equals(_connected, worker))
        {
            Volatile.Read(ref _retarget)?.TrySetResult();
        }
    }

    internal async ValueTask DisposeLinkAsync()
    {
        if (_link is { } link)
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        TimeSpan delay = _config.LinkRetryDelay > TimeSpan.Zero ? _config.LinkRetryDelay : TimeSpan.FromSeconds(1);
        TimeSpan nextDelay = delay;
        int registrations = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            IPEndPoint worker = _target;
            Volatile.Write(ref _status, (int)GatewayWorkerLinkStatus.Connecting);
            var link = new GatewaySessionLinkClient(_config.ToManifest(_incarnation), worker, _config.LinkSecret, _loggers.CreateLogger<GatewaySessionLinkClient>(), _warden?.Posture);
            if (_realmLiveness is { } liveness)
            {
                string node = NodeId;
                link.WorkerStatusReceived = report => liveness.OnWorkerStatus(node, report); // the realm-list row of the realm is written from these reports
            }

            if (_transfers is { } transfers)
            {
                // The worker's coordinator drives staged transfers over this link; each frame is answered with an ack on the same link.
                link.TransferControlReceived = control => _ = transfers.HandleControlAsync(control, link.SendTransferAck, stoppingToken);
            }

            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(10));
                await link.ConnectAsync(attempt.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await link.DisposeAsync().ConfigureAwait(false);
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                Interlocked.Increment(ref _failures);
                _lastError = ex.Message;
                Volatile.Write(ref _status, (int)GatewayWorkerLinkStatus.Backoff);
                GatewayMeters.CountLinkFailure(_config.NodeId);
                _logger.LogWarning("Worker link {Node} to {Worker} not established ({Message}); retrying in {Delay}", NodeId, worker, ex.Message, nextDelay);
                try
                {
                    await Task.Delay(nextDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                nextDelay = TimeSpan.FromTicks(Math.Min(nextDelay.Ticks * 2, MaxLinkRetryDelay.Ticks));
                continue;
            }

            nextDelay = delay;
            var retarget = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _retarget, retarget);
            _connected = worker;
            _lastError = null;
            _routes.LinkUp(NodeId, link.WorkerOwnedMaps, link.WorkerAllowsInstanceMaps);
            _link = link;
            Volatile.Write(ref _status, (int)GatewayWorkerLinkStatus.Ready);
            if (registrations++ > 0)
            {
                Interlocked.Increment(ref _reconnects);
                GatewayMeters.CountLinkReconnect(_config.NodeId);
            }

            _logger.LogInformation("Worker link {Node} to {Worker} registered", NodeId, worker);
            Volatile.Read(ref _linkUp).TrySetResult();
            if (!Equals(_target, worker))
            {
                retarget.TrySetResult(); // retargeted while this attempt was connecting
            }

            try
            {
                await Task.WhenAny(link.Ended, retarget.Task).WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // the server disposes the link after the clients are gone
            }

            Volatile.Write(ref _linkUp, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _link = null;
            _connected = null;
            _routes.LinkDown(NodeId);
            if (retarget.Task.IsCompleted)
            {
                Volatile.Write(ref _status, (int)GatewayWorkerLinkStatus.Connecting);
                _logger.LogInformation("Worker link {Node} to {Worker} closed for the retarget to {Target}; its clients were disconnected", NodeId, worker, _target);
            }
            else
            {
                _lastError = "link lost";
                Volatile.Write(ref _status, (int)GatewayWorkerLinkStatus.Backoff);
                _logger.LogWarning("Worker link {Node} to {Worker} lost; its clients were disconnected", NodeId, worker);
            }

            await link.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>One gateway connection per account: a newer authenticated connection of the account closes the older one (vmangos World::AddSession_).</summary>
public sealed class GatewayAccountClaims
{
    private readonly ConcurrentDictionary<int, GatewayClientConnection> _byAccount = new();

    public int Count => _byAccount.Count;

    public GatewayClientConnection? Find(int accountId) => _byAccount.GetValueOrDefault(accountId);

    internal void Claim(int accountId, GatewayClientConnection connection)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentNullException.ThrowIfNull(connection);
        GatewayClientConnection? previous = null;
        _byAccount.AddOrUpdate(accountId, connection, (_, existing) =>
        {
            previous = existing;
            return connection;
        });

        if (previous is not null && !ReferenceEquals(previous, connection))
        {
            previous.Kick("superseded by a newer login of the account");
        }
    }

    internal void Release(int accountId, GatewayClientConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _byAccount.TryRemove(new KeyValuePair<int, GatewayClientConnection>(accountId, connection));
    }
}

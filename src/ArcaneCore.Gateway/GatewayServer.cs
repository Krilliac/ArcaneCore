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
/// The gateway daemon's two loops: the client listener (admission through a <see cref="NetGuard"/> built from
/// <c>Net:Protection</c>, one <see cref="GatewayClientConnection"/> per client) and the worker link (connect, and reconnect
/// with backoff when it is lost or refused). Phase 3a: a lost link drops every client of it, and a client that authenticates
/// while the link is down is answered AUTH_UNAVAILABLE.
/// </summary>
public sealed class GatewayServer : BackgroundService
{
    private static readonly TimeSpan MaxLinkRetryDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILoggerFactory _loggers;
    private readonly IOptions<NetProtectionOptions>? _protection;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<GatewayClientConnection, Task> _connections = new();
    private readonly Guid _incarnation = Guid.NewGuid();
    private TcpListener? _listener;
    private volatile GatewaySessionLinkClient? _link;
    private TaskCompletionSource _linkUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _retarget;
    private volatile IPEndPoint? _worker;
    private volatile IPEndPoint? _linkWorker;
    private long _nextGeneration;

    public GatewayServer(
        IServiceScopeFactory scopes,
        IOptions<GatewayOptions> options,
        ILoggerFactory loggers,
        IOptions<NetProtectionOptions>? protection = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Value);
        ArgumentNullException.ThrowIfNull(loggers);
        _scopes = scopes;
        _options = options;
        _loggers = loggers;
        _protection = protection;
        _logger = loggers.CreateLogger<GatewayServer>();
    }

    internal GatewayOptions Options => _options.Value;

    internal IServiceScopeFactory Scopes => _scopes;

    internal ILoggerFactory Loggers => _loggers;

    /// <summary>The one-connection-per-account claim of this gateway.</summary>
    public GatewayAccountClaims Claims { get; } = new();

    /// <summary>The listener's guard (null before start).</summary>
    public NetGuard? Guard { get; private set; }

    /// <summary>The bound client endpoint (after start).</summary>
    public IPEndPoint ListenEndPoint => (IPEndPoint)(_listener ?? throw new InvalidOperationException("not started")).LocalEndpoint;

    /// <summary>The current worker link, or null while it is down.</summary>
    public GatewaySessionLinkClient? Link => _link is { IsConnected: true } link ? link : null;

    /// <summary>The worker the link connects to: Gateway:WorkerAddress/WorkerPort until <see cref="RetargetWorkerAsync"/> moves it.</summary>
    public IPEndPoint WorkerEndPoint => _worker ?? ConfiguredWorker();

    /// <summary>
    /// Move the worker link to <paramref name="worker"/> (the reroute of a whole-World handoff, Tier 3b): the link to the old worker is closed,
    /// which disconnects its clients (they log in again through the new link; a seamless per-session reroute is not built), and the link loop
    /// connects to the new worker with the same secret and manifest. Completes when the new link is registered. In-process only: nothing
    /// remote can call it. Idempotent.
    /// </summary>
    public async Task RetargetWorkerAsync(IPEndPoint worker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);
        _worker = worker;
        _logger.LogInformation("Worker link retargeted to {Worker}", worker);
        while (!(Link is not null && Equals(_linkWorker, worker)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_link is not null && !Equals(_linkWorker, worker))
                Volatile.Read(ref _retarget)?.TrySetResult();
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Open client connections.</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>Completes when a worker link is registered (the current one, or the next).</summary>
    public Task WaitForLinkAsync(CancellationToken cancellationToken) => Volatile.Read(ref _linkUp).Task.WaitAsync(cancellationToken);

    /// <summary>
    /// The next session generation: unique and ascending for this process. The link client calls it and queues the SessionOpen
    /// under one lock (<see cref="GatewaySessionLinkClient.OpenAsync(Func{long}, Func{long, GatewaySessionOpenRequest}, IGatewaySessionSink, CancellationToken)"/>),
    /// so every SessionOpen on a link is newer than the last even when clients log in at once.
    /// </summary>
    internal long NextGeneration() => Interlocked.Increment(ref _nextGeneration);

    private IPEndPoint ConfiguredWorker() => new(IPAddress.Parse(_options.Value.WorkerAddress), _options.Value.WorkerPort);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        GatewayOptions config = _options.Value;
        GatewaySessionLinkHost.ThrowIfWeakSecret(config.LinkSecret, "Gateway:LinkSecret"); // fail at start, not in the link loop
        ArgumentException.ThrowIfNullOrWhiteSpace(config.BindAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.WorkerAddress);
        ArgumentOutOfRangeException.ThrowIfLessThan(config.Port, IPEndPoint.MinPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(config.Port, IPEndPoint.MaxPort);
        ArgumentOutOfRangeException.ThrowIfLessThan(config.WorkerPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(config.WorkerPort, IPEndPoint.MaxPort);
        ArgumentOutOfRangeException.ThrowIfNegative(config.MaxConnections);
        ArgumentOutOfRangeException.ThrowIfNegative(config.MaxConnectionsPerIp);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(config.MaxOutboundBytes);
        var listener = new TcpListener(IPAddress.Parse(config.BindAddress), config.Port);
        listener.Start();
        _listener = listener;
        Guard = new NetGuard(_protection?.Value ?? new NetProtectionOptions(), () => config.MaxConnections, () => config.MaxConnectionsPerIp,
            _logger, bindAddress: IPAddress.Parse(config.BindAddress));
        _logger.LogInformation("Gateway listening on {Endpoint}; worker link to {Worker}:{Port}", listener.LocalEndpoint, config.WorkerAddress, config.WorkerPort);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TcpListener listener = _listener ?? throw new InvalidOperationException("the gateway listener was not started");
        NetGuard guard = Guard!;
        Task linkLoop = Task.Run(() => LinkLoopAsync(stoppingToken), CancellationToken.None);
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
            await linkLoop.ConfigureAwait(false);
            if (_link is { } link)
            {
                await link.DisposeAsync().ConfigureAwait(false);
            }

            _logger.LogInformation("Gateway stopped");
        }
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
            _logger.LogFault("gateway-session", ex, "[{Endpoint}] gateway connection error", connection.RemoteEndpoint);
        }
        finally
        {
            lease?.Dispose();
            _connections.TryRemove(connection, out _);
        }
    }

    private async Task LinkLoopAsync(CancellationToken stoppingToken)
    {
        GatewayOptions config = _options.Value;
        TimeSpan delay = config.LinkRetryDelay > TimeSpan.Zero ? config.LinkRetryDelay : TimeSpan.FromSeconds(1);
        TimeSpan nextDelay = delay;
        while (!stoppingToken.IsCancellationRequested)
        {
            IPEndPoint worker = WorkerEndPoint;
            var link = new GatewaySessionLinkClient(config.ToManifest(_incarnation), worker, config.LinkSecret, _loggers.CreateLogger<GatewaySessionLinkClient>());
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

                _logger.LogWarning("Worker link to {Worker} not established ({Message}); retrying in {Delay}", worker, ex.Message, nextDelay);
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
            _linkWorker = worker;
            _link = link;
            _logger.LogInformation("Worker link to {Worker} registered", worker);
            Volatile.Read(ref _linkUp).TrySetResult();
            try
            {
                await Task.WhenAny(link.Ended, retarget.Task).WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // ExecuteAsync disposes the link after the clients are gone
            }

            Volatile.Write(ref _linkUp, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _link = null;
            if (retarget.Task.IsCompleted)
                _logger.LogInformation("Worker link to {Worker} closed for the retarget to {Target}; its clients were disconnected", worker, WorkerEndPoint);
            else
                _logger.LogWarning("Worker link to {Worker} lost; its clients were disconnected", worker);
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

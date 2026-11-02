using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.Gateway.Transfers;
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
        GatewayRealmLiveness? realmLiveness = null,
        IOptions<GatewayHandoffOptions>? handoff = null,
        IOptions<GatewayControlOptions>? control = null,
        IOptions<GatewayTransferOptions>? transfers = null)
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
        // Gateway:Handoff:Enabled or Gateway:Transfers:Enabled turns the control channels on; Gateway:Control:* are their reply timeouts. Both off (the default), no worker link gets a channel.
        ControlTimeouts = handoff?.Value.Enabled == true || transfers?.Value.Enabled == true ? (control?.Value ?? new GatewayControlOptions()).ToTimeouts() : null;
        _logger = loggers.CreateLogger<GatewayServer>();
        Transfers = new GatewayTransferRouter(new GatewayServerTransferEnvironment(this));
    }

    /// <summary>The gateway side of cross-worker player transfers: buffers, replays and re-points sessions on the worker coordinator's control frames.</summary>
    public GatewayTransferRouter Transfers { get; }

    /// <summary>The reply timeouts of the control channels (<c>Gateway:Control:*</c>), or null when <c>Gateway:Handoff:Enabled</c> and <c>Gateway:Transfers:Enabled</c> are both off and the worker links carry no channel.</summary>
    internal ArcaneCore.World.Cluster.Control.LinkControlTimeouts? ControlTimeouts { get; }

    /// <summary>
    /// Raised, with the worker's node id (<c>Gateway:Workers:N:NodeId</c>), each time a worker link is registered (the first time and after every reconnect or retarget). It runs on the link's supervisor task:
    /// handlers must only signal (the recovery loops wake a pass), never block.
    /// </summary>
    internal event Action<string>? WorkerLinkUp;

    private void RaiseWorkerLinkUp(string nodeId) => WorkerLinkUp?.Invoke(nodeId);

    /// <summary>
    /// Takes the <see cref="ControlSubmit"/> frames workers send (design _wf/handoff-control-DESIGN.md section 2.3, commit C10): the worker whose link carried the frame and the request. Set by the gateway's transfer
    /// service while it runs; the handler answers on the worker's link itself (<see cref="GatewayWorkerLink.Link"/>). With none set every request is answered with a refusal.
    /// </summary>
    internal Func<GatewayWorkerLink, ControlSubmit, Task>? ControlSubmitHandler { get; set; }

    /// <summary>
    /// True while the gateway's operator-restart service (<c>GatewayRestartOrchestration</c>, hosted with <c>Gateway:Handoff:Enabled</c>) listens for <see cref="ControlRestartSubmit"/> frames on the worker
    /// links. It answers those itself; with it off the gateway answers them with a <see cref="ControlReasonCode.NotHosted"/> refusal instead of leaving the worker to time out.
    /// </summary>
    internal volatile bool RestartSubmitsHandled;

    private void RouteControlSubmit(GatewayWorkerLink worker, ControlSubmit submit)
    {
        if (submit.Body is ControlRestartSubmit)
        {
            if (!RestartSubmitsHandled)
            {
                worker.Link?.SendControlSubmitReply(new ControlSubmitReply(
                    submit.RequestId, false, Guid.Empty, "this gateway hosts no operator restart (Gateway:Handoff:Enabled is off)", ControlReasonCode.NotHosted));
            }

            return; // answered by GatewayRestartOrchestration, subscribed to the same link event
        }

        Func<GatewayWorkerLink, ControlSubmit, Task>? handler = ControlSubmitHandler;
        if (handler is null)
        {
            worker.Link?.SendControlSubmitReply(new ControlSubmitReply(
                submit.RequestId, false, Guid.Empty, "this gateway hosts no player transfers (Gateway:Transfers:Enabled is off)", ControlReasonCode.NotHosted));
            return;
        }

        _ = AnswerControlSubmitAsync(handler, worker, submit);
    }

    private async Task AnswerControlSubmitAsync(Func<GatewayWorkerLink, ControlSubmit, Task> handler, GatewayWorkerLink worker, ControlSubmit submit)
    {
        try
        {
            await handler(worker, submit).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "The control submit {RequestId} of worker {Node} failed", submit.RequestId, worker.NodeId);
            worker.Link?.SendControlSubmitReply(new ControlSubmitReply(submit.RequestId, false, Guid.Empty, "the gateway failed to take the request", ControlReasonCode.GatewayFailure));
        }
    }

    /// <summary>This gateway process's link incarnation (the manifest every link it opens advertises, so a worker counts all of them as one gateway).</summary>
    internal Guid Incarnation => _incarnation;

    /// <summary>The link supervisor of the worker with node id <paramref name="nodeId"/>, or null when none is configured (or before start).</summary>
    internal GatewayWorkerLink? FindWorker(string nodeId)
    {
        foreach (GatewayWorkerLink worker in _workers)
        {
            if (string.Equals(worker.NodeId, nodeId, StringComparison.Ordinal))
            {
                return worker;
            }
        }

        return null;
    }

    /// <summary>
    /// Test seam: runs on a client read loop after a forwarded packet's session generation is read and before the packet is routed (held, dropped or sent),
    /// so a test can let a transfer's route switch complete in that window. Null (the default) in production.
    /// </summary>
    internal Action<GatewayClientConnection, ArcaneCore.Protocol.WorldOpcode>? BeforeRouteClientPacket { get; set; }

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
    /// which disconnects its clients (they log in again through the new link; <see cref="RetargetWorkerSeamlessAsync(IPEndPoint, CancellationToken)"/> carries them across instead), and the link supervisor
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

    /// <summary>
    /// The seamless reroute of a whole-World handoff: like <see cref="RetargetWorkerAsync"/>, but the clients on the old worker are not disconnected. The first
    /// worker's supervisor connects to <paramref name="worker"/> while the old link is still open, every connection forwarding on the old link is re-pointed to
    /// the new one (<see cref="GatewayClientConnection.HandOffAsync"/>: the character is adopted by the new world, the client gets a synthesized
    /// SMSG_TRANSFER_PENDING / SMSG_NEW_WORLD, the packets held since <see cref="HoldSessionsForHandoff()"/> follow), and only then is the old link closed. A client whose
    /// move fails is disconnected, the others are unaffected. Completes when every move ended. Without a registered link (or when the link already points at
    /// <paramref name="worker"/>) it is the plain retarget. A gateway restart still drops every client. In-process only. Idempotent.
    /// </summary>
    public async Task RetargetWorkerSeamlessAsync(IPEndPoint worker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);
        GatewayWorkerLink[] workers = _workers;
        if (workers.Length == 0 || (workers[0].Link is null && !HasFlushedHold(workers[0])) || Equals(workers[0].ConnectedEndPoint, worker))
        {
            await RetargetWorkerAsync(worker, cancellationToken).ConfigureAwait(false);
            return;
        }

        _worker = worker;
        _logger.LogInformation("Worker link seamlessly retargeted to {Worker}", worker);
        await RetargetSlotSeamlessAsync(workers[0], worker, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The node-keyed form of <see cref="RetargetWorkerSeamlessAsync(IPEndPoint, CancellationToken)"/>: moves only the link of the worker whose node id is
    /// <paramref name="nodeId"/> (the source of a whole-World handoff) and the sessions forwarding on it; every other worker's link and sessions are not touched.
    /// Without a registered link on that node (or when it already points at <paramref name="worker"/>) it is the plain retarget of that node's link.
    /// Throws <see cref="ArgumentException"/> for a node id that is not a configured worker. In-process only. Idempotent.
    /// </summary>
    public async Task RetargetWorkerSeamlessAsync(string nodeId, IPEndPoint worker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);
        GatewayWorkerLink slot = RequireWorker(nodeId);
        if (ReferenceEquals(slot, _workers[0]))
        {
            _worker = worker;
        }

        if ((slot.Link is null && !HasFlushedHold(slot)) || Equals(slot.ConnectedEndPoint, worker))
        {
            _logger.LogInformation("Worker link {Node} retargeted to {Worker}", slot.NodeId, worker);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                slot.Retarget(worker);
                if (slot.Link is not null && Equals(slot.ConnectedEndPoint, worker))
                {
                    return;
                }

                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Worker link {Node} seamlessly retargeted to {Worker}", slot.NodeId, worker);
        await RetargetSlotSeamlessAsync(slot, worker, cancellationToken).ConfigureAwait(false);
    }

    private async Task RetargetSlotSeamlessAsync(GatewayWorkerLink slot, IPEndPoint worker, CancellationToken cancellationToken)
    {
        var moved = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        slot.Retarget(worker, async (previous, next, token) =>
        {
            try
            {
                moved.TrySetResult(await MoveSessionsAsync(previous, next, slot, token).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                moved.TrySetException(ex);
            }
        });
        await moved.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private GatewayWorkerLink RequireWorker(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        foreach (GatewayWorkerLink worker in _workers)
        {
            if (string.Equals(worker.NodeId, nodeId, StringComparison.Ordinal))
            {
                return worker;
            }
        }

        throw new ArgumentException($"'{nodeId}' is not a configured gateway worker", nameof(nodeId));
    }

    private async Task<int> MoveSessionsAsync(GatewaySessionLinkClient? previous, GatewaySessionLinkClient next, GatewayWorkerLink slot, CancellationToken cancellationToken)
    {
        var moves = new List<Task<bool>>();
        foreach (GatewayClientConnection connection in _connections.Keys)
        {
            if (previous is not null && connection.IsForwardingOn(previous))
            {
                moves.Add(connection.HandOffAsync(previous, next, cancellationToken));
            }
            else if (connection.FlushedHoldLinkOf(slot) is { } lost && connection.IsForwardingOn(lost))
            {
                // The source died after the handoff flushed (its link is gone, the old session with it): the hold survived, so the connection moves from the link its hold began on.
                moves.Add(connection.HandOffAsync(lost, next, cancellationToken));
            }
        }

        bool[] results = await Task.WhenAll(moves).ConfigureAwait(false);

        // A connection that holds but was not moved (it left Forwarding after the hold began, a per-player transfer re-pointed it, or a login redirect took it) would keep
        // holding until it overflows: release it to the world it is on. A connection the move finished no longer holds, so this is a no-op for it.
        // Keyed on the link the hold began on, not the link the connection forwards on now: another worker's connections may be holding for a handoff of their own.
        foreach (GatewayClientConnection connection in _connections.Keys)
        {
            connection.EndHandoffHold(previous, slot);
        }

        int moved = results.Count(result => result);
        _logger.LogInformation("World handoff moved {Moved} of {Total} sessions to the promoted world", moved, results.Length);
        return moved;
    }

    /// <summary>
    /// The source world froze: hold the client packets of every connection forwarding on the first worker's link (instead of sending them to the frozen world),
    /// so <see cref="RetargetWorkerSeamlessAsync(IPEndPoint, CancellationToken)"/> delivers them to the promoted world, or <see cref="ReleaseHandoffHold()"/> returns them to the source when the
    /// handoff aborts. Each connection holds at most 256 packets; one past that is disconnected. Returns how many connections now hold.
    /// </summary>
    public int HoldSessionsForHandoff()
    {
        GatewayWorkerLink[] workers = _workers;
        if (workers.Length == 0 || workers[0].Link is not { } link)
        {
            return 0;
        }

        int held = 0;
        foreach (GatewayClientConnection connection in _connections.Keys)
        {
            if (connection.BeginHandoffHold(link, workers[0]))
            {
                held++;
            }
        }

        return held;
    }

    /// <summary>
    /// The node-keyed form of <see cref="HoldSessionsForHandoff()"/>: holds only the connections forwarding on the link of the worker whose node id is
    /// <paramref name="nodeId"/> (the source of the handoff); sessions on other workers keep flowing. Returns how many connections now hold (0 while that
    /// worker has no registered link). Throws <see cref="ArgumentException"/> for a node id that is not a configured worker.
    /// </summary>
    public int HoldSessionsForHandoff(string nodeId)
    {
        GatewayWorkerLink slot = RequireWorker(nodeId);
        if (slot.Link is not { } link)
        {
            return 0;
        }

        int held = 0;
        foreach (GatewayClientConnection connection in _connections.Keys)
        {
            if (connection.BeginHandoffHold(link, slot))
            {
                held++;
            }
        }

        return held;
    }

    /// <summary>
    /// The node-keyed form of <see cref="ReleaseHandoffHold()"/>: the handoff of that worker aborted, so every connection that node's hold took (whatever link it
    /// forwards on now, and even when the node has no link or a re-registered one) replays its held packets and forwards again. Another worker's holds stay in place.
    /// Idempotent. Throws <see cref="ArgumentException"/> for a node id that is not a configured worker.
    /// </summary>
    public void ReleaseHandoffHold(string nodeId)
    {
        GatewayWorkerLink slot = RequireWorker(nodeId);
        GatewaySessionLinkClient? link = slot.Link;
        foreach (GatewayClientConnection connection in _connections.Keys)
        {
            connection.EndHandoffHold(link, slot);
        }
    }

    /// <summary>
    /// The handoff of the first configured worker reached Flushed (the source's saves are durable): from now on a connection this worker's hold took keeps its hold, and its
    /// client, when the source's link is lost (the source crashed); the reroute then moves it to the promoted world. Before this call the loss of the link disconnects the client.
    /// Returns how many connections it marked. Idempotent.
    /// </summary>
    public int MarkHandoffFlushed()
    {
        GatewayWorkerLink[] workers = _workers;
        return workers.Length == 0 ? 0 : MarkFlushed(workers[0]);
    }

    /// <summary>
    /// The node-keyed form of <see cref="MarkHandoffFlushed()"/>: marks the connections the hold of the worker whose node id is <paramref name="nodeId"/> took.
    /// Throws <see cref="ArgumentException"/> for a node id that is not a configured worker.
    /// </summary>
    public int MarkHandoffFlushed(string nodeId) => MarkFlushed(RequireWorker(nodeId));

    private int MarkFlushed(GatewayWorkerLink slot)
    {
        int marked = 0;
        foreach (GatewayClientConnection connection in _connections.Keys)
        {
            if (connection.MarkHandoffFlushed(slot))
            {
                marked++;
            }
        }

        return marked;
    }

    private bool HasFlushedHold(GatewayWorkerLink slot)
    {
        foreach (GatewayClientConnection connection in _connections.Keys)
        {
            if (connection.FlushedHoldLinkOf(slot) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The handoff aborted: every connection that holds replays its held packets to the world it is still on and forwards again. Idempotent.</summary>
    public void ReleaseHandoffHold()
    {
        foreach (GatewayClientConnection connection in _connections.Keys)
        {
            connection.EndHandoffHold();
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
            workers[index] = new GatewayWorkerLink(entry, endpoint, config, _incarnation, _loggers, Warden, _logger, routes, _realmLiveness, Transfers, ControlTimeouts, RaiseWorkerLinkUp, RouteControlSubmit);
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
    private readonly ArcaneCore.World.Cluster.Control.LinkControlTimeouts? _controlTimeouts;
    private volatile ArcaneCore.World.Cluster.Control.LinkControlChannel? _control;
    private volatile IPEndPoint _target;
    private volatile IPEndPoint? _connected;
    private volatile GatewaySessionLinkClient? _link;
    private volatile string? _lastError;
    private readonly Lock _statusGate = new();
    private readonly Action<string>? _linkUpObserver;
    private readonly Action<GatewayWorkerLink, ControlSubmit>? _controlSubmit;
    private StatusBox? _lastReport;
    private int _status;
    private long _failures;
    private long _reconnects;
    private TaskCompletionSource _linkUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _retarget;
    private TaskCompletionSource? _backoffWake;
    private Func<GatewaySessionLinkClient?, GatewaySessionLinkClient, CancellationToken, Task>? _migrate;
    private GatewaySessionLinkClient? _draining;

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
        GatewayTransferRouter? transfers = null,
        ArcaneCore.World.Cluster.Control.LinkControlTimeouts? controlTimeouts = null,
        Action<string>? linkUpObserver = null,
        Action<GatewayWorkerLink, ControlSubmit>? controlSubmit = null)
    {
        _controlSubmit = controlSubmit;
        ArgumentNullException.ThrowIfNull(routes);
        _controlTimeouts = controlTimeouts;
        _linkUpObserver = linkUpObserver;
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

    /// <summary>
    /// The control channel of the registered link (design _wf/handoff-control-DESIGN.md section 2.4), or null while the link is down or <c>Gateway:Handoff:Enabled</c> is off.
    /// One channel per link: a reconnect or a retarget builds a new one, and the old one fails its pending requests when its link ends.
    /// </summary>
    public ArcaneCore.World.Cluster.Control.LinkControlChannel? ControlChannel => _control is { IsConnected: true } channel ? channel : null;

    /// <summary>The latest <c>WorkerStatus</c> report of the registered link, or null while the link is down or no report has arrived on it yet.</summary>
    public WorkerStatusReport? LastStatus
    {
        get
        {
            lock (_statusGate)
            {
                return _lastReport is { } box ? box.Report : null;
            }
        }
    }

    private sealed record StatusBox(WorkerStatusReport Report);

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

        // With no link the supervisor may be sitting out a retry backoff (up to 30 s) toward the endpoint the slot just left: wake it so the new endpoint is tried at once (the hold budget of a
        // handoff whose source died is spent waiting for exactly this connection).
        Volatile.Read(ref _backoffWake)?.TrySetResult();
    }

    /// <summary>
    /// Like <see cref="Retarget(IPEndPoint)"/>, but the registered link stays open until the new one is registered: <paramref name="migrate"/> then runs once with the old
    /// link (null when it was already lost) and the new one, and the old link is closed after it returns.
    /// </summary>
    internal void Retarget(IPEndPoint worker, Func<GatewaySessionLinkClient?, GatewaySessionLinkClient, CancellationToken, Task> migrate)
    {
        ArgumentNullException.ThrowIfNull(migrate);
        Volatile.Write(ref _migrate, migrate);
        Retarget(worker);
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
        try
        {
            await RunLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            if (Interlocked.Exchange(ref _draining, null) is { } draining)
            {
                await draining.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        TimeSpan delay = _config.LinkRetryDelay > TimeSpan.Zero ? _config.LinkRetryDelay : TimeSpan.FromSeconds(1);
        TimeSpan nextDelay = delay;
        int registrations = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            IPEndPoint worker = _target;
            Volatile.Write(ref _status, (int)GatewayWorkerLinkStatus.Connecting);
            var link = new GatewaySessionLinkClient(_config.ToManifest(_incarnation), worker, _config.LinkSecret, _loggers.CreateLogger<GatewaySessionLinkClient>(), _warden?.Posture);
            string node = NodeId;
            GatewayRealmLiveness? liveness = _realmLiveness;
            bool linkEnded = false; // guarded by _statusGate: a report that arrives after this link went down must not repopulate _lastReport
            link.WorkerStatusReceived = report =>
            {
                lock (_statusGate)
                {
                    if (!linkEnded)
                    {
                        _lastReport = new StatusBox(report); // the transfer map router reads readiness and load from the latest report
                    }
                }

                liveness?.OnWorkerStatus(node, report); // the realm-list row of the realm is written from these reports
            };

            if (_transfers is { } transfers)
            {
                // The worker's coordinator drives staged transfers over this link; each frame is answered with an ack on the same link.
                link.TransferControlReceived = control => _ = transfers.HandleControlAsync(control, link.SendTransferAck, stoppingToken);
            }

            if (_controlSubmit is { } routeSubmit)
            {
                // A worker's request to start an operation (a far teleport): answered on this link by the gateway's transfer service.
                // Kind 2 (transfer) is answered by the transfer service; kind 1 (operator restart) by GatewayRestartOrchestration, subscribed to the same event, or refused here when it is off.
                link.ControlSubmitReceived += submit => routeSubmit(this, submit);
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
                var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Exchange(ref _backoffWake, wake); // a full fence: Retarget stores _target and then reads this field, this publishes it and then reads _target
                bool woken = true;
                try
                {
                    if (!Equals(_target, worker))
                    {
                        wake.TrySetResult(); // retargeted while this attempt was connecting: no wait at all
                    }

                    await wake.Task.WaitAsync(nextDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    woken = false; // the backoff elapsed
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    Volatile.Write(ref _backoffWake, null);
                }

                nextDelay = woken ? delay : TimeSpan.FromTicks(Math.Min(nextDelay.Ticks * 2, MaxLinkRetryDelay.Ticks));
                continue;
            }

            nextDelay = delay;
            var retarget = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _retarget, retarget);
            _connected = worker;
            _lastError = null;
            _routes.LinkUp(NodeId, link.WorkerOwnedMaps, link.WorkerAllowsInstanceMaps);
            if (_controlTimeouts is { } timeouts)
            {
                _control = new ArcaneCore.World.Cluster.Control.LinkControlChannel(link, timeouts, _loggers.CreateLogger<ArcaneCore.World.Cluster.Control.LinkControlChannel>());
            }

            _link = link;
            Volatile.Write(ref _status, (int)GatewayWorkerLinkStatus.Ready);
            if (registrations++ > 0)
            {
                Interlocked.Increment(ref _reconnects);
                GatewayMeters.CountLinkReconnect(_config.NodeId);
            }

            _logger.LogInformation("Worker link {Node} to {Worker} registered", NodeId, worker);
            Volatile.Read(ref _linkUp).TrySetResult();
            _linkUpObserver?.Invoke(NodeId);
            if (!Equals(_target, worker))
            {
                retarget.TrySetResult(); // retargeted while this attempt was connecting
            }

            if (Equals(_target, worker) && Interlocked.Exchange(ref _migrate, null) is { } migrate)
            {
                // A seamless retarget: the old link (kept open below) hands its sessions to this one, then closes.
                GatewaySessionLinkClient? previous = Interlocked.Exchange(ref _draining, null);
                try
                {
                    await migrate(previous, link, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogWarning("Worker link {Node}: the session migration to {Worker} failed ({Message})", NodeId, worker, ex.Message);
                }
                finally
                {
                    if (previous is not null)
                    {
                        await previous.DisposeAsync().ConfigureAwait(false);
                    }
                }
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
            _control = null;
            _connected = null;
            lock (_statusGate)
            {
                linkEnded = true;
                _lastReport = null;
            }

            _routes.LinkDown(NodeId);
            if (retarget.Task.IsCompleted)
            {
                Volatile.Write(ref _status, (int)GatewayWorkerLinkStatus.Connecting);
                _logger.LogInformation("Worker link {Node} to {Worker} closed for the retarget to {Target}", NodeId, worker, _target);
            }
            else
            {
                _lastError = "link lost";
                Volatile.Write(ref _status, (int)GatewayWorkerLinkStatus.Backoff);
                _logger.LogWarning("Worker link {Node} to {Worker} lost; its clients were disconnected", NodeId, worker);
            }

            if (retarget.Task.IsCompleted && Volatile.Read(ref _migrate) is not null)
            {
                // One link is kept at a time. An earlier kept link that still carries sessions (the retarget moved on before its sessions were migrated) is the one to keep, and the newer link,
                // which carries none, is closed instead: disposing the older one would disconnect those players. Otherwise the older one nobody uses is closed, never leaked.
                GatewaySessionLinkClient? older = Volatile.Read(ref _draining);
                GatewaySessionLinkClient kept = older is not null && older.IsConnected && older.SessionCount > 0 && link.SessionCount == 0 ? older : link;
                GatewaySessionLinkClient? dropped = ReferenceEquals(kept, link) ? older : link;
                Volatile.Write(ref _draining, kept);
                if (dropped is not null && !ReferenceEquals(dropped, kept))
                {
                    await dropped.DisposeAsync().ConfigureAwait(false);
                }

                continue; // the link is kept open: the next registration moves its sessions, then closes it
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

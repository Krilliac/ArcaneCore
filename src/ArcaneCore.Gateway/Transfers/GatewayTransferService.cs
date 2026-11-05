using System.Collections.Concurrent;
using ArcaneCore.Gateway.Handoff;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Cluster.Transfer.Remote;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Gateway.Transfers;

/// <summary>
/// The opt-in key of the gateway's per-player transfer hosting (<c>Gateway:Transfers:Enabled</c>, design _wf/handoff-control-DESIGN.md sections 2.6 and 4). Off, the default: the gateway opens no characters
/// database connection and no control channel for transfers, and behaves exactly as before.
/// </summary>
[ConfigDaemons(ConfigDaemon.Gateway)]
public sealed class GatewayTransferOptions
{
    public const string SectionName = "Gateway:Transfers";

    /// <summary>`true` lets this gateway coordinate cross-worker player transfers and finish interrupted ones from the journal: it connects to the characters database (Database:Characters:*, schema validation only, nothing is created or upgraded) and opens a control channel to every worker. The workers must have Cluster:Worker:Transfers:Enabled on. `false`, the default, changes nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often the gateway looks for unresolved player transfers in the journal and recovers them (before the commit they abort, after it they roll forward). Default 5 s.</summary>
    public TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a player transfer may stay unresolved before the gateway raises an error-severity anomaly (it keeps retrying). Default 10 minutes.</summary>
    public TimeSpan AnomalyAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Every problem with the bound values, as <c>Gateway:Transfers:Key: problem</c> lines; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (RecoveryInterval <= TimeSpan.Zero)
        {
            problems.Add($"{SectionName}:RecoveryInterval: must be positive.");
        }

        if (AnomalyAfter <= TimeSpan.Zero)
        {
            problems.Add($"{SectionName}:AnomalyAfter: must be positive.");
        }

        return problems;
    }
}

/// <summary>
/// What <see cref="GatewayTransferService.SubmitAsync"/> answered: whether the gateway took the transfer and why not, and, when it did, the task that ends with the outcome (it never faults for a transfer failure;
/// cancelling the service's stop token settles it like any other failure). <see cref="Code"/> is the structured form of <see cref="Reason"/> that goes to the worker on the wire.
/// </summary>
public sealed record GatewayTransferSubmission(bool Accepted, string Reason, Guid TransferId, Task<PlayerTransferOutcome>? Completion, ControlReasonCode Code = ControlReasonCode.None);

/// <summary>
/// <see cref="ITransferGatewayRouter"/> for the gateway's own coordinator: the same router, except that a route switch for a session that is no longer staged is done, not an error. After the commit the transfer
/// is the journal's, and a client that left (or a gateway that restarted: staged routes are in memory only and a restarted gateway has no sessions) has nothing left to switch; failing there would keep the recovery of a
/// committed transfer from ever completing. A staged transfer's own failures (a wrong target, a target that went away) still throw.
/// Thread-safe when the inner router and <paramref name="isStaged"/> are.
/// </summary>
internal sealed class GatewayCommitTolerantRouter(ITransferGatewayRouter inner, Func<Guid, long, bool> isStaged, ILogger logger) : ITransferGatewayRouter
{
    public Task StageWorldPortAsync(
        Guid transferId, ulong characterGuid, long sessionGeneration, WorkerNodeId sourceWorkerId, WorkerNodeId targetWorkerId,
        uint targetMapId, uint targetInstanceId, TransferPosition targetPosition, CancellationToken cancellationToken = default) =>
        inner.StageWorldPortAsync(
            transferId, characterGuid, sessionGeneration, sourceWorkerId, targetWorkerId, targetMapId, targetInstanceId, targetPosition, cancellationToken);

    public Task CancelStagedWorldPortAsync(
        Guid transferId, ulong characterGuid, long sessionGeneration, WorkerNodeId restoredWorkerId, string abortReason, CancellationToken cancellationToken = default) =>
        inner.CancelStagedWorldPortAsync(transferId, characterGuid, sessionGeneration, restoredWorkerId, abortReason, cancellationToken);

    public async Task CommitRouteSwitchAsync(
        Guid transferId, ulong characterGuid, long sessionGeneration, WorkerNodeId targetWorkerId, CharacterOwnershipToken targetOwnershipToken,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.CommitRouteSwitchAsync(transferId, characterGuid, sessionGeneration, targetWorkerId, targetOwnershipToken, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (!isStaged(transferId, sessionGeneration))
        {
            logger.LogInformation(
                "Transfer {TransferId}: nothing to switch for session {Generation} (the client is gone or this gateway restarted): {Message}", transferId, sessionGeneration, ex.Message);
        }
    }
}

/// <summary>
/// What <see cref="GatewayTransferService"/> is built from. <see cref="ForServer"/> is the production wiring: one <see cref="RemoteTransferSourceWorker"/> and one <see cref="RemoteTransferTargetWorker"/> per configured
/// <c>Gateway:Workers:N</c> entry (registered once; each finds its worker's live control channel on every call), the <see cref="GatewayClusterMapRouter"/> and the gateway's own transfer router. Tests build it from fakes.
/// </summary>
internal sealed record GatewayTransferWiring(
    IReadOnlyCollection<string> SlotNodes,
    IClusterMapRouter MapRouter,
    ITransferGatewayRouter GatewayRouter,
    IReadOnlyCollection<ITransferSourceWorker> Sources,
    IReadOnlyCollection<ITransferTargetWorker> Targets,
    Func<WorkerNodeId, Guid, CancellationToken, Task<bool>>? IncarnationCheck,
    Func<long, bool> SessionExists,
    Func<string, IReadOnlyList<string>> SlotNames,
    Func<WorkerNodeId, CancellationToken, Task<int>>? AdmittedGateways = null,
    Func<uint, uint, CancellationToken, Task>? WaitForMapOwner = null)
{
    public static GatewayTransferWiring ForServer(GatewayServer server, GatewayHandoffService? handoffs, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(loggers);
        ILogger logger = loggers.CreateLogger<GatewayTransferService>();
        var nodes = new List<string>();
        var sources = new List<ITransferSourceWorker>();
        var targets = new List<ITransferTargetWorker>();
        var remoteTargets = new Dictionary<WorkerNodeId, RemoteTransferTargetWorker>();
        foreach (GatewayWorkerEntry entry in server.Options.EffectiveWorkers())
        {
            string node = entry.NodeId;
            Func<LinkControlChannel?> channel = () => server.FindWorker(node)?.ControlChannel;
            var id = new WorkerNodeId(node);
            var target = new RemoteTransferTargetWorker(id, channel, logger);
            nodes.Add(node);
            sources.Add(new RemoteTransferSourceWorker(id, channel, logger));
            targets.Add(target);
            remoteTargets[id] = target;
        }

        // Design 4.3, pre-commit row: the remote target's incarnation must still be the one the transfer reserved under.
        // Orchestrator decision Q1: one gateway per realm. The worker's TargetDescribe (every transfer-enabled worker answers it, source or target) reports how many distinct gateways it has admitted.
        async Task<int> Gateways(WorkerNodeId worker, CancellationToken cancellationToken) =>
            remoteTargets.TryGetValue(worker, out RemoteTransferTargetWorker? remote)
                ? (await remote.RefreshAsync(cancellationToken).ConfigureAwait(false)).AdmittedGateways
                : throw new InvalidOperationException($"worker '{worker.Value}' is not a configured slot");

        Task<bool> Check(WorkerNodeId worker, Guid transferId, CancellationToken cancellationToken) =>
            remoteTargets.TryGetValue(worker, out RemoteTransferTargetWorker? target) ? target.IncarnationUnchangedSinceReserveAsync(transferId, cancellationToken) : Task.FromResult(false);

        return new GatewayTransferWiring(
            nodes,
            new GatewayClusterMapRouter(
                () => server.Routes,
                node => server.FindWorker(node) is { } worker ? new GatewayWorkerView(worker.Link is not null, worker.LastStatus) : null),
            new GatewayCommitTolerantRouter(server.Transfers, server.Transfers.IsStaged, logger),
            sources,
            targets,
            Check,
            generation => server.FindConnection(generation) is IGatewayTransferSession { IsOpen: true },
            node => handoffs?.NodeNames(node) ?? new[] { node },
            Gateways,
            (mapId, instanceId, cancellationToken) => server.WaitForMapOwnerLinkAsync(mapId, instanceId, GatewayTransferService.MapOwnerLinkWait, cancellationToken));
    }
}

/// <summary>
/// The gateway's per-player transfer coordinator (design _wf/handoff-control-DESIGN.md sections 1, 4, 5 and 6, commit C8). It runs the unchanged <see cref="PlayerTransferCoordinator"/> through
/// <see cref="PlayerTransferCoordinatorHost"/> over the control channels of the workers (<see cref="RemoteTransferSourceWorker"/>, <see cref="RemoteTransferTargetWorker"/>) and the gateway's own
/// <see cref="GatewayTransferRouter"/> (in process), with the <see cref="GatewayClusterMapRouter"/> as placement.
/// <list type="bullet">
/// <item><description>Opt-in: it exists only with <c>Gateway:Transfers:Enabled</c>; the journal is the characters database, read validate-only.</description></item>
/// <item><description><see cref="SubmitAsync"/> answers refused, or accepted with the transfer running in the background, at once unless the target map's configured owner has no link right now: it then waits up to <see cref="MapOwnerLinkWait"/> for that link first. A refusal needs no cleanup: nothing was journalled.</description></item>
/// <item><description>Transfers and whole-World handoffs keep apart through <see cref="GatewaySlotStateMachine"/> and the journal: a transfer is refused while its source or target slot is handing off or has an unresolved handoff
/// in the journal; a handoff (<see cref="EnterHandoffAsync"/>, called by <see cref="GatewayHandoffService"/>) is refused while transfers involve its slot, running or unresolved.</description></item>
/// <item><description>The pre-commit check (design 4.3): after stage 4 the target is described again and a transfer whose target process changed since the reserve is aborted instead of committed.</description></item>
/// <item><description>Recovery (<see cref="RecoverOnceAsync"/>, driven by <see cref="GatewayTransferRecoveryService"/>): <see cref="TransferRecoveryService"/> over the journal rows this gateway owns (not the ones running here, not a world handoff's,
/// not a slot that is handing off). Committed transfers roll forward, pre-commit ones abort; it needs no client session (the route switch of a session that is gone is a no-op).</description></item>
/// </list>
/// Thread-safe. Stopping cancels the transfers that have not committed (they abort and resume the source) and waits for the running ones, bounded by the stop token; a committed one it left behind is rolled forward by recovery.
/// </summary>
public sealed class GatewayTransferService : IHostedService, IAsyncDisposable, IGatewayHandoffGuard
{
    /// <summary>
    /// How long a submit waits for the link of a target map's configured owner before it refuses the teleport. The player stays in the world and nothing is journalled meanwhile, so this bounds how long
    /// a teleport to a map whose worker is really down takes to fail (what the source worker already did to the player for the teleport stays done either way: <c>TeleportToRemoteMap</c>); it is far inside the worker's in-flight expiry (<see cref="GatewayClusterFarTeleportRouter.InFlightExpiry"/>).
    /// </summary>
    internal static readonly TimeSpan MapOwnerLinkWait = TimeSpan.FromSeconds(10);

    private readonly GatewayTransferOptions _options;
    private readonly ITransferJournalScan _journal;
    private readonly IWorldHandoffStore? _handoffStore;
    private readonly IClusterMapRouter _mapRouter;
    private readonly Func<WorkerNodeId, Guid, CancellationToken, Task<bool>>? _incarnationCheck;
    private readonly Func<long, bool> _sessionExists;
    private readonly Func<string, IReadOnlyList<string>> _slotNames;
    private readonly Func<WorkerNodeId, CancellationToken, Task<int>>? _admittedGateways;
    private readonly Func<uint, uint, CancellationToken, Task>? _waitForMapOwner;
    private readonly HashSet<string> _slotSet;
    private readonly PlayerTransferCoordinator _coordinator;
    private readonly PlayerTransferCoordinatorHost _host;
    private readonly TransferRecoveryService _recovery;
    private readonly GatewaySlotStateMachine _machine;
    private readonly ConcurrentDictionary<Guid, byte> _running = new();
    private readonly WorldHandoffInFlight _inFlight = new();
    private readonly CancellationTokenSource _runs = new();
    private readonly ILogger _logger;
    private readonly Dictionary<Guid, DateTime> _firstSeen = [];
    private readonly HashSet<Guid> _reported = [];
    private readonly OwnedJournalScan _scan;
    private readonly GatewayServer? _server;
    private int _stopped;
    private int _linkUpWired;

    /// <summary>
    /// Builds the production service for <paramref name="server"/> (its worker links must carry control channels: <c>Gateway:Transfers:Enabled</c>). When <paramref name="handoffs"/> is given it is coordinated with: its handoffs
    /// enter this service first (<see cref="IGatewayHandoffGuard"/>).
    /// </summary>
    /// <param name="handoffStore">The handoff journal, to refuse transfers of a slot with an unresolved handoff and to leave a handoff's own transfers alone; null when this gateway hosts no handoffs.</param>
    public GatewayTransferService(
        GatewayServer server,
        ITransferDecisionStore store,
        ITransferJournalScan journal,
        GatewayTransferOptions options,
        IWorldHandoffStore? handoffStore = null,
        GatewayHandoffService? handoffs = null,
        ILoggerFactory? loggers = null)
        : this(options, store, journal, handoffStore, GatewayTransferWiring.ForServer(server, handoffs, loggers ?? NullLoggerFactory.Instance), loggers)
    {
        if (server.ControlTimeouts is null)
        {
            throw new InvalidOperationException("the gateway transfer service needs Gateway:Transfers:Enabled");
        }

        _server = server;
        server.ControlSubmitHandler = HandleSubmitAsync;
        if (handoffs is not null)
        {
            handoffs.Guard = this;
        }
    }

    internal GatewayTransferService(
        GatewayTransferOptions options,
        ITransferDecisionStore store,
        ITransferJournalScan journal,
        IWorldHandoffStore? handoffStore,
        GatewayTransferWiring wiring,
        ILoggerFactory? loggers = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(wiring);
        ILoggerFactory factory = loggers ?? NullLoggerFactory.Instance;
        _logger = factory.CreateLogger<GatewayTransferService>();
        _options = options;
        _journal = journal;
        _handoffStore = handoffStore;
        _mapRouter = wiring.MapRouter;
        _incarnationCheck = wiring.IncarnationCheck;
        _sessionExists = wiring.SessionExists;
        _slotNames = wiring.SlotNames;
        _admittedGateways = wiring.AdmittedGateways;
        _waitForMapOwner = wiring.WaitForMapOwner;
        _slotSet = new HashSet<string>(wiring.SlotNodes, StringComparer.Ordinal);
        _machine = new GatewaySlotStateMachine(wiring.SlotNodes);
        _coordinator = new PlayerTransferCoordinator(wiring.MapRouter, store, wiring.GatewayRouter, wiring.Sources, wiring.Targets);
        _host = new PlayerTransferCoordinatorHost(_coordinator, store, factory.CreateLogger<PlayerTransferCoordinatorHost>());
        _scan = new OwnedJournalScan(this);
        _recovery = new TransferRecoveryService(
            _coordinator, store, _scan, wiring.GatewayRouter, factory.CreateLogger<TransferRecoveryService>());
    }

    /// <summary>
    /// Raised when a worker link of the gateway comes up (design section 6: transfer recovery also runs then, not only on its interval). Raised on the link's supervisor task between <see cref="StartAsync"/>
    /// and <see cref="StopAsync"/>; handlers must only signal. Never raised for a service built without a server (tests).
    /// </summary>
    internal event Action? LinkUp;

    /// <summary>The slots transfers can involve (every <c>Gateway:Workers:N:NodeId</c>).</summary>
    public IReadOnlyCollection<string> Slots => _slotSet;

    /// <summary>The state of one slot (Idle, Transferring, HandingOff).</summary>
    internal GatewaySlotStateMachine Machine => _machine;

    /// <summary>The transfers running on this service (the host's stop waits for them).</summary>
    public WorldHandoffInFlight InFlight => _inFlight;

    /// <summary>Whether this service is running transfer <paramref name="transferId"/> right now.</summary>
    public bool IsRunning(Guid transferId) => _running.ContainsKey(transferId);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_server is not null && Interlocked.Exchange(ref _linkUpWired, 1) == 0)
        {
            _server.WorkerLinkUp += OnWorkerLinkUp;
        }

        return Task.CompletedTask;
    }

    private void OnWorkerLinkUp(string nodeId)
    {
        if (_slotSet.Contains(nodeId))
        {
            LinkUp?.Invoke();
        }
    }

    /// <summary>
    /// Cancels the transfers that have not committed (they abort and resume their source), waits for every running transfer (bounded by <paramref name="cancellationToken"/>; one past its commit that did not finish is
    /// rolled forward by <see cref="RecoverOnceAsync"/> on the next start) and refuses new submissions.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        if (_server is not null)
        {
            _server.WorkerLinkUp -= OnWorkerLinkUp;
            if (_server.ControlSubmitHandler is { } installed && ReferenceEquals(installed.Target, this))
            {
                _server.ControlSubmitHandler = null; // a worker's request is refused from now on, not queued behind a stopped service
            }
        }

        await _runs.CancelAsync().ConfigureAwait(false);
        try
        {
            await _inFlight.WhenIdleAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("The gateway stopped while a player transfer was still finishing; it will be recovered from its journal on the next start");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _runs.Dispose();
    }

    /// <summary>
    /// Takes one transfer request (design 2.3, after the control handler checked that the source is the node of the link it arrived on). It is refused, with the reason and with nothing journalled, when the gateway is stopping,
    /// the session is gone, the target map cannot be routed or its worker cannot take a player, the source and target are the same worker, a slot involved is handing off or has an unresolved handoff in the journal.
    /// A map whose configured owner has no link right now (a worker that is starting, restarting, or up but between two dials of the link's backoff) is routed again once that link registers, within <see cref="MapOwnerLinkWait"/>.
    /// Otherwise the transfer runs in the background (the target pinned to the one routed here) and the submission carries its completion.
    /// </summary>
    public async Task<GatewayTransferSubmission> SubmitAsync(PlayerTransferRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Volatile.Read(ref _stopped) != 0)
        {
            return Refused(request, ControlReasonCode.GatewayStopping, "the gateway is stopping");
        }

        if (!_sessionExists(request.SessionGeneration))
        {
            return Refused(request, ControlReasonCode.SessionNotOpen, $"session {request.SessionGeneration} is not an open client session of this gateway");
        }

        WorkerNodeId target;
        if (request.ExplicitTargetWorkerId is { IsEmpty: false } explicitTarget)
        {
            if (!_mapRouter.TryGetWorker(explicitTarget, out ClusterWorkerStatus status) || !status.CanAcceptPlayer)
            {
                return Refused(request, ControlReasonCode.TargetNotReady, $"target worker '{explicitTarget}' is not ready or has no available capacity ({ClusterMapPlacementTable.FailureWorkerNotReady})");
            }

            target = explicitTarget;
        }
        else
        {
            bool routed = _mapRouter.TryResolveTargetWorker(request.TargetMapId, request.TargetInstanceId, out target, out string? failure);
            if (!routed && failure == ClusterMapPlacementTable.FailureMapUnassigned && _waitForMapOwner is { } waitForMapOwner)
            {
                // A worker whose link is down owns nothing for routing, whether it is dead or only between two dials of the link's backoff. Nothing is journalled or frozen yet, so the wait adds only
                // its delay; a refusal after it leaves what a refusal always left (the source worker revived a ghost and took the player off its transport when it sent this request, and no refusal
                // undoes that). The gateway may have begun stopping and the client may have left meanwhile.
                await waitForMapOwner(request.TargetMapId, request.TargetInstanceId, cancellationToken).ConfigureAwait(false);
                if (Volatile.Read(ref _stopped) != 0)
                {
                    return Refused(request, ControlReasonCode.GatewayStopping, "the gateway is stopping");
                }

                if (!_sessionExists(request.SessionGeneration))
                {
                    return Refused(request, ControlReasonCode.SessionNotOpen, $"session {request.SessionGeneration} is not an open client session of this gateway");
                }

                // However the wait ended: a link can register right at its deadline, and the wait watches one worker while another that could own the map may be the one that came up.
                routed = _mapRouter.TryResolveTargetWorker(request.TargetMapId, request.TargetInstanceId, out target, out failure);
            }

            if (!routed)
            {
                return Refused(
                    request, CodeOfPlacementFailure(failure), $"target map ({request.TargetMapId}, instance {request.TargetInstanceId}) cannot be routed: {failure ?? ClusterMapPlacementTable.FailureNoEligibleWorker}");
            }
        }

        if (target == request.SourceWorkerId)
        {
            return Refused(request, ControlReasonCode.SameWorker, "the source and the target are the same worker; use a local teleport");
        }

        if (_admittedGateways is { } admitted)
        {
            foreach (WorkerNodeId involved in new[] { request.SourceWorkerId, target })
            {
                int gateways;
                try
                {
                    gateways = await admitted(involved, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    return Refused(request, ControlReasonCode.WorkerUnreachable, $"worker '{involved.Value}' could not be asked how many gateways it admits ({ex.Message})");
                }

                if (gateways > 1)
                {
                    return Refused(request, ControlReasonCode.MultipleGateways, $"worker '{involved.Value}' reports {gateways} admitted gateways; a transfer needs exactly one gateway per realm");
                }
            }
        }

        if (!_machine.TryBeginTransfer(request.SourceWorkerId.Value, target.Value, out IDisposable? lease, out string reason))
        {
            return Refused(request, ControlReasonCode.SlotBusy, reason);
        }

        bool started = false;
        try
        {
            if (await UnresolvedHandoffOfAsync([request.SourceWorkerId.Value, target.Value], cancellationToken).ConfigureAwait(false) is { } blockedBy)
            {
                return Refused(request, ControlReasonCode.Unresolved, blockedBy);
            }

            if (!_running.TryAdd(request.TransferId, 0))
            {
                return Refused(request, ControlReasonCode.AlreadyRunning, $"transfer {request.TransferId} is already running");
            }

            if (Volatile.Read(ref _stopped) != 0)
            {
                // StopAsync ran between the first check and the registration above: it may already have seen the service idle, so this transfer must not start.
                _running.TryRemove(request.TransferId, out _);
                return Refused(request, ControlReasonCode.GatewayStopping, "the gateway is stopping");
            }

            PlayerTransferRequest pinned = request with { ExplicitTargetWorkerId = target };
            IDisposable held = lease!;
            Task<PlayerTransferOutcome> completion = _inFlight.Track(Task.Run(() => RunAsync(pinned, held), CancellationToken.None));
            started = true;
            return new GatewayTransferSubmission(true, string.Empty, request.TransferId, completion);
        }
        finally
        {
            if (!started)
            {
                lease!.Dispose();
            }
        }
    }

    /// <summary>
    /// Takes a worker's <see cref="ControlSubmit"/> (design section 2.3, commit C10) and answers on the link it arrived on. The source worker of the transfer is the slot node id of that link
    /// (<c>Gateway:Workers:N:NodeId</c>, the id the slot state machine and the journal scan key on), never a name the frame carries; the session must be open on that very link with the account the frame names.
    /// A refusal answers at once with its reason; an accepted transfer answers with its id and, when it ends, sends a <see cref="ControlNotice"/> of kind transfer outcome on the source's link (the
    /// completed outcome too, so the source clears its in-flight entry). A kind other than a transfer request is refused (the operator restart path is a later commit). Never throws for a transfer failure.
    /// </summary>
    internal async Task HandleSubmitAsync(GatewayWorkerLink worker, ControlSubmit submit)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(submit);
        GatewaySessionLinkClient? link = worker.Link;
        if (link is null)
        {
            return; // the link went down while the request was being taken; no answer is sent, so the worker's in-flight entry for it stays until its expiry (GatewayClusterFarTeleportRouter.InFlightExpiry)
        }

        if (submit.Body is not ControlTransferSubmit body)
        {
            link.SendControlSubmitReply(new ControlSubmitReply(submit.RequestId, false, Guid.Empty, "this request kind is not available over the control channel", ControlReasonCode.UnsupportedKind));
            return;
        }

        string node = worker.NodeId;
        if (!_slotSet.Contains(node))
        {
            link.SendControlSubmitReply(new ControlSubmitReply(submit.RequestId, false, body.TransferId, $"worker '{node}' is not a transfer slot of this gateway", ControlReasonCode.NotATransferSlot));
            return;
        }

        if (!link.HasSession(body.SessionGeneration)
            || _server?.FindConnection(body.SessionGeneration) is not { } connection
            || connection.AccountId != body.AccountId)
        {
            link.SendControlSubmitReply(new ControlSubmitReply(
                submit.RequestId, false, body.TransferId, $"session {body.SessionGeneration} of account {body.AccountId} is not open on the link of worker '{node}'", ControlReasonCode.SessionNotOpen));
            return;
        }

        PlayerTransferRequest request = RequestOf(node, body);
        GatewayTransferSubmission submission = await SubmitAsync(request, _runs.Token).ConfigureAwait(false);
        // The submit can wait for a map owner's link, and the source's link may have been replaced meanwhile (a reconnect). Only this answer or the outcome notice ends the worker's in-flight entry.
        (worker.Link ?? link).SendControlSubmitReply(new ControlSubmitReply(submit.RequestId, submission.Accepted, submission.TransferId, submission.Reason, submission.Code));
        if (!submission.Accepted || submission.Completion is not { } completion)
        {
            return;
        }

        PlayerTransferOutcome outcome = await completion.ConfigureAwait(false);
        // The source's link may have been replaced while the transfer ran (a reconnect): the notice goes to whichever link of the slot is up now.
        GatewaySessionLinkClient? current = worker.Link;
        if (current is null)
        {
            return;
        }

        ControlNoticeOutcome result = outcome.Succeeded ? ControlNoticeOutcome.Completed : ControlNoticeOutcome.Aborted;
        string text = outcome.Succeeded ? string.Empty : outcome.FailureMessage ?? outcome.FailureCode ?? "the transfer was aborted";
        ControlReasonCode code = outcome.Succeeded ? ControlReasonCode.None : OutcomeCode(outcome.FailureCode);
        current.SendControlNotice(new ControlNotice(ControlNoticeKind.TransferOutcome, body.SessionGeneration, body.AccountId, body.CharacterGuid, body.TransferId, result, text, code));
    }

    /// <summary>The reason code of an aborted transfer: a placement failure keeps its specific code (capacity, not ready, no worker), anything else is <see cref="ControlReasonCode.TransferAborted"/>.</summary>
    internal static ControlReasonCode OutcomeCode(string? failureCode)
    {
        ControlReasonCode placement = failureCode is null ? ControlReasonCode.Unspecified : CodeOfPlacementFailure(failureCode);
        return placement == ControlReasonCode.Unspecified ? ControlReasonCode.TransferAborted : placement;
    }

    /// <inheritdoc />
    public async Task<IDisposable> EnterHandoffAsync(string slotNode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotNode);
        if (!_machine.TryBeginHandoff(slotNode, out IDisposable? lease, out string reason))
        {
            throw new WorldHandoffUnavailableException(reason);
        }

        try
        {
            var names = new HashSet<string>(_slotNames(slotNode), StringComparer.Ordinal) { slotNode };
            foreach (TransferDecisionRecord record in await _journal.ListUnresolvedAsync(cancellationToken).ConfigureAwait(false))
            {
                if (names.Contains(record.SourceWorkerId.Value) || names.Contains(record.TargetWorkerId.Value))
                {
                    throw new WorldHandoffUnavailableException(
                        $"slot '{slotNode}' has an unresolved player transfer ({record.TransferId}, stage {record.CurrentStage}, {record.DecisionState}); it must be recovered first");
                }
            }

            return lease!;
        }
        catch
        {
            lease!.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One recovery pass over the journal (<see cref="TransferRecoveryService.RecoverUnresolvedAsync"/> on the rows this gateway owns), then one error-severity anomaly for every transfer still unresolved after
    /// <see cref="GatewayTransferOptions.AnomalyAfter"/> (the retries go on). A transfer that fails stays journalled and is tried again on the next pass. Must not run concurrently with itself.
    /// </summary>
    public async Task<TransferRecoverySummary> RecoverOnceAsync(CancellationToken cancellationToken)
    {
        // One journal scan per pass: the recovery reads it, the anomaly check below reuses it. A transfer that resolved during the pass is therefore still in it and is dropped from the
        // bookkeeping on the next pass; one that sat unresolved past AnomalyAfter and resolved just now is reported once, which is accurate (it was stuck that long).
        TransferRecoverySummary summary;
        IReadOnlyList<TransferDecisionRecord> open;
        _scan.BeginPass();
        try
        {
            summary = await _recovery.RecoverUnresolvedAsync(null, "gateway-recovery", cancellationToken).ConfigureAwait(false);
            open = await _scan.ListUnresolvedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _scan.EndPass();
        }

        DateTime now = DateTime.UtcNow;
        var ids = new HashSet<Guid>();
        foreach (TransferDecisionRecord record in open)
        {
            ids.Add(record.TransferId);
            if (!_firstSeen.TryGetValue(record.TransferId, out DateTime first))
            {
                _firstSeen[record.TransferId] = now;
            }
            else if (now - first >= _options.AnomalyAfter && _reported.Add(record.TransferId))
            {
                AnomalyLedger.Shared.Report(
                    "cluster", "transfer-recovery-stuck", record.TransferId.ToString(),
                    $"character={record.CharacterGuid} stage={record.CurrentStage} state={record.DecisionState} source={record.SourceWorkerId.Value} target={record.TargetWorkerId.Value}", AnomalySeverity.Error);
            }
        }

        foreach (Guid gone in _firstSeen.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _firstSeen.Remove(gone);
            _reported.Remove(gone);
        }

        return summary;
    }

    private async Task<PlayerTransferOutcome> RunAsync(PlayerTransferRequest request, IDisposable lease)
    {
        try
        {
            return await _host.TransferAsync(request, AfterStageAsync, _runs.Token).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(request.TransferId, out _);
            lease.Dispose();
        }
    }

    /// <summary>
    /// The coordinator's per-stage hook. After stage 4 (the last abortable stage) it asks the target again whether its incarnation is still the one the transfer reserved under; when it is not (the process restarted and lost
    /// the reservation and the shadow) the transfer is aborted here, and the coordinator ends it as an aborted outcome instead of committing to a worker that holds nothing. A check that cannot be answered throws, which aborts the transfer.
    /// </summary>
    private async Task AfterStageAsync(PlayerTransferStage stage, TransferDecisionRecord record, CancellationToken cancellationToken)
    {
        if (stage != PlayerTransferStage.StageWorldPort || _incarnationCheck is null)
        {
            return;
        }

        if (!await _incarnationCheck(record.TargetWorkerId, record.TransferId, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Transfer {TransferId}: target {Target} is not the process it reserved under; aborting before the commit", record.TransferId, record.TargetWorkerId.Value);
            await _coordinator.TryAbortTransferAsync(record.TransferId, TargetIncarnationChanged, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Abort reason recorded when the target's incarnation changed between the reserve and the commit.</summary>
    public const string TargetIncarnationChanged = "target-incarnation-changed";

    private async Task<string?> UnresolvedHandoffOfAsync(string[] workers, CancellationToken cancellationToken)
    {
        if (_handoffStore is null)
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string worker in workers)
        {
            names.Add(worker);
            names.UnionWith(_slotNames(worker));
        }

        foreach (WorldHandoffRecord handoff in await _handoffStore.GetUnresolvedAsync(cancellationToken).ConfigureAwait(false))
        {
            if (names.Contains(handoff.SourceWorkerId.Value) || names.Contains(handoff.TargetWorkerId.Value))
            {
                return $"a world handoff of the slot is unresolved ({handoff.HandoffId}, stage {handoff.Stage})";
            }
        }

        return null;
    }

    private static GatewayTransferSubmission Refused(PlayerTransferRequest request, ControlReasonCode code, string reason) => new(false, reason, request.TransferId, null, code);

    /// <summary>
    /// The transfer request a worker's submit stands for. <paramref name="sourceNode"/> is the slot node id of the link the submit arrived on (never a name the frame carries). A ship passenger keeps its
    /// transport (the retail <c>TELE_TO_NOT_LEAVE_TRANSPORT</c> option): the intent rides with the request instead of being lost at the gateway (design risk R7).
    /// </summary>
    internal static PlayerTransferRequest RequestOf(string sourceNode, ControlTransferSubmit body) => new(
        body.TransferId, body.CharacterGuid, body.AccountId, body.SessionGeneration, new WorkerNodeId(sourceNode),
        body.SourceMapId, body.SourceInstanceId, body.TargetMapId, body.TargetInstanceId, body.TargetPosition, body.SourceToken)
    {
        NotLeaveTransport = (body.Options & ControlTransferOptions.NotLeaveTransport) != 0,
    };

    /// <summary>
    /// The wire reason code of a placement failure string (<see cref="ClusterMapPlacementTable"/> <c>Failure*</c>, the coordinator's failure codes): the gateway maps its own strings here so a worker never
    /// parses them. A capacity limit is its own code (the retail "instance full" abort); an unknown string is <see cref="ControlReasonCode.NoEligibleWorker"/> when null, else <see cref="ControlReasonCode.Unspecified"/>.
    /// </summary>
    internal static ControlReasonCode CodeOfPlacementFailure(string? failure) => failure switch
    {
        null => ControlReasonCode.NoEligibleWorker,
        ClusterMapPlacementTable.FailureWorkerAtPlayerCapacity or ClusterMapPlacementTable.FailureWorkerAtInstanceCapacity => ControlReasonCode.Capacity,
        ClusterMapPlacementTable.FailureWorkerNotReady or ClusterMapPlacementTable.FailureWorkerDraining or ClusterMapPlacementTable.FailureWorkerUnknown => ControlReasonCode.TargetNotReady,
        ClusterMapPlacementTable.FailureNoEligibleWorker or ClusterMapPlacementTable.FailureMapUnassigned or ClusterMapPlacementTable.FailureInstanceUnbound
            or ClusterMapPlacementTable.FailureInvalidInstanceId => ControlReasonCode.NoEligibleWorker,
        _ => ControlReasonCode.Unspecified,
    };

    /// <summary>
    /// The journal rows <see cref="RecoverOnceAsync"/> may resolve: those between two of this gateway's slots that are not running here, do not belong to a world handoff (its own recovery finishes them) and whose slots are not
    /// handing off. Rows are read at call time; a transfer that starts later is in <c>_running</c> before its first row exists.
    /// </summary>
    private sealed class OwnedJournalScan(GatewayTransferService owner) : ITransferJournalScan
    {
        private IReadOnlyList<TransferDecisionRecord>? _pass;
        private bool _inPass;

        /// <summary>From now until <see cref="EndPass"/> the first scan is kept and answers every later call (one pass of <see cref="RecoverOnceAsync"/>, which never runs concurrently with itself).</summary>
        public void BeginPass()
        {
            _pass = null;
            _inPass = true;
        }

        public void EndPass()
        {
            _inPass = false;
            _pass = null;
        }

        public async Task<IReadOnlyList<TransferDecisionRecord>> ListUnresolvedAsync(CancellationToken cancellationToken = default)
        {
            if (_inPass && _pass is { } cached)
            {
                return cached;
            }

            IReadOnlyList<TransferDecisionRecord> scanned = await ScanAsync(cancellationToken).ConfigureAwait(false);
            if (_inPass)
            {
                _pass = scanned;
            }

            return scanned;
        }

        private async Task<IReadOnlyList<TransferDecisionRecord>> ScanAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<TransferDecisionRecord> all = await owner._journal.ListUnresolvedAsync(cancellationToken).ConfigureAwait(false);
            if (all.Count == 0)
            {
                return all;
            }

            HashSet<Guid>? handoffOwned = null;
            if (owner._handoffStore is { } handoffs)
            {
                handoffOwned = [];
                foreach (WorldHandoffRecord handoff in await handoffs.GetUnresolvedAsync(cancellationToken).ConfigureAwait(false))
                {
                    foreach (Guid id in await handoffs.GetTransferIdsAsync(handoff.HandoffId, cancellationToken).ConfigureAwait(false))
                    {
                        handoffOwned.Add(id);
                    }
                }
            }

            var owned = new List<TransferDecisionRecord>(all.Count);
            foreach (TransferDecisionRecord record in all)
            {
                string source = record.SourceWorkerId.Value;
                string target = record.TargetWorkerId.Value;
                if (!owner._slotSet.Contains(source) || !owner._slotSet.Contains(target)
                    || owner._running.ContainsKey(record.TransferId)
                    || handoffOwned?.Contains(record.TransferId) == true
                    || owner._machine.StateOf(source) == GatewaySlotState.HandingOff
                    || owner._machine.StateOf(target) == GatewaySlotState.HandingOff)
                {
                    continue;
                }

                owned.Add(record);
            }

            return owned;
        }
    }
}

/// <summary>
/// Drives <see cref="GatewayTransferService.RecoverOnceAsync"/>: at start and then every <see cref="GatewayTransferOptions.RecoveryInterval"/> (design section 6). A pass that finds transfers it could not resolve waits
/// longer (doubling up to 60 s) before the next one. The loop never faults and never cancels a roll-forward in progress except at host stop. Thread safety: one loop task.
/// </summary>
public sealed class GatewayTransferRecoveryService(
    GatewayTransferService transfers,
    GatewayTransferOptions options,
    ILogger<GatewayTransferRecoveryService>? logger = null) : IHostedService, IDisposable
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger = logger ?? NullLogger<GatewayTransferRecoveryService>.Instance;
    private readonly CancellationTokenSource _stopping = new();
    private readonly RecoveryWake _wake = new();
    private Task _loop = Task.CompletedTask;
    private int _wired;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _wired, 1) == 0)
        {
            transfers.LinkUp += OnLinkUp;
        }

        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Releases the stop token source and the wake signal; call it after <see cref="StopAsync"/>.</summary>
    public void Dispose()
    {
        transfers.LinkUp -= OnLinkUp;
        _wake.Dispose();
        _stopping.Dispose();
    }

    private void OnLinkUp() => _wake.Signal();

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        transfers.LinkUp -= OnLinkUp;
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("The gateway stopped before the transfer recovery loop ended");
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        int failedPasses = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait = options.RecoveryInterval;
            try
            {
                TransferRecoverySummary summary = await transfers.RecoverOnceAsync(stoppingToken).ConfigureAwait(false);
                failedPasses = summary.Failed > 0 ? failedPasses + 1 : 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failedPasses++;
                _logger.LogFault("gateway.transfer-recovery", ex, "A transfer recovery pass failed");
            }

            if (failedPasses > 0)
            {
                wait = TimeSpan.FromTicks(Math.Min(options.RecoveryInterval.Ticks * (1L << Math.Min(failedPasses, 6)), MaxBackoff.Ticks));
            }

            try
            {
                if (await _wake.WaitAsync(wait, stoppingToken).ConfigureAwait(false))
                {
                    failedPasses = 0; // a worker link came up: the pass that follows is not held back by the backoff of the failures before it
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

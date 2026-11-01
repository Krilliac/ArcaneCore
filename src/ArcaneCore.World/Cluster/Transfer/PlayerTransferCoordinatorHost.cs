using System.Collections.Concurrent;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.World.Cluster.Gateway;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Cluster.Transfer;

/// <summary>
/// The production <see cref="ITransferGatewayRouter"/> on a source worker: each call is a control frame (<see cref="GatewayTransferWire"/>) on the link
/// that carries the player's session (<see cref="GatewaySessionLinkHost.TrySendTransferControl"/>), and the call completes when the gateway's
/// <see cref="GatewayTransferAck"/> for that transfer and phase arrives. A refusal, a missing link or a missing ack within <see cref="AckTimeout"/> throws, so
/// the coordinator aborts (stages 1..4) or rolls forward again later (post-commit; the gateway treats a repeated commit as done).
/// Thread safety: any thread; one pending ack per (transfer, phase). Registers itself on <see cref="GatewaySessionLinkHost.TransferAckReceived"/>: one router per host.
/// </summary>
public sealed class LinkTransferGatewayRouter : ITransferGatewayRouter
{
    /// <summary>Default wait for the gateway's ack.</summary>
    public static readonly TimeSpan DefaultAckTimeout = TimeSpan.FromSeconds(15);

    private readonly GatewaySessionLinkHost _host;
    private readonly ConcurrentDictionary<(Guid TransferId, GatewayTransferPhase Phase), TaskCompletionSource<GatewayTransferAck>> _pending = new();

    public LinkTransferGatewayRouter(GatewaySessionLinkHost host, TimeSpan? ackTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        AckTimeout = ackTimeout ?? DefaultAckTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(AckTimeout, TimeSpan.Zero);
        host.TransferAckReceived = OnAck;
    }

    public TimeSpan AckTimeout { get; }

    public Task StageWorldPortAsync(
        Guid transferId,
        ulong characterGuid,
        long sessionGeneration,
        WorkerNodeId sourceWorkerId,
        WorkerNodeId targetWorkerId,
        uint targetMapId,
        uint targetInstanceId,
        TransferPosition targetPosition,
        CancellationToken cancellationToken = default) =>
        ExchangeAsync(
            new GatewayTransferStage(transferId, characterGuid, sessionGeneration, sourceWorkerId.Value, targetWorkerId.Value, targetMapId, targetInstanceId, targetPosition),
            cancellationToken);

    public Task CancelStagedWorldPortAsync(
        Guid transferId,
        ulong characterGuid,
        long sessionGeneration,
        WorkerNodeId restoredWorkerId,
        string abortReason,
        CancellationToken cancellationToken = default) =>
        ExchangeAsync(new GatewayTransferCancel(transferId, characterGuid, sessionGeneration, restoredWorkerId.Value, abortReason), cancellationToken);

    public Task CommitRouteSwitchAsync(
        Guid transferId,
        ulong characterGuid,
        long sessionGeneration,
        WorkerNodeId targetWorkerId,
        CharacterOwnershipToken targetOwnershipToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetOwnershipToken);
        return ExchangeAsync(
            new GatewayTransferCommit(transferId, characterGuid, sessionGeneration, targetWorkerId.Value, targetOwnershipToken.Scope, targetOwnershipToken.Epoch, targetOwnershipToken.Incarnation),
            cancellationToken);
    }

    private async Task ExchangeAsync(GatewayTransferControl control, CancellationToken cancellationToken)
    {
        var key = (control.TransferId, control.Phase);
        var pending = new TaskCompletionSource<GatewayTransferAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(key, pending))
        {
            throw new InvalidOperationException($"transfer {control.TransferId} already has a {control.Phase} frame in flight");
        }

        try
        {
            if (!_host.TrySendTransferControl(control.SessionGeneration, control))
            {
                throw new InvalidOperationException($"no gateway link carries session {control.SessionGeneration}; the {control.Phase} frame was not sent");
            }

            GatewayTransferAck ack;
            try
            {
                ack = await pending.Task.WaitAsync(AckTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException($"the gateway did not answer the {control.Phase} frame of transfer {control.TransferId} within {AckTimeout}");
            }

            if (!ack.Accepted)
            {
                throw new InvalidOperationException($"the gateway refused the {control.Phase} of transfer {control.TransferId}: {ack.Reason}");
            }
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    private void OnAck(GatewayTransferAck ack)
    {
        if (_pending.TryGetValue((ack.TransferId, ack.Phase), out TaskCompletionSource<GatewayTransferAck>? pending))
        {
            pending.TrySetResult(ack);
        }
    }
}

/// <summary>
/// Runs <see cref="PlayerTransferCoordinator"/> on the source worker (S5c): the source participant is the worker's own, the targets are reached through
/// the participants registered for them (the peer link), the gateway through <see cref="LinkTransferGatewayRouter"/> over the worker's gateway link and
/// every decision is journalled in the <see cref="ITransferDecisionStore"/> (production: <c>EfTransferDecisionStore</c>).
/// <para>
/// <see cref="TransferAsync"/> allows one transfer per character at a time. A failure before the commit is aborted (the source resumes, the gateway
/// resumes routing to it, the target drops its shadow); a failure after the commit is rolled forward once (<see cref="PlayerTransferCoordinator.RecoverTransferAsync"/>)
/// and otherwise left to <see cref="RecoverCharacterAsync"/>, which a supervisor calls for a character whose transfer is unresolved.
/// Thread safety: any thread; the in-flight set is guarded by a lock.
/// </para>
/// </summary>
public sealed class PlayerTransferCoordinatorHost
{
    public const string FailureTransferInFlight = "transfer-in-flight";
    public const string FailureTransferFaulted = "transfer-faulted";

    private readonly PlayerTransferCoordinator _coordinator;
    private readonly ITransferDecisionStore _store;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly HashSet<ulong> _inFlight = [];

    public PlayerTransferCoordinatorHost(PlayerTransferCoordinator coordinator, ITransferDecisionStore store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _coordinator = coordinator;
        _store = store;
        _logger = logger;
    }

    /// <summary>Builds the coordinator for this worker: <paramref name="source"/> is the local participant, <paramref name="targets"/> the participants of the other workers.</summary>
    public static PlayerTransferCoordinatorHost Create(
        IClusterMapRouter mapRouter,
        ITransferDecisionStore store,
        ITransferGatewayRouter gatewayRouter,
        ITransferSourceWorker source,
        IEnumerable<ITransferTargetWorker> targets,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targets);
        var coordinator = new PlayerTransferCoordinator(mapRouter, store, gatewayRouter, [source], targets);
        return new PlayerTransferCoordinatorHost(coordinator, store, logger);
    }

    /// <summary>Transfers one player. Never throws for a transfer failure: the outcome says whether it stands, was aborted or still needs recovery.</summary>
    public async Task<PlayerTransferOutcome> TransferAsync(PlayerTransferRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (!_inFlight.Add(request.CharacterGuid))
            {
                return Failed(PlayerTransferStage.None, PlayerTransferDecisionState.Aborted, FailureTransferInFlight, $"character {request.CharacterGuid} already has a transfer in flight", null);
            }
        }

        try
        {
            return await _coordinator.ExecuteTransferAsync(request, afterStageHook: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("cluster.transfer-host", ex, "Transfer {TransferId} of character {Character} failed", request.TransferId, request.CharacterGuid);
            return await SettleAsync(request, ex).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(request.CharacterGuid);
            }
        }
    }

    /// <summary>Resolves every unresolved transfer of <paramref name="characterGuid"/> (before the commit: abort; after: roll forward). Returns how many were resolved.</summary>
    public async Task<int> RecoverCharacterAsync(ulong characterGuid, string reason = "crash-recovery", CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TransferDecisionRecord> unresolved = await _store.GetUnresolvedForCharacterAsync(characterGuid, cancellationToken).ConfigureAwait(false);
        int resolved = 0;
        foreach (TransferDecisionRecord record in unresolved)
        {
            await _coordinator.RecoverTransferAsync(record.TransferId, reason, cancellationToken).ConfigureAwait(false);
            resolved++;
        }

        return resolved;
    }

    /// <summary>After a thrown failure: abort when the decision is not committed, otherwise roll forward once.</summary>
    private async Task<PlayerTransferOutcome> SettleAsync(PlayerTransferRequest request, Exception cause)
    {
        string message = cause.Message;
        TransferDecisionRecord? record = null;
        try
        {
            record = await _store.GetDecisionAsync(request.TransferId, CancellationToken.None).ConfigureAwait(false);
            if (record is { IsCommittedOrBeyond: false, DecisionState: PlayerTransferDecisionState.PreCommit })
            {
                await _coordinator.TryAbortTransferAsync(request.TransferId, FailureTransferFaulted, CancellationToken.None).ConfigureAwait(false);
            }
            else if (record is { DecisionState: PlayerTransferDecisionState.Committed })
            {
                record = await _coordinator.RecoverTransferAsync(request.TransferId, FailureTransferFaulted, CancellationToken.None).ConfigureAwait(false);
                if (record.DecisionState == PlayerTransferDecisionState.Completed)
                {
                    return new PlayerTransferOutcome(true, record.CurrentStage, record.DecisionState, null, null, record);
                }
            }

            record = await _store.GetDecisionAsync(request.TransferId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("cluster.transfer-host-settle", ex, "Settling transfer {TransferId} failed; it stays journalled for recovery", request.TransferId);
        }

        return Failed(record?.CurrentStage ?? PlayerTransferStage.None, record?.DecisionState ?? PlayerTransferDecisionState.PreCommit, FailureTransferFaulted, message, record);
    }

    private static PlayerTransferOutcome Failed(PlayerTransferStage stage, PlayerTransferDecisionState state, string code, string message, TransferDecisionRecord? record) =>
        new(false, stage, state, code, message, record);
}

using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Cluster.Transfer;

/// <summary>A world-port the gateway has staged (stage 4) and not yet switched or cancelled.</summary>
public sealed record StagedWorldPortRoute(Guid TransferId, ulong CharacterGuid, long SessionGeneration, WorkerNodeId SourceWorkerId);

/// <summary>Lists the staged world-ports a gateway still holds, so the startup sweep can drop the ones whose transfer is gone.</summary>
public interface IStagedRouteSource
{
    /// <summary>A snapshot of the staged routes; may be stale by the time it is read.</summary>
    IReadOnlyList<StagedWorldPortRoute> ListStagedRoutes();
}

/// <summary>What one startup sweep did. <see cref="Failed"/> counts resolutions that threw on every attempt; those transfers stay journalled for the next sweep.</summary>
public sealed record TransferRecoverySummary(int Scanned, int RolledForward, int RolledBack, int Failed, int StagedRoutesDropped)
{
    public static TransferRecoverySummary Empty { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>
/// Startup journal sweep for the cross-worker player transfer (S5e). On worker, gateway and coordinator start it reads every unresolved transfer
/// from the durable journal (<see cref="ITransferJournalScan"/>) and resolves each by its durable decision state through
/// <see cref="PlayerTransferCoordinator"/>:
/// <list type="bullet">
/// <item><description><see cref="PlayerTransferDecisionState.Committed"/>: roll forward (retire the source, activate the target, switch the gateway route; the ownership epoch is already the target's).</description></item>
/// <item><description><see cref="PlayerTransferDecisionState.PreCommit"/>: roll back (discard the shadow, resume the source, restore the gateway route; the source epoch stays).</description></item>
/// </list>
/// Any exception is an ambiguous answer (the call may or may not have taken effect, or the commit may have landed meanwhile), so the next attempt starts from a
/// fresh durable status lookup instead of from what the failed attempt assumed; every step the coordinator repeats is idempotent. The gateway sweep
/// (<see cref="SweepStagedRoutesAsync"/>) drops staged routes whose transfer is missing from the journal or already aborted.
/// <para>
/// Thread safety: stateless apart from its collaborators; safe from any thread, but run one sweep per process start (two concurrent sweeps only repeat idempotent steps).
/// </para>
/// </summary>
public sealed class TransferRecoveryService
{
    /// <summary>Abort reason recorded for a transfer rolled back by the startup sweep.</summary>
    public const string StartupReason = "startup-recovery";

    /// <summary>Reason handed to the gateway when it drops a staged route whose transfer is gone.</summary>
    public const string OrphanRouteReason = "staged-route-orphaned";

    private readonly PlayerTransferCoordinator _coordinator;
    private readonly ITransferDecisionStore _store;
    private readonly ITransferJournalScan _journal;
    private readonly ITransferGatewayRouter _gateway;
    private readonly ILogger _logger;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryDelay;

    public TransferRecoveryService(
        PlayerTransferCoordinator coordinator,
        ITransferDecisionStore store,
        ITransferJournalScan journal,
        ITransferGatewayRouter gateway,
        ILogger logger,
        int maxAttempts = 3,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        TimeSpan delay = retryDelay ?? TimeSpan.FromMilliseconds(500);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        _coordinator = coordinator;
        _store = store;
        _journal = journal;
        _gateway = gateway;
        _logger = logger;
        _maxAttempts = maxAttempts;
        _retryDelay = delay;
    }

    private enum Resolution
    {
        None,
        RolledForward,
        RolledBack,
    }

    /// <summary>
    /// The full startup sweep: resolves the unresolved transfers (<see cref="RecoverUnresolvedAsync"/>), then drops orphaned staged routes
    /// (<see cref="SweepStagedRoutesAsync"/>) when <paramref name="stagedRoutes"/> is given (gateway side).
    /// </summary>
    public async Task<TransferRecoverySummary> RunStartupSweepAsync(
        WorkerNodeId? localWorker = null,
        IStagedRouteSource? stagedRoutes = null,
        CancellationToken cancellationToken = default)
    {
        TransferRecoverySummary summary = await RecoverUnresolvedAsync(localWorker, StartupReason, cancellationToken).ConfigureAwait(false);
        if (stagedRoutes is null)
            return summary;

        (int dropped, int failed) = await SweepStagedRoutesCoreAsync(stagedRoutes, cancellationToken).ConfigureAwait(false);
        return summary with { StagedRoutesDropped = dropped, Failed = summary.Failed + failed };
    }

    /// <summary>
    /// Resolves every unresolved transfer in the journal. A worker passes its own id and only handles transfers it is the source or target of;
    /// the coordinator and gateway pass <see langword="null"/> and handle all of them. One transfer's failure never stops the sweep.
    /// </summary>
    public async Task<TransferRecoverySummary> RecoverUnresolvedAsync(
        WorkerNodeId? localWorker = null,
        string reason = StartupReason,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TransferDecisionRecord> unresolved = await _journal.ListUnresolvedAsync(cancellationToken).ConfigureAwait(false);

        int scanned = 0, forward = 0, back = 0, failed = 0;
        foreach (TransferDecisionRecord record in unresolved)
        {
            if (localWorker is { } local && record.SourceWorkerId != local && record.TargetWorkerId != local)
                continue;

            scanned++;
            try
            {
                switch (await ResolveAsync(record.TransferId, reason, cancellationToken).ConfigureAwait(false))
                {
                    case Resolution.RolledForward:
                        forward++;
                        break;
                    case Resolution.RolledBack:
                        back++;
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                failed++;
                _logger.LogFault("cluster.transfer-recovery", ex, "Recovery of transfer {TransferId} (character {Character}, stage {Stage}) failed on every attempt; it stays journalled", record.TransferId, record.CharacterGuid, record.CurrentStage);
            }
        }

        if (scanned > 0)
        {
            _logger.LogInformation("Transfer recovery: {Scanned} unresolved, {Forward} rolled forward, {Back} rolled back, {Failed} failed", scanned, forward, back, failed);
        }

        return new TransferRecoverySummary(scanned, forward, back, failed, 0);
    }

    /// <summary>
    /// Gateway start: drops every staged route whose transfer is no longer in the journal or was aborted, restoring routing to the route's source worker.
    /// A route whose transfer is still unresolved or completed is left alone (recovery or the commit owns it). Returns how many were dropped.
    /// </summary>
    public async Task<int> SweepStagedRoutesAsync(IStagedRouteSource stagedRoutes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagedRoutes);
        (int dropped, _) = await SweepStagedRoutesCoreAsync(stagedRoutes, cancellationToken).ConfigureAwait(false);
        return dropped;
    }

    private async Task<(int Dropped, int Failed)> SweepStagedRoutesCoreAsync(IStagedRouteSource stagedRoutes, CancellationToken cancellationToken)
    {
        int dropped = 0, failed = 0;
        foreach (StagedWorldPortRoute route in stagedRoutes.ListStagedRoutes())
        {
            try
            {
                // The durable status lookup decides: only a missing or aborted transfer makes the staged route an orphan.
                TransferDecisionRecord? record = await _store.GetDecisionAsync(route.TransferId, cancellationToken).ConfigureAwait(false);
                if (record is not null && record.DecisionState != PlayerTransferDecisionState.Aborted)
                    continue;

                await _gateway.CancelStagedWorldPortAsync(
                    route.TransferId, route.CharacterGuid, route.SessionGeneration, route.SourceWorkerId, OrphanRouteReason, cancellationToken).ConfigureAwait(false);
                dropped++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                failed++;
                _logger.LogFault("cluster.transfer-recovery-route", ex, "Dropping the staged route of transfer {TransferId} failed; it stays staged", route.TransferId);
            }
        }

        return (dropped, failed);
    }

    /// <summary>Resolves one transfer, re-reading the durable status before every attempt. Throws the last failure when every attempt failed.</summary>
    private async Task<Resolution> ResolveAsync(Guid transferId, string reason, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                TransferDecisionRecord? record = await _store.GetDecisionAsync(transferId, cancellationToken).ConfigureAwait(false);
                if (record is null)
                    return Resolution.None;

                switch (record.DecisionState)
                {
                    case PlayerTransferDecisionState.Completed:
                        return Resolution.None;

                    case PlayerTransferDecisionState.Committed:
                        await _coordinator.CompleteCommittedTransferAsync(transferId, cancellationToken).ConfigureAwait(false);
                        return Resolution.RolledForward;

                    default:
                        // PreCommit, or Aborted by an earlier attempt whose cleanup did not finish: the store abort is idempotent and the cleanup is repeated.
                        // If the commit landed since the lookup, the coordinator refuses and the next attempt's lookup rolls forward instead.
                        await _coordinator.AbortTransferAsync(transferId, reason, cancellationToken).ConfigureAwait(false);
                        return Resolution.RolledBack;
                }
            }
            catch (Exception ex) when (attempt < _maxAttempts && ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Recovery attempt {Attempt} of transfer {TransferId} failed; re-reading its durable status", attempt, transferId);
                if (_retryDelay > TimeSpan.Zero)
                    await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

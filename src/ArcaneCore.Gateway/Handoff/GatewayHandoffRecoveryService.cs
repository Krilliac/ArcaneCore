using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Gateway.Handoff;

/// <summary>
/// Finishes the gateway's interrupted whole-World handoffs from the journal (design _wf/handoff-control-DESIGN.md section 6): at start and then every
/// <see cref="GatewayHandoffOptions.RecoveryInterval"/> it reads the unresolved handoffs and hands each to <see cref="GatewayHandoffService.RecoverRecordAsync"/> (before the epoch takeover the
/// handoff aborts and the source resumes; after it, it rolls forward). A recovery that fails (a link not up yet, a worker refusing) backs off exponentially up to 60 s for that handoff
/// and is retried; one still unresolved after <see cref="GatewayHandoffOptions.AnomalyAfter"/> raises one error-severity anomaly (the retries go on). The loop never faults and never cancels
/// a roll-forward. Records of other slots or nodes are left to whoever owns them. Thread safety: one loop task; <see cref="RecoverOnceAsync"/> must not run concurrently with itself.
/// </summary>
public sealed class GatewayHandoffRecoveryService(
    GatewayHandoffService handoffs,
    IWorldHandoffStore store,
    GatewayHandoffOptions options,
    ILogger<GatewayHandoffRecoveryService>? logger = null) : IHostedService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger = logger ?? NullLogger<GatewayHandoffRecoveryService>.Instance;
    private readonly Dictionary<Guid, Attempts> _attempts = [];
    private readonly CancellationTokenSource _stopping = new();
    private Task _loop = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("The gateway stopped before the handoff recovery loop ended");
        }
    }

    /// <summary>One pass: reconcile the slots' roles, then try every unresolved handoff that is due. Returns how many were resolved in this pass.</summary>
    public async Task<int> RecoverOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await handoffs.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            _logger.LogFault("gateway.handoff-recovery", ex, "Reconciling the handoff slots failed");
        }

        IReadOnlyList<WorldHandoffRecord> unresolved;
        try
        {
            unresolved = await store.GetUnresolvedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            _logger.LogFault("gateway.handoff-recovery", ex, "Reading the unresolved world handoffs failed");
            return 0;
        }

        var open = new HashSet<Guid>();
        int resolved = 0;
        DateTime now = DateTime.UtcNow;
        foreach (WorldHandoffRecord record in unresolved)
        {
            open.Add(record.HandoffId);
            if (!_attempts.TryGetValue(record.HandoffId, out Attempts? state))
            {
                state = new Attempts(now);
                _attempts[record.HandoffId] = state;
            }

            if (now < state.NextAttempt)
            {
                continue;
            }

            try
            {
                WorldHandoffRecord? done = await handoffs.RecoverRecordAsync(record, cancellationToken).ConfigureAwait(false);
                if (done is null)
                {
                    continue; // not this gateway's, or running now
                }

                _logger.LogInformation("Recovered world handoff {Handoff}: now {Stage}", record.HandoffId, done.Stage);
                resolved++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                state.Failures++;
                TimeSpan wait = TimeSpan.FromTicks(Math.Min(options.RecoveryInterval.Ticks * (1L << Math.Min(state.Failures, 6)), MaxBackoff.Ticks));
                state.NextAttempt = DateTime.UtcNow + wait;
                _logger.LogWarning("Recovering world handoff {Handoff} failed ({Message}); retrying in {Wait}", record.HandoffId, ex.Message, wait);
                if (!state.Reported && DateTime.UtcNow - state.FirstSeen >= options.AnomalyAfter)
                {
                    state.Reported = true;
                    AnomalyLedger.Shared.Report(
                        "cluster", "handoff-recovery-stuck", record.HandoffId.ToString(),
                        $"stage={record.Stage} state={record.DecisionState} error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
                }
            }
        }

        foreach (Guid gone in _attempts.Keys.Where(id => !open.Contains(id)).ToArray())
        {
            _attempts.Remove(gone);
        }

        return resolved;
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(options.RecoveryInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogFault("gateway.handoff-recovery", ex, "A handoff recovery pass failed");
                try
                {
                    await Task.Delay(options.RecoveryInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private sealed class Attempts(DateTime firstSeen)
    {
        public DateTime FirstSeen { get; } = firstSeen;

        public DateTime NextAttempt { get; set; } = DateTime.MinValue;

        public int Failures { get; set; }

        public bool Reported { get; set; }
    }
}

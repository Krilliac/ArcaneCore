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
/// and is retried; one still unresolved after <see cref="GatewayHandoffOptions.AnomalyAfter"/> raises one error-severity anomaly (the retries go on), also when every pass skipped it because its slot
/// was busy. A pass also runs, with the backoff cleared, as soon as the worker link or the standby link of a slot comes up (design section 6). The loop never faults and never cancels
/// a roll-forward. Records of other slots or nodes are left to whoever owns them. Thread safety: one loop task; <see cref="RecoverOnceAsync"/> must not run concurrently with itself.
/// Dispose it after <see cref="StopAsync"/> (it owns the stop token source and the wake signal).
/// </summary>
public sealed class GatewayHandoffRecoveryService(
    GatewayHandoffService handoffs,
    IWorldHandoffStore store,
    GatewayHandoffOptions options,
    ILogger<GatewayHandoffRecoveryService>? logger = null) : IHostedService, IDisposable
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger = logger ?? NullLogger<GatewayHandoffRecoveryService>.Instance;
    private readonly Dictionary<Guid, Attempts> _attempts = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly RecoveryWake _wake = new();
    private Task _loop = Task.CompletedTask;
    private int _wired;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _wired, 1) == 0)
        {
            handoffs.LinkUp += OnLinkUp;
        }

        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        handoffs.LinkUp -= OnLinkUp;
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

    public void Dispose()
    {
        handoffs.LinkUp -= OnLinkUp;
        _wake.Dispose();
        _stopping.Dispose();
    }

    private void OnLinkUp(string slotNode) => _wake.Signal();

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
                ReportIfStuck(record, state, "waiting to retry");
                continue;
            }

            try
            {
                WorldHandoffRecord? done = await handoffs.RecoverRecordAsync(record, cancellationToken).ConfigureAwait(false);
                if (done is null)
                {
                    // Not this gateway's (left to its owner), or running now, or its slot is busy. A record that is ours and that no pass manages to take up still counts towards the anomaly.
                    if (handoffs.Owns(record) && !handoffs.IsRunning(record.HandoffId))
                    {
                        ReportIfStuck(record, state, "skipped, its slot is busy");
                    }

                    continue;
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
                ReportIfStuck(record, state, $"error={ex.GetType().Name}: {ex.Message}");
            }
        }

        foreach (Guid gone in _attempts.Keys.Where(id => !open.Contains(id)).ToArray())
        {
            _attempts.Remove(gone);
        }

        return resolved;
    }

    /// <summary>Raises the one error-severity anomaly of a handoff that has been unresolved for <see cref="GatewayHandoffOptions.AnomalyAfter"/>, whatever kept this pass from resolving it.</summary>
    private void ReportIfStuck(WorldHandoffRecord record, Attempts state, string detail)
    {
        if (!state.Reported && DateTime.UtcNow - state.FirstSeen >= options.AnomalyAfter)
        {
            state.Reported = true;
            AnomalyLedger.Shared.Report(
                "cluster", "handoff-recovery-stuck", record.HandoffId.ToString(),
                $"stage={record.Stage} state={record.DecisionState} {detail}", AnomalySeverity.Error);
        }
    }

    private void ClearBackoff()
    {
        foreach (Attempts state in _attempts.Values)
        {
            state.NextAttempt = DateTime.MinValue;
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverOnceAsync(stoppingToken).ConfigureAwait(false);
                if (await _wake.WaitAsync(options.RecoveryInterval, stoppingToken).ConfigureAwait(false))
                {
                    ClearBackoff(); // a link came up: what was waiting for it is retried now, not after its backoff
                }
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
                    await _wake.WaitAsync(options.RecoveryInterval, stoppingToken).ConfigureAwait(false);
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

/// <summary>
/// A wake-up for a recovery loop: link-up handlers <see cref="Signal"/> (never blocking, any number of times, coalesced into one), the loop <see cref="WaitAsync"/>s for it or for the interval.
/// Thread-safe. Disposing makes later signals no-ops.
/// </summary>
internal sealed class RecoveryWake : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending.
        }
        catch (ObjectDisposedException)
        {
            // The loop is gone.
        }
    }

    /// <summary>True when woken by <see cref="Signal"/>, false when <paramref name="interval"/> passed. Throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.</summary>
    public Task<bool> WaitAsync(TimeSpan interval, CancellationToken cancellationToken) => _signal.WaitAsync(interval, cancellationToken);

    public void Dispose() => _signal.Dispose();
}

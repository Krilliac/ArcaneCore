using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Cluster.Transfer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Control;

/// <summary>
/// The worker's self-heal (design _wf/handoff-control-DESIGN.md section 3.5, commit C11). A worker that lent its world to a gateway-driven operation can be left
/// holding it when the gateway dies: the draining source stays admission-closed or frozen (whole-World handoff), the source of a transfer keeps a suspended player out of
/// its map. The worker then reads the operation's journal row itself, every <see cref="DefaultInterval"/> while it holds something:
/// <list type="bullet">
/// <item><description>row Aborted: the operation ended before its point of no return, so the worker undoes its half locally (handoff: <see cref="IWorldHandoffSource.ResumeAsync"/>;
/// transfer: <see cref="ITransferSourceWorker.ResumeAfterAbortAsync"/>). Whoever aborted the row (the restarted gateway, a second gateway's recovery, an operator) is irrelevant;</description></item>
/// <item><description>row Committed or Completed: the point of no return passed, so the worker only rolls forward locally (handoff: <see cref="IWorldHandoffSource.RetireAsync"/>, which keeps the
/// world frozen and closed; transfer: <see cref="ITransferSourceWorker.RetireSourcePlayerAsync"/>). It never resumes a committed operation;</description></item>
/// <item><description>row PreCommit: nothing. A worker never aborts on its own while the journal says the operation may still commit, because the coordinator may still be alive
/// or about to be recovered and a worker-side resume would race its takeover.</description></item>
/// </list>
/// <para>
/// What the worker holds is learned by observing the control requests it executes: this service decorates the <see cref="IGatewayControlHandler"/> (the
/// <see cref="WorkerControlService"/>) and tracks an operation from the moment a handoff <see cref="ControlVerb.StopAdmissions"/> or <see cref="ControlVerb.Freeze"/> or a transfer
/// <see cref="ControlVerb.Suspend"/> reaches it, before the participant runs (so a verb whose reply never reached the gateway is still tracked), until an Ok
/// <see cref="ControlVerb.Resume"/>, <see cref="ControlVerb.Retire"/>, <see cref="ControlVerb.ResumeSource"/> or <see cref="ControlVerb.RetireSource"/> ends it, or until the self-heal did.
/// A verb the service refused (nothing ran) does not start tracking. The participants' resume and retire calls are idempotent, so a heal that races the gateway's own verb is harmless.
/// An operation whose originating verb is still running is never dropped after a heal, because that verb may still complete its freeze or suspend after the heal ran; the next pass repeats it.
/// </para>
/// <para>
/// The loop runs only while something is tracked (started by the first tracked verb, ends when the table is empty), so an idle worker, and every worker with both opt-in keys off, runs nothing.
/// It does not depend on the gateway link: the journal is read through the worker's own store. A journal read or a participant call that fails is logged and retried next pass; after
/// <see cref="MaxAttempts"/> failed passes the operation is dropped with an error anomaly for the operator.
/// </para>
/// Thread-safe: <see cref="HandleAsync"/> runs concurrently; passes are serialised.
/// </summary>
public sealed class WorkerSelfHealService : IGatewayControlHandler, IAsyncDisposable, IDisposable
{
    /// <summary>How often the journal is re-read while the worker holds an operation (design section 3.5: 2 s).</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    /// <summary>The shortest interval accepted: <see cref="PeriodicTimer"/> works in whole milliseconds and refuses less.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1);

    /// <summary>Failed passes after which an operation is dropped with an anomaly.</summary>
    public const int MaxAttempts = 30;

    private readonly IGatewayControlHandler _inner;
    private readonly WorkerControlHandoff? _handoff;
    private readonly WorkerControlTransfer? _transfers;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly Dictionary<(ControlKind Kind, Guid Operation), Held> _held = [];
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _healed;
    private bool _disposed;

    /// <summary>Wraps <paramref name="inner"/>; <paramref name="handoff"/> and <paramref name="transfers"/> are the participants and journals the worker was built with (null: that kind is never tracked).</summary>
    public WorkerSelfHealService(
        IGatewayControlHandler inner,
        WorkerControlHandoff? handoff,
        WorkerControlTransfer? transfers,
        TimeSpan? interval = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        TimeSpan every = interval ?? DefaultInterval;
        ArgumentOutOfRangeException.ThrowIfLessThan(every, MinInterval);
        _inner = inner;
        _handoff = handoff;
        _transfers = transfers;
        _interval = every;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Builds the self-heal over a <see cref="WorkerControlService"/> and its own participants.</summary>
    public static WorkerSelfHealService For(WorkerControlService control, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        return new WorkerSelfHealService(control, control.Handoff, control.Transfers, logger: logger);
    }

    /// <summary>The factory <c>AddWorkerControl</c> registers next to the control service; the endpoint installs the result as the link host's control handler.</summary>
    internal static WorkerSelfHealService Resolve(IServiceProvider services)
        => For(services.GetRequiredService<WorkerControlService>(), services.GetService<ILoggerFactory>()?.CreateLogger<WorkerSelfHealService>());

    /// <summary>How many operations the worker holds now (a test seam and a diagnostic).</summary>
    public int HeldCount
    {
        get { lock (_gate) return _held.Count; }
    }

    /// <summary>How many operations the self-heal has resumed or retired locally.</summary>
    public int Healed => Volatile.Read(ref _healed);

    /// <inheritdoc />
    public async Task<ControlReply> HandleAsync(ControlRequest request, GatewayControlContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!StartsHolding(request.Kind, request.Verb))
        {
            ControlReply plain = await _inner.HandleAsync(request, context, cancellationToken).ConfigureAwait(false);
            if (plain.Status == ControlStatus.Ok && EndsHolding(request.Kind, request.Verb))
            {
                lock (_gate)
                {
                    // A start verb of the same operation that is still running may take the world after this end verb: keep the entry, the next pass reads the journal.
                    if (_held.TryGetValue((request.Kind, request.OperationId), out Held? ending) && ending.Running == 0)
                    {
                        _held.Remove((request.Kind, request.OperationId));
                    }
                }
            }

            return plain;
        }

        (Held entry, bool created) = Hold(request.Kind, request.OperationId);
        ControlReply reply;
        try
        {
            reply = await _inner.HandleAsync(request, context, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The verb may have run before the link or the host stopped: keep what it may have taken.
            Release(request.Kind, request.OperationId, entry, drop: false);
            throw;
        }

        // Refused or Disabled: nothing ran. Drop the entry only when this request created it; an earlier verb of the operation may have taken something that still needs healing.
        bool nothingRan = reply.Status is ControlStatus.Refused or ControlStatus.Disabled;
        Release(request.Kind, request.OperationId, entry, drop: created && nothingRan);
        return reply;
    }

    /// <summary>
    /// One pass over everything held: reads each operation's journal row and heals it as described on the type. Returns how many operations are still held afterwards.
    /// A test seam; the loop calls it every interval.
    /// </summary>
    public async Task<int> RunPassAsync(CancellationToken cancellationToken)
    {
        await _pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            KeyValuePair<(ControlKind Kind, Guid Operation), Held>[] snapshot;
            lock (_gate)
            {
                snapshot = [.. _held];
            }

            foreach (KeyValuePair<(ControlKind Kind, Guid Operation), Held> item in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await HealOneAsync(item.Key.Kind, item.Key.Operation, item.Value, cancellationToken).ConfigureAwait(false);
            }

            return HeldCount;
        }
        finally
        {
            _pass.Release();
        }
    }

    private async Task HealOneAsync(ControlKind kind, Guid operation, Held entry, CancellationToken ct)
    {
        try
        {
            HealResult result = kind == ControlKind.WorldHandoff
                ? await HealHandoffAsync(operation, ct).ConfigureAwait(false)
                : await HealTransferAsync(operation, ct).ConfigureAwait(false);
            entry.Attempts = 0;
            if (result == HealResult.Waiting)
            {
                return;
            }

            if (result == HealResult.Healed)
            {
                Interlocked.Increment(ref _healed);
            }

            lock (_gate)
            {
                // A freeze or suspend that is still running may take the world after this heal: keep the entry and heal again next pass.
                if (entry.Running == 0)
                {
                    RemoveIfSame(kind, operation, entry);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            entry.Attempts++;
            _logger.LogFault("cluster.self-heal", ex, "Self-heal of {Kind} operation {Operation} failed (attempt {Attempt})", kind, operation, entry.Attempts);
            if (entry.Attempts >= MaxAttempts)
            {
                bool dropped;
                lock (_gate)
                {
                    // Not while a start verb of the operation still runs: it may yet take the world, so the entry stays and the next pass tries again.
                    dropped = entry.Running == 0;
                    if (dropped)
                    {
                        RemoveIfSame(kind, operation, entry);
                    }
                }

                if (!dropped)
                {
                    return;
                }

                AnomalyLedger.Shared.Report(
                    "cluster", "self-heal-failed", operation.ToString("D"),
                    $"kind={kind} attempts={MaxAttempts} error={ex.GetType().Name}: {ex.Message}; this worker may still be frozen or hold a suspended player", AnomalySeverity.Error);
            }
        }
    }

    private async Task<HealResult> HealHandoffAsync(Guid operation, CancellationToken ct)
    {
        if (_handoff is not { Source: { } source } handoff)
        {
            return HealResult.Gone;
        }

        WorldHandoffRecord? record = await handoff.Journal.GetAsync(operation, ct).ConfigureAwait(false);
        if (record is null)
        {
            return HealResult.Gone;
        }

        if (!WorkerNodeNames.Equal(record.SourceWorkerId, source.WorkerId))
        {
            _logger.LogWarning("Self-heal: handoff {Operation} names source {Source} in the journal, this worker is {Worker}; not healing it", operation, record.SourceWorkerId.Value, source.WorkerId.Value);
            return HealResult.Gone;
        }

        switch (record.DecisionState)
        {
            case PlayerTransferDecisionState.Aborted:
                _logger.LogWarning("Self-heal: handoff {Operation} is aborted in the journal ({Reason}); resuming this world", operation, record.AbortReason);
                await source.ResumeAsync(ct).ConfigureAwait(false);
                return HealResult.Healed;
            case PlayerTransferDecisionState.Committed or PlayerTransferDecisionState.Completed:
                _logger.LogWarning("Self-heal: handoff {Operation} is {State} in the journal; retiring this world", operation, record.DecisionState);
                await source.RetireAsync(ct).ConfigureAwait(false);
                return HealResult.Healed;
            default:
                return HealResult.Waiting;
        }
    }

    private async Task<HealResult> HealTransferAsync(Guid operation, CancellationToken ct)
    {
        if (_transfers is not { Source: { } source } transfers)
        {
            return HealResult.Gone;
        }

        TransferDecisionRecord? record = await transfers.Journal.GetDecisionAsync(operation, ct).ConfigureAwait(false);
        if (record is null)
        {
            return HealResult.Gone;
        }

        if (!WorkerNodeNames.Equal(record.SourceWorkerId, source.WorkerId))
        {
            _logger.LogWarning("Self-heal: transfer {Operation} names source {Source} in the journal, this worker is {Worker}; not healing it", operation, record.SourceWorkerId.Value, source.WorkerId.Value);
            return HealResult.Gone;
        }

        switch (record.DecisionState)
        {
            case PlayerTransferDecisionState.Aborted:
                _logger.LogWarning("Self-heal: transfer {Operation} is aborted in the journal ({Reason}); resuming character {Character}", operation, record.AbortReason, record.CharacterGuid);
                await source.ResumeAfterAbortAsync(
                    operation, record.CharacterGuid, record.SourceOwnershipToken, record.AbortReason ?? "self-heal: the journal aborted the transfer", ct).ConfigureAwait(false);
                return HealResult.Healed;
            case PlayerTransferDecisionState.Committed or PlayerTransferDecisionState.Completed:
                _logger.LogWarning("Self-heal: transfer {Operation} is {State} in the journal; retiring character {Character} here", operation, record.DecisionState, record.CharacterGuid);
                await source.RetireSourcePlayerAsync(operation, record.CharacterGuid, record.SourceOwnershipToken, ct).ConfigureAwait(false);
                return HealResult.Healed;
            default:
                return HealResult.Waiting;
        }
    }

    private static bool StartsHolding(ControlKind kind, ControlVerb verb) =>
        (kind == ControlKind.WorldHandoff && verb is ControlVerb.StopAdmissions or ControlVerb.Freeze)
        || (kind == ControlKind.PlayerTransfer && verb == ControlVerb.Suspend);

    private static bool EndsHolding(ControlKind kind, ControlVerb verb) =>
        (kind == ControlKind.WorldHandoff && verb is ControlVerb.Resume or ControlVerb.Retire)
        || (kind == ControlKind.PlayerTransfer && verb is ControlVerb.ResumeSource or ControlVerb.RetireSource);

    private (Held Entry, bool Created) Hold(ControlKind kind, Guid operation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = (kind, operation);
            bool created = !_held.TryGetValue(key, out Held? entry);
            if (entry is null)
            {
                entry = new Held();
                _held[key] = entry;
            }

            entry.Running++;
            EnsureLoop();
            return (entry, created);
        }
    }

    private void Release(ControlKind kind, Guid operation, Held entry, bool drop)
    {
        lock (_gate)
        {
            entry.Running--;
            if (drop && entry.Running == 0)
            {
                RemoveIfSame(kind, operation, entry);
            }
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void RemoveIfSame(ControlKind kind, Guid operation, Held entry)
    {
        if (_held.TryGetValue((kind, operation), out Held? current) && ReferenceEquals(current, entry))
        {
            _held.Remove((kind, operation));
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>. Starts the loop when none runs; the loop ends, under the same gate, when the table is empty.</summary>
    private void EnsureLoop()
    {
        if (_loop is null)
        {
            CancellationToken token = _stop.Token;
            _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        bool cleanExit = false;
        try
        {
            using var timer = new PeriodicTimer(_interval, _time);
            while (true)
            {
                lock (_gate)
                {
                    if (_held.Count == 0)
                    {
                        _loop = null;
                        cleanExit = true;
                        return;
                    }
                }

                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    return;
                }

                await RunPassAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("cluster.self-heal", ex, "The worker self-heal loop stopped unexpectedly");
        }
        finally
        {
            if (!cleanExit)
            {
                lock (_gate)
                {
                    _loop = null;
                }
            }
        }
    }

    /// <summary>Stops the loop without waiting for it (use <see cref="DisposeAsync"/> to wait). What the worker holds stays as it is (the process is ending).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _stop.Cancel();
    }

    /// <summary>Stops the loop. What the worker holds stays as it is (the process is ending).</summary>
    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            loop = _loop;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>What one heal found: the row is still PreCommit (<see cref="Waiting"/>), it ended the operation and the local step ran (<see cref="Healed"/>), or there is nothing to heal (<see cref="Gone"/>: no row, or no participant).</summary>
    private enum HealResult
    {
        Waiting,
        Healed,
        Gone,
    }

    /// <summary>One held operation. <see cref="Running"/> counts the verbs of it still executing (guarded by the service's gate); <see cref="Attempts"/> is only touched by the serialised pass.</summary>
    private sealed class Held
    {
        public int Running { get; set; }

        public int Attempts { get; set; }
    }
}

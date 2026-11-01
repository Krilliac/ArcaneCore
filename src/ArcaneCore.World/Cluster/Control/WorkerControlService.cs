using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Cluster.Transfer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Control;

/// <summary>
/// Opt-in key of the worker's whole-World handoff control (<c>Cluster:Worker:Handoff:Enabled</c>). Off, the default: the worker answers every handoff
/// <see cref="ControlRequest"/> with <see cref="ControlStatus.Disabled"/> and registers nothing for it. Design _wf/handoff-control-DESIGN.md section 2.6.
/// </summary>
public sealed class WorkerHandoffControlOptions
{
    public const string SectionName = "Cluster:Worker:Handoff";

    /// <summary>`true` lets an admitted gateway drive this worker's side of a whole-World handoff over the gateway link (Describe, Warm, StopAdmissions, Freeze, Flush, Resume, ReleaseWriterLock, Retire, Promote; needs Cluster:Worker:Enabled). `false`, the default, answers every handoff request Disabled.</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Opt-in key of the worker's per-player transfer control (<c>Cluster:Worker:Transfers:Enabled</c>). Off, the default: the worker answers every transfer
/// <see cref="ControlRequest"/> with <see cref="ControlStatus.Disabled"/> and builds no transfer participant. Design _wf/handoff-control-DESIGN.md section 2.6.
/// </summary>
public sealed class WorkerTransferControlOptions
{
    public const string SectionName = "Cluster:Worker:Transfers";

    /// <summary>`true` builds this worker's production transfer source and target and lets an admitted gateway drive them over the gateway link (TargetDescribe, Reserve, Install, Activate, Discard, Suspend, ResumeSource, RetireSource; needs Cluster:Worker:Enabled). `false`, the default, answers every transfer request Disabled.</summary>
    public bool Enabled { get; set; }
}

/// <summary>What <see cref="ControlVerb.Describe"/> reports about this worker; read when the request arrives, so a promotion or a drain shows at once.</summary>
public sealed record WorkerControlIdentity(
    WorkerNodeId WorkerId,
    bool IsStandby,
    Guid Incarnation,
    string OwnerNode,
    string OwnershipScope,
    IReadOnlyList<uint> OwnedMaps);

/// <summary>The handoff side of a worker: its journal and its participants. <paramref name="Source"/> is null on a standby, <paramref name="Target"/> on an active worker.</summary>
public sealed record WorkerControlHandoff(IWorldHandoffStore Journal, IWorldHandoffSource? Source, IWorldHandoffTarget? Target);

/// <summary>
/// The transfer side of a worker: its journal (the decision store, readable by every worker) and its participants, plus the target's capacity for
/// <see cref="ControlVerb.TargetDescribe"/>.
/// </summary>
public sealed record WorkerControlTransfer(
    ITransferDecisionStore Journal,
    ITransferSourceWorker? Source,
    ITransferTargetWorker? Target,
    Func<bool> AdmissionOpen,
    Func<int> FreeReservations);

/// <summary>
/// The worker end of the unified control channel (design _wf/handoff-control-DESIGN.md sections 2 and 3): executes the gateway's <see cref="ControlRequest"/>s
/// against this worker's local handoff and transfer participants. The journal is the source of truth: before a mutating verb runs, the worker reads the
/// operation's journal row and refuses (<see cref="ControlStatus.Refused"/>, nothing changed) unless the row's decision state, stage and named workers allow
/// the verb (section 2.5 table). On top of that the worker remembers, bounded:
/// <list type="bullet">
/// <item><description>the successful reply per (operation, verb) of the verbs whose repeat must not run twice (Freeze, Suspend and the other single-shot verbs; a repeat, or one that
/// arrives while the first still runs, gets the same reply and the participant is called once). A refused or failed verb is not remembered, so an abort verb can be retried;</description></item>
/// <item><description>tombstones: an operation whose Resume, Discard or ResumeSource ran refuses every later verb that would start or continue it (a resumed handoff refuses a later Freeze, a
/// discarded transfer refuses a later Install), even if the journal row was not written yet. Install and the describe verbs are never remembered: the target's install is
/// idempotent per transfer id and a repeat must join the load that runs (section 4.4).</description></item>
/// </list>
/// A kind whose opt-in key is off answers <see cref="ControlStatus.Disabled"/> before anything is read. A participant that throws answers
/// <see cref="ControlStatus.Failed"/>. Thread-safe: any number of requests run at once; the participants and the journal are themselves safe for it.
/// </summary>
public sealed class WorkerControlService(
    bool handoffEnabled,
    WorkerControlHandoff? handoff,
    bool transfersEnabled,
    WorkerControlTransfer? transfers,
    Func<WorkerControlIdentity> identity,
    ILogger? logger = null) : IGatewayControlHandler
{
    /// <summary>How many operations' replies and tombstones are remembered. A suspend reply holds up to 16 KiB, so the bound also bounds memory.</summary>
    private const int Remembered = 512;

    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid Operation, ControlVerb Verb), Task<ControlReply>> _results = [];
    private readonly Queue<(Guid Operation, ControlVerb Verb)> _resultOrder = new();
    private readonly HashSet<(Guid Operation, ControlKind Kind)> _ended = [];
    private readonly Queue<(Guid Operation, ControlKind Kind)> _endedOrder = new();
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>Whether the worker's handoff key is on.</summary>
    public bool HandoffEnabled => handoffEnabled;

    /// <summary>Whether the worker's transfer key is on.</summary>
    public bool TransfersEnabled => transfersEnabled;

    /// <inheritdoc />
    public async Task<ControlReply> HandleAsync(ControlRequest request, GatewayControlContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool handoffKind = request.Kind == ControlKind.WorldHandoff;
        if (!(handoffKind ? handoffEnabled : transfersEnabled))
        {
            return Reply(request, ControlStatus.Disabled,
                handoffKind ? "whole-World handoff control is off on this worker (Cluster:Worker:Handoff:Enabled)"
                            : "player transfer control is off on this worker (Cluster:Worker:Transfers:Enabled)");
        }

        try
        {
            return handoffKind
                ? await HandoffAsync(request, context, cancellationToken).ConfigureAwait(false)
                : await TransferAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            // A journal read that failed, an argument of the wrong type: the verb did not run.
            _logger.LogFault("cluster.control", ex, "Control request {Verb} of operation {Operation} failed before it ran", request.Verb, request.OperationId);
            return Reply(request, ControlStatus.Failed, ex.Message);
        }
    }

    // ------------------------------------------------------------------ handoff (kind 1)

    private async Task<ControlReply> HandoffAsync(ControlRequest request, GatewayControlContext context, CancellationToken ct)
    {
        if (handoff is not { } h)
        {
            return Reply(request, ControlStatus.Refused, "this worker has no handoff journal");
        }

        if (request.Verb == ControlVerb.Describe)
        {
            return DescribeHandoff(request, h, context);
        }

        if (request.Verb != ControlVerb.Resume && IsEnded(request))
        {
            return Reply(request, ControlStatus.Refused, "the handoff was resumed on this worker; it cannot start again");
        }

        return await RunOnceAsync(request, () => RunHandoffVerbAsync(request, h, ct)).ConfigureAwait(false);
    }

    private ControlReply DescribeHandoff(ControlRequest request, WorkerControlHandoff h, GatewayControlContext context)
    {
        WorkerControlIdentity who = identity();
        CharacterOwnershipToken? held = null;
        if (!who.IsStandby && h.Source is { } source)
        {
            try
            {
                held = source.Ownership;
            }
            catch (InvalidOperationException)
            {
                // The source world holds no characters ownership (unfenced single node): nothing to report.
            }
        }

        return Reply(request, ControlStatus.Ok, string.Empty, new ControlDescribePayload(
            who.WorkerId.Value, who.IsStandby, who.Incarnation, who.OwnerNode, who.OwnershipScope, who.OwnedMaps, held, context.AdmittedGateways, HandoffEnabled));
    }

    private async Task<ControlReply> RunHandoffVerbAsync(ControlRequest request, WorkerControlHandoff h, CancellationToken ct)
    {
        WorldHandoffRecord? record = await h.Journal.GetAsync(request.OperationId, ct).ConfigureAwait(false);
        if (record is null)
        {
            return Reply(request, ControlStatus.Refused, $"the journal has no handoff {request.OperationId}");
        }

        string? refusal;
        switch (request.Verb)
        {
            case ControlVerb.Warm:
            {
                var args = (ControlWarmArguments)request.Arguments;
                refusal = RowNamesTarget(record, h.Target) ?? PreCommitAt(record, WorldHandoffStage.Begun);
                if (refusal is null && args.ExpectedIncarnation != h.Target!.Incarnation)
                {
                    refusal = $"this standby's incarnation is {h.Target.Incarnation}, the coordinator expected {args.ExpectedIncarnation}";
                }

                if (refusal is not null)
                {
                    return Reply(request, ControlStatus.Refused, refusal);
                }

                await h.Target!.WarmAsync(ct).ConfigureAwait(false);
                return Ok(request);
            }

            case ControlVerb.StopAdmissions:
                refusal = RowNamesSource(record, h.Source) ?? PreCommitAt(record, WorldHandoffStage.Warm);
                if (refusal is not null)
                {
                    return Reply(request, ControlStatus.Refused, refusal);
                }

                await h.Source!.StopAdmissionsAsync(ct).ConfigureAwait(false);
                return Ok(request);
            case ControlVerb.Freeze:
                refusal = RowNamesSource(record, h.Source) ?? PreCommitAt(record, WorldHandoffStage.AdmissionsStopped);
                if (refusal is not null)
                {
                    return Reply(request, ControlStatus.Refused, refusal);
                }

                await h.Source!.FreezeAsync(ct).ConfigureAwait(false);
                return Ok(request);
            case ControlVerb.Flush:
                refusal = RowNamesSource(record, h.Source) ?? PreCommitAt(record, WorldHandoffStage.Frozen);
                if (refusal is not null)
                {
                    return Reply(request, ControlStatus.Refused, refusal);
                }

                await h.Source!.FlushAsync(ct).ConfigureAwait(false);
                return Ok(request);
            case ControlVerb.Resume:
                refusal = RowNamesSource(record, h.Source)
                    ?? (record.DecisionState is PlayerTransferDecisionState.PreCommit or PlayerTransferDecisionState.Aborted
                        ? null
                        : $"the handoff is {record.DecisionState}; a committed handoff only rolls forward");
                if (refusal is not null)
                {
                    return Reply(request, ControlStatus.Refused, refusal);
                }

                MarkEnded(request);
                await h.Source!.ResumeAsync(ct).ConfigureAwait(false);
                return Ok(request);
            case ControlVerb.ReleaseWriterLock:
                refusal = RowNamesSource(record, h.Source) ?? Committed(record);
                if (refusal is not null)
                {
                    return Reply(request, ControlStatus.Refused, refusal);
                }

                await h.Source!.ReleaseWriterLockAsync(ct).ConfigureAwait(false);
                return Ok(request);
            case ControlVerb.Retire:
                refusal = RowNamesSource(record, h.Source) ?? Committed(record);
                if (refusal is not null)
                {
                    return Reply(request, ControlStatus.Refused, refusal);
                }

                await h.Source!.RetireAsync(ct).ConfigureAwait(false);
                return Ok(request);
            case ControlVerb.Promote:
            {
                CharacterOwnershipToken token = ((ControlTokenArguments)request.Arguments).Token;
                refusal = RowNamesTarget(record, h.Target) ?? Committed(record);
                if (refusal is null && record.TargetOwnershipToken != token)
                {
                    refusal = "the promotion token is not the target token the journal committed";
                }

                if (refusal is not null)
                {
                    return Reply(request, ControlStatus.Refused, refusal);
                }

                await h.Target!.PromoteAsync(token, ct).ConfigureAwait(false);
                return Ok(request);
            }

            default:
                return Reply(request, ControlStatus.Refused, $"{request.Verb} is not a handoff verb a worker runs");
        }
    }

    private static string? PreCommitAt(WorldHandoffRecord record, WorldHandoffStage stage) =>
        record.DecisionState != PlayerTransferDecisionState.PreCommit ? $"the handoff is {record.DecisionState}, not PreCommit"
        : record.Stage != stage ? $"the handoff is at {record.Stage}; this verb needs {stage}"
        : null;

    private static string? Committed(WorldHandoffRecord record) =>
        record.DecisionState == PlayerTransferDecisionState.Committed ? null : $"the handoff is {record.DecisionState}, not Committed";

    private static string? RowNamesSource(WorldHandoffRecord record, IWorldHandoffSource? source) =>
        source is null ? "this worker is not the draining source of a handoff"
        : !WorkerNodeNames.Equal(record.SourceWorkerId, source.WorkerId) ? $"the journal row names source {record.SourceWorkerId.Value}, this worker is {source.WorkerId.Value}"
        : null;

    private static string? RowNamesTarget(WorldHandoffRecord record, IWorldHandoffTarget? target) =>
        target is null ? "this worker is not the standby target of a handoff"
        : !WorkerNodeNames.Equal(record.TargetWorkerId, target.WorkerId) ? $"the journal row names target {record.TargetWorkerId.Value}, this worker is {target.WorkerId.Value}"
        : null;

    // ------------------------------------------------------------------ transfer (kind 2)

    private async Task<ControlReply> TransferAsync(ControlRequest request, CancellationToken ct)
    {
        if (transfers is not { } t)
        {
            return Reply(request, ControlStatus.Refused, "this worker has no transfer journal");
        }

        if (request.Verb == ControlVerb.TargetDescribe)
        {
            if (t.Target is not { } target)
            {
                return Reply(request, ControlStatus.Refused, "this worker has no transfer target");
            }

            return Reply(request, ControlStatus.Ok, string.Empty,
                new ControlTargetDescribePayload(target.Incarnation, target.OwnershipScope ?? string.Empty, t.AdmissionOpen(), Math.Max(0, t.FreeReservations())));
        }

        if (request.Verb is not (ControlVerb.Discard or ControlVerb.ResumeSource) && IsEnded(request))
        {
            return Reply(request, ControlStatus.Refused, "the transfer was discarded or resumed on this worker; it cannot start again");
        }

        // Install is never remembered: the target's install is idempotent per transfer id, and the post-commit Install must join the load the first one started.
        return request.Verb == ControlVerb.Install
            ? await RunTransferVerbAsync(request, t, ct).ConfigureAwait(false)
            : await RunOnceAsync(request, () => RunTransferVerbAsync(request, t, ct)).ConfigureAwait(false);
    }

    private async Task<ControlReply> RunTransferVerbAsync(ControlRequest request, WorkerControlTransfer t, CancellationToken ct)
    {
        Guid id = request.OperationId;
        TransferDecisionRecord? record = await t.Journal.GetDecisionAsync(id, ct).ConfigureAwait(false);
        if (record is null)
        {
            return Reply(request, ControlStatus.Refused, $"the journal has no transfer {id}");
        }

        string? refusal;
        switch (request.Verb)
        {
            case ControlVerb.Reserve:
            {
                var args = (ControlReserveArguments)request.Arguments;
                refusal = RowNamesTarget(record, t.Target) ?? PreCommitAt(record, PlayerTransferStage.Reserve) ?? SameCharacter(record, args.CharacterGuid);
                if (refusal is not null)
                {
                    return Reply(request, ControlStatus.Refused, refusal);
                }

                bool reserved = await t.Target!.TryReserveSlotAsync(id, args.CharacterGuid, args.MapId, args.InstanceId, ct).ConfigureAwait(false);
                return Reply(request, ControlStatus.Ok, string.Empty, new ControlReservePayload(reserved, args.InstanceId));
            }

            case ControlVerb.Suspend:
            {
                var args = (ControlSuspendArguments)request.Arguments;
                refusal = RowNamesSource(record, t.Source) ?? PreCommitAt(record, PlayerTransferStage.Reserve) ?? SameCharacter(record, args.CharacterGuid);
                if (refusal is null && !string.Equals(args.TargetWorker, record.TargetWorkerId.Value, StringComparison.Ordinal))
                {
                    refusal = $"the journal row names target {record.TargetWorkerId.Value}, the request names {args.TargetWorker}";
                }

                if (refusal is not null)
                {
                    return Reply(request, ControlStatus.Refused, refusal);
                }

                PlayerTransferBundle bundle = await t.Source!.SuspendAndCaptureAsync(
                    id, args.CharacterGuid, record.TargetWorkerId, args.MapId, args.InstanceId, args.Position, ct).ConfigureAwait(false);
                byte[] json = PlayerTransferBundleJson.SerializeToUtf8(bundle);
                if (json.Length > ClusterControlWire.MaxBundleBytes)
                {
                    // The player is suspended; the coordinator aborts on the failure and ResumeSource brings it back.
                    return Reply(request, ControlStatus.Failed, $"the captured bundle is {json.Length} B, over the {ClusterControlWire.MaxBundleBytes} B the control channel carries");
                }

                return Reply(request, ControlStatus.Ok, string.Empty, new ControlSuspendPayload(json));
            }

            case ControlVerb.Install:
                refusal = RowNamesTarget(record, t.Target)
                    ?? (record.DecisionState == PlayerTransferDecisionState.Committed
                        || (record.DecisionState == PlayerTransferDecisionState.PreCommit
                            && record.CurrentStage is PlayerTransferStage.SuspendAndCapture or PlayerTransferStage.InstallPassive or PlayerTransferStage.StageWorldPort)
                        ? null
                        : $"the transfer is {record.DecisionState} at {record.CurrentStage}; Install needs a captured bundle before the commit or a committed transfer");
                if (refusal is null && record.CapturedBundle is null)
                {
                    refusal = "the journal row holds no captured bundle";
                }

                if (refusal is not null)
                {
                    return Reply(request, ControlStatus.Refused, refusal);
                }

                await t.Target!.InstallPassiveShadowAsync(id, record.CapturedBundle!, ct).ConfigureAwait(false);
                return Ok(request);
            case ControlVerb.Activate:
            {
                CharacterOwnershipToken token = ((ControlTokenArguments)request.Arguments).Token;
                refusal = RowNamesTarget(record, t.Target) ?? CommittedTransfer(record);
                if (refusal is null && record.TargetOwnershipToken != token)
                {
                    refusal = "the activation token is not the target token the journal committed";
                }

                if (refusal is null && record.CapturedBundle is null)
                {
                    refusal = "the journal row holds no captured bundle";
                }

                if (refusal is not null)
                {
                    return Reply(request, ControlStatus.Refused, refusal);
                }

                await t.Target!.ActivateTargetPlayerAsync(id, record.CapturedBundle!, token, ct).ConfigureAwait(false);
                return Ok(request);
            }

            case ControlVerb.Discard:
            {
                string reason = ((ControlReasonArguments)request.Arguments).Reason;
                refusal = RowNamesTarget(record, t.Target) ?? NotCommitted(record);
                if (refusal is not null)
                {
                    return Reply(request, ControlStatus.Refused, refusal);
                }

                MarkEnded(request);
                await t.Target!.DiscardPassiveShadowAsync(id, record.CharacterGuid, reason, ct).ConfigureAwait(false);
                return Ok(request);
            }

            case ControlVerb.ResumeSource:
            {
                string reason = ((ControlReasonArguments)request.Arguments).Reason;
                refusal = RowNamesSource(record, t.Source) ?? NotCommitted(record);
                if (refusal is not null)
                {
                    return Reply(request, ControlStatus.Refused, refusal);
                }

                MarkEnded(request);
                await t.Source!.ResumeAfterAbortAsync(id, record.CharacterGuid, record.SourceOwnershipToken, reason, ct).ConfigureAwait(false);
                return Ok(request);
            }

            case ControlVerb.RetireSource:
            {
                CharacterOwnershipToken token = ((ControlTokenArguments)request.Arguments).Token;
                refusal = RowNamesSource(record, t.Source) ?? CommittedTransfer(record);
                if (refusal is null && record.SourceOwnershipToken != token)
                {
                    refusal = "the superseded token is not the source token the journal holds";
                }

                if (refusal is not null)
                {
                    return Reply(request, ControlStatus.Refused, refusal);
                }

                await t.Source!.RetireSourcePlayerAsync(id, record.CharacterGuid, token, ct).ConfigureAwait(false);
                return Ok(request);
            }

            default:
                return Reply(request, ControlStatus.Refused, $"{request.Verb} is not a transfer verb a worker runs");
        }
    }

    private static string? PreCommitAt(TransferDecisionRecord record, PlayerTransferStage stage) =>
        record.DecisionState != PlayerTransferDecisionState.PreCommit ? $"the transfer is {record.DecisionState}, not PreCommit"
        : record.CurrentStage != stage ? $"the transfer is at {record.CurrentStage}; this verb needs {stage}"
        : null;

    private static string? CommittedTransfer(TransferDecisionRecord record) =>
        record.DecisionState == PlayerTransferDecisionState.Committed ? null : $"the transfer is {record.DecisionState}, not Committed";

    private static string? NotCommitted(TransferDecisionRecord record) =>
        record.DecisionState is PlayerTransferDecisionState.PreCommit or PlayerTransferDecisionState.Aborted
            ? null
            : $"the transfer is {record.DecisionState}; a committed transfer only rolls forward";

    private static string? SameCharacter(TransferDecisionRecord record, ulong characterGuid) =>
        record.CharacterGuid == characterGuid ? null : $"the journal row is for character {record.CharacterGuid}, the request names {characterGuid}";

    private static string? RowNamesSource(TransferDecisionRecord record, ITransferSourceWorker? source) =>
        source is null ? "this worker has no transfer source"
        : !WorkerNodeNames.Equal(record.SourceWorkerId, source.WorkerId) ? $"the journal row names source {record.SourceWorkerId.Value}, this worker is {source.WorkerId.Value}"
        : null;

    private static string? RowNamesTarget(TransferDecisionRecord record, ITransferTargetWorker? target) =>
        target is null ? "this worker has no transfer target"
        : !WorkerNodeNames.Equal(record.TargetWorkerId, target.WorkerId) ? $"the journal row names target {record.TargetWorkerId.Value}, this worker is {target.WorkerId.Value}"
        : null;

    // ------------------------------------------------------------------ memory: single-shot replies and tombstones

    /// <summary>
    /// Runs <paramref name="run"/> once per (operation, verb): a concurrent or later repeat gets the same reply (with its own request id) and the participant is not called
    /// again. Only an Ok reply is kept; a refused or failed one is forgotten so the verb can be asked again. A participant exception becomes a Failed reply.
    /// </summary>
    private async Task<ControlReply> RunOnceAsync(ControlRequest request, Func<Task<ControlReply>> run)
    {
        var key = (request.OperationId, request.Verb);
        var completion = new TaskCompletionSource<ControlReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ControlReply>? running;
        lock (_gate)
        {
            if (!_results.TryGetValue(key, out running))
            {
                _results[key] = completion.Task;
                _resultOrder.Enqueue(key);
                while (_resultOrder.Count > Remembered)
                {
                    _results.Remove(_resultOrder.Dequeue());
                }
            }
        }

        if (running is not null)
        {
            ControlReply first = await running.ConfigureAwait(false);
            return first with { RequestId = request.RequestId };
        }

        ControlReply reply;
        try
        {
            reply = await run().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Forget(key);
            completion.TrySetResult(Reply(request, ControlStatus.Failed, "the worker is stopping"));
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("cluster.control", ex, "Control verb {Verb} of operation {Operation} failed", request.Verb, request.OperationId);
            reply = Reply(request, ControlStatus.Failed, ex.Message);
        }

        if (reply.Status != ControlStatus.Ok)
        {
            Forget(key);
        }

        completion.TrySetResult(reply);
        return reply;
    }

    private void Forget((Guid Operation, ControlVerb Verb) key)
    {
        lock (_gate)
        {
            _results.Remove(key);
        }
    }

    private bool IsEnded(ControlRequest request)
    {
        lock (_gate)
        {
            return _ended.Contains((request.OperationId, request.Kind));
        }
    }

    private void MarkEnded(ControlRequest request)
    {
        var key = (request.OperationId, request.Kind);
        lock (_gate)
        {
            if (_ended.Add(key))
            {
                _endedOrder.Enqueue(key);
                while (_endedOrder.Count > Remembered)
                {
                    _ended.Remove(_endedOrder.Dequeue());
                }
            }
        }
    }

    // ------------------------------------------------------------------ replies

    private static ControlReply Ok(ControlRequest request) => Reply(request, ControlStatus.Ok, string.Empty);

    private static ControlReply Reply(ControlRequest request, ControlStatus status, string reason, ControlPayload? payload = null) =>
        new(request.RequestId, request.Kind, request.OperationId, request.Verb, status, reason, payload ?? ControlNoPayload.Instance);
}

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

/// <summary>The handoff side of a worker: its journal and its participants. <paramref name="Source"/> is null on a standby until it is promoted (then the control service attaches the draining source), <paramref name="Target"/> on an active worker.</summary>
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

/// <summary>What a promoted standby attaches to its <see cref="WorkerControlService"/>: the handoff side with the draining source it now has, and the transfer side it now builds (null: that side stays as it was).</summary>
public sealed record WorkerPromotedParticipants(WorkerControlHandoff? Handoff, WorkerControlTransfer? Transfers);

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
    ILogger? logger = null,
    Func<WorkerPromotedParticipants>? onPromoted = null) : IGatewayControlHandler
{
    /// <summary>How many operations' replies and tombstones are remembered. A suspend reply holds up to 16 KiB, so the bound also bounds memory.</summary>
    private const int Remembered = 512;

    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid Operation, ControlVerb Verb), ResultEntry> _results = [];
    private readonly LinkedList<(Guid Operation, ControlVerb Verb)> _resultOrder = new();
    private int _joinedRepeats;
    private readonly HashSet<(Guid Operation, ControlKind Kind)> _ended = [];
    private readonly Queue<(Guid Operation, ControlKind Kind)> _endedOrder = new();
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    // A standby has no draining handoff source and no transfer participants (it is the handoff's target and takes no players). Promotion makes it an active worker, so the participants
    // are built then, once, by onPromoted and swapped in under _attachGate; null once attached (or for a worker that was never a standby).
    private readonly Lock _attachGate = new();
    private Func<WorkerPromotedParticipants>? _onPromoted = onPromoted;
    private WorkerControlHandoff? _handoff = handoff;
    private WorkerControlTransfer? _transfers = transfers;

    /// <summary>Whether the worker's handoff key is on.</summary>
    public bool HandoffEnabled => handoffEnabled;

    /// <summary>Whether the worker's transfer key is on.</summary>
    public bool TransfersEnabled => transfersEnabled;

    /// <summary>The handoff journal and participants this service runs against (null: none); read by <see cref="WorkerSelfHealService"/>.</summary>
    public WorkerControlHandoff? Handoff => Current().Handoff;

    /// <summary>The transfer journal and participants this service runs against (null: none); read by <see cref="WorkerSelfHealService"/>.</summary>
    public WorkerControlTransfer? Transfers => Current().Transfers;

    /// <summary>
    /// The participants now: those the service was built with, until the worker is no longer a standby; then, once, those <c>onPromoted</c> builds. A build that throws attaches nothing and is
    /// retried by the next call (the request that hit it is answered Failed). Thread-safe: callers on any thread see either the standby's participants or the promoted ones, never a mix.
    /// </summary>
    private (WorkerControlHandoff? Handoff, WorkerControlTransfer? Transfers) Current()
    {
        if (Volatile.Read(ref _onPromoted) is { } build && !identity().IsStandby)
        {
            lock (_attachGate)
            {
                if (_onPromoted is not null)
                {
                    WorkerPromotedParticipants promoted = build();
                    _handoff = promoted.Handoff ?? _handoff;
                    _transfers = promoted.Transfers ?? _transfers;
                    Volatile.Write(ref _onPromoted, null);
                }
            }
        }

        lock (_attachGate)
            return (_handoff, _transfers);
    }

    /// <summary>How many repeats of a single-shot verb joined an identical request that was still running or already answered (instead of calling the participant again). A test seam and a diagnostic.</summary>
    public int JoinedRepeats => Volatile.Read(ref _joinedRepeats);

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
                : await TransferAsync(request, context, cancellationToken).ConfigureAwait(false);
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
        if (Current().Handoff is not { } h)
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
            case ControlVerb.RetireWrites:
                refusal = RowNamesSource(record, h.Source) ?? Committed(record);
                if (refusal is not null)
                {
                    return Reply(request, ControlStatus.Refused, refusal);
                }

                await h.Source!.RetireWritesAsync(ct).ConfigureAwait(false);
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

    private async Task<ControlReply> TransferAsync(ControlRequest request, GatewayControlContext context, CancellationToken ct)
    {
        if (Current().Transfers is not { } t)
        {
            return Reply(request, ControlStatus.Refused, "this worker has no transfer journal");
        }

        if (request.Verb == ControlVerb.TargetDescribe)
        {
            if (t.Target is not { } target)
            {
                // A source-only worker still answers: the gateway asks every involved worker how many gateways it admits (decision Q1). It reports no incarnation, no open admission and
                // no free reservations, so it can never be chosen as a target (Reserve on it is refused by RowNamesTarget).
                return Reply(request, ControlStatus.Ok, string.Empty,
                    new ControlTargetDescribePayload(Guid.Empty, string.Empty, false, 0, context.AdmittedGateways));
            }

            return Reply(request, ControlStatus.Ok, string.Empty,
                new ControlTargetDescribePayload(target.Incarnation, target.OwnershipScope ?? string.Empty, t.AdmissionOpen(), Math.Max(0, t.FreeReservations()), context.AdmittedGateways));
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

                // Q8: the reply carries the instance the target bound (the requested id unless it allocated one for an instance-0 request).
                TransferSlotReservation reservation = await t.Target!.ReserveSlotAsync(id, args.CharacterGuid, args.MapId, args.InstanceId, ct).ConfigureAwait(false);
                return Reply(request, ControlStatus.Ok, string.Empty, new ControlReservePayload(reservation.Reserved, reservation.InstanceId));
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
        ResultEntry? existing;
        ResultEntry? own = null;
        lock (_gate)
        {
            if (!_results.TryGetValue(key, out existing))
            {
                own = new ResultEntry(completion.Task, _resultOrder.AddLast(key));
                _results[key] = own;
                EvictFinished();
            }
        }

        if (existing is not null)
        {
            Interlocked.Increment(ref _joinedRepeats);
            ControlReply first = await existing.Result.ConfigureAwait(false);
            return first with { RequestId = request.RequestId };
        }

        ControlReply reply;
        try
        {
            reply = await run().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(key, own!, keep: false);
            completion.TrySetResult(Reply(request, ControlStatus.Failed, "the worker is stopping"));
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("cluster.control", ex, "Control verb {Verb} of operation {Operation} failed", request.Verb, request.OperationId);
            reply = Reply(request, ControlStatus.Failed, ex.Message);
        }

        // Only an Ok reply is kept; a refused or failed one is forgotten so the verb can be asked again.
        Finish(key, own!, keep: reply.Status == ControlStatus.Ok);
        completion.TrySetResult(reply);
        return reply;
    }

    /// <summary>
    /// Marks the entry finished and, when it is not kept, removes it from the table and the order list together (so a verb asked again is queued once). Evicts the oldest
    /// finished entries beyond <see cref="Remembered"/>; an entry whose verb still runs is never evicted, so a repeat arriving later can still join it.
    /// </summary>
    private void Finish((Guid Operation, ControlVerb Verb) key, ResultEntry entry, bool keep)
    {
        lock (_gate)
        {
            entry.Done = true;
            if (!keep && _results.TryGetValue(key, out ResultEntry? current) && ReferenceEquals(current, entry))
            {
                _results.Remove(key);
                _resultOrder.Remove(entry.Node);
            }

            EvictFinished();
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>. Drops the oldest finished entries while the table is over its bound; running entries are skipped.</summary>
    private void EvictFinished()
    {
        LinkedListNode<(Guid Operation, ControlVerb Verb)>? node = _resultOrder.First;
        while (_results.Count > Remembered && node is not null)
        {
            LinkedListNode<(Guid Operation, ControlVerb Verb)>? next = node.Next;
            if (_results[node.Value].Done)
            {
                _results.Remove(node.Value);
                _resultOrder.Remove(node);
            }

            node = next;
        }
    }

    /// <summary>One remembered (operation, verb): the shared reply task, its place in the age order, and whether the verb has finished. <see cref="Done"/> is read and written under the service's gate.</summary>
    private sealed class ResultEntry(Task<ControlReply> task, LinkedListNode<(Guid Operation, ControlVerb Verb)> node)
    {
        public Task<ControlReply> Result { get; } = task;

        public LinkedListNode<(Guid Operation, ControlVerb Verb)> Node { get; } = node;

        public bool Done { get; set; }
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

// Ported from Krilliac/Sonder-Inference src/scheduler/include/sonder/inference/scheduler/types.hpp @1c4c152 (Apache-2.0)
// Ported from Krilliac/Sonder-Inference src/scheduler/src/types.cpp @1c4c152 (Apache-2.0)
//
// Core value types for the in-process bot chat inference scheduler. The scheduler is pure
// policy: it decides which sequences prefill/decode in each engine step. It never executes a
// backend and never inspects prompt text; all intent arrives as explicit metadata.
//
// Units used throughout (C# has no typedefs): request ids are ulong, token counts are uint,
// block counts are ulong and times are long microseconds since an arbitrary epoch.

namespace ArcaneCore.World.Playerbots.Chat.Inference.Scheduling;

/// <summary>
/// ArcaneCore bot chat workload classes, ordered from most to least latency-sensitive. The
/// numeric value is the default priority rank (lower = more urgent); a request may override it.
/// </summary>
public enum WorkloadClass : byte
{
    /// <summary>A real player addressed the bot (whisper, say, party). Highest priority.</summary>
    InteractivePlayer = 0,

    /// <summary>A bot answering another bot.</summary>
    BotToBot = 1,

    /// <summary>Channel and needs chatter. Lowest priority; droppable when queued too long.</summary>
    Ambient = 2,
}

/// <summary>Request lifecycle.</summary>
public enum RequestState : byte
{
    Created,
    WaitingAdmission,
    Admitted,
    Prefill,
    Decode,
    Completed,
    Preempted,
    Cancelled,
    Failed,
    Rejected,

    /// <summary>Droppable work (Ambient by default) that waited past its queue deadline.</summary>
    Dropped,
}

public enum SchedulePhase : byte
{
    Prefill,
    Decode,
}

/// <summary>How a preempted sequence will be resumed.</summary>
public enum PreemptionMode : byte
{
    /// <summary>Drop KV, re-prefill prompt plus generated tokens later.</summary>
    Recompute,

    /// <summary>Move KV to a lower tier and swap back in on resume (decision stub).</summary>
    Swap,
}

public enum PreemptionReason : byte
{
    /// <summary>A running decode needed a block and none were free.</summary>
    KvPressure,

    /// <summary>An urgent waiting request displaced a lower class.</summary>
    PriorityAdmission,
}

/// <summary>Why a request ended in a non-completed terminal state.</summary>
public enum FailureReason : byte
{
    None,

    /// <summary>Bounded requeue count exceeded (no-progress guard).</summary>
    RequeueLimit,

    /// <summary>Prompt plus generation can never fit the KV capacity.</summary>
    NeverFits,

    /// <summary>An identical sequence is already active.</summary>
    DuplicateSequence,

    DuplicateId,
    InvalidRequest,

    /// <summary>Droppable request waited longer than its queue deadline.</summary>
    QueueTimeout,
}

/// <summary>Explicit metadata for one generation request.</summary>
public sealed class RequestSpec
{
    public ulong Id { get; set; }

    public WorkloadClass Workload { get; set; } = WorkloadClass.BotToBot;

    /// <summary>Opaque bot/conversation identifier (never interpreted, telemetry only).</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>Optional override of the class-derived priority rank (lower = urgent).</summary>
    public int? PriorityOverride { get; set; }

    public uint PromptTokens { get; set; }

    public uint MaxNewTokens { get; set; }

    public bool Cancellable { get; set; } = true;

    /// <summary>
    /// Whether the request may be dropped while still queued past
    /// <see cref="SchedulerConfig.DroppableQueueTimeoutUs"/>. Null means "drop Ambient only".
    /// Once admitted a request is never dropped (its work would be wasted).
    /// </summary>
    public bool? Droppable { get; set; }

    /// <summary>
    /// Optional identity of the exact sequence (model + prompt hash, etc.). When non-empty, a
    /// second active request with the same fingerprint is refused unless
    /// <see cref="AllowDuplicate"/> is set.
    /// </summary>
    public string SequenceFingerprint { get; set; } = string.Empty;

    public bool AllowDuplicate { get; set; }

    /// <summary>
    /// Optional host admission check, run after a successful KV reservation. Must not block or
    /// throw; returning false releases the reservation and leaves the request queued.
    /// </summary>
    public Func<bool>? TryAdmit { get; set; }

    /// <summary>Optional non-reserving readiness probe (e.g. prompt still being tokenized).</summary>
    public Func<bool>? AdmissionReady { get; set; }

    /// <summary>Host FIFO order in strict admission mode when preparation finishes out of order.</summary>
    public ulong? AdmissionOrder { get; set; }

    public bool IsDroppable => Droppable ?? Workload == WorkloadClass.Ambient;

    public RequestSpec Clone() => (RequestSpec)MemberwiseClone();
}

public static class SchedulerNames
{
    public const int WorkloadClassCount = 3;

    public static int IndexOf(WorkloadClass c) => (int)c;

    public static string ToName(this WorkloadClass c) => c switch
    {
        WorkloadClass.InteractivePlayer => "interactive_player",
        WorkloadClass.BotToBot => "bot_to_bot",
        WorkloadClass.Ambient => "ambient",
        _ => "unknown",
    };

    public static string ToName(this RequestState s) => s switch
    {
        RequestState.Created => "created",
        RequestState.WaitingAdmission => "waiting_admission",
        RequestState.Admitted => "admitted",
        RequestState.Prefill => "prefill",
        RequestState.Decode => "decode",
        RequestState.Completed => "completed",
        RequestState.Preempted => "preempted",
        RequestState.Cancelled => "cancelled",
        RequestState.Failed => "failed",
        RequestState.Rejected => "rejected",
        RequestState.Dropped => "dropped",
        _ => "unknown",
    };

    public static bool IsTerminal(this RequestState s) =>
        s is RequestState.Completed or RequestState.Cancelled or RequestState.Failed
            or RequestState.Rejected or RequestState.Dropped;

    public static string ToName(this PreemptionMode m) => m == PreemptionMode.Swap ? "swap" : "recompute";

    public static string ToName(this PreemptionReason r) =>
        r == PreemptionReason.KvPressure ? "kv_pressure" : "priority_admission";

    public static string ToName(this FailureReason r) => r switch
    {
        FailureReason.None => "none",
        FailureReason.RequeueLimit => "requeue_limit",
        FailureReason.NeverFits => "never_fits",
        FailureReason.DuplicateSequence => "duplicate_sequence",
        FailureReason.DuplicateId => "duplicate_id",
        FailureReason.InvalidRequest => "invalid_request",
        FailureReason.QueueTimeout => "queue_timeout",
        _ => "unknown",
    };
}

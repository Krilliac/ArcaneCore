// Ported from Krilliac/Sonder-Inference src/scheduler/include/sonder/inference/scheduler/config.hpp @1c4c152 (Apache-2.0)
//
// Tunable scheduling policy. Defaults are conservative starting points, not benchmarked values.

namespace ArcaneCore.World.Playerbots.Chat.Inference.Scheduling;

public sealed class SchedulerConfig
{
    /// <summary>
    /// Strict admission: rank by explicit class and FIFO within class. Aging, lookahead bypass,
    /// starvation promotion and priority preemption are disabled in this mode.
    /// </summary>
    public bool StrictPriorityAdmission { get; set; }

    // ---- Per-step budgets (continuous batching) ----

    /// <summary>Maximum tokens (prefill + decode) processed in one engine step.</summary>
    public uint MaxStepTokens { get; set; } = 2048;

    /// <summary>Maximum sequences participating in one engine step.</summary>
    public uint MaxStepSequences { get; set; } = 64;

    /// <summary>Maximum sequences resident (admitted, holding KV) at once.</summary>
    public uint MaxRunningSequences { get; set; } = 128;

    // ---- Chunked prefill (Sarathi-style) ----

    /// <summary>Largest prefill chunk for a single sequence in one step.</summary>
    public uint PrefillChunkTokens { get; set; } = 512;

    /// <summary>
    /// Upper bound on total prefill tokens in one step. Together with the decode-first ordering
    /// this reserves capacity for decode (TTFT vs time-between-tokens).
    /// </summary>
    public uint MaxStepPrefillTokens { get; set; } = 1024;

    // ---- Admission control ----

    /// <summary>
    /// Blocks kept free at admission as headroom for decode growth, so running sequences are not
    /// immediately preempted (no out-of-memory as control flow).
    /// </summary>
    public ulong AdmissionWatermarkBlocks { get; set; } = 4;

    /// <summary>
    /// How many waiting requests past a blocked head may be considered for admission in one step
    /// (bounded head-of-line bypass). Bypass is disabled while the blocked head is starving or
    /// top urgency.
    /// </summary>
    public uint AdmissionLookahead { get; set; } = 8;

    /// <summary>Allow urgent waiting requests to preempt lower-class running ones.</summary>
    public bool EnablePriorityPreemption { get; set; } = true;

    /// <summary>
    /// Minimum rank gap (victim rank - candidate rank) for priority preemption. With the default
    /// ranks (InteractivePlayer 0, BotToBot 1, Ambient 2) only a player turn can displace ambient
    /// chatter.
    /// </summary>
    public int PriorityPreemptionMinRankGap { get; set; } = 2;

    // ---- Fairness / starvation ----

    /// <summary>Every interval of queue wait improves the effective rank by 1.</summary>
    public long AgingIntervalUs { get; set; } = 2_000_000;

    /// <summary>
    /// A waiting request older than this is treated as top priority and cannot be bypassed by
    /// lookahead admission.
    /// </summary>
    public long StarvationThresholdUs { get; set; } = 10_000_000;

    /// <summary>
    /// ArcaneCore addition: droppable requests (Ambient by default) still queued, never admitted,
    /// this long after arrival are dropped instead of promoted. Stale chatter is worthless.
    /// Zero disables dropping.
    /// </summary>
    public long DroppableQueueTimeoutUs { get; set; } = 5_000_000;

    // ---- Preemption / no-progress guards ----

    /// <summary>A request preempted more than this many times fails (RequeueLimit).</summary>
    public uint MaxRequeueCount { get; set; } = 8;

    /// <summary>
    /// Swap (instead of recompute) when enabled and the victim holds at least
    /// <see cref="SwapMinTokens"/> of KV. A decision stub: the engine performs the movement.
    /// </summary>
    public bool EnableSwap { get; set; }

    public uint SwapMinTokens { get; set; } = 1024;

    // ---- Statistics ----

    /// <summary>
    /// Inter-token gaps retained per workload class (the most recent ones; a ring). Bounds memory
    /// in a long-running engine.
    /// </summary>
    public uint LatencySampleWindow { get; set; } = 4096;

    public SchedulerConfig Clone() => (SchedulerConfig)MemberwiseClone();
}

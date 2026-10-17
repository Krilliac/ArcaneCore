// Ported from Krilliac/Sonder-Inference src/scheduler/include/sonder/inference/scheduler/stats.hpp @1c4c152 (Apache-2.0)
// Ported from Krilliac/Sonder-Inference src/scheduler/src/stats.cpp @1c4c152 (Apache-2.0)
//
// Scheduler telemetry: queue time, TTFT, inter-token latency, batch occupancy, preemptions and
// starvation.

namespace ArcaneCore.World.Playerbots.Chat.Inference.Scheduling;

/// <summary>Latency summary over a set of samples (microseconds).</summary>
public readonly record struct LatencySummary(
    ulong Count, long Min, long Max, double Mean, long P50, long P95, long P99)
{
    /// <summary>Summarise <paramref name="samples"/>; the span is sorted in place.</summary>
    public static LatencySummary From(Span<long> samples)
    {
        if (samples.IsEmpty)
        {
            return default;
        }

        samples.Sort();
        decimal sum = 0;
        foreach (long s in samples)
        {
            sum += s;
        }

        return new LatencySummary(
            (ulong)samples.Length,
            samples[0],
            samples[^1],
            (double)(sum / samples.Length),
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            Percentile(samples, 0.99));
    }

    /// <summary>Summarise a copy of <paramref name="samples"/> (the list is not reordered).</summary>
    public static LatencySummary From(IReadOnlyList<long> samples)
    {
        if (samples.Count == 0)
        {
            return default;
        }

        long[] copy = new long[samples.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = samples[i];
        }

        return From(copy.AsSpan());
    }

    // Nearest-rank percentile on a sorted, non-empty span.
    private static long Percentile(ReadOnlySpan<long> sorted, double p)
    {
        int n = sorted.Length;
        int rank = (int)Math.Ceiling(p * n);
        rank = Math.Clamp(rank, 1, n);
        return sorted[rank - 1];
    }
}

/// <summary>Lifecycle timestamps for a single request (kept after completion until forgotten).</summary>
public struct RequestTimeline
{
    public ulong Id;
    public WorkloadClass Workload;
    public RequestState FinalState;
    public FailureReason Failure;
    public long Arrival;
    public long FirstAdmitted;
    public long FirstToken;
    public long Finished;
    public uint GeneratedTokens;
    public uint Preemptions;
    public long MaxInterTokenGap;

    /// <summary>Includes requeue waits after preemption.</summary>
    public long TotalQueueTime;

    public RequestTimeline()
    {
        FirstAdmitted = -1;
        FirstToken = -1;
        Finished = -1;
    }

    public readonly long Ttft => FirstToken < 0 ? -1 : FirstToken - Arrival;
}

public struct ClassStats
{
    public ulong Submitted;
    public ulong Completed;
    public ulong Cancelled;
    public ulong Failed;
    public ulong Rejected;
    public ulong Dropped;
    public ulong Preemptions;
    public ulong GeneratedTokens;
    public LatencySummary QueueTime;
    public LatencySummary Ttft;
    public LatencySummary InterToken;
    public LatencySummary EndToEnd;
}

/// <summary>
/// Aggregated snapshot. Cheap counters are maintained live; latency summaries are computed on
/// demand by <see cref="InferenceScheduler.Stats"/>.
/// </summary>
public sealed class SchedulerStats
{
    public long Now;
    public ulong Steps;
    public ulong EmptySteps;
    public ulong PrefillTokens;
    public ulong DecodeTokens;

    /// <summary>Sum over steps of sequences scheduled.</summary>
    public ulong SequencesScheduled;

    /// <summary>Sum of MaxStepTokens over non-empty steps.</summary>
    public ulong BudgetTokens;

    public ulong PreemptionsKvPressure;
    public ulong PreemptionsPriority;
    public ulong PreemptionsRecompute;
    public ulong PreemptionsSwap;

    /// <summary>KV discarded by recompute preemption.</summary>
    public ulong RecomputedTokens;

    /// <summary>KV moved out by swap preemption.</summary>
    public ulong SwappedTokens;

    public ulong StarvationPromotions;

    /// <summary>Requests admitted past a blocked head.</summary>
    public ulong LookaheadAdmissions;

    /// <summary>Steps where the queue head was KV-blocked.</summary>
    public ulong AdmissionKvBlocked;

    /// <summary>Droppable requests dropped after waiting past their deadline.</summary>
    public ulong QueueTimeoutDrops;

    /// <summary>Admitted and holding KV.</summary>
    public int ActiveSequences;

    /// <summary>Waiting (including preempted).</summary>
    public int QueuedSequences;

    /// <summary>Oldest queue age ever observed.</summary>
    public long MaxStarvationAge;

    public long CurrentOldestQueueAge;

    public ClassStats[] PerClass = new ClassStats[SchedulerNames.WorkloadClassCount];

    public ref ClassStats For(WorkloadClass c) => ref PerClass[(int)c];

    public double BatchTokenOccupancy =>
        BudgetTokens == 0 ? 0.0 : (double)(PrefillTokens + DecodeTokens) / BudgetTokens;

    public double PrefillShare
    {
        get
        {
            ulong total = PrefillTokens + DecodeTokens;
            return total == 0 ? 0.0 : (double)PrefillTokens / total;
        }
    }

    public double MeanBatchSequences
    {
        get
        {
            ulong busy = Steps - EmptySteps;
            return busy == 0 ? 0.0 : (double)SequencesScheduled / busy;
        }
    }

    /// <summary>Deep copy (the per-class array is not shared).</summary>
    public SchedulerStats Clone()
    {
        var copy = (SchedulerStats)MemberwiseClone();
        copy.PerClass = (ClassStats[])PerClass.Clone();
        return copy;
    }
}

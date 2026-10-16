// Ported from Krilliac/Sonder-Inference src/scheduler/include/sonder/inference/scheduler/simulator.hpp @1c4c152 (Apache-2.0)
// Ported from Krilliac/Sonder-Inference src/scheduler/src/simulator.cpp @1c4c152 (Apache-2.0)
//
// Deterministic discrete-step simulator: drives an InferenceScheduler against a fixed KV pool and
// a synthetic step-cost model. No backend is executed.

namespace ArcaneCore.World.Playerbots.Chat.Inference.Scheduling;

/// <summary>Synthetic engine cost: overhead + prefill tokens * cost + decode sequences * cost.</summary>
public readonly record struct StepCostModel(long StepOverheadUs = 2_000, long PrefillUsPerToken = 20, long DecodeUsPerSequence = 150)
{
    public StepCostModel()
        : this(2_000, 20, 150)
    {
    }

    public long StepTime(StepPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsEmpty)
        {
            return 0;
        }

        long decodeSeqs = 0;
        foreach (ScheduledWork w in plan.Work)
        {
            if (w.Phase == SchedulePhase.Decode)
            {
                decodeSeqs++;
            }
        }

        return StepOverheadUs + (PrefillUsPerToken * plan.PrefillTokens) + (DecodeUsPerSequence * decodeSeqs);
    }
}

public readonly record struct Arrival(long At, RequestSpec Spec);

public readonly record struct CancellationEvent(long At, ulong Id);

public sealed class SimulationResult
{
    public SchedulerStats Stats { get; set; } = new();

    public List<RequestTimeline> Timelines { get; set; } = [];

    public long Makespan { get; set; }

    public ulong Steps { get; set; }

    public bool HitStepLimit { get; set; }

    // Invariant observations (checked every step).
    public uint MaxStepTokensSeen { get; set; }

    public uint MaxStepSequencesSeen { get; set; }

    public uint MaxPrefillChunkSeen { get; set; }

    public int MaxRunningSeen { get; set; }

    public ulong PeakKvBlocks { get; set; }

    public ulong BudgetViolations { get; set; }

    public ulong KvOvercommits { get; set; }

    public ulong RejectedSubmissions { get; set; }

    public double GeneratedTokensPerSecond
    {
        get
        {
            if (Makespan <= 0)
            {
                return 0.0;
            }

            ulong tokens = 0;
            foreach (RequestTimeline t in Timelines)
            {
                tokens += t.GeneratedTokens;
            }

            return tokens * 1e6 / Makespan;
        }
    }
}

public sealed class SchedulerSimulator(SchedulerConfig config, ulong kvBlocks, uint blockSize, StepCostModel? cost = null)
{
    private readonly SchedulerConfig _config = config.Clone();
    private readonly StepCostModel _cost = cost ?? new StepCostModel();

    /// <summary>Optional per-step observer (called after planning, before completion).</summary>
    public Action<StepPlan, InferenceScheduler>? OnStep { get; set; }

    /// <summary>Optional end-of-sequence injection: return true to finish the request early this step.</summary>
    public Func<ulong, InferenceScheduler, bool>? EarlyStop { get; set; }

    public SimulationResult Run(
        IReadOnlyList<Arrival> arrivals,
        IReadOnlyList<CancellationEvent>? cancellations = null,
        ulong maxSteps = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(arrivals);
        // Stable sort by time (OrderBy is stable).
        Arrival[] arr = [.. arrivals.OrderBy(static a => a.At)];
        CancellationEvent[] cancels = cancellations is null ? [] : [.. cancellations.OrderBy(static c => c.At)];

        var clock = new SimClock();
        var kv = new FixedKvCapacity(kvBlocks, blockSize);
        var sched = new InferenceScheduler(_config, clock, kv);
        var result = new SimulationResult();

        int nextArrival = 0;
        int nextCancel = 0;
        long start = arr.Length == 0 ? 0 : arr[0].At;
        clock.AdvanceTo(start);

        while (result.Steps < maxSteps)
        {
            while (nextArrival < arr.Length && arr[nextArrival].At <= clock.NowUs)
            {
                if (!sched.Submit(arr[nextArrival].Spec).Accepted)
                {
                    result.RejectedSubmissions++;
                }

                nextArrival++;
            }

            while (nextCancel < cancels.Length && cancels[nextCancel].At <= clock.NowUs)
            {
                _ = sched.Cancel(cancels[nextCancel].Id);
                nextCancel++;
            }

            if (sched.IsIdle && nextArrival >= arr.Length)
            {
                break;
            }

            StepPlan plan = sched.PlanStep();
            result.Steps++;

            // Invariants.
            uint seqs = (uint)plan.Work.Count;
            result.MaxStepTokensSeen = Math.Max(result.MaxStepTokensSeen, plan.TotalTokens);
            result.MaxStepSequencesSeen = Math.Max(result.MaxStepSequencesSeen, seqs);
            result.MaxRunningSeen = Math.Max(result.MaxRunningSeen, sched.Running.Count);
            if (plan.TotalTokens > _config.MaxStepTokens ||
                plan.PrefillTokens > _config.MaxStepPrefillTokens ||
                seqs > _config.MaxStepSequences)
            {
                result.BudgetViolations++;
            }

            foreach (ScheduledWork w in plan.Work)
            {
                if (w.Phase == SchedulePhase.Prefill)
                {
                    result.MaxPrefillChunkSeen = Math.Max(result.MaxPrefillChunkSeen, w.NumTokens);
                }
            }

            if (kv.UsedBlocks > kv.TotalBlocks)
            {
                result.KvOvercommits++;
            }

            OnStep?.Invoke(plan, sched);

            var outcome = new StepOutcome();
            if (EarlyStop is { } earlyStop)
            {
                foreach (ScheduledWork w in plan.Work)
                {
                    if (earlyStop(w.Id, sched))
                    {
                        outcome.FinishedEarly.Add(w.Id);
                    }
                }
            }

            if (plan.IsEmpty)
            {
                sched.CompleteStep(plan, outcome);
                // Nothing runnable: jump to the next external event.
                long next = -1;
                if (nextArrival < arr.Length)
                {
                    next = arr[nextArrival].At;
                }

                if (nextCancel < cancels.Length && (next < 0 || cancels[nextCancel].At < next))
                {
                    next = cancels[nextCancel].At;
                }

                if (next < 0)
                {
                    break; // stuck with no future events (should not happen)
                }

                clock.AdvanceTo(next);
                continue;
            }

            clock.Advance(_cost.StepTime(plan));
            sched.CompleteStep(plan, outcome);
        }

        result.HitStepLimit = result.Steps >= maxSteps && !sched.IsIdle;
        result.Stats = sched.Stats();
        result.Timelines = sched.Timelines();
        result.Makespan = clock.NowUs - start;
        result.PeakKvBlocks = kv.PeakUsedBlocks;
        return result;
    }
}

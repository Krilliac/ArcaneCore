// Ported from Krilliac/Sonder-Inference src/scheduler/tests/scheduler_unit_tests.cpp @1c4c152 (Apache-2.0)
//
// Unit tests for the scheduler policy (queue, batching, admission, preemption, fairness,
// telemetry). The upstream seven workload classes map onto ArcaneCore's three; where a test needs
// the upstream rank spread it says so. Tests at the end cover the ArcaneCore additions.

using ArcaneCore.World.Playerbots.Chat.Inference.Scheduling;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference.Scheduling;

public sealed class InferenceSchedulerTests
{
    internal static RequestSpec Spec(ulong id, WorkloadClass cls, uint prompt, uint maxNew) => new()
    {
        Id = id,
        Workload = cls,
        TaskId = $"task-{id}",
        PromptTokens = prompt,
        MaxNewTokens = maxNew,
    };

    private static StepPlan Step(InferenceScheduler sched, SimClock clock, long dt = 1000, StepOutcome? outcome = null)
    {
        StepPlan plan = sched.PlanStep();
        clock.Advance(dt);
        sched.CompleteStep(plan, outcome);
        return plan;
    }

    private static bool HasWork(StepPlan plan, ulong id) => plan.Work.Exists(w => w.Id == id);

    private static ScheduledWork GetWork(StepPlan plan, ulong id)
    {
        int index = plan.Work.FindIndex(w => w.Id == id);
        Assert.True(index >= 0, $"request {id} has no work in step {plan.StepIndex}");
        return plan.Work[index];
    }

    private static bool WasPreempted(StepPlan plan, ulong id) => plan.Preempted.Exists(e => e.Id == id);

    [Fact]
    public void QueueOrdersByWorkloadClass()
    {
        var clock = new SimClock();
        var kv = new FixedKvCapacity(1000, 16);
        var sched = new InferenceScheduler(new SchedulerConfig(), clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.Ambient, 32, 4)).Accepted);
        Assert.True(sched.Submit(Spec(2, WorkloadClass.BotToBot, 32, 4)).Accepted);
        Assert.True(sched.Submit(Spec(3, WorkloadClass.InteractivePlayer, 32, 4)).Accepted);
        Assert.True(sched.Submit(Spec(4, WorkloadClass.BotToBot, 32, 4)).Accepted);
        Assert.Equal(new ulong[] { 3, 2, 4, 1 }, sched.QueueOrder());
    }

    [Fact]
    public void PriorityOverrideApplies()
    {
        var clock = new SimClock();
        var kv = new FixedKvCapacity(1000, 16);
        var sched = new InferenceScheduler(new SchedulerConfig(), clock, kv);
        RequestSpec ambient = Spec(1, WorkloadClass.Ambient, 32, 4);
        ambient.PriorityOverride = -1;
        Assert.True(sched.Submit(Spec(2, WorkloadClass.InteractivePlayer, 32, 4)).Accepted);
        Assert.True(sched.Submit(ambient).Accepted);
        Assert.Equal(1UL, sched.QueueOrder()[0]);
        Assert.Equal(-1, sched.EffectiveRank(1));
    }

    [Fact]
    public void AgingImprovesEffectiveRank()
    {
        var cfg = new SchedulerConfig { AgingIntervalUs = 1000, StarvationThresholdUs = 1_000_000 };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(1000, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.Ambient, 32, 4)).Accepted);
        Assert.Equal(2, sched.EffectiveRank(1));
        clock.Advance(2500);
        Assert.Equal(0, sched.EffectiveRank(1));
        // A freshly arrived bot-to-bot request (rank 1) now sorts behind the aged request.
        Assert.True(sched.Submit(Spec(2, WorkloadClass.BotToBot, 32, 4)).Accepted);
        Assert.Equal(new ulong[] { 1, 2 }, sched.QueueOrder());
    }

    [Fact]
    public void StarvingRequestJumpsToFront()
    {
        var cfg = new SchedulerConfig
        {
            AgingIntervalUs = 1_000_000_000, // isolate the starvation guard
            StarvationThresholdUs = 5000,
        };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(1000, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.Ambient, 32, 4)).Accepted);
        clock.Advance(6000);
        Assert.True(sched.Submit(Spec(2, WorkloadClass.InteractivePlayer, 32, 4)).Accepted);
        Assert.Equal(1UL, sched.QueueOrder()[0]);
        StepPlan plan = Step(sched, clock);
        Assert.Equal(1UL, sched.Stats().StarvationPromotions);
        Assert.Equal(6000L, sched.Stats().MaxStarvationAge);
        Assert.Equal(1UL, plan.Admitted[0]);
    }

    [Fact]
    public void ChunkedPrefillSplitsLongPrompt()
    {
        var cfg = new SchedulerConfig { PrefillChunkTokens = 512, MaxStepPrefillTokens = 1024 };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(1000, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.BotToBot, 1300, 3)).Accepted);

        StepPlan p1 = Step(sched, clock);
        ScheduledWork w1 = Assert.Single(p1.Work);
        Assert.Equal(512u, w1.NumTokens);
        Assert.Equal(0u, w1.ContextOffset);
        Assert.False(w1.CompletesPrefill);
        StepPlan p2 = Step(sched, clock);
        Assert.Equal(512u, p2.Work[0].NumTokens);
        Assert.Equal(512u, p2.Work[0].ContextOffset);
        StepPlan p3 = Step(sched, clock);
        Assert.Equal(276u, p3.Work[0].NumTokens);
        Assert.True(p3.Work[0].CompletesPrefill);
        Assert.Equal(RequestState.Decode, sched.State(1));
        Assert.Equal(1u, sched.Timeline(1)!.Value.GeneratedTokens);
        Assert.Equal(3000L, sched.Timeline(1)!.Value.FirstToken);

        StepPlan p4 = Step(sched, clock);
        Assert.Equal(SchedulePhase.Decode, p4.Work[0].Phase);
        Assert.Equal(1u, p4.Work[0].NumTokens);
        Step(sched, clock);
        Assert.Equal(RequestState.Completed, sched.State(1));
        Assert.True(sched.IsIdle);
        Assert.Equal(0UL, kv.UsedBlocks);
    }

    [Fact]
    public void DecodeFirstAndPrefillCapped()
    {
        var cfg = new SchedulerConfig { MaxStepTokens = 600, MaxStepPrefillTokens = 256, PrefillChunkTokens = 512 };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(1000, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.Ambient, 16, 50)).Accepted);
        Step(sched, clock); // prefill completes -> decoding
        Assert.Equal(RequestState.Decode, sched.State(1));
        // A huge interactive prefill must not displace the running decode.
        Assert.True(sched.Submit(Spec(2, WorkloadClass.InteractivePlayer, 4000, 5)).Accepted);
        StepPlan plan = Step(sched, clock);
        ScheduledWork d = GetWork(plan, 1);
        ScheduledWork p = GetWork(plan, 2);
        Assert.Equal(SchedulePhase.Decode, d.Phase);
        Assert.Equal(SchedulePhase.Prefill, p.Phase);
        Assert.Equal(256u, p.NumTokens);
        Assert.True(plan.TotalTokens <= cfg.MaxStepTokens);
    }

    [Fact]
    public void StepBudgetsRespectedUnderLoad()
    {
        var cfg = new SchedulerConfig
        {
            MaxStepTokens = 300,
            MaxStepPrefillTokens = 200,
            MaxStepSequences = 5,
            PrefillChunkTokens = 64,
        };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(10_000, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        for (ulong i = 1; i <= 40; ++i)
        {
            Assert.True(sched.Submit(Spec(i, (WorkloadClass)(i % 3), 100 + (10 * (uint)i), 8)).Accepted);
        }

        int guard = 0;
        while (!sched.IsIdle && guard++ < 10_000)
        {
            StepPlan plan = Step(sched, clock);
            Assert.True(plan.TotalTokens <= cfg.MaxStepTokens);
            Assert.True(plan.PrefillTokens <= cfg.MaxStepPrefillTokens);
            Assert.True(plan.Work.Count <= cfg.MaxStepSequences);
            foreach (ScheduledWork w in plan.Work)
            {
                Assert.True(w.NumTokens <= cfg.PrefillChunkTokens);
            }
        }

        Assert.True(sched.IsIdle);
        SchedulerStats st = sched.Stats();
        ulong completed = 0;
        foreach (ClassStats c in st.PerClass)
        {
            completed += c.Completed;
        }

        Assert.Equal(40UL, completed);
    }

    [Fact]
    public void AdmissionBlockedByKvAndWatermark()
    {
        var cfg = new SchedulerConfig { AdmissionWatermarkBlocks = 2, EnablePriorityPreemption = false };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(10, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.BotToBot, 64, 30)).Accepted); // 4 blocks
        Step(sched, clock);
        Assert.Equal(4UL, kv.UsedBlocks);
        // Needs 5 blocks; the free blocks do not cover 5 + the watermark of 2.
        Assert.True(sched.Submit(Spec(2, WorkloadClass.BotToBot, 80, 4)).Accepted);
        StepPlan plan = Step(sched, clock);
        Assert.Empty(plan.Admitted);
        Assert.Equal(RequestState.WaitingAdmission, sched.State(2));
        Assert.Equal(1UL, sched.Stats().AdmissionKvBlocked);
    }

    [Fact]
    public void WatermarkWaivedWhenNothingRunning()
    {
        var cfg = new SchedulerConfig { AdmissionWatermarkBlocks = 8 };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(10, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.BotToBot, 150, 10)).Accepted);
        StepPlan plan = Step(sched, clock);
        Assert.Single(plan.Admitted);
    }

    [Fact]
    public void LookaheadAdmitsPastBlockedHead()
    {
        var cfg = new SchedulerConfig { AdmissionWatermarkBlocks = 0, EnablePriorityPreemption = false };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(10, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.BotToBot, 96, 40)).Accepted); // 6 blocks
        Step(sched, clock);
        Assert.True(sched.Submit(Spec(2, WorkloadClass.BotToBot, 96, 4)).Accepted); // needs 6, blocked
        Assert.True(sched.Submit(Spec(3, WorkloadClass.BotToBot, 32, 4)).Accepted); // needs 2, fits
        StepPlan plan = Step(sched, clock);
        Assert.Equal(3UL, Assert.Single(plan.Admitted));
        Assert.Equal(1UL, sched.Stats().LookaheadAdmissions);
    }

    [Fact]
    public void InteractiveHeadIsNeverBypassed()
    {
        var cfg = new SchedulerConfig { AdmissionWatermarkBlocks = 0, EnablePriorityPreemption = false };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(10, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.BotToBot, 96, 40)).Accepted);
        Step(sched, clock);
        Assert.True(sched.Submit(Spec(2, WorkloadClass.InteractivePlayer, 96, 4)).Accepted);
        Assert.True(sched.Submit(Spec(3, WorkloadClass.BotToBot, 32, 4)).Accepted);
        StepPlan plan = Step(sched, clock);
        Assert.Empty(plan.Admitted);
    }

    [Fact]
    public void KvPressurePreemptsLowestPriority()
    {
        var cfg = new SchedulerConfig { AdmissionWatermarkBlocks = 0, EnablePriorityPreemption = false };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(4, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.InteractivePlayer, 32, 16)).Accepted); // 2 blocks
        Assert.True(sched.Submit(Spec(2, WorkloadClass.Ambient, 32, 16)).Accepted); // 2 blocks
        Step(sched, clock); // both prefill, each emits its first token
        Assert.Equal(0UL, kv.FreeBlocks);
        // The next decode for either needs a 3rd block per sequence; ambient chatter yields.
        StepPlan plan = Step(sched, clock);
        Assert.True(WasPreempted(plan, 2));
        Assert.False(WasPreempted(plan, 1));
        Assert.True(HasWork(plan, 1));
        Assert.Equal(RequestState.Preempted, sched.State(2));
        Assert.Equal(PreemptionReason.KvPressure, plan.Preempted[0].Reason);
        Assert.Equal(PreemptionMode.Recompute, plan.Preempted[0].Mode);
        Assert.Equal(32u, plan.Preempted[0].KvTokens);
        Assert.Equal(1UL, plan.Preempted[0].Beneficiary);
        SchedulerStats st = sched.Stats();
        Assert.Equal(1UL, st.PreemptionsKvPressure);
        Assert.Equal(32UL, st.RecomputedTokens);
        // Everything still finishes: the preempted request recomputes later.
        int guard = 0;
        while (!sched.IsIdle && guard++ < 1000)
        {
            Step(sched, clock);
        }

        Assert.Equal(RequestState.Completed, sched.State(1));
        Assert.Equal(RequestState.Completed, sched.State(2));
        Assert.Equal(16u, sched.Timeline(2)!.Value.GeneratedTokens);
    }

    [Fact]
    public void SwapDecisionStubAndResume()
    {
        var cfg = new SchedulerConfig
        {
            EnableSwap = true,
            SwapMinTokens = 32,
            AdmissionWatermarkBlocks = 0,
            EnablePriorityPreemption = false,
        };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(4, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.Equal(PreemptionMode.Recompute, sched.DecidePreemptionMode(31));
        Assert.Equal(PreemptionMode.Swap, sched.DecidePreemptionMode(32));

        Assert.True(sched.Submit(Spec(1, WorkloadClass.InteractivePlayer, 32, 3)).Accepted);
        Assert.True(sched.Submit(Spec(2, WorkloadClass.Ambient, 32, 3)).Accepted);
        Step(sched, clock);
        StepPlan plan = Step(sched, clock);
        Assert.True(WasPreempted(plan, 2));
        Assert.Equal(PreemptionMode.Swap, plan.Preempted[0].Mode);
        Assert.Equal(32UL, sched.Stats().SwappedTokens);
        // After 1 completes, 2 swaps back in straight to decode (no re-prefill).
        int guard = 0;
        bool resumedInDecode = false;
        while (!sched.IsIdle && guard++ < 100)
        {
            StepPlan p = Step(sched, clock);
            if (HasWork(p, 2))
            {
                Assert.Equal(SchedulePhase.Decode, GetWork(p, 2).Phase);
                resumedInDecode = true;
            }
        }

        Assert.True(resumedInDecode);
        Assert.Equal(RequestState.Completed, sched.State(2));
        Assert.Equal(64UL, sched.Stats().PrefillTokens);
    }

    [Fact]
    public void PriorityPreemptionAdmitsInteractive()
    {
        var cfg = new SchedulerConfig { AdmissionWatermarkBlocks = 0 };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(8, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.Ambient, 60, 4)).Accepted); // 4 blocks
        Assert.True(sched.Submit(Spec(2, WorkloadClass.Ambient, 60, 4)).Accepted); // 4 blocks
        Step(sched, clock);
        Assert.Equal(0UL, kv.FreeBlocks);
        Assert.True(sched.Submit(Spec(3, WorkloadClass.InteractivePlayer, 48, 4)).Accepted);
        StepPlan plan = Step(sched, clock);
        Assert.True(WasPreempted(plan, 2)); // the newest of the worst class yields first
        Assert.False(WasPreempted(plan, 1)); // one victim was enough
        Assert.Contains(3UL, plan.Admitted);
        Assert.Equal(PreemptionReason.PriorityAdmission, plan.Preempted[0].Reason);
        Assert.Equal(3UL, plan.Preempted[0].Beneficiary);
        Assert.False(HasWork(plan, 2)); // victim's decode was withdrawn
        Assert.Equal(1UL, sched.Stats().PreemptionsPriority);
    }

    [Fact]
    public void PriorityPreemptionRespectsRankGap()
    {
        var cfg = new SchedulerConfig { AdmissionWatermarkBlocks = 0, PriorityPreemptionMinRankGap = 2 };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(8, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.BotToBot, 120, 8)).Accepted); // rank 1, 8 blocks
        Step(sched, clock);
        Assert.True(sched.Submit(Spec(2, WorkloadClass.InteractivePlayer, 48, 4)).Accepted); // rank 0
        StepPlan plan = Step(sched, clock);
        Assert.Empty(plan.Preempted);
        Assert.Empty(plan.Admitted);
    }

    [Fact]
    public void RequeueLimitFailsRequest()
    {
        var cfg = new SchedulerConfig
        {
            AdmissionWatermarkBlocks = 0,
            EnablePriorityPreemption = false,
            MaxRequeueCount = 0,
        };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(4, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.InteractivePlayer, 32, 16)).Accepted);
        Assert.True(sched.Submit(Spec(2, WorkloadClass.Ambient, 32, 16)).Accepted);
        Step(sched, clock);
        StepPlan plan = Step(sched, clock);
        Assert.Equal(2UL, Assert.Single(plan.Failed));
        Assert.True(plan.Preempted[0].Failed);
        Assert.Equal(RequestState.Failed, sched.State(2));
        Assert.Equal(FailureReason.RequeueLimit, sched.Timeline(2)!.Value.Failure);
        Assert.Equal(1UL, sched.Stats().For(WorkloadClass.Ambient).Failed);
    }

    [Fact]
    public void CancelReleasesKvAndIgnoresInflightWork()
    {
        var clock = new SimClock();
        var kv = new FixedKvCapacity(100, 16);
        var sched = new InferenceScheduler(new SchedulerConfig(), clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.BotToBot, 64, 10)).Accepted);
        Assert.True(sched.Submit(Spec(2, WorkloadClass.BotToBot, 64, 10)).Accepted);
        StepPlan plan = sched.PlanStep();
        Assert.True(kv.BlocksHeld(1) > 0);
        Assert.True(sched.Cancel(1));
        Assert.Equal(0UL, kv.BlocksHeld(1));
        clock.Advance(1000);
        sched.CompleteStep(plan);
        Assert.Equal(RequestState.Cancelled, sched.State(1));
        Assert.Equal(0u, sched.Timeline(1)!.Value.GeneratedTokens);
        Assert.Equal(1u, sched.Timeline(2)!.Value.GeneratedTokens);
        Assert.False(sched.Cancel(1)); // already terminal
        Assert.False(sched.Cancel(99)); // unknown
        Assert.True(sched.Submit(Spec(3, WorkloadClass.BotToBot, 64, 10)).Accepted);
        Assert.True(sched.Cancel(3)); // queued request
        Assert.Equal(RequestState.Cancelled, sched.State(3));
    }

    [Fact]
    public void NonCancellableRequestRefused()
    {
        var clock = new SimClock();
        var kv = new FixedKvCapacity(100, 16);
        var sched = new InferenceScheduler(new SchedulerConfig(), clock, kv);
        RequestSpec s = Spec(1, WorkloadClass.Ambient, 16, 2);
        s.Cancellable = false;
        Assert.True(sched.Submit(s).Accepted);
        Assert.False(sched.Cancel(1));
        Assert.Equal(RequestState.WaitingAdmission, sched.State(1));
    }

    [Fact]
    public void DuplicateSequenceGuard()
    {
        var clock = new SimClock();
        var kv = new FixedKvCapacity(100, 16);
        var sched = new InferenceScheduler(new SchedulerConfig(), clock, kv);
        RequestSpec a = Spec(1, WorkloadClass.BotToBot, 16, 1);
        a.SequenceFingerprint = "model@rev:prompt#abc";
        RequestSpec b = a.Clone();
        b.Id = 2;
        RequestSpec c = a.Clone();
        c.Id = 3;
        c.AllowDuplicate = true;
        Assert.True(sched.Submit(a).Accepted);
        SubmitResult r = sched.Submit(b);
        Assert.False(r.Accepted);
        Assert.Equal(FailureReason.DuplicateSequence, r.Reason);
        Assert.True(sched.Submit(c).Accepted); // explicitly requested duplicate
        int guard = 0;
        while (!sched.IsIdle && guard++ < 100)
        {
            Step(sched, clock);
        }

        RequestSpec d = a.Clone();
        d.Id = 4;
        Assert.True(sched.Submit(d).Accepted); // fingerprint freed after completion
    }

    [Fact]
    public void InvalidAndOversizedRequestsRejected()
    {
        var clock = new SimClock();
        var kv = new FixedKvCapacity(10, 16);
        var sched = new InferenceScheduler(new SchedulerConfig(), clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.InteractivePlayer, 16, 1)).Accepted);
        Assert.Equal(FailureReason.DuplicateId, sched.Submit(Spec(1, WorkloadClass.InteractivePlayer, 16, 1)).Reason);
        Assert.Equal(FailureReason.InvalidRequest, sched.Submit(Spec(2, WorkloadClass.InteractivePlayer, 0, 1)).Reason);
        Assert.Equal(FailureReason.InvalidRequest, sched.Submit(Spec(3, WorkloadClass.InteractivePlayer, 16, 0)).Reason);
        Assert.Equal(FailureReason.NeverFits, sched.Submit(Spec(4, WorkloadClass.InteractivePlayer, 150, 20)).Reason);
        Assert.Equal(4UL, sched.Stats().PerClass[0].Rejected);
        Assert.Equal(5UL, sched.Stats().PerClass[0].Submitted);
    }

    [Fact]
    public void PlanCompleteProtocolEnforced()
    {
        var clock = new SimClock();
        var kv = new FixedKvCapacity(10, 16);
        var sched = new InferenceScheduler(new SchedulerConfig(), clock, kv);
        StepPlan plan = sched.PlanStep();
        Assert.Throws<InvalidOperationException>(() => sched.PlanStep());
        sched.CompleteStep(plan);
        Assert.Throws<InvalidOperationException>(() => sched.CompleteStep(plan));
        Assert.Equal(1UL, sched.Stats().EmptySteps);
    }

    [Fact]
    public void InvalidConfigRejected()
    {
        var clock = new SimClock();
        var kv = new FixedKvCapacity(10, 16);
        var cfg = new SchedulerConfig { MaxStepTokens = 0 };
        Assert.Throws<ArgumentException>(() => new InferenceScheduler(cfg, clock, kv));
    }

    [Fact]
    public void EarlyStopCompletesSequence()
    {
        var clock = new SimClock();
        var kv = new FixedKvCapacity(100, 16);
        var sched = new InferenceScheduler(new SchedulerConfig(), clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.InteractivePlayer, 16, 100)).Accepted);
        Step(sched, clock);
        var eos = new StepOutcome();
        eos.FinishedEarly.Add(1);
        Step(sched, clock, 1000, eos);
        Assert.Equal(RequestState.Completed, sched.State(1));
        Assert.Equal(2u, sched.Timeline(1)!.Value.GeneratedTokens);
        Assert.Equal(0UL, kv.UsedBlocks);
    }

    [Fact]
    public void StatsTrackLatencyAndOccupancy()
    {
        var cfg = new SchedulerConfig { MaxStepTokens = 100 };
        var clock = new SimClock(10_000);
        var kv = new FixedKvCapacity(100, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.InteractivePlayer, 20, 4)).Accepted);
        clock.Advance(500); // queue wait before the first plan
        Step(sched, clock, 1000); // prefill + first token at 11'500
        Step(sched, clock, 200);
        Step(sched, clock, 300);
        Step(sched, clock, 400);
        SchedulerStats st = sched.Stats();
        ClassStats c = st.PerClass[0];
        Assert.Equal(1UL, c.Completed);
        Assert.Equal(4UL, c.GeneratedTokens);
        Assert.Equal(500L, c.QueueTime.Max);
        Assert.Equal(1500L, c.Ttft.Max);
        Assert.Equal(3UL, c.InterToken.Count);
        Assert.Equal(200L, c.InterToken.Min);
        Assert.Equal(400L, c.InterToken.Max);
        Assert.Equal(2400L, c.EndToEnd.Max);
        Assert.Equal(20UL, st.PrefillTokens);
        Assert.Equal(3UL, st.DecodeTokens);
        Assert.Equal(4UL, st.Steps);
        Assert.InRange(st.BatchTokenOccupancy, 0.05, 0.07);
        Assert.Equal(400L, sched.Timeline(1)!.Value.MaxInterTokenGap);
    }

    [Fact]
    public void LatencySummaryPercentiles()
    {
        long[] v = new long[100];
        for (int i = 0; i < 100; i++)
        {
            v[i] = 100 - i;
        }

        LatencySummary s = LatencySummary.From(v.AsSpan());
        Assert.Equal(100UL, s.Count);
        Assert.Equal(1L, s.Min);
        Assert.Equal(100L, s.Max);
        Assert.Equal(50L, s.P50);
        Assert.Equal(95L, s.P95);
        Assert.Equal(99L, s.P99);
        Assert.InRange(s.Mean, 50.49, 50.51);
        Assert.Equal(0UL, LatencySummary.From(Span<long>.Empty).Count);
    }

    [Fact]
    public void SimClockIsMonotonicAndManual()
    {
        var c = new SimClock(5);
        Assert.Equal(5L, c.NowUs);
        c.Advance(10);
        Assert.Equal(15L, c.NowUs);
        c.Advance(-3);
        Assert.Equal(15L, c.NowUs);
        c.AdvanceTo(12);
        Assert.Equal(15L, c.NowUs);
        c.AdvanceTo(40);
        Assert.Equal(40L, c.NowUs);
    }

    [Fact]
    public void FixedKvCapacityAllOrNothing()
    {
        var kv = new FixedKvCapacity(10, 16);
        Assert.True(kv.TryReserve(1, 6));
        Assert.False(kv.TryReserve(2, 5));
        Assert.Equal(0UL, kv.BlocksHeld(2));
        Assert.True(kv.TryReserve(2, 4));
        Assert.Equal(0UL, kv.FreeBlocks);
        kv.Release(1);
        Assert.Equal(6UL, kv.FreeBlocks);
        Assert.Equal(10UL, kv.PeakUsedBlocks);
        Assert.Equal(3UL, KvBlocks.ForTokens(33, 16));
        Assert.Equal(2UL, KvBlocks.ForTokens(32, 16));
    }

    [Fact]
    public void ForgetDropsTerminalRequestsOnly()
    {
        var clock = new SimClock();
        var kv = new FixedKvCapacity(100, 16);
        var sched = new InferenceScheduler(new SchedulerConfig(), clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.InteractivePlayer, 16, 2)).Accepted);
        Assert.True(sched.Submit(Spec(2, WorkloadClass.InteractivePlayer, 16, 50)).Accepted);
        Assert.Equal(2, sched.Tracked);
        Assert.False(sched.Forget(1)); // still running
        Step(sched, clock);
        Step(sched, clock);
        Assert.Equal(RequestState.Completed, sched.State(1));
        Assert.True(sched.Forget(1));
        Assert.False(sched.Forget(1)); // already gone
        Assert.Null(sched.State(1));
        Assert.Equal(1, sched.Tracked);
        Assert.True(sched.Cancel(2));
        Assert.True(sched.Forget(2));
        Assert.Equal(0, sched.Tracked);
        Assert.False(sched.Forget(99));
        // Counters survive forgetting.
        Assert.Equal(1UL, sched.Stats().For(WorkloadClass.InteractivePlayer).Completed);
        Assert.True(sched.IsIdle);
    }

    [Fact]
    public void InterTokenSamplesAreBoundedByTheWindow()
    {
        var cfg = new SchedulerConfig { LatencySampleWindow = 8 };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(1000, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.InteractivePlayer, 16, 100)).Accepted);
        Step(sched, clock, 1000); // prefill + first token
        for (long i = 1; i < 100; ++i)
        {
            Step(sched, clock, i); // gaps 1..99
        }

        Assert.Equal(RequestState.Completed, sched.State(1));
        ClassStats c = sched.Stats().For(WorkloadClass.InteractivePlayer);
        Assert.Equal(8UL, c.InterToken.Count);
        Assert.Equal(92L, c.InterToken.Min); // only the most recent gaps are kept
        Assert.Equal(99L, c.InterToken.Max);
    }

    [Fact]
    public void StalledWorkIsNotAccountedAndIsPlannedAgain()
    {
        var clock = new SimClock();
        var kv = new FixedKvCapacity(100, 16);
        var sched = new InferenceScheduler(new SchedulerConfig(), clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.InteractivePlayer, 16, 4)).Accepted);
        var stalled = new StepOutcome();
        stalled.Stalled.Add(1);
        StepPlan first = Step(sched, clock, 1000, stalled);
        ScheduledWork w = GetWork(first, 1);
        Assert.True(w.CompletesPrefill);
        Assert.Equal(RequestState.Prefill, sched.State(1)); // prefill not accounted
        Assert.Equal(0u, sched.Timeline(1)!.Value.GeneratedTokens);
        StepPlan again = Step(sched, clock);
        w = GetWork(again, 1);
        Assert.Equal(SchedulePhase.Prefill, w.Phase);
        Assert.Equal(0u, w.ContextOffset);
        Assert.True(w.CompletesPrefill);
        Assert.Equal(RequestState.Decode, sched.State(1));
        Assert.Equal(1u, sched.Timeline(1)!.Value.GeneratedTokens);
        StepPlan decode = Step(sched, clock, 1000, stalled);
        w = GetWork(decode, 1);
        Assert.Equal(SchedulePhase.Decode, w.Phase);
        Assert.Equal(1u, sched.Timeline(1)!.Value.GeneratedTokens); // stalled decode: no token
        StepPlan retry = Step(sched, clock);
        Assert.Equal(w.ContextOffset, GetWork(retry, 1).ContextOffset);
        Assert.Equal(2u, sched.Timeline(1)!.Value.GeneratedTokens);
    }

    // ---------------------------------------------------------------- ArcaneCore additions

    [Fact]
    public void AmbientIsDroppedAfterItsQueueDeadline()
    {
        var cfg = new SchedulerConfig { MaxRunningSequences = 1, DroppableQueueTimeoutUs = 5000 };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(100, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.InteractivePlayer, 16, 100)).Accepted);
        Assert.True(sched.Submit(Spec(2, WorkloadClass.Ambient, 16, 4)).Accepted);

        StepPlan? droppedIn = null;
        for (int i = 0; i < 20 && droppedIn is null; i++)
        {
            StepPlan plan = Step(sched, clock);
            if (plan.Dropped.Contains(2UL))
            {
                droppedIn = plan;
            }
        }

        Assert.NotNull(droppedIn);
        Assert.Equal(5000L, droppedIn.PlannedAt);
        Assert.Equal(RequestState.Dropped, sched.State(2));
        Assert.Equal(FailureReason.QueueTimeout, sched.Timeline(2)!.Value.Failure);
        SchedulerStats st = sched.Stats();
        Assert.Equal(1UL, st.For(WorkloadClass.Ambient).Dropped);
        Assert.Equal(0UL, st.For(WorkloadClass.Ambient).Failed);
        Assert.Equal(1UL, st.QueueTimeoutDrops);
        Assert.False(sched.Cancel(2)); // terminal
    }

    [Fact]
    public void PlayerAndBotRequestsAreNeverDropped()
    {
        var cfg = new SchedulerConfig
        {
            MaxRunningSequences = 1,
            DroppableQueueTimeoutUs = 2000,
            StarvationThresholdUs = 1_000_000_000,
        };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(100, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.InteractivePlayer, 16, 100)).Accepted);
        Assert.True(sched.Submit(Spec(2, WorkloadClass.BotToBot, 16, 4)).Accepted);
        RequestSpec keep = Spec(3, WorkloadClass.Ambient, 16, 4);
        keep.Droppable = false;
        Assert.True(sched.Submit(keep).Accepted);
        for (int i = 0; i < 20; i++)
        {
            Step(sched, clock);
        }

        Assert.Equal(RequestState.WaitingAdmission, sched.State(2));
        Assert.Equal(RequestState.WaitingAdmission, sched.State(3));
        Assert.Equal(0UL, sched.Stats().QueueTimeoutDrops);
    }

    [Fact]
    public void AdmittedAmbientIsNotDropped()
    {
        var cfg = new SchedulerConfig { DroppableQueueTimeoutUs = 5000 };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(100, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.Ambient, 16, 20)).Accepted);
        int guard = 0;
        while (!sched.IsIdle && guard++ < 100)
        {
            Step(sched, clock);
        }

        Assert.Equal(RequestState.Completed, sched.State(1));
        Assert.Equal(20u, sched.Timeline(1)!.Value.GeneratedTokens);
    }

    [Fact]
    public void OnlyAPlayerTurnCanDisplaceAmbientChatter()
    {
        var cfg = new SchedulerConfig { AdmissionWatermarkBlocks = 0 };
        var clock = new SimClock();
        var kv = new FixedKvCapacity(8, 16);
        var sched = new InferenceScheduler(cfg, clock, kv);
        Assert.True(sched.Submit(Spec(1, WorkloadClass.Ambient, 60, 6)).Accepted); // 4 blocks
        Assert.True(sched.Submit(Spec(2, WorkloadClass.Ambient, 60, 6)).Accepted); // 4 blocks
        Step(sched, clock);
        Assert.Equal(0UL, kv.FreeBlocks);

        // Bot-to-bot (rank 1) is one rank above ambient (rank 2): below the default gap of 2.
        Assert.True(sched.Submit(Spec(3, WorkloadClass.BotToBot, 48, 4)).Accepted);
        StepPlan botStep = Step(sched, clock);
        Assert.Empty(botStep.Preempted);
        Assert.Empty(botStep.Admitted);

        Assert.True(sched.Submit(Spec(4, WorkloadClass.InteractivePlayer, 48, 4)).Accepted);
        StepPlan playerStep = Step(sched, clock);
        Assert.True(WasPreempted(playerStep, 2));
        Assert.Contains(4UL, playerStep.Admitted);
        Assert.Equal(RequestState.WaitingAdmission, sched.State(3));
    }

    [Fact]
    public void WorkloadClassRanksAndNames()
    {
        Assert.Equal(0, (int)WorkloadClass.InteractivePlayer);
        Assert.Equal(1, (int)WorkloadClass.BotToBot);
        Assert.Equal(2, (int)WorkloadClass.Ambient);
        Assert.Equal("ambient", WorkloadClass.Ambient.ToName());
        Assert.True(RequestState.Dropped.IsTerminal());
        Assert.False(RequestState.Preempted.IsTerminal());
        Assert.True(Spec(1, WorkloadClass.Ambient, 1, 1).IsDroppable);
        Assert.False(Spec(1, WorkloadClass.BotToBot, 1, 1).IsDroppable);
    }
}

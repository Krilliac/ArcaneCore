// Ported from Krilliac/Sonder-Inference src/scheduler/tests/scheduler_sim_tests.cpp @1c4c152 (Apache-2.0)
//
// Deterministic simulation tests: mixed player / bot-to-bot fan-out / ambient workloads with
// latency, throughput and safety invariants. The upstream workload used seven classes; each
// request here keeps its upstream rank through PriorityOverride (interactive 0, critic 2,
// implementation worker 3, background 5) so the policy sees the same rank spread the upstream
// thresholds were tuned against, while the statistics bucket by ArcaneCore class. Ambient dropping
// is disabled because these workloads assert that every request completes.

using ArcaneCore.World.Playerbots.Chat.Inference.Scheduling;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference.Scheduling;

public sealed class SchedulerSimulatorTests
{
    private const long Ms = 1000;

    private static RequestSpec Spec(ulong id, WorkloadClass cls, int upstreamRank, uint prompt, uint maxNew) => new()
    {
        Id = id,
        Workload = cls,
        PriorityOverride = upstreamRank,
        TaskId = $"sim-{id}",
        PromptTokens = prompt,
        MaxNewTokens = maxNew,
    };

    private static SchedulerConfig MixedConfig() => new()
    {
        MaxStepTokens = 2048,
        MaxStepPrefillTokens = 1024,
        PrefillChunkTokens = 512,
        MaxStepSequences = 64,
        MaxRunningSequences = 48,
        AdmissionWatermarkBlocks = 8,
        AgingIntervalUs = 500 * Ms,
        StarvationThresholdUs = 3000 * Ms,
        DroppableQueueTimeoutUs = 0,
    };

    /// <summary>
    /// 4 long ambient jobs, a 28-request bot-to-bot burst, and a steady trickle of player turns.
    /// </summary>
    private static List<Arrival> MixedWorkload()
    {
        var a = new List<Arrival>();
        ulong id = 1;
        for (int i = 0; i < 4; ++i)
        {
            a.Add(new Arrival(0, Spec(id++, WorkloadClass.Ambient, 5, 3000, 200)));
        }

        for (int i = 0; i < 24; ++i)
        {
            a.Add(new Arrival(50 * Ms, Spec(id++, WorkloadClass.BotToBot, 3, 800, 96)));
        }

        for (int i = 0; i < 4; ++i)
        {
            a.Add(new Arrival(60 * Ms, Spec(id++, WorkloadClass.BotToBot, 2, 600, 64)));
        }

        for (int i = 0; i < 10; ++i)
        {
            a.Add(new Arrival((100 * Ms) + (i * 200 * Ms), Spec(id++, WorkloadClass.InteractivePlayer, 0, 200, 48)));
        }

        return a;
    }

    private static ClassStats Cls(SimulationResult r, WorkloadClass c) => r.Stats.For(c);

    [Fact]
    public void MixedWorkloadLatencyAndThroughputInvariants()
    {
        SchedulerConfig cfg = MixedConfig();
        var sim = new SchedulerSimulator(cfg, 600, 16);
        List<Arrival> workload = MixedWorkload();
        SimulationResult r = sim.Run(workload);

        // Safety: budgets and KV capacity are never exceeded.
        Assert.False(r.HitStepLimit);
        Assert.Equal(0UL, r.BudgetViolations);
        Assert.Equal(0UL, r.KvOvercommits);
        Assert.True(r.PeakKvBlocks <= 600);
        Assert.True(r.MaxStepTokensSeen <= cfg.MaxStepTokens);
        Assert.True(r.MaxPrefillChunkSeen <= cfg.PrefillChunkTokens);
        Assert.True(r.MaxRunningSeen <= cfg.MaxRunningSequences);

        // Liveness: every request completes, including ambient work.
        Assert.All(r.Timelines, t => Assert.Equal(RequestState.Completed, t.FinalState));
        Assert.Equal(workload.Count, r.Timelines.Count);
        Assert.Equal(4UL, Cls(r, WorkloadClass.Ambient).Completed);

        // Latency: player turns get their first token quickly and ahead of the bot fan-out and
        // ambient classes.
        ClassStats player = Cls(r, WorkloadClass.InteractivePlayer);
        ClassStats ambient = Cls(r, WorkloadClass.Ambient);
        // Upstream compared against the implementation-worker class alone (ids 5..28 here).
        var workerTtft = new List<long>();
        foreach (RequestTimeline t in r.Timelines)
        {
            if (t.Id is >= 5 and <= 28)
            {
                workerTtft.Add(t.Ttft);
            }
        }

        Assert.True(player.Ttft.P95 < LatencySummary.From(workerTtft).P50);
        Assert.True(player.Ttft.P95 < ambient.Ttft.P50);
        Assert.True(player.Ttft.Max < 100 * Ms);
        // Decode-first + chunked prefill bound inter-token latency for player streams to roughly
        // one maximal step.
        Assert.True(player.InterToken.Max < 60 * Ms);
        Assert.Equal(0UL, player.Preemptions);

        // Throughput: continuous batching actually batches.
        Assert.True(r.Stats.MeanBatchSequences > 4.0);
        Assert.True(r.GeneratedTokensPerSecond > 1000.0);
        Assert.True(r.Stats.DecodeTokens > 0 && r.Stats.PrefillTokens > 0);
    }

    [Fact]
    public void SimulationIsDeterministic()
    {
        var sim = new SchedulerSimulator(MixedConfig(), 600, 16);
        SimulationResult a = sim.Run(MixedWorkload());
        SimulationResult b = sim.Run(MixedWorkload());
        Assert.Equal(a.Steps, b.Steps);
        Assert.Equal(a.Makespan, b.Makespan);
        Assert.Equal(a.Timelines.Count, b.Timelines.Count);
        for (int i = 0; i < a.Timelines.Count && i < b.Timelines.Count; ++i)
        {
            Assert.Equal(a.Timelines[i].FirstToken, b.Timelines[i].FirstToken);
            Assert.Equal(a.Timelines[i].Finished, b.Timelines[i].Finished);
            Assert.Equal(a.Timelines[i].Preemptions, b.Timelines[i].Preemptions);
        }
    }

    [Fact]
    public void KvPressureWorkloadStaysSafeAndLive()
    {
        SchedulerConfig cfg = MixedConfig();
        cfg.AdmissionWatermarkBlocks = 1;
        var sim = new SchedulerSimulator(cfg, 240, 16); // 3840 tokens: heavy contention
        SimulationResult r = sim.Run(MixedWorkload());
        Assert.False(r.HitStepLimit);
        Assert.Equal(0UL, r.KvOvercommits);
        Assert.True(r.PeakKvBlocks <= 240);
        Assert.True(r.Stats.PreemptionsKvPressure + r.Stats.PreemptionsPriority > 0);
        ulong done = 0;
        ulong failed = 0;
        foreach (ClassStats c in r.Stats.PerClass)
        {
            done += c.Completed;
            failed += c.Failed;
        }

        Assert.Equal(42UL, done + failed);
        Assert.Equal(10UL, Cls(r, WorkloadClass.InteractivePlayer).Completed);
        // Urgent work is preempted no more than lower classes.
        Assert.True(Cls(r, WorkloadClass.InteractivePlayer).Preemptions <=
                    Cls(r, WorkloadClass.Ambient).Preemptions + Cls(r, WorkloadClass.BotToBot).Preemptions);
    }

    [Fact]
    public void CancellationMidRunFreesCapacity()
    {
        var sim = new SchedulerSimulator(MixedConfig(), 600, 16);
        var cancels = new List<CancellationEvent>();
        for (ulong id = 5; id <= 12; ++id)
        {
            cancels.Add(new CancellationEvent(80 * Ms, id)); // 8 bot-to-bot workers
        }

        SimulationResult r = sim.Run(MixedWorkload(), cancels);
        Assert.False(r.HitStepLimit);
        Assert.Equal(8UL, Cls(r, WorkloadClass.BotToBot).Cancelled);
        // 16 surviving workers plus the 4 critic-ranked bot requests.
        Assert.Equal(20UL, Cls(r, WorkloadClass.BotToBot).Completed);
        Assert.Equal(0, r.Stats.ActiveSequences);
        Assert.Equal(0, r.Stats.QueuedSequences);
    }

    [Fact]
    public void StarvationGuardBoundsAmbientWait()
    {
        // Two slots, a saturating stream of player turns and one ambient job.
        static SchedulerConfig Make(long starvation) => new()
        {
            MaxRunningSequences = 2,
            AgingIntervalUs = 1_000_000 * Ms, // disable aging, isolate the guard
            StarvationThresholdUs = starvation,
            EnablePriorityPreemption = false,
            DroppableQueueTimeoutUs = 0,
        };

        var w = new List<Arrival> { new(0, Spec(1, WorkloadClass.Ambient, 5, 256, 16)) };
        ulong id = 2;
        for (long t = 0; t < 4000 * Ms; t += 20 * Ms)
        {
            w.Add(new Arrival(t, Spec(id++, WorkloadClass.InteractivePlayer, 0, 128, 16)));
            w.Add(new Arrival(t, Spec(id++, WorkloadClass.InteractivePlayer, 0, 128, 16)));
        }

        SimulationResult guarded = new SchedulerSimulator(Make(500 * Ms), 1000, 16).Run(w);
        SimulationResult unguarded = new SchedulerSimulator(Make(1_000_000 * Ms), 1000, 16).Run(w);
        RequestTimeline bgGuarded = guarded.Timelines[0];
        RequestTimeline bgUnguarded = unguarded.Timelines[0];
        Assert.Equal(RequestState.Completed, bgGuarded.FinalState);
        Assert.True(bgGuarded.TotalQueueTime < 700 * Ms);
        Assert.True(bgUnguarded.TotalQueueTime > 2000 * Ms);
        Assert.True(guarded.Stats.StarvationPromotions >= 1);
    }

    [Fact]
    public void EarlyStopShortensGeneration()
    {
        var sim = new SchedulerSimulator(MixedConfig(), 600, 16)
        {
            EarlyStop = static (id, s) => s.Timeline(id) is { } t && t.GeneratedTokens >= 10,
        };
        SimulationResult r = sim.Run(MixedWorkload());
        Assert.False(r.HitStepLimit);
        Assert.All(r.Timelines, t =>
        {
            Assert.Equal(RequestState.Completed, t.FinalState);
            Assert.True(t.GeneratedTokens <= 11u);
        });
    }

    [Fact]
    public void AmbientBacklogIsDroppedUnderPlayerLoad()
    {
        // ArcaneCore addition: with dropping on, ambient chatter that cannot get a slot within its
        // deadline is dropped instead of being promoted, and player turns all complete.
        var cfg = new SchedulerConfig
        {
            MaxRunningSequences = 2,
            EnablePriorityPreemption = false,
            DroppableQueueTimeoutUs = 200 * Ms,
        };
        var w = new List<Arrival>();
        ulong id = 1;
        for (int i = 0; i < 5; i++)
        {
            w.Add(new Arrival(0, Spec(id++, WorkloadClass.Ambient, 2, 128, 16)));
        }

        for (long t = 0; t < 1000 * Ms; t += 20 * Ms)
        {
            w.Add(new Arrival(t, Spec(id++, WorkloadClass.InteractivePlayer, 0, 128, 16)));
            w.Add(new Arrival(t, Spec(id++, WorkloadClass.InteractivePlayer, 0, 128, 16)));
        }

        SimulationResult r = new SchedulerSimulator(cfg, 1000, 16).Run(w);
        Assert.False(r.HitStepLimit);
        ClassStats ambient = Cls(r, WorkloadClass.Ambient);
        Assert.Equal(5UL, ambient.Completed + ambient.Dropped);
        Assert.True(ambient.Dropped > 0);
        Assert.Equal(0UL, Cls(r, WorkloadClass.InteractivePlayer).Dropped);
        Assert.Equal(100UL, Cls(r, WorkloadClass.InteractivePlayer).Completed);
    }
}

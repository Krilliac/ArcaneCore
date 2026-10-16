using ArcaneCore.World.Playerbots.Chat.Inference;
using ArcaneCore.World.Playerbots.Chat.Inference.Scheduling;
using ArcaneCore.World.Playerbots.Chat.Inference.Serving;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference.Serving;

/// <summary>
/// The ported scheduler, KV block cache and sampler wired into the in-process engine: slots as KV capacity (a player's line
/// preempts ambient chatter, stale ambient work is dropped), the shared prefix cache (restored KV gives the same logits as computing
/// it) and the scheduled engine (greedy replies equal the slot's own generation, the prompt is reused).
/// </summary>
public sealed class ChatServingTests
{
    [Fact]
    public void Slot_capacity_hands_out_whole_slots()
    {
        var capacity = new SlotKvCapacity(slots: 2, slotTokens: 64);
        Assert.Equal(4UL, capacity.TotalBlocks);
        Assert.Equal(4UL, capacity.FreeBlocks);
        Assert.True(capacity.TryReserve(1, 3));
        Assert.True(capacity.TryReserve(1, 1)); // grows within its own slot
        Assert.Equal(0, capacity.SlotOf(1));
        Assert.True(capacity.TryReserve(2, 4));
        Assert.Equal(1, capacity.SlotOf(2));
        Assert.Equal(0UL, capacity.FreeBlocks);
        Assert.False(capacity.TryReserve(3, 1));
        Assert.False(capacity.TryReserve(2, 5)); // more than a slot holds
        capacity.Release(1);
        Assert.True(capacity.IsFree(0));
        Assert.Equal(4UL, capacity.FreeBlocks);
        Assert.Equal(0UL, capacity.BlocksHeld(1));
    }

    private static (InferenceScheduler Scheduler, SimClock Clock, SlotKvCapacity Capacity) OneSlot(int ambientQueueMs = 4000)
    {
        var clock = new SimClock();
        var capacity = new SlotKvCapacity(1, 256);
        SchedulerConfig config = InProcessChatEngine.SchedulerConfigFor(new ChatServingOptions(AmbientQueueMs: ambientQueueMs), 1);
        return (new InferenceScheduler(config, clock, capacity), clock, capacity);
    }

    private static RequestSpec Spec(ulong id, WorkloadClass workload) => new() { Id = id, Workload = workload, PromptTokens = 40, MaxNewTokens = 8 };

    [Fact]
    public void A_players_line_preempts_ambient_chatter_on_the_only_slot()
    {
        (InferenceScheduler scheduler, SimClock clock, SlotKvCapacity capacity) = OneSlot();
        Assert.True(scheduler.Submit(Spec(1, WorkloadClass.Ambient)).Accepted);
        StepPlan first = scheduler.PlanStep();
        Assert.Contains(1UL, first.Admitted);
        clock.Advance(1000);
        scheduler.CompleteStep(first);
        Assert.Equal(0, capacity.SlotOf(1));

        Assert.True(scheduler.Submit(Spec(2, WorkloadClass.InteractivePlayer)).Accepted);
        StepPlan second = scheduler.PlanStep();
        Assert.Contains(second.Preempted, p => p.Id == 1 && p.Reason == PreemptionReason.PriorityAdmission && p.Beneficiary == 2);
        Assert.Contains(2UL, second.Admitted);
        Assert.Equal(0, capacity.SlotOf(2));
        Assert.DoesNotContain(second.Work, w => w.Id == 1);
        scheduler.CompleteStep(second);
        Assert.Equal(RequestState.Preempted, scheduler.State(1));
    }

    [Fact]
    public void Bot_to_bot_work_waits_for_a_player_but_is_not_dropped()
    {
        (InferenceScheduler scheduler, SimClock clock, _) = OneSlot();
        scheduler.Submit(Spec(1, WorkloadClass.InteractivePlayer));
        scheduler.CompleteStep(scheduler.PlanStep());
        scheduler.Submit(Spec(2, WorkloadClass.BotToBot));
        clock.Advance(60_000_000);
        StepPlan plan = scheduler.PlanStep();
        Assert.Empty(plan.Preempted); // a lower class never displaces a higher one
        Assert.Empty(plan.Dropped);
        scheduler.CompleteStep(plan);
        Assert.Equal(RequestState.WaitingAdmission, scheduler.State(2));
    }

    [Fact]
    public void Stale_ambient_chatter_is_dropped_from_the_queue()
    {
        (InferenceScheduler scheduler, SimClock clock, _) = OneSlot(ambientQueueMs: 2000);
        scheduler.Submit(Spec(1, WorkloadClass.InteractivePlayer));
        scheduler.CompleteStep(scheduler.PlanStep());
        scheduler.Submit(Spec(2, WorkloadClass.Ambient));
        clock.Advance(1_000_000);
        StepPlan early = scheduler.PlanStep();
        Assert.Empty(early.Dropped);
        scheduler.CompleteStep(early);
        clock.Advance(1_500_000);
        StepPlan late = scheduler.PlanStep();
        Assert.Contains(2UL, late.Dropped);
        scheduler.CompleteStep(late);
        Assert.Equal(RequestState.Dropped, scheduler.State(2));
        Assert.Equal(1UL, scheduler.Stats().QueueTimeoutDrops);
    }

    [Fact]
    public void A_reused_plan_is_cleared_between_steps()
    {
        (InferenceScheduler scheduler, SimClock clock, _) = OneSlot();
        var plan = new StepPlan();
        scheduler.Submit(Spec(1, WorkloadClass.InteractivePlayer));
        Assert.Same(plan, scheduler.PlanStep(plan));
        Assert.Single(plan.Admitted);
        clock.Advance(10);
        scheduler.CompleteStep(plan);
        scheduler.PlanStep(plan);
        Assert.Empty(plan.Admitted);
        Assert.Single(plan.Work);
        scheduler.CompleteStep(plan);
    }

    private static int[] Prompt(int count)
    {
        var ids = new int[count];
        for (int i = 0; i < count; i++) ids[i] = 97 + (i * 7 % 26);
        return ids;
    }

    [Fact]
    public void Prefix_cache_restores_the_same_keys_and_values_as_computing_them()
    {
        TinyModel tiny = TinyModel.Create("qwen2", 8, 2, 1, 16, layers: 2);
        string path = tiny.Builder.WriteTemp();
        try
        {
            using ChatModel model = ChatModel.Load(path);
            using var computed = new ChatInferenceContext(model, 64, 1);
            using var restored = new ChatInferenceContext(model, 64, 1);
            var store = new KvPrefixStore(model, 64);
            List<int> tokens = [.. Prompt(40)];

            computed.Prefill(tokens, 0, tokens.Count, wantLogits: true);
            float[] expected = computed.Logits.ToArray();
            store.Register(computed, tokens, tokens.Count);
            Assert.Equal(32, store.RegisteredTokens); // whole blocks of 16, at most half the pool

            int have = store.Restore(restored, tokens, restored.KeepCommonPrefix(tokens));
            Assert.Equal(32, have);
            Assert.Equal(32, restored.CachedCount);
            restored.Prefill(tokens, have, tokens.Count, wantLogits: true);
            ReadOnlySpan<float> actual = restored.Logits;
            for (int i = 0; i < expected.Length; i++) Assert.True(Math.Abs(expected[i] - actual[i]) <= 1e-4f * Math.Max(1f, Math.Abs(expected[i])), $"logit {i}");

            // A prompt that differs inside the first block shares nothing.
            List<int> other = [.. tokens];
            other[3] = 120;
            using var third = new ChatInferenceContext(model, 64, 1);
            Assert.Equal(0, store.Restore(third, other, 0));
        }
        finally
        {
            GgufBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task Scheduled_engine_matches_the_slots_own_greedy_generation_and_reuses_the_prompt()
    {
        TinyModel tiny = TinyModel.Create("llama", 8, 2, 1, 16);
        string path = tiny.Builder.WriteTemp();
        try
        {
            int[] prompt = Prompt(20);
            var greedy = new ChatGenerationSettings(MaxNewTokens: 4, BudgetMs: 60_000, Temperature: 0f, TopK: 1, TopP: 1f, RepetitionPenalty: 1f, Seed: 1, StopAtNewline: false);
            IReadOnlyList<int> reference;
            using (ChatModel model = ChatModel.Load(path))
            using (var context = new ChatInferenceContext(model, 64, 1))
            {
                reference = context.Generate(prompt, greedy, CancellationToken.None).Tokens;
            }

            using InProcessChatEngine engine = InProcessChatEngine.Over(ChatModel.Load(path), 64, 1, 2, new ChatServingOptions(PrefixCacheTokens: 64));
            ChatGenerationResult first = await engine.GenerateAsync(prompt, greedy, WorkloadClass.BotToBot, CancellationToken.None);
            Assert.Equal(ChatGenerationStatus.Completed, first.Status);
            Assert.Equal(reference, first.Tokens);
            Assert.True(first.TtftMs >= 0);

            ChatGenerationResult second = await engine.GenerateAsync(prompt, greedy, CancellationToken.None);
            Assert.Equal(reference, second.Tokens);
            Assert.True(second.ReusedTokens > 0); // from the slot it ran on before, or the prefix cache

            ChatGenerationResult tooLong = await engine.GenerateAsync(Prompt(62), greedy, CancellationToken.None);
            Assert.Equal(ChatGenerationStatus.ContextFull, tooLong.Status);
            Assert.Equal(ChatGenerationStatus.Overrun, (await engine.GenerateAsync(prompt, greedy with { BudgetMs = -1 }, CancellationToken.None)).Status);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.GenerateAsync(prompt, greedy, cancelled.Token));
        }
        finally
        {
            GgufBuilder.Delete(path);
        }
    }
}

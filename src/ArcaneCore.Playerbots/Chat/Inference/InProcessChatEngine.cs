using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots.Chat.Inference.Scheduling;
using ArcaneCore.World.Playerbots.Chat.Inference.Serving;

namespace ArcaneCore.World.Playerbots.Chat.Inference;

/// <summary>
/// The in-process chat model at run time: the model (its file mapped once), <see cref="Slots"/> generation slots (each a
/// <see cref="ChatInferenceContext"/>: its own KV cache and worker team) and one BelowNormal engine thread that runs the ported
/// iteration-level <see cref="InferenceScheduler"/> over them. Pure managed code: no native library, no other process.
/// <para>
/// Each engine step the scheduler plans, for every resident reply, either a chunk of prompt prefill or one decode token; the step runs
/// (in parallel across slots) and its outcome goes back to the scheduler. Requests carry a workload class:
/// <see cref="WorkloadClass.InteractivePlayer"/> (a real player addressed the bot) before <see cref="WorkloadClass.BotToBot"/> before
/// <see cref="WorkloadClass.Ambient"/> (guild chatter and announcements). A queued request of a higher class preempts a running reply
/// of a lower one when no slot is free (the preempted reply is recomputed later from its prompt and the tokens written so far), and
/// an ambient request still queued after <see cref="ChatServingOptions.AmbientQueueMs"/> is dropped: stale chatter is worth nothing.
/// A request is cancelled when its caller's token fires (its deadline, the bot leaving, a newer line superseding it).
/// </para>
/// <para>
/// The shared prefix: a slot keeps the tokens it last held and a new prompt computes only what differs; across slots, whole blocks
/// of prompt KV are kept in <see cref="KvPrefixStore"/> (the ported block cache), so the fixed system rules every bot prompt starts
/// with are computed once (<see cref="WarmPrefixAsync"/> at load) and copied, not recomputed, into whichever slot a reply lands in.
/// </para>
/// </summary>
internal sealed class InProcessChatEngine : IDisposable
{
    private const int RememberedRequests = 256;

    private readonly Slot[] _slots;
    private readonly SlotKvCapacity _capacity;
    private readonly InferenceScheduler _scheduler;
    private readonly KvPrefixStore? _prefix;
    private readonly ConcurrentQueue<Command> _inbox = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Dictionary<ulong, Request> _requests = [];
    private readonly Queue<ulong> _ended = new();
    private readonly List<Request> _scratch = [];
    private readonly StepPlan _plan = new();
    private readonly StepOutcome _outcome = new();
    private readonly CountdownEvent _stepDone = new(1);
    private readonly Thread _loop;
    private long _nextId;
    private long _lastPublish;
    private int _warmTokens;
    private ChatServingSnapshot _snapshot = ChatServingSnapshot.Empty;
    private volatile bool _stopping;
    private volatile bool _loopExited; // the engine thread has drained its inbox for the last time
    private int _disposed;

    private InProcessChatEngine(ChatModel model, int contextTokens, int threads, int slots, ChatServingOptions serving)
    {
        Model = model;
        Serving = serving;
        _slots = new Slot[slots];
        try
        {
            for (int i = 0; i < slots; i++) _slots[i] = new Slot(this, new ChatInferenceContext(model, contextTokens, threads, $"bot-chat-{i}"), i, slots > 1);
            _capacity = new SlotKvCapacity(slots, _slots[0].Context.ContextTokens);
            _scheduler = new InferenceScheduler(SchedulerConfigFor(serving, slots), new SteadyClock(), _capacity);
            if (serving.PrefixCacheTokens >= KvPrefixStore.BlockSizeTokens) _prefix = new KvPrefixStore(model, serving.PrefixCacheTokens);
        }
        catch
        {
            foreach (Slot? slot in _slots) slot?.Stop(TimeSpan.FromSeconds(5));
            throw;
        }

        _loop = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "bot-chat scheduler" };
        _loop.Start();
    }

    public ChatModel Model { get; }

    /// <summary>The scheduling settings the engine was built with.</summary>
    public ChatServingOptions Serving { get; }

    public int Slots => _slots.Length;

    public int Threads => _slots[0].Context.Threads;

    public int ContextTokens => _slots[0].Context.ContextTokens;

    /// <summary>Bytes the slots and the prefix cache allocated (caches and buffers; the weights are the mapped file).</summary>
    public long AllocatedBytes => _slots.Sum(slot => slot.Context.AllocatedBytes) + (_prefix?.Bytes ?? 0);

    /// <summary>Positions the shared prefix cache holds (0: none).</summary>
    public int PrefixCacheTokens => _prefix?.Tokens ?? 0;

    /// <summary>The scheduler and prefix-cache state as last published by the engine thread (any thread).</summary>
    public ChatServingSnapshot ServingStats => Volatile.Read(ref _snapshot);

    /// <summary>Load <paramref name="path"/> and build the slots. Throws (<see cref="InvalidDataException"/>, I/O) with the reason when the file cannot be run.</summary>
    public static InProcessChatEngine Load(string path, int contextTokens, int threads, int slots, ChatServingOptions? serving = null)
    {
        ChatModel model = ChatModel.Load(path);
        try
        {
            return new InProcessChatEngine(model, contextTokens, threads, Math.Clamp(slots, 1, 8), serving ?? ChatServingOptions.Default);
        }
        catch
        {
            model.Dispose();
            throw;
        }
    }

    /// <summary>Build an engine over an already loaded model (tests; the engine then owns it).</summary>
    internal static InProcessChatEngine Over(ChatModel model, int contextTokens, int threads, int slots, ChatServingOptions? serving = null)
        => new(model, contextTokens, threads, Math.Clamp(slots, 1, 8), serving ?? ChatServingOptions.Default);

    /// <summary>The scheduler policy for the bot chat engine: a slot per running reply, decode first, short prefill chunks.</summary>
    internal static SchedulerConfig SchedulerConfigFor(ChatServingOptions serving, int slots) => new()
    {
        // The slots are the real limit (SlotKvCapacity): at most `slots` sequences ever hold KV, so at most `slots` have work in a
        // step. One more place in each cap lets a waiting urgent request reach the capacity check (and preempt) while every slot is
        // decoding, instead of the planner stopping at the running-sequence or step-sequence cap first.
        MaxRunningSequences = (uint)slots + 1,
        MaxStepSequences = (uint)slots + 1,
        MaxStepTokens = 4096,
        PrefillChunkTokens = (uint)Math.Clamp(serving.PrefillChunkTokens, ChatInferenceContext.MaxBatch, 1024),
        MaxStepPrefillTokens = (uint)(Math.Clamp(serving.PrefillChunkTokens, ChatInferenceContext.MaxBatch, 1024) * slots),
        AdmissionWatermarkBlocks = 0,
        AdmissionLookahead = 4,
        EnablePriorityPreemption = true,
        PriorityPreemptionMinRankGap = 1, // a player's line displaces bot-to-bot and ambient work; bot-to-bot displaces ambient
        DroppableQueueTimeoutUs = Math.Max(0, serving.AmbientQueueMs) * 1000L,
        LatencySampleWindow = 1024,
    };

    /// <summary>Generate as an interactive reply (the probe and tests).</summary>
    public Task<ChatGenerationResult> GenerateAsync(IReadOnlyList<int> prompt, ChatGenerationSettings settings, CancellationToken cancellationToken)
        => GenerateAsync(prompt, settings, WorkloadClass.InteractivePlayer, cancellationToken);

    /// <summary>
    /// Queue a reply to <paramref name="prompt"/> (templated token ids) under the scheduler. <paramref name="cancellationToken"/>
    /// cancels it: while it still waits for a slot the task is cancelled (<see cref="OperationCanceledException"/>); once it has run,
    /// it ends as <see cref="ChatGenerationStatus.Cancelled"/>. The budget (<see cref="ChatGenerationSettings.BudgetMs"/>) runs from
    /// its first admission.
    /// </summary>
    public Task<ChatGenerationResult> GenerateAsync(IReadOnlyList<int> prompt, ChatGenerationSettings settings, WorkloadClass workload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_stopping) // the engine thread failed: answer at once, the next provider (the templates) takes the line
            return Task.FromResult(new ChatGenerationResult(ChatGenerationStatus.Failed, string.Empty, prompt.Count, 0, 0, 0, 0, "failed", []));
        if (prompt.Count == 0 || prompt.Count + settings.MaxNewTokens > ContextTokens)
            return Task.FromResult(new ChatGenerationResult(ChatGenerationStatus.ContextFull, string.Empty, prompt.Count, 0, 0, 0, 0, "context", []));
        int vocabulary = Model.Config.Vocabulary;
        var tokens = new List<int>(prompt.Count + settings.MaxNewTokens);
        foreach (int id in prompt)
        {
            if ((uint)id >= (uint)vocabulary) throw new ArgumentOutOfRangeException(nameof(prompt), $"token {id} is outside the vocabulary");
            tokens.Add(id);
        }

        var request = new Request((ulong)Interlocked.Increment(ref _nextId), workload, settings, tokens, cancellationToken);
        if (cancellationToken.CanBeCanceled)
            request.Registration = cancellationToken.Register(static state =>
            {
                var (engine, r) = ((InProcessChatEngine, Request))state!;
                engine.Post(new Command(CommandKind.Cancel, r, null));
            }, (this, request));
        Post(new Command(CommandKind.Submit, request, null));
        // The engine thread may have drained its inbox for the last time between the checks above and the post: nothing would ever
        // dequeue this request, so fail it here (TrySet is idempotent when the drain got there first).
        if (_loopExited || _stopping)
        {
            request.Registration.Dispose();
            request.Done.TrySetResult(new ChatGenerationResult(ChatGenerationStatus.Failed, string.Empty, prompt.Count, 0, 0, 0, 0, "failed", []));
        }

        return request.Done.Task;
    }

    /// <summary>
    /// Compute <paramref name="prefix"/> (the fixed start every bot prompt shares) once on a free slot and keep its whole blocks in the
    /// prefix cache. Returns the tokens now cached (0 when there is no prefix cache or no free slot).
    /// </summary>
    public Task<int> WarmPrefixAsync(IReadOnlyList<int> prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var warm = new WarmWork([.. prefix]);
        Post(new Command(CommandKind.Warm, null, warm));
        if (_loopExited || _stopping) warm.Done.TrySetResult(0); // posted after the last drain: nothing would dequeue it
        return warm.Done.Task;
    }

    private void Post(Command command)
    {
        _inbox.Enqueue(command);
        _wake.Release();
    }

    // --- the engine thread ----------------------------------------------------------------------------------------------------

    private void Loop()
    {
        try
        {
            while (!_stopping)
            {
                DrainInbox();
                if (_stopping) break;
                if (_scheduler.IsIdle)
                {
                    Publish(force: false);
                    _wake.Wait(1000);
                    continue;
                }

                EnforceBudgets();
                if (Step()) continue;
                _wake.Wait(5); // nothing runnable this step (every waiting request blocked): wait for a change
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The scheduler itself failed: every request fails over to the next provider (the templates), the engine stops taking work.
            FaultLedger.Shared.Record("playerbot.chat-inference", error);
            AnomalyLedger.Shared.Report("bot-chat", "inference-fault", "in-process chat engine", $"{error.GetType().Name}: {error.Message}");
            _stopping = true;
            FailAll(error);
            _loopExited = true;
            return;
        }

        FailAll(null);
        _loopExited = true;
    }

    private void DrainInbox()
    {
        while (_inbox.TryDequeue(out Command command))
        {
            switch (command.Kind)
            {
                case CommandKind.Submit:
                    Submit(command.Request!);
                    break;
                case CommandKind.Cancel:
                    if (_requests.ContainsKey(command.Request!.Id))
                    {
                        _scheduler.Cancel(command.Request.Id);
                        End(command.Request, ChatGenerationStatus.Cancelled);
                    }

                    break;
                case CommandKind.Warm:
                    Warm(command.Warm!);
                    break;
            }
        }
    }

    private void Submit(Request r)
    {
        if (r.Cancellation.IsCancellationRequested)
        {
            r.Registration.Dispose();
            r.Done.TrySetCanceled(r.Cancellation);
            return;
        }

        var spec = new RequestSpec
        {
            Id = r.Id,
            Workload = r.Workload,
            PromptTokens = (uint)r.PromptCount,
            MaxNewTokens = (uint)r.Settings.MaxNewTokens,
        };
        SubmitResult submitted = _scheduler.Submit(spec);
        _requests[r.Id] = r;
        if (!submitted.Accepted)
        {
            // Rejected requests are never tracked as live by the scheduler; only its counters saw them.
            End(r, submitted.Reason == FailureReason.NeverFits ? ChatGenerationStatus.ContextFull : ChatGenerationStatus.Failed, forget: false);
        }
    }

    private void EnforceBudgets()
    {
        _scratch.Clear();
        long now = Stopwatch.GetTimestamp();
        foreach (Request r in _requests.Values)
            if (r.AdmittedAt != 0 && Stopwatch.GetElapsedTime(r.AdmittedAt, now).TotalMilliseconds > r.Settings.BudgetMs) _scratch.Add(r);
        foreach (Request r in _scratch)
        {
            _scheduler.Cancel(r.Id);
            End(r, ChatGenerationStatus.Overrun);
        }
    }

    /// <summary>Plan, run and complete one step. False when the plan was empty.</summary>
    private bool Step()
    {
        StepPlan plan = _scheduler.PlanStep(_plan);
        foreach (ulong id in plan.Dropped)
            if (_requests.TryGetValue(id, out Request? dropped)) End(dropped, ChatGenerationStatus.Dropped);
        foreach (PreemptionEvent preempted in plan.Preempted)
        {
            if (!_requests.TryGetValue(preempted.Id, out Request? r)) continue;
            r.Slot = -1; // its KV is recomputed (or restored from the prefix cache) wherever it lands next
            r.Preemptions++;
            if (ChatServingMeters.Preemptions.Enabled) ChatServingMeters.Preemptions.Add(1, new KeyValuePair<string, object?>("workload", r.Workload.ToName()));
        }

        foreach (ulong id in plan.Failed)
            if (_requests.TryGetValue(id, out Request? failed)) End(failed, ChatGenerationStatus.Failed);
        foreach (ulong id in plan.Admitted)
            if (_requests.TryGetValue(id, out Request? admitted) && admitted.AdmittedAt == 0) admitted.AdmittedAt = Stopwatch.GetTimestamp();

        if (plan.IsEmpty)
        {
            _scheduler.CompleteStep(plan, StepOutcomeCleared());
            return false;
        }

        // Starting (or resuming) on a slot touches the shared prefix cache: done here, on this thread, before the slots run.
        foreach (ScheduledWork work in plan.Work)
        {
            Request r = _requests[work.Id];
            int slot = _capacity.SlotOf(work.Id);
            if (slot < 0) throw new InvalidOperationException($"scheduled request {work.Id} holds no slot");
            if (work.Phase == SchedulePhase.Prefill && (work.ContextOffset == 0 || r.Slot != slot)) Prepare(r, slot);
            r.Work = work;
        }

        RunWork(plan.Work);
        StepOutcome outcome = StepOutcomeCleared();
        foreach (ScheduledWork work in plan.Work)
        {
            Request r = _requests[work.Id];
            if (r.Error is not null) _scheduler.Cancel(r.Id); // CompleteStep then ignores its work
            else if (r.Stop != ChatStopReason.None) outcome.FinishedEarly.Add(r.Id);
        }

        _scheduler.CompleteStep(plan, outcome);
        foreach (ScheduledWork work in plan.Work)
        {
            if (!_requests.TryGetValue(work.Id, out Request? r)) continue;
            if (r.Error is { } error)
            {
                if (r.Slot >= 0) _slots[r.Slot].Context.Reset(); // the cache may hold half a pass
                r.Slot = -1;
                End(r, ChatGenerationStatus.Failed, error: error);
                continue;
            }

            if (r.RegisterPrefix && _prefix is not null && r.Slot >= 0)
            {
                _prefix.Register(_slots[r.Slot].Context, r.Tokens, r.PromptCount);
                r.RegisterPrefix = false;
            }

            if (_scheduler.State(r.Id) == RequestState.Completed) End(r, ChatGenerationStatus.Completed);
        }

        return true;
    }

    private StepOutcome StepOutcomeCleared()
    {
        _outcome.FinishedEarly.Clear();
        _outcome.Stalled.Clear();
        return _outcome;
    }

    /// <summary>A request starts (or resumes after preemption) on <paramref name="slot"/>: reuse what the slot or the prefix cache holds, rebuild its sampling state.</summary>
    private void Prepare(Request r, int slot)
    {
        ChatInferenceContext context = _slots[slot].Context;
        int have = context.KeepCommonPrefix(r.Tokens);
        if (_prefix is not null)
        {
            int restored = _prefix.Restore(context, r.Tokens, have);
            if (restored > have && ChatServingMeters.PrefixTokens.Enabled) ChatServingMeters.PrefixTokens.Add(restored - have);
            have = restored;
        }

        if (!r.Started)
        {
            r.Reused = have;
            r.RegisterPrefix = true;
            r.Started = true;
        }

        r.Slot = slot;
        ReadOnlySpan<int> all = CollectionsMarshal.AsSpan(r.Tokens);
        context.Decoder.Begin(r.Settings, all[..r.PromptCount], all[r.PromptCount..]);
    }

    private void RunWork(List<ScheduledWork> work)
    {
        if (work.Count == 1)
        {
            Execute(_requests[work[0].Id]);
            return;
        }

        _stepDone.Reset(work.Count - 1);
        for (int i = 1; i < work.Count; i++)
        {
            Request r = _requests[work[i].Id];
            _slots[r.Slot].Post(r);
        }

        Execute(_requests[work[0].Id]);
        _stepDone.Wait();
    }

    /// <summary>Run one request's planned work on its slot (the engine thread, or the slot's own thread when several run at once).</summary>
    private void Execute(Request r)
    {
        try
        {
            ChatInferenceContext context = _slots[r.Slot].Context;
            ScheduledWork work = r.Work;
            long started = Stopwatch.GetTimestamp();
            if (work.Phase == SchedulePhase.Prefill)
            {
                int from = context.CachedCount;
                int to = (int)(work.ContextOffset + work.NumTokens);
                if (from < to) context.Prefill(r.Tokens, from, to, wantLogits: work.CompletesPrefill);
                r.PrefillMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (work.CompletesPrefill) Next(r, context);
            }
            else
            {
                if (context.CachedCount != r.Tokens.Count - 1)
                    throw new InvalidOperationException($"slot holds {context.CachedCount} tokens for a sequence of {r.Tokens.Count}");
                context.Decode(r.Tokens[^1]);
                Next(r, context);
                r.DecodeMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.chat-inference", error);
            AnomalyLedger.Shared.Report("bot-chat", "inference-fault", "in-process chat engine", $"{error.GetType().Name}: {error.Message}");
            r.Error = error;
        }
    }

    private static void Next(Request r, ChatInferenceContext context)
    {
        ChatReplyDecoder decoder = context.Decoder;
        int token = decoder.Choose(context.Logits, out ChatStopReason stop);
        if (stop != ChatStopReason.None)
        {
            r.Stop = stop;
            return;
        }

        r.Tokens.Add(token);
        r.Stop = decoder.Commit(token);
    }

    private void Warm(WarmWork warm)
    {
        int done = 0;
        try
        {
            int slot = -1;
            for (int i = 0; i < _slots.Length && slot < 0; i++)
                if (_capacity.IsFree(i)) slot = i;
            if (_prefix is not null && slot >= 0 && warm.Tokens.Count >= KvPrefixStore.BlockSizeTokens && warm.Tokens.Count < ContextTokens)
            {
                ChatInferenceContext context = _slots[slot].Context;
                warm.Tokens.Add(0); // a stand-in last token: everything before it may be kept (the last is never reused)
                int have = context.KeepCommonPrefix(warm.Tokens);
                warm.Tokens.RemoveAt(warm.Tokens.Count - 1);
                if (have < warm.Tokens.Count) context.Prefill(warm.Tokens, have, warm.Tokens.Count, wantLogits: false);
                _prefix.Register(context, warm.Tokens, warm.Tokens.Count);
                done = warm.Tokens.Count / (int)KvPrefixStore.BlockSizeTokens * (int)KvPrefixStore.BlockSizeTokens;
                _warmTokens = done;
            }

            warm.Done.TrySetResult(done);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.chat-inference", error);
            AnomalyLedger.Shared.Report("bot-chat", "inference-fault", "in-process chat engine", $"{error.GetType().Name}: {error.Message}");
            warm.Done.TrySetException(error);
        }

        Publish(force: true);
    }

    private void End(Request r, ChatGenerationStatus status, bool forget = true, Exception? error = null)
    {
        if (!_requests.Remove(r.Id)) return;
        r.Registration.Dispose();
        if (forget) Remember(r.Id);
        if (error is not null)
        {
            r.Done.TrySetException(error);
            Publish(force: true);
            return;
        }

        if (status == ChatGenerationStatus.Cancelled && r.AdmittedAt == 0 && r.Cancellation.IsCancellationRequested)
        {
            r.Done.TrySetCanceled(r.Cancellation); // never started: as before, the wait itself is cancelled
            Publish(force: true);
            return;
        }

        RequestTimeline? timeline = _scheduler.Timeline(r.Id);
        double queueMs = 0, ttftMs = 0, itlMs = 0;
        int generated = r.Tokens.Count - r.PromptCount;
        if (timeline is { } t)
        {
            queueMs = t.TotalQueueTime / 1000.0;
            ttftMs = t.Ttft >= 0 ? t.Ttft / 1000.0 : 0;
            if (t.FirstToken >= 0 && t.Finished > t.FirstToken && t.GeneratedTokens > 1) itlMs = (t.Finished - t.FirstToken) / 1000.0 / (t.GeneratedTokens - 1);
        }

        string text = string.Empty;
        string reason = status switch
        {
            ChatGenerationStatus.Overrun => "budget",
            ChatGenerationStatus.Cancelled => "cancelled",
            ChatGenerationStatus.ContextFull => "context",
            ChatGenerationStatus.Dropped => "dropped",
            ChatGenerationStatus.Failed => "failed",
            _ => ChatInferenceContext.StopText(r.Stop),
        };
        if (status == ChatGenerationStatus.Completed && r.Slot >= 0) text = _slots[r.Slot].Context.Decoder.Text();
        if (status == ChatGenerationStatus.Completed)
        {
            var tag = new KeyValuePair<string, object?>("workload", r.Workload.ToName());
            if (ChatServingMeters.TimeToFirstToken.Enabled) ChatServingMeters.TimeToFirstToken.Record(ttftMs, tag);
            if (ChatServingMeters.QueueTime.Enabled) ChatServingMeters.QueueTime.Record(queueMs, tag);
            if (itlMs > 0 && ChatServingMeters.InterToken.Enabled) ChatServingMeters.InterToken.Record(itlMs, tag);
        }
        else if (status == ChatGenerationStatus.Dropped && ChatServingMeters.Dropped.Enabled)
        {
            ChatServingMeters.Dropped.Add(1, new KeyValuePair<string, object?>("workload", r.Workload.ToName()));
        }

        IReadOnlyList<int> tokens = generated > 0 ? r.Tokens.GetRange(r.PromptCount, generated) : [];
        r.Done.TrySetResult(new ChatGenerationResult(status, text, r.PromptCount, r.Reused, generated, r.PrefillMs, r.DecodeMs, reason, tokens,
            queueMs, ttftMs, itlMs, r.Preemptions));
        Publish(force: true);
    }

    /// <summary>Keep the last <see cref="RememberedRequests"/> ended requests in the scheduler (its latency percentiles), forget older ones.</summary>
    private void Remember(ulong id)
    {
        _ended.Enqueue(id);
        while (_ended.Count > RememberedRequests) _scheduler.Forget(_ended.Dequeue());
    }

    private void Publish(bool force)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force && Stopwatch.GetElapsedTime(_lastPublish, now).TotalSeconds < 1) return;
        _lastPublish = now;
        Volatile.Write(ref _snapshot, ChatServingSnapshot.From(_scheduler.Stats(), _prefix, _warmTokens));
    }

    private void FailAll(Exception? error)
    {
        foreach (Request r in _requests.Values)
        {
            r.Registration.Dispose();
            if (error is null) r.Done.TrySetCanceled();
            else r.Done.TrySetException(error);
        }

        _requests.Clear();
        while (_inbox.TryDequeue(out Command command))
        {
            command.Request?.Registration.Dispose();
            command.Request?.Done.TrySetCanceled();
            command.Warm?.Done.TrySetResult(0);
        }
    }

    /// <summary>Stop the engine and slot threads; the file is unmapped only when every thread has stopped (otherwise it is left mapped).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping = true;
        _wake.Release();
        bool stopped = _loop.Join(TimeSpan.FromSeconds(10));
        foreach (Slot slot in _slots) stopped &= slot.Stop(TimeSpan.FromSeconds(10));
        if (!stopped) return;
        Model.Dispose();
        _stepDone.Dispose();
        _wake.Dispose();
    }

    private enum CommandKind : byte
    {
        Submit,
        Cancel,
        Warm,
    }

    private readonly record struct Command(CommandKind Kind, Request? Request, WarmWork? Warm);

    private sealed class WarmWork(List<int> tokens)
    {
        public List<int> Tokens { get; } = tokens;

        public TaskCompletionSource<int> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>One reply in the engine: its tokens (prompt, then what it wrote), its slot while resident and its timings.</summary>
    private sealed class Request(ulong id, WorkloadClass workload, ChatGenerationSettings settings, List<int> tokens, CancellationToken cancellation)
    {
        public ulong Id { get; } = id;

        public WorkloadClass Workload { get; } = workload;

        public ChatGenerationSettings Settings { get; } = settings;

        public List<int> Tokens { get; } = tokens;

        public int PromptCount { get; } = tokens.Count;

        public CancellationToken Cancellation { get; } = cancellation;

        public CancellationTokenRegistration Registration { get; set; }

        public TaskCompletionSource<ChatGenerationResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Slot { get; set; } = -1;

        public ScheduledWork Work { get; set; }

        public bool Started { get; set; }

        public bool RegisterPrefix { get; set; }

        public int Reused { get; set; }

        public int Preemptions { get; set; }

        public long AdmittedAt { get; set; }

        public double PrefillMs { get; set; }

        public double DecodeMs { get; set; }

        public ChatStopReason Stop { get; set; }

        public Exception? Error { get; set; }
    }

    /// <summary>A generation slot: its context and, when several slots run in one step, its own thread.</summary>
    private sealed class Slot
    {
        private readonly InProcessChatEngine _engine;
        private readonly Thread? _thread;
        private readonly SemaphoreSlim _signal = new(0);
        private Request? _work;
        private volatile bool _stopping;

        public Slot(InProcessChatEngine engine, ChatInferenceContext context, int index, bool threaded)
        {
            _engine = engine;
            Context = context;
            if (!threaded) return;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = $"bot-chat-{index} generate",
            };
            _thread.Start();
        }

        public ChatInferenceContext Context { get; }

        public void Post(Request request)
        {
            Volatile.Write(ref _work, request);
            _signal.Release();
        }

        private void Loop()
        {
            while (true)
            {
                _signal.Wait();
                if (_stopping) return;
                Request? work = Interlocked.Exchange(ref _work, null);
                if (work is null) continue;
                try
                {
                    _engine.Execute(work);
                }
                finally
                {
                    _engine._stepDone.Signal();
                }
            }
        }

        public bool Stop(TimeSpan timeout)
        {
            _stopping = true;
            _signal.Release();
            bool joined = _thread?.Join(timeout) ?? true;
            if (joined) Context.Dispose();
            return joined;
        }
    }
}

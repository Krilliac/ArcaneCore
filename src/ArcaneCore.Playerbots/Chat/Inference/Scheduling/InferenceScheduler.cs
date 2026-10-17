// Ported from Krilliac/Sonder-Inference src/scheduler/include/sonder/inference/scheduler/scheduler.hpp @1c4c152 (Apache-2.0)
// Ported from Krilliac/Sonder-Inference src/scheduler/src/scheduler.cpp @1c4c152 (Apache-2.0)
//
// Iteration-level scheduler: priority request queue, continuous batching step planner, chunked
// prefill, KV admission control, preemption and fairness.
//
// Usage per engine step:
//     StepPlan plan = scheduler.PlanStep();
//     ... backend executes plan.Work (outside this module) ...
//     clock advances by the step duration
//     scheduler.CompleteStep(plan, outcome);
//
// Exactly one plan may be in flight at a time. Not thread-safe: the owning engine loop
// serialises access.
//
// ArcaneCore additions over the source: three bot chat workload classes and a queue deadline for
// droppable (Ambient) work, which is dropped rather than starvation-promoted.

namespace ArcaneCore.World.Playerbots.Chat.Inference.Scheduling;

/// <summary>One sequence's work in a step.</summary>
public readonly record struct ScheduledWork(
    ulong Id,
    SchedulePhase Phase,
    uint NumTokens,
    uint ContextOffset,
    bool CompletesPrefill)
{
    // NumTokens: a prefill chunk, or 1 for decode.
    // ContextOffset: tokens already holding KV before this work (position offset).
    // CompletesPrefill: this chunk finishes prefill (and so yields the first token).
}

public readonly record struct PreemptionEvent(
    ulong Id,
    PreemptionReason Reason,
    PreemptionMode Mode,
    uint KvTokens,
    ulong Beneficiary,
    bool Failed)
{
    // KvTokens: KV tokens discarded (recompute) or moved (swap).
    // Beneficiary: request whose progress required the preemption (0 if none).
    // Failed: the victim hit the requeue limit and failed instead of requeueing.
}

public sealed class StepPlan
{
    public ulong StepIndex { get; set; }

    public long PlannedAt { get; set; }

    public List<ScheduledWork> Work { get; } = [];

    public List<ulong> Admitted { get; } = [];

    public List<PreemptionEvent> Preempted { get; } = [];

    public List<ulong> Failed { get; } = [];

    /// <summary>Droppable requests dropped at the start of this step (queue deadline).</summary>
    public List<ulong> Dropped { get; } = [];

    public uint PrefillTokens { get; set; }

    public uint DecodeTokens { get; set; }

    public uint TotalTokens => PrefillTokens + DecodeTokens;

    public bool IsEmpty => Work.Count == 0;

    /// <summary>ArcaneCore addition: empty the plan for reuse (<see cref="InferenceScheduler.PlanStep(StepPlan)"/>).</summary>
    internal void Clear(ulong stepIndex, long plannedAt)
    {
        StepIndex = stepIndex;
        PlannedAt = plannedAt;
        Work.Clear();
        Admitted.Clear();
        Preempted.Clear();
        Failed.Clear();
        Dropped.Clear();
        PrefillTokens = 0;
        DecodeTokens = 0;
    }
}

/// <summary>Backend feedback for a completed step.</summary>
public sealed class StepOutcome
{
    /// <summary>Sequences that emitted an end-of-sequence token this step.</summary>
    public List<ulong> FinishedEarly { get; } = [];

    /// <summary>
    /// Sequences that did not execute their planned work this step (the backend had not produced
    /// the token yet when the engine closed the step). Their work is not accounted: no prefill
    /// progress, no KV growth, no token. The next plan schedules them again.
    /// </summary>
    public List<ulong> Stalled { get; } = [];

    internal static StepOutcome Empty { get; } = new();
}

public readonly record struct SubmitResult(bool Accepted, FailureReason Reason);

public sealed class InferenceScheduler
{
    private const int StarvingRank = int.MinValue / 2;

    private readonly SchedulerConfig _config;
    private readonly ISchedulerClock _clock;
    private readonly IKvCapacity _kv;

    private readonly Dictionary<ulong, Sequence> _seqs = [];
    private readonly List<ulong> _waiting = [];
    private readonly List<ulong> _running = [];
    private readonly Dictionary<string, int> _activeFingerprints = new(StringComparer.Ordinal);
    private ulong? _inFlight;
    private ulong _nextSubmitOrder;

    // Live counters.
    private readonly SchedulerStats _counters = new();

    // Most recent inter-token gaps per class (ring of LatencySampleWindow).
    private readonly List<long>[] _interTokenSamples;
    private readonly int[] _interTokenNext = new int[SchedulerNames.WorkloadClassCount];

    // Reusable scratch buffers so planning a step does not allocate per sequence.
    private readonly List<ulong> _sortedRunning = [];
    private readonly List<ulong> _sortedWaiting = [];
    private readonly List<ulong> _victimOrder = [];
    private readonly List<ulong> _victims = [];
    private readonly List<ulong> _dropScratch = [];
    private readonly Comparison<ulong> _runningComparison;
    private readonly Comparison<ulong> _waitingComparison;
    private long _sortNow;

    public InferenceScheduler(SchedulerConfig config, ISchedulerClock clock, IKvCapacity kv)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(kv);
        _config = config.Clone();
        _clock = clock;
        _kv = kv;
        if (_config.MaxStepTokens == 0 || _config.MaxStepSequences == 0 ||
            _config.MaxRunningSequences == 0 || _config.PrefillChunkTokens == 0)
        {
            throw new ArgumentException("SchedulerConfig budgets must be non-zero", nameof(config));
        }

        if (_config.AgingIntervalUs <= 0)
        {
            throw new ArgumentException("SchedulerConfig.AgingIntervalUs must be positive", nameof(config));
        }

        if (_kv.BlockSizeTokens == 0)
        {
            throw new ArgumentException("KV capacity block size must be non-zero", nameof(kv));
        }

        _interTokenSamples = new List<long>[SchedulerNames.WorkloadClassCount];
        for (int i = 0; i < _interTokenSamples.Length; i++)
        {
            _interTokenSamples[i] = [];
        }

        _runningComparison = CompareRunning;
        _waitingComparison = CompareWaiting;
    }

    public SchedulerConfig Config => _config;

    /// <summary>Requests currently tracked (live plus terminal, not yet forgotten).</summary>
    public int Tracked => _seqs.Count;

    /// <summary>True when nothing is waiting or running.</summary>
    public bool IsIdle => _waiting.Count == 0 && _running.Count == 0;

    public bool PlanInFlight => _inFlight.HasValue;

    public IReadOnlyList<ulong> Running => _running;

    // ---------------------------------------------------------------- submission

    /// <summary>Enqueue a request. Rejected requests are counted but never scheduled.</summary>
    public SubmitResult Submit(RequestSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        long now = _clock.NowUs;
        ref ClassStats cls = ref _counters.For(spec.Workload);
        cls.Submitted++;

        if (_seqs.ContainsKey(spec.Id))
        {
            return Reject(ref cls, FailureReason.DuplicateId);
        }

        if (spec.PromptTokens == 0 || spec.MaxNewTokens == 0 ||
            (ulong)spec.PromptTokens + spec.MaxNewTokens > uint.MaxValue)
        {
            return Reject(ref cls, FailureReason.InvalidRequest);
        }

        ulong worstCase = KvBlocks.ForTokens(spec.PromptTokens + spec.MaxNewTokens, _kv.BlockSizeTokens);
        if (worstCase > _kv.TotalBlocks)
        {
            return Reject(ref cls, FailureReason.NeverFits);
        }

        if (spec.SequenceFingerprint.Length != 0 && !spec.AllowDuplicate &&
            _activeFingerprints.ContainsKey(spec.SequenceFingerprint))
        {
            return Reject(ref cls, FailureReason.DuplicateSequence);
        }

        RequestSpec own = spec.Clone();
        var s = new Sequence(own)
        {
            BaseRank = own.PriorityOverride ?? (int)own.Workload,
            SubmitOrder = _nextSubmitOrder++,
            EnqueuedAt = now,
            PrefillTarget = own.PromptTokens,
            State = RequestState.WaitingAdmission,
        };
        s.Tl.Id = own.Id;
        s.Tl.Workload = own.Workload;
        s.Tl.Arrival = now;
        s.Tl.FinalState = RequestState.WaitingAdmission;
        if (own.SequenceFingerprint.Length != 0)
        {
            _activeFingerprints[own.SequenceFingerprint] = _activeFingerprints.GetValueOrDefault(own.SequenceFingerprint) + 1;
        }

        _seqs.Add(own.Id, s);
        _waiting.Add(own.Id);
        return new SubmitResult(true, FailureReason.None);

        static SubmitResult Reject(ref ClassStats cls, FailureReason why)
        {
            cls.Rejected++;
            return new SubmitResult(false, why);
        }
    }

    /// <summary>
    /// Cancel a queued or running request, releasing its KV immediately. Returns false for
    /// unknown, terminal, or non-cancellable requests.
    /// </summary>
    public bool Cancel(ulong id)
    {
        if (!_seqs.TryGetValue(id, out Sequence? s) || s.State.IsTerminal() || !s.Spec.Cancellable)
        {
            return false;
        }

        Finish(s, RequestState.Cancelled, FailureReason.None, _clock.NowUs);
        return true;
    }

    /// <summary>
    /// Drop a terminal request's bookkeeping. Engines that run indefinitely call this once they no
    /// longer need the request, so memory stays bounded by live requests. Counters in
    /// <see cref="Stats"/> are unaffected; per-request latency summaries then cover only requests
    /// not yet forgotten.
    /// </summary>
    public bool Forget(ulong id)
    {
        if (!_seqs.TryGetValue(id, out Sequence? s) || !s.State.IsTerminal())
        {
            return false;
        }

        _seqs.Remove(id);
        return true;
    }

    // ---------------------------------------------------------------- ordering

    private bool IsStarving(Sequence s, long now) =>
        !_config.StrictPriorityAdmission && now - s.EnqueuedAt >= _config.StarvationThresholdUs;

    private int WaitingRank(Sequence s, long now)
    {
        if (_config.StrictPriorityAdmission)
        {
            return s.BaseRank;
        }

        if (IsStarving(s, now))
        {
            return StarvingRank;
        }

        long age = Math.Max(0, now - s.EnqueuedAt);
        int boost = (int)Math.Min(age / _config.AgingIntervalUs, int.MaxValue / 4);
        return s.BaseRank - boost;
    }

    // Better first: lower rank, then older submission (the newest is the preemption victim).
    private int CompareRunning(ulong a, ulong b)
    {
        Sequence sa = _seqs[a];
        Sequence sb = _seqs[b];
        if (sa.BaseRank != sb.BaseRank)
        {
            return sa.BaseRank.CompareTo(sb.BaseRank);
        }

        return sa.SubmitOrder.CompareTo(sb.SubmitOrder);
    }

    private int CompareWaiting(ulong a, ulong b)
    {
        Sequence sa = _seqs[a];
        Sequence sb = _seqs[b];
        int ra = WaitingRank(sa, _sortNow);
        int rb = WaitingRank(sb, _sortNow);
        if (ra != rb)
        {
            return ra.CompareTo(rb);
        }

        if (_config.StrictPriorityAdmission)
        {
            int c = (sa.Spec.AdmissionOrder ?? sa.SubmitOrder).CompareTo(sb.Spec.AdmissionOrder ?? sb.SubmitOrder);
            return c != 0 ? c : sa.SubmitOrder.CompareTo(sb.SubmitOrder);
        }

        if (sa.BaseRank != sb.BaseRank)
        {
            return sa.BaseRank.CompareTo(sb.BaseRank);
        }

        if (sa.EnqueuedAt != sb.EnqueuedAt)
        {
            return sa.EnqueuedAt.CompareTo(sb.EnqueuedAt);
        }

        return sa.SubmitOrder.CompareTo(sb.SubmitOrder);
    }

    // Both comparisons end on the unique submit order, so List.Sort (unstable) is deterministic.
    private void SortWaitingInto(List<ulong> into, long now)
    {
        into.Clear();
        into.AddRange(_waiting);
        _sortNow = now;
        into.Sort(_waitingComparison);
    }

    private void SortRunningInto(List<ulong> into)
    {
        into.Clear();
        into.AddRange(_running);
        into.Sort(_runningComparison);
    }

    private ulong WorstRunning()
    {
        ulong worst = _running[0];
        for (int i = 1; i < _running.Count; i++)
        {
            if (CompareRunning(_running[i], worst) > 0)
            {
                worst = _running[i];
            }
        }

        return worst;
    }

    /// <summary>
    /// Effective queue rank of a waiting request (lower = sooner), including aging. Null if the
    /// request is not waiting.
    /// </summary>
    public int? EffectiveRank(ulong id)
    {
        if (!_seqs.TryGetValue(id, out Sequence? s) || !_waiting.Contains(id))
        {
            return null;
        }

        return WaitingRank(s, _clock.NowUs);
    }

    /// <summary>Waiting requests in the order admission would consider them.</summary>
    public List<ulong> QueueOrder()
    {
        var order = new List<ulong>(_waiting.Count);
        SortWaitingInto(order, _clock.NowUs);
        return order;
    }

    /// <summary>Pure decision stub: how a victim would be preempted right now.</summary>
    public PreemptionMode DecidePreemptionMode(uint kvTokens)
    {
        // Stub policy: swapping large contexts is assumed cheaper than recompute. A real cost
        // model (transfer bandwidth vs prefill throughput) belongs here once the cache exposes
        // spill tiers.
        return _config.EnableSwap && kvTokens >= _config.SwapMinTokens ? PreemptionMode.Swap : PreemptionMode.Recompute;
    }

    // ---------------------------------------------------------------- helpers

    private static void AddWork(StepPlan plan, ref Budget budget, Sequence s, SchedulePhase phase, uint n)
    {
        bool completes = phase == SchedulePhase.Prefill && s.PrefillDone + n == s.PrefillTarget;
        plan.Work.Add(new ScheduledWork(s.Spec.Id, phase, n, s.KvTokens, completes));
        budget.Tokens -= n;
        budget.Sequences -= 1;
        if (phase == SchedulePhase.Prefill)
        {
            budget.PrefillTokens -= n;
            plan.PrefillTokens += n;
        }
        else
        {
            plan.DecodeTokens += n;
        }
    }

    private static void RemoveWork(StepPlan plan, ref Budget budget, ulong id)
    {
        int index = plan.Work.FindIndex(w => w.Id == id);
        if (index < 0)
        {
            return;
        }

        ScheduledWork w = plan.Work[index];
        budget.Tokens += w.NumTokens;
        budget.Sequences += 1;
        if (w.Phase == SchedulePhase.Prefill)
        {
            budget.PrefillTokens += w.NumTokens;
            plan.PrefillTokens -= w.NumTokens;
        }
        else
        {
            plan.DecodeTokens -= w.NumTokens;
        }

        plan.Work.RemoveAt(index);
    }

    private void Finish(Sequence s, RequestState terminal, FailureReason why, long now)
    {
        ulong id = s.Spec.Id;
        _kv.Release(id);
        _waiting.Remove(id);
        _running.Remove(id);
        s.State = terminal;
        s.Tl.FinalState = terminal;
        s.Tl.Failure = why;
        s.Tl.Finished = now;
        string fp = s.Spec.SequenceFingerprint;
        if (fp.Length != 0 && _activeFingerprints.TryGetValue(fp, out int users))
        {
            // Only drop the fingerprint once no other active request still uses it.
            if (users <= 1)
            {
                _activeFingerprints.Remove(fp);
            }
            else
            {
                _activeFingerprints[fp] = users - 1;
            }
        }

        ref ClassStats cs = ref _counters.For(s.Spec.Workload);
        switch (terminal)
        {
            case RequestState.Completed: cs.Completed++; break;
            case RequestState.Cancelled: cs.Cancelled++; break;
            case RequestState.Failed: cs.Failed++; break;
            case RequestState.Dropped: cs.Dropped++; _counters.QueueTimeoutDrops++; break;
            default: break;
        }
    }

    private void EmitToken(Sequence s, long now)
    {
        s.Generated++;
        s.Tl.GeneratedTokens = s.Generated;
        int cls = (int)s.Spec.Workload;
        _counters.PerClass[cls].GeneratedTokens++;
        if (s.Tl.FirstToken < 0)
        {
            s.Tl.FirstToken = now;
        }
        else if (s.LastTokenTime >= 0)
        {
            long gap = now - s.LastTokenTime;
            s.Tl.MaxInterTokenGap = Math.Max(s.Tl.MaxInterTokenGap, gap);
            List<long> samples = _interTokenSamples[cls];
            int window = (int)Math.Min(_config.LatencySampleWindow, int.MaxValue);
            if (window == 0)
            {
                // Statistics disabled: keep nothing.
            }
            else if (samples.Count < window)
            {
                samples.Add(gap);
            }
            else
            {
                int next = _interTokenNext[cls];
                samples[next % window] = gap;
                _interTokenNext[cls] = (next + 1) % window;
            }
        }

        s.LastTokenTime = now;
    }

    private void Preempt(StepPlan plan, ref Budget budget, ulong victim, PreemptionReason reason, ulong beneficiary, long now)
    {
        Sequence s = _seqs[victim];
        RemoveWork(plan, ref budget, victim);

        PreemptionMode mode = DecidePreemptionMode(s.KvTokens);
        uint kvTokens = s.KvTokens;

        _kv.Release(victim);
        _running.Remove(victim);

        if (reason == PreemptionReason.KvPressure)
        {
            _counters.PreemptionsKvPressure++;
        }
        else
        {
            _counters.PreemptionsPriority++;
        }

        _counters.For(s.Spec.Workload).Preemptions++;
        s.Tl.Preemptions++;

        if (mode == PreemptionMode.Swap)
        {
            _counters.PreemptionsSwap++;
            _counters.SwappedTokens += s.KvTokens;
            s.Swapped = true; // KV logically retained in a lower tier
        }
        else
        {
            _counters.PreemptionsRecompute++;
            _counters.RecomputedTokens += s.KvTokens;
            s.Swapped = false;
            // Recompute prompt plus everything generated so far; the chunk that completes this
            // prefill yields the next token.
            s.PrefillTarget = s.Spec.PromptTokens + s.Generated;
            s.PrefillDone = 0;
            s.KvTokens = 0;
        }

        s.Requeues++;
        bool failed = false;
        if (s.Requeues > _config.MaxRequeueCount)
        {
            failed = true;
            plan.Failed.Add(victim);
            Finish(s, RequestState.Failed, FailureReason.RequeueLimit, now);
        }
        else
        {
            s.State = RequestState.Preempted;
            s.EnqueuedAt = now;
            s.StarvingCounted = false;
            _waiting.Add(victim);
        }

        plan.Preempted.Add(new PreemptionEvent(victim, reason, mode, kvTokens, beneficiary, failed));
    }

    // ---------------------------------------------------------------- planning

    private void ScheduleDecode(StepPlan plan, ref Budget budget, long now)
    {
        uint bs = _kv.BlockSizeTokens;
        SortRunningInto(_sortedRunning);
        foreach (ulong id in _sortedRunning)
        {
            if (!_seqs.TryGetValue(id, out Sequence? s) || s.State != RequestState.Decode)
            {
                continue;
            }

            if (!_running.Contains(id))
            {
                continue;
            }

            if (budget.Sequences == 0 || budget.Tokens == 0)
            {
                break;
            }

            bool selfPreempted = false;
            while (true)
            {
                ulong want = KvBlocks.ForTokens(s.KvTokens + 1, bs);
                ulong held = _kv.BlocksHeld(id);
                if (want <= held || _kv.TryReserve(id, want - held))
                {
                    break;
                }

                // Out of KV: preempt the lowest-priority running sequence (possibly this one).
                // Newest, least urgent work yields first.
                ulong victim = WorstRunning();
                Preempt(plan, ref budget, victim, PreemptionReason.KvPressure, id, now);
                if (victim == id)
                {
                    selfPreempted = true;
                    break;
                }
            }

            if (selfPreempted || s.State != RequestState.Decode)
            {
                continue;
            }

            AddWork(plan, ref budget, s, SchedulePhase.Decode, 1);
        }
    }

    private void SchedulePrefill(StepPlan plan, ref Budget budget)
    {
        SortRunningInto(_sortedRunning);
        foreach (ulong id in _sortedRunning)
        {
            Sequence s = _seqs[id];
            if (s.State != RequestState.Prefill)
            {
                continue;
            }

            if (budget.Sequences == 0)
            {
                break;
            }

            uint remaining = s.PrefillTarget - s.PrefillDone;
            uint chunk = Min(_config.PrefillChunkTokens, remaining, budget.PrefillTokens, budget.Tokens);
            if (chunk == 0)
            {
                break;
            }

            AddWork(plan, ref budget, s, SchedulePhase.Prefill, chunk);
        }
    }

    private bool TryPriorityPreemption(StepPlan plan, ref Budget budget, Sequence cand, ulong need, long now)
    {
        if (_config.StrictPriorityAdmission || !_config.EnablePriorityPreemption)
        {
            return false;
        }

        int candRank = cand.BaseRank;
        SortRunningInto(_victimOrder);
        _victims.Clear();
        ulong freeable = _kv.FreeBlocks;
        ulong target = need + _config.AdmissionWatermarkBlocks;
        for (int i = _victimOrder.Count - 1; i >= 0 && freeable < target; i--)
        {
            Sequence v = _seqs[_victimOrder[i]];
            if (v.BaseRank - candRank < _config.PriorityPreemptionMinRankGap)
            {
                break;
            }

            _victims.Add(_victimOrder[i]);
            freeable += _kv.BlocksHeld(_victimOrder[i]);
        }

        if (freeable < target)
        {
            return false; // would not help; preempt nothing
        }

        foreach (ulong v in _victims)
        {
            Preempt(plan, ref budget, v, PreemptionReason.PriorityAdmission, cand.Spec.Id, now);
        }

        return true;
    }

    private void Admit(StepPlan plan, ref Budget budget, long now)
    {
        uint bs = _kv.BlockSizeTokens;
        bool headBlocked = false;
        uint bypassed = 0;

        SortWaitingInto(_sortedWaiting, now);
        foreach (ulong id in _sortedWaiting)
        {
            if (_running.Count >= _config.MaxRunningSequences)
            {
                break;
            }

            if (budget.Sequences == 0 || budget.Tokens == 0)
            {
                break;
            }

            if (!_seqs.TryGetValue(id, out Sequence? s) || s.State.IsTerminal())
            {
                continue;
            }

            if (s.Spec.AdmissionReady is { } ready && !ready())
            {
                continue;
            }

            bool resumesDecode = s.Swapped && s.PrefillDone == s.PrefillTarget;
            if (!resumesDecode && budget.PrefillTokens == 0)
            {
                break;
            }

            // Reserve the whole (re)prefill context up front; decode growth is reserved
            // block-by-block, protected by the admission watermark.
            uint ctx = _config.StrictPriorityAdmission
                ? s.Spec.PromptTokens + s.Spec.MaxNewTokens
                : (s.Swapped ? Math.Max(s.KvTokens, s.PrefillTarget) : s.PrefillTarget);
            ulong need = KvBlocks.ForTokens(ctx, bs);
            ulong watermark = _config.StrictPriorityAdmission
                ? 0
                : (_running.Count == 0 ? 0 : _config.AdmissionWatermarkBlocks);
            bool fits = _kv.FreeBlocks >= need + watermark;
            if (!fits && TryPriorityPreemption(plan, ref budget, s, need, now))
            {
                fits = _kv.FreeBlocks >= need + (_running.Count == 0 ? 0 : watermark);
            }

            if (fits && _kv.TryReserve(id, need))
            {
                if (s.Spec.TryAdmit is { } tryAdmit && !tryAdmit())
                {
                    _kv.Release(id);
                    // A class at its concurrency cap must not stall another eligible class.
                    continue;
                }
            }
            else if (fits)
            {
                fits = false;
            }

            if (!fits)
            {
                if (!headBlocked)
                {
                    headBlocked = true;
                    _counters.AdmissionKvBlocked++;
                    // Never bypass a starving or top-urgency request.
                    if (IsStarving(s, now) || s.BaseRank <= 0)
                    {
                        break;
                    }
                }

                if (_config.StrictPriorityAdmission || ++bypassed > _config.AdmissionLookahead)
                {
                    break;
                }

                continue;
            }

            if (headBlocked)
            {
                _counters.LookaheadAdmissions++;
            }

            _waiting.Remove(id);
            _running.Add(id);
            if (s.Tl.FirstAdmitted < 0)
            {
                s.Tl.FirstAdmitted = now;
            }

            s.Tl.TotalQueueTime += now - s.EnqueuedAt;
            plan.Admitted.Add(id);

            if (resumesDecode)
            {
                s.State = RequestState.Decode; // swap-in handled by the engine
                s.Swapped = false;
                continue; // decodes next step
            }

            s.Swapped = false;
            s.State = RequestState.Prefill;
            uint chunk = Min(_config.PrefillChunkTokens, s.PrefillTarget - s.PrefillDone, budget.PrefillTokens, budget.Tokens);
            if (chunk > 0)
            {
                AddWork(plan, ref budget, s, SchedulePhase.Prefill, chunk);
            }
        }
    }

    // ArcaneCore addition: drop droppable work that was never admitted and has waited too long.
    private void DropExpired(StepPlan plan, long now)
    {
        long timeout = _config.DroppableQueueTimeoutUs;
        if (timeout <= 0)
        {
            return;
        }

        _dropScratch.Clear();
        foreach (ulong id in _waiting)
        {
            Sequence s = _seqs[id];
            if (s.Spec.IsDroppable && s.Tl.FirstAdmitted < 0 && now - s.Tl.Arrival >= timeout)
            {
                _dropScratch.Add(id);
            }
        }

        foreach (ulong id in _dropScratch)
        {
            Finish(_seqs[id], RequestState.Dropped, FailureReason.QueueTimeout, now);
            plan.Dropped.Add(id);
        }
    }

    /// <summary>Plan the next engine step. Throws if a plan is already in flight.</summary>
    public StepPlan PlanStep() => PlanStep(new StepPlan());

    /// <summary>
    /// ArcaneCore addition: plan the next step into <paramref name="reuse"/> (cleared first), so a long-running engine loop allocates
    /// no plan per step (a decode step is one token per sequence).
    /// </summary>
    public StepPlan PlanStep(StepPlan reuse)
    {
        ArgumentNullException.ThrowIfNull(reuse);
        if (_inFlight.HasValue)
        {
            throw new InvalidOperationException("InferenceScheduler.PlanStep: previous plan not completed");
        }

        long now = _clock.NowUs;
        StepPlan plan = reuse;
        plan.Clear(_counters.Steps, now);

        DropExpired(plan, now);

        // Starvation telemetry.
        long oldest = 0;
        foreach (ulong id in _waiting)
        {
            Sequence s = _seqs[id];
            oldest = Math.Max(oldest, now - s.EnqueuedAt);
            if (!s.StarvingCounted && IsStarving(s, now))
            {
                s.StarvingCounted = true;
                _counters.StarvationPromotions++;
            }
        }

        _counters.CurrentOldestQueueAge = oldest;
        _counters.MaxStarvationAge = Math.Max(_counters.MaxStarvationAge, oldest);

        var budget = new Budget
        {
            Tokens = _config.MaxStepTokens,
            PrefillTokens = Math.Min(_config.MaxStepPrefillTokens, _config.MaxStepTokens),
            Sequences = _config.MaxStepSequences,
        };

        // Decode first: running sequences keep their inter-token latency and prefill can never
        // starve them (decode capacity is reserved).
        ScheduleDecode(plan, ref budget, now);
        // Continue in-progress chunked prefills.
        SchedulePrefill(plan, ref budget);
        // Admit new/preempted work into the remaining budget.
        Admit(plan, ref budget, now);

        _inFlight = plan.StepIndex;
        return plan;
    }

    /// <summary>Apply the results of <paramref name="plan"/>. Throws on a plan mismatch.</summary>
    public void CompleteStep(StepPlan plan, StepOutcome? outcome = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        outcome ??= StepOutcome.Empty;
        if (_inFlight != plan.StepIndex)
        {
            throw new InvalidOperationException("InferenceScheduler.CompleteStep: plan does not match in-flight step");
        }

        _inFlight = null;
        long now = _clock.NowUs;

        _counters.Steps++;
        if (plan.IsEmpty)
        {
            _counters.EmptySteps++;
            return;
        }

        _counters.BudgetTokens += _config.MaxStepTokens;
        _counters.SequencesScheduled += (ulong)plan.Work.Count;

        foreach (ScheduledWork w in plan.Work)
        {
            if (!_seqs.TryGetValue(w.Id, out Sequence? s))
            {
                continue;
            }

            // Cancelled (or otherwise finished) after planning: ignore the result.
            if (s.State != RequestState.Prefill && s.State != RequestState.Decode)
            {
                continue;
            }

            if (outcome.Stalled.Contains(w.Id))
            {
                continue; // not executed this step; planned again next step
            }

            if (w.Phase == SchedulePhase.Prefill)
            {
                _counters.PrefillTokens += w.NumTokens;
                s.PrefillDone += w.NumTokens;
                s.KvTokens += w.NumTokens;
                if (s.PrefillDone >= s.PrefillTarget)
                {
                    s.State = RequestState.Decode;
                    EmitToken(s, now);
                }
            }
            else
            {
                _counters.DecodeTokens += w.NumTokens;
                s.KvTokens += 1;
                EmitToken(s, now);
            }

            bool eos = outcome.FinishedEarly.Contains(w.Id);
            if (s.Generated >= s.Spec.MaxNewTokens || (eos && s.Generated > 0))
            {
                Finish(s, RequestState.Completed, FailureReason.None, now);
            }
        }
    }

    // ---------------------------------------------------------------- queries

    public RequestState? State(ulong id) => _seqs.TryGetValue(id, out Sequence? s) ? s.State : null;

    public RequestTimeline? Timeline(ulong id)
    {
        if (!_seqs.TryGetValue(id, out Sequence? s))
        {
            return null;
        }

        RequestTimeline t = s.Tl;
        t.FinalState = s.State;
        return t;
    }

    /// <summary>Every tracked request's timeline, ordered by request id.</summary>
    public List<RequestTimeline> Timelines()
    {
        var output = new List<RequestTimeline>(_seqs.Count);
        foreach (Sequence s in _seqs.Values)
        {
            RequestTimeline t = s.Tl;
            t.FinalState = s.State;
            output.Add(t);
        }

        output.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return output;
    }

    public SchedulerStats Stats()
    {
        SchedulerStats output = _counters.Clone();
        output.Now = _clock.NowUs;
        output.ActiveSequences = _running.Count;
        output.QueuedSequences = _waiting.Count;

        var queue = new List<long>[SchedulerNames.WorkloadClassCount];
        var ttft = new List<long>[SchedulerNames.WorkloadClassCount];
        var e2e = new List<long>[SchedulerNames.WorkloadClassCount];
        for (int c = 0; c < SchedulerNames.WorkloadClassCount; c++)
        {
            queue[c] = [];
            ttft[c] = [];
            e2e[c] = [];
        }

        foreach (Sequence s in _seqs.Values)
        {
            int c = (int)s.Spec.Workload;
            if (s.Tl.FirstAdmitted >= 0)
            {
                queue[c].Add(s.Tl.TotalQueueTime);
            }

            if (s.Tl.FirstToken >= 0)
            {
                ttft[c].Add(s.Tl.Ttft);
            }

            if (s.State == RequestState.Completed)
            {
                e2e[c].Add(s.Tl.Finished - s.Tl.Arrival);
            }
        }

        for (int c = 0; c < SchedulerNames.WorkloadClassCount; c++)
        {
            ref ClassStats cs = ref output.PerClass[c];
            cs.QueueTime = LatencySummary.From(queue[c]);
            cs.Ttft = LatencySummary.From(ttft[c]);
            cs.EndToEnd = LatencySummary.From(e2e[c]);
            cs.InterToken = LatencySummary.From(_interTokenSamples[c]);
        }

        return output;
    }

    private static uint Min(uint a, uint b, uint c, uint d) => Math.Min(Math.Min(a, b), Math.Min(c, d));

    private struct Budget
    {
        public uint Tokens;
        public uint PrefillTokens;
        public uint Sequences;
    }

    private sealed class Sequence(RequestSpec spec)
    {
        public RequestSpec Spec = spec;
        public RequestState State = RequestState.Created;
        public int BaseRank;
        public ulong SubmitOrder;
        public long EnqueuedAt;

        /// <summary>Tokens to (re)compute before decode.</summary>
        public uint PrefillTarget;

        public uint PrefillDone;

        /// <summary>Tokens whose KV is materialised.</summary>
        public uint KvTokens;

        public uint Generated;
        public bool Swapped;
        public uint Requeues;
        public long LastTokenTime = -1;
        public bool StarvingCounted;
        public RequestTimeline Tl = new();
    }
}

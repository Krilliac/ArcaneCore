namespace ArcaneCore.Game.Rules;

/// <summary>
/// Runs rules: <see cref="Fire"/> evaluates every rule of one event for one subject, <see cref="Update"/> advances the subject's delayed
/// runs. One evaluator per host thread (its <see cref="Random"/> is not thread-safe); it holds no per-subject data, the
/// <see cref="RuleSubjectState"/> does.
/// <para>
/// Check order for each rule of the event, in priority order (lower first, ties by id): the event primitive's
/// <see cref="RuleEvent.Matches"/>; the phase mask; the combat flags; the once-only flag; the cooldown; the subject filter's columns; the
/// filter's stable share; then the rule's chance, as a stable share with <see cref="RuleFlags.StableShare"/> or as a fresh roll (one
/// <see cref="Random.Next(int)"/> draw, only when the chance is below 100); then the condition (undecidable fails closed). Only a rule
/// that passed every check draws a cooldown, so a rule blocked earlier never touches the random sequence.
/// </para>
/// <para>
/// Actions run in seq order. An action with a delay ends the synchronous part: the rest of the sequence is parked in one of the
/// subject's <see cref="RuleSubjectState.PendingCapacity"/> pending slots and resumed by <see cref="Update"/>. Starting a list that is
/// already pending for the subject restarts it. Nothing here allocates or looks anything up by string: the catalog compiled every
/// reference to an object at load (<c>RuleEngineAllocationTests</c> pins this).
/// </para>
/// </summary>
public sealed class RuleEvaluator(Random random)
{
    /// <summary>How deep started lists may nest (a list starting a list starting a list ...).</summary>
    public const int MaxListDepth = 8;

    private readonly Random _random = random ?? throw new ArgumentNullException(nameof(random));

    /// <summary>The condition check; without one every conditioned rule fails closed.</summary>
    public IRuleConditionGate? Conditions { get; init; }

    /// <summary>The traced subjects (<c>.rules trace</c>).</summary>
    public RuleTracer? Tracer { get; init; }

    /// <summary>Where traced evaluations go; without a sink nothing is traced.</summary>
    public IRuleTraceSink? TraceSink { get; init; }

    public RuleEvaluatorCounters Counters { get; } = new();

    /// <summary>
    /// Fire <paramref name="eventType"/> for <paramref name="subject"/>: evaluate each rule of the event in the subject's binding and run
    /// the actions of those that pass. Returns how many rules ran. Host thread; allocation free.
    /// </summary>
    public int Fire(RuleSubjectState state, IRuleSubject subject, short eventType, in RuleEventArgs args, long nowMs, IRuleSubject? invoker = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(subject);
        ReadOnlySpan<BoundRule> rules = state.Binding.ForEvent(eventType);
        if (rules.IsEmpty)
        {
            return 0;
        }

        ulong guid = subject.Guid;
        bool traceSubject = TraceSink is not null && Tracer is { Count: > 0 } tracer && tracer.IsTraced(guid);
        int ran = 0;
        foreach (BoundRule bound in rules)
        {
            CompiledRule rule = bound.Rule;
            int roll = -1;
            bool? condition = null;
            RuleOutcome outcome = Check(rule, bound.Slot, state, subject, guid, args, nowMs, invoker, ref roll, ref condition);
            int actionsRun = 0;
            int actionsFailed = 0;
            if (outcome == RuleOutcome.Ran)
            {
                ran++;
                Counters.Fired++;
                state.Fired[bound.Slot] = true;
                if (rule.CooldownMaxMs > 0)
                {
                    int cooldown = rule.CooldownMinMs == rule.CooldownMaxMs ? rule.CooldownMinMs : _random.Next(rule.CooldownMinMs, rule.CooldownMaxMs + 1);
                    state.CooldownUntil[bound.Slot] = nowMs + cooldown;
                }

                RunSequence(rule.Actions, 0, resumed: false, null, rule.Id, state, subject, invoker, nowMs, depth: 0, ref actionsRun, ref actionsFailed);
            }

            if (TraceSink is { } sink && (traceSubject || rule.Has(RuleFlags.DebugTrace)))
            {
                sink.Write(new RuleTraceEntry(nowMs, guid, rule.Id, eventType, outcome, roll, condition, actionsRun, actionsFailed));
            }
        }

        return ran;
    }

    /// <summary>Run the delayed actions that are due for <paramref name="subject"/>. Host thread; allocation free.</summary>
    public void Update(RuleSubjectState state, IRuleSubject subject, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(subject);
        Span<PendingRun> pending = state.Pending;
        for (int i = 0; i < pending.Length; i++)
        {
            if (pending[i].Actions is not { } actions || pending[i].DueMs > nowMs)
            {
                continue;
            }

            PendingRun run = pending[i];
            pending[i] = default;
            int actionsRun = 0;
            int actionsFailed = 0;
            // A resumed run starts a new synchronous chain: the depth limit guards one firing's nesting, not a list that repeats itself after a delay.
            RunSequence(actions, run.Next, resumed: true, run.List, run.RuleId, state, subject, run.Invoker, nowMs, run.List is null ? 0 : 1, ref actionsRun, ref actionsFailed);
        }
    }

    /// <summary>
    /// Start <paramref name="list"/> for the subject of <paramref name="context"/> (the <c>start_rule_list</c> action): its actions up to the
    /// first delay run now, the rest is parked. False when the nesting limit was reached.
    /// </summary>
    public bool StartList(in RuleActionContext context, CompiledRuleList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (context.Depth >= MaxListDepth)
        {
            Counters.DepthLimited++;
            return false;
        }

        // A list that is already pending for this subject restarts.
        Span<PendingRun> pending = context.State.Pending;
        for (int i = 0; i < pending.Length; i++)
        {
            if (ReferenceEquals(pending[i].List, list))
            {
                pending[i] = default;
            }
        }

        int actionsRun = 0;
        int actionsFailed = 0;
        RunSequence(list.Actions, 0, resumed: false, list, 0, context.State, context.Subject, context.Invoker, context.NowMs, context.Depth + 1,
            ref actionsRun, ref actionsFailed);
        return true;
    }

    private RuleOutcome Check(CompiledRule rule, int slot, RuleSubjectState state, IRuleSubject subject, ulong guid, in RuleEventArgs args,
        long nowMs, IRuleSubject? invoker, ref int roll, ref bool? condition)
    {
        if (!rule.Event.Matches(rule, args))
        {
            return RuleOutcome.EventMismatch;
        }

        if (rule.PhaseMask != 0 && (state.Phase is < 0 or > 31 || (rule.PhaseMask & (1u << state.Phase)) == 0))
        {
            return RuleOutcome.Phase;
        }

        if ((rule.Has(RuleFlags.CombatOnly) && !subject.IsInCombat) || (rule.Has(RuleFlags.OutOfCombatOnly) && subject.IsInCombat))
        {
            return RuleOutcome.Combat;
        }

        if (!rule.Has(RuleFlags.Repeatable) && state.Fired[slot])
        {
            return RuleOutcome.AlreadyFired;
        }

        if (nowMs < state.CooldownUntil[slot])
        {
            return RuleOutcome.Cooldown;
        }

        if (rule.Filter is { } filter)
        {
            RuleSubjectFacts facts = subject.Facts;
            if (!filter.Matches(facts))
            {
                return RuleOutcome.Filter;
            }

            if (filter.SharePct < 100)
            {
                roll = RuleShare.Bucket(filter.ShareSeed, rule.Id, guid);
                if (roll >= filter.SharePct)
                {
                    return RuleOutcome.Share;
                }
            }
        }

        if (rule.ChancePct < 100)
        {
            if (rule.Has(RuleFlags.StableShare))
            {
                int seed = unchecked((int)((uint)(rule.Filter?.ShareSeed ?? 0) ^ RuleShare.StableChanceSalt));
                roll = RuleShare.Bucket(seed, rule.Id, guid);
                if (roll >= rule.ChancePct)
                {
                    return RuleOutcome.Share;
                }
            }
            else
            {
                roll = _random.Next(100);
                if (roll >= rule.ChancePct)
                {
                    return RuleOutcome.Chance;
                }
            }
        }

        if (rule.ConditionId != 0)
        {
            condition = Conditions?.Evaluate(rule.ConditionId, subject, invoker);
            if (condition != true)
            {
                return RuleOutcome.Condition;
            }
        }

        return RuleOutcome.Ran;
    }

    private void RunSequence(CompiledAction[] actions, int start, bool resumed, CompiledRuleList? list, int ruleId, RuleSubjectState state,
        IRuleSubject subject, IRuleSubject? invoker, long nowMs, int depth, ref int actionsRun, ref int actionsFailed)
    {
        for (int i = start; i < actions.Length; i++)
        {
            CompiledAction action = actions[i];
            if (action.DelayMs > 0 && !(resumed && i == start))
            {
                Park(state, actions, i, list, ruleId, invoker, nowMs + action.DelayMs);
                return;
            }

            var context = new RuleActionContext(this, state, subject, invoker, action, null, nowMs, depth);
            IRuleSubject? target = action.Target.Resolve(context);
            RuleActionResult result = target is null ? RuleActionResult.NoTarget : action.Action.Execute(context.WithTarget(target));
            if (result == RuleActionResult.Done)
            {
                actionsRun++;
                Counters.ActionsRun++;
            }
            else
            {
                actionsFailed++;
                Counters.ActionsFailed++;
            }
        }
    }

    private void Park(RuleSubjectState state, CompiledAction[] actions, int next, CompiledRuleList? list, int ruleId, IRuleSubject? invoker, long dueMs)
    {
        Span<PendingRun> pending = state.Pending;
        for (int i = 0; i < pending.Length; i++)
        {
            if (pending[i].Actions is null)
            {
                pending[i] = new PendingRun { Actions = actions, List = list, Next = next, DueMs = dueMs, Invoker = invoker, RuleId = ruleId };
                return;
            }
        }

        Counters.DroppedRuns++;
    }
}

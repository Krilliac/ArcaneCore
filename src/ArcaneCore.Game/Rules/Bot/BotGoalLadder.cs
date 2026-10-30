using ArcaneCore.Kernel.Rules;

namespace ArcaneCore.Game.Rules.Bot;

/// <summary>One candidate of a ladder pass, after ranking: which catalogue goal, which driver runs it, and the weight it won with.</summary>
public readonly record struct BotCandidate(short GoalId, PlayerbotLadderRung Rung, int Weight);

/// <summary>
/// The playerbot as a rule subject: its facts for subject filters, and the proposals the pass collects. The brain's adapter derives from
/// this and supplies the player; tests derive their own. One instance per bot; world thread only. Fixed-size buffers: a pass allocates
/// nothing (a proposal past <see cref="MaxProposals"/> is dropped and counted in <see cref="DroppedProposals"/>).
/// </summary>
public abstract class BotRuleSubject : IBotRuleSubject
{
    /// <summary>How many proposals and suppressions one pass keeps.</summary>
    public const int MaxProposals = 32;

    private readonly Proposal[] _proposals = new Proposal[MaxProposals];
    private readonly BotCandidate[] _candidates = new BotCandidate[MaxProposals];
    private readonly Ranked[] _ranked = new Ranked[MaxProposals];
    private readonly short[] _suppressed = new short[MaxProposals];
    private int _proposalCount;
    private int _suppressedCount;
    private bool _collecting;

    public abstract ulong Guid { get; }

    public abstract bool IsInCombat { get; }

    public abstract RuleSubjectFacts Facts { get; }

    public virtual Entities.Player? Player => null;

    /// <summary>The bot's money in copper, read only when a <c>bot.gold_below</c> rule is loaded.</summary>
    public abstract long Money { get; }

    /// <summary>The bot's free bag slots, read only when a <c>bot.free_bag_slots_below</c> rule is loaded.</summary>
    public abstract int FreeBagSlots { get; }

    /// <summary>Proposals dropped because the buffer was full or no pass was collecting (a delayed <c>bot.propose_goal</c> arrives late).</summary>
    public long DroppedProposals { get; private set; }

    /// <summary>The ranked candidates of the last pass.</summary>
    public ReadOnlySpan<BotCandidate> Candidates => _candidates.AsSpan(0, CandidateCount);

    public int CandidateCount { get; private set; }

    internal RuleSubjectState? State { get; set; }

    internal RuleCatalog? StateCatalog { get; set; }

    internal void BeginPass()
    {
        _proposalCount = 0;
        _suppressedCount = 0;
        CandidateCount = 0;
        _collecting = true;
    }

    internal void EndPass(RuleCatalog catalog)
    {
        _collecting = false;
        int taken = 0;
        for (int i = 0; i < _proposalCount; i++)
        {
            Proposal proposal = _proposals[i];
            if (!catalog.Goals.TryGetValue(proposal.GoalId, out PlayerbotGoalRecord? goal) || !goal.Enabled || goal.Driver <= 0 || IsSuppressed(proposal.GoalId))
            {
                continue;
            }

            // Insertion sort: weight high to low, then the catalogue's base_priority high to low; equal ones keep the order the rules proposed in.
            var ranked = new Ranked(proposal.GoalId, goal.Driver, proposal.Weight, goal.BasePriority);
            int at = taken;
            while (at > 0 && ranked.Beats(_ranked[at - 1]))
            {
                _ranked[at] = _ranked[at - 1];
                at--;
            }

            _ranked[at] = ranked;
            taken++;
        }

        int count = 0;
        ulong driversSeen = 0;
        for (int r = 0; r < taken; r++)
        {
            Ranked candidate = _ranked[r];
            // A driver runs once per pass: a second proposal for the same driver would only repeat a failed attempt.
            ulong bit = 1UL << (candidate.Driver & 63);
            if ((driversSeen & bit) != 0)
            {
                continue;
            }

            driversSeen |= bit;
            _candidates[count++] = new BotCandidate(candidate.GoalId, (PlayerbotLadderRung)candidate.Driver, candidate.Weight);
        }

        CandidateCount = count;
    }

    private bool IsSuppressed(short goalId)
    {
        for (int i = 0; i < _suppressedCount; i++)
        {
            if (_suppressed[i] == goalId)
            {
                return true;
            }
        }

        return false;
    }

    public void Propose(short goalId, int weight)
    {
        if (!_collecting || _proposalCount == MaxProposals)
        {
            DroppedProposals++;
            return;
        }

        _proposals[_proposalCount++] = new Proposal(goalId, weight);
    }

    public void Suppress(short goalId)
    {
        if (!_collecting || _suppressedCount == MaxProposals)
        {
            DroppedProposals++;
            return;
        }

        _suppressed[_suppressedCount++] = goalId;
    }

    private readonly record struct Proposal(short GoalId, int Weight);

    private readonly record struct Ranked(short GoalId, short Driver, int Weight, int BasePriority)
    {
        public bool Beats(Ranked other) => Weight > other.Weight || (Weight == other.Weight && BasePriority > other.BasePriority);
    }
}

/// <summary>
/// The goal ladder as rules (design 5.3 and section 11): a pass fires the bot's events, collects the <c>bot.propose_goal</c> proposals,
/// drops disabled and suppressed goals, ranks the rest by weight (ties by <c>playerbot_goal.base_priority</c>, then proposal order) and
/// leaves one candidate per driver, best first. The brain then tries the drivers in that order until one acts (fall-through). Conditions of
/// the rules are checked when a rule fires, at the start of the pass, before any driver runs; they draw nothing from the bot's random sequence.
/// One ladder per evaluator (world thread).
/// </summary>
public sealed class BotGoalLadder(RuleEvaluator evaluator)
{
    private readonly RuleEvaluator _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));

    /// <summary>
    /// Run a pass for <paramref name="subject"/> against <paramref name="catalog"/> and rank the candidates (<see cref="BotRuleSubject.Candidates"/>).
    /// The subject's rule state is rebuilt when the catalog is not the one it was bound to (a reload). Returns the candidate count.
    /// </summary>
    public int Choose(BotRuleSubject subject, RuleCatalog catalog, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!ReferenceEquals(subject.StateCatalog, catalog) || subject.State is null)
        {
            subject.State = catalog.Bind(RuleHostKind.Bot).CreateState();
            subject.StateCatalog = catalog;
        }

        RuleSubjectState state = subject.State;
        subject.BeginPass();
        try
        {
            _evaluator.TryFire(state, subject, BotRuleIds.GoalEvaluate, default, nowMs);
            if (!state.Binding.ForEvent(BotRuleIds.GoldBelow).IsEmpty)
            {
                _evaluator.TryFire(state, subject, BotRuleIds.GoldBelow, new RuleEventArgs((int)Math.Min(subject.Money, int.MaxValue)), nowMs);
            }

            if (!state.Binding.ForEvent(BotRuleIds.FreeBagSlotsBelow).IsEmpty)
            {
                _evaluator.TryFire(state, subject, BotRuleIds.FreeBagSlotsBelow, new RuleEventArgs(subject.FreeBagSlots), nowMs);
            }
        }
        finally
        {
            subject.EndPass(catalog);
        }

        return subject.CandidateCount;
    }
}

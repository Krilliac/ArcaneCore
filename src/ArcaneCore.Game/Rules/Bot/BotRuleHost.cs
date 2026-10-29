using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Rules;

namespace ArcaneCore.Game.Rules.Bot;

/// <summary>
/// One playerbot as the rule engine sees it. The bot host fires its events at the start of each out-of-combat goal-ladder pass and the
/// <c>bot.propose_goal</c> and <c>bot.suppress_goal</c> actions leave their proposals here for the brain to rank
/// (<c>PlayerbotRuleLadder</c> in the Playerbots pack). World thread only.
/// </summary>
public interface IBotRuleSubject : IRuleSubject
{
    /// <summary>The bot's player (null while it is not in the world); condition checks use it as their source.</summary>
    Player? Player { get; }

    /// <summary>Add a candidate goal for the pass in progress (the catalogue row <paramref name="goalId"/> names its driver).</summary>
    void Propose(short goalId, int weight);

    /// <summary>Remove every candidate of <paramref name="goalId"/> from the pass in progress, whatever rule proposed it and in whatever order.</summary>
    void Suppress(short goalId);
}

/// <summary>The bot host (design section 3): subjects are playerbots, rules are global.</summary>
public sealed class BotRuleHostDefinition : RuleHostDefinition
{
    public override RuleHostKind Kind => RuleHostKind.Bot;

    public override string Name => "bot";

    public override string Description => "Playerbots: rules that propose goals to a bot's goal ladder.";

    public override IReadOnlyList<RuleOwnerKind> Owners { get; } = [RuleOwnerKind.Global];

    public override RuleCapabilities Capabilities => RuleCapabilities.None;
}

/// <summary><c>bot.goal_evaluate</c>: the bot is choosing its next goal (once per out-of-combat ladder pass).</summary>
[RulePrimitive(RuleHostKind.Bot, BotRuleIds.GoalEvaluate, "bot.goal_evaluate", Description = "The bot is choosing its next goal (once per out-of-combat ladder pass); its rules propose goals.")]
public sealed class BotGoalEvaluateEvent : RuleEvent;

/// <summary><c>bot.gold_below(copper)</c>: fired with the ladder pass; matches while the bot's money is below the threshold.</summary>
[RulePrimitive(RuleHostKind.Bot, BotRuleIds.GoldBelow, "bot.gold_below", Description = "Fires with each ladder pass while the bot holds less than the given copper.")]
[RuleParam(1, "copper", RuleParamKind.NonNegative, Description = "The money threshold, in copper.")]
public sealed class BotGoldBelowEvent : RuleEvent
{
    public override bool Matches(CompiledRule rule, in RuleEventArgs args) => args.Value1 < rule.EventParam(1);
}

/// <summary><c>bot.free_bag_slots_below(slots)</c>: fired with the ladder pass; matches while fewer bag slots are free.</summary>
[RulePrimitive(RuleHostKind.Bot, BotRuleIds.FreeBagSlotsBelow, "bot.free_bag_slots_below", Description = "Fires with each ladder pass while the bot has fewer free bag slots than given.")]
[RuleParam(1, "slots", RuleParamKind.NonNegative, Description = "The free-slot threshold.")]
public sealed class BotFreeBagSlotsBelowEvent : RuleEvent
{
    public override bool Matches(CompiledRule rule, in RuleEventArgs args) => args.Value1 < rule.EventParam(1);
}

/// <summary><c>bot.propose_goal(goal_id, weight)</c>: add a candidate to the ladder pass.</summary>
[RulePrimitive(RuleHostKind.Bot, BotRuleIds.ProposeGoal, "bot.propose_goal",
    Description = "Propose a goal to the bot's ladder pass: the highest weight is tried first (ties by playerbot_goal.base_priority), and a goal whose driver does not act lets the next candidate run.")]
[RuleParam(1, "goal_id", RuleParamKind.GoalId, Description = "playerbot_goal.goal_id of a goal that has a driver.")]
[RuleParam(2, "weight", RuleParamKind.Int, Description = "Shipped rungs are 100 apart from 1600 down to 200; the policy sits at 1150.")]
public sealed class BotProposeGoalAction : RuleAction
{
    public override RuleActionResult Execute(in RuleActionContext context)
    {
        if (context.Subject is not IBotRuleSubject bot)
        {
            return RuleActionResult.Unsupported;
        }

        bot.Propose((short)context.Action.Param(1), context.Action.Param(2));
        return RuleActionResult.Done;
    }
}

/// <summary><c>bot.suppress_goal(goal_id)</c>: take a goal out of the pass (a way for a server to veto a shipped rung for some bots).</summary>
[RulePrimitive(RuleHostKind.Bot, BotRuleIds.SuppressGoal, "bot.suppress_goal",
    Description = "Remove every proposal of a goal from this ladder pass, whoever proposed it; with a subject filter it switches a rung off for a share of bots.")]
[RuleParam(1, "goal_id", RuleParamKind.GoalId, Description = "playerbot_goal.goal_id of a goal that has a driver.")]
public sealed class BotSuppressGoalAction : RuleAction
{
    public override RuleActionResult Execute(in RuleActionContext context)
    {
        if (context.Subject is not IBotRuleSubject bot)
        {
            return RuleActionResult.Unsupported;
        }

        bot.Suppress((short)context.Action.Param(1));
        return RuleActionResult.Done;
    }
}

/// <summary>
/// The condition gate of the bot host: <c>conditions</c> rows are checked with the bot's player as the source, through the delegate the
/// host supplies (<c>ConditionFeature.IsSatisfied</c>). A subject that is not a bot in the world is undecidable and fails closed.
/// </summary>
public sealed class BotConditionGate(Func<uint, Player, bool> isSatisfied) : IRuleConditionGate
{
    private readonly Func<uint, Player, bool> _isSatisfied = isSatisfied ?? throw new ArgumentNullException(nameof(isSatisfied));

    public bool? Evaluate(uint conditionId, IRuleSubject subject, IRuleSubject? invoker)
        => subject is IBotRuleSubject { Player: { } player } ? _isSatisfied(conditionId, player) : null;
}

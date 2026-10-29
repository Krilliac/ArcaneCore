using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Rules.Creatures;

namespace ArcaneCore.Game.Rules.Creatures;

// The creature host's targets: the selectors of cmangos GetTargetByType (CreatureEventAI.cpp:1715-1825), one class each. A creature target also
// carries the row's action gate (the random-action pick and the combat-action abort of the row marker): an action that does not take part in
// this firing resolves to no target and is skipped without counting as a failure. target_param1 = 1 makes a selector optional: an
// unresolved target becomes "no unit" and the action still runs (EventAI actions that ignore the resolver's error flag).

/// <summary>A target selector of the creature host.</summary>
public abstract class CreatureTarget : RuleTarget
{
    /// <summary>The cmangos <c>Target</c> value the selector implements (<c>EventAiTarget</c> numbering).</summary>
    protected abstract int SelectorType { get; }

    public override IRuleSubject? Resolve(in RuleActionContext context)
        => CreatureRuleRunner.Current?.ResolveTarget(context.Action.Seq, SelectorType, optional: context.Action.TargetParam(1) != 0);
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.TargetSelf, "creature.self", Description = "The creature itself.")]
[RuleParam(1, "optional", RuleParamKind.Bool, Description = "1: an unresolved target becomes no unit and the action still runs.")]
public sealed class CreatureSelfTarget : CreatureTarget
{
    protected override int SelectorType => 0;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.TargetVictim, "creature.victim", Description = "The creature's current victim.")]
[RuleParam(1, "optional", RuleParamKind.Bool, Description = "1: an unresolved target becomes no unit and the action still runs.")]
public sealed class CreatureVictimTarget : CreatureTarget
{
    protected override int SelectorType => 1;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.TargetSecondOnThreat, "creature.second_on_threat", Description = "The unit second on the threat list.")]
[RuleParam(1, "optional", RuleParamKind.Bool, Description = "1: an unresolved target becomes no unit and the action still runs.")]
public sealed class CreatureSecondOnThreatTarget : CreatureTarget
{
    protected override int SelectorType => 2;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.TargetLastOnThreat, "creature.last_on_threat", Description = "The unit last on the threat list.")]
[RuleParam(1, "optional", RuleParamKind.Bool, Description = "1: an unresolved target becomes no unit and the action still runs.")]
public sealed class CreatureLastOnThreatTarget : CreatureTarget
{
    protected override int SelectorType => 3;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.TargetRandomOnThreat, "creature.random_on_threat", Description = "A random unit of the threat list.")]
[RuleParam(1, "optional", RuleParamKind.Bool, Description = "1: an unresolved target becomes no unit and the action still runs.")]
public sealed class CreatureRandomOnThreatTarget : CreatureTarget
{
    protected override int SelectorType => 4;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.TargetRandomNotTop, "creature.random_not_top", Description = "A random unit of the threat list other than the top one.")]
[RuleParam(1, "optional", RuleParamKind.Bool, Description = "1: an unresolved target becomes no unit and the action still runs.")]
public sealed class CreatureRandomNotTopTarget : CreatureTarget
{
    protected override int SelectorType => 5;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.TargetInvoker, "creature.invoker", Description = "Whoever caused the event.")]
[RuleParam(1, "optional", RuleParamKind.Bool, Description = "1: an unresolved target becomes no unit and the action still runs.")]
public sealed class CreatureInvokerTarget : CreatureTarget
{
    protected override int SelectorType => 6;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.TargetInvokerOwner, "creature.invoker_owner", Description = "The beneficiary of the event's invoker (the invoker itself while there are no owners).")]
[RuleParam(1, "optional", RuleParamKind.Bool, Description = "1: an unresolved target becomes no unit and the action still runs.")]
public sealed class CreatureInvokerOwnerTarget : CreatureTarget
{
    protected override int SelectorType => 7;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.TargetRandomPlayer, "creature.random_player", Description = "A random player of the threat list.")]
[RuleParam(1, "optional", RuleParamKind.Bool, Description = "1: an unresolved target becomes no unit and the action still runs.")]
public sealed class CreatureRandomPlayerTarget : CreatureTarget
{
    protected override int SelectorType => 8;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.TargetRandomNotTopPlayer, "creature.random_not_top_player", Description = "A random player of the threat list other than the top entry.")]
[RuleParam(1, "optional", RuleParamKind.Bool, Description = "1: an unresolved target becomes no unit and the action still runs.")]
public sealed class CreatureRandomNotTopPlayerTarget : CreatureTarget
{
    protected override int SelectorType => 9;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.TargetEventSender, "creature.event_sender", Description = "The unit that sent the AI event.")]
[RuleParam(1, "optional", RuleParamKind.Bool, Description = "1: an unresolved target becomes no unit and the action still runs.")]
public sealed class CreatureEventSenderTarget : CreatureTarget
{
    protected override int SelectorType => 10;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.TargetSpawner, "creature.spawner", Description = "The creature or unit that summoned this one.")]
[RuleParam(1, "optional", RuleParamKind.Bool, Description = "1: an unresolved target becomes no unit and the action still runs.")]
public sealed class CreatureSpawnerTarget : CreatureTarget
{
    protected override int SelectorType => 11;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.TargetEventTarget, "creature.event_target", Description = "The target the event filled in (a friend that needs help, a unit picked from the threat list).")]
[RuleParam(1, "optional", RuleParamKind.Bool, Description = "1: an unresolved target becomes no unit and the action still runs.")]
public sealed class CreatureEventTargetTarget : CreatureTarget
{
    protected override int SelectorType => 12;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.TargetDefault, "creature.default_target", Description = "No unit target: a spell uses its own default target.")]
public sealed class CreatureDefaultTarget : CreatureTarget
{
    protected override int SelectorType => 15;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.TargetTextAudience, "creature.text_audience",
    Description = "Who a creature text goes to: the invoker when it is a player, else the victim when the event has no invoker.")]
public sealed class CreatureTextAudienceTarget : RuleTarget
{
    public override IRuleSubject? Resolve(in RuleActionContext context)
        => CreatureRuleRunner.Current?.ResolveTextAudience(context.Action.Seq);
}

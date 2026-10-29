namespace ArcaneCore.Game.Rules;

/// <summary><c>set_phase(phase)</c>: the subject's rules continue in phase <c>phase</c> (the EventAI/SmartAI phase).</summary>
[RulePrimitive(RuleHostKind.Any, 1, "set_phase", Description = "Set the subject's rule phase; rules whose phase_mask lacks it stop firing.")]
[RuleParam(1, "phase", RuleParamKind.Phase, Description = "The new phase, 0..31.")]
public sealed class SetPhaseAction : RuleAction
{
    public override RuleActionResult Execute(in RuleActionContext context)
    {
        context.State.Phase = context.Action.Param(1);
        return RuleActionResult.Done;
    }
}

/// <summary><c>start_rule_list(list_id)</c>: run a <c>rule_list</c> for the subject (relay scripts, timed action lists).</summary>
[RulePrimitive(RuleHostKind.Any, 2, "start_rule_list", Description = "Start a rule list for the subject: its actions up to the first delay run now, the rest later; a list already running restarts.")]
[RuleParam(1, "list_id", RuleParamKind.RuleListId, Description = "rule_list.id.")]
public sealed class StartRuleListAction : RuleAction
{
    public override RuleActionResult Execute(in RuleActionContext context)
        => context.Action.Reference is CompiledRuleList list && context.Evaluator.StartList(context, list)
            ? RuleActionResult.Done
            : RuleActionResult.Unsupported;
}

/// <summary><c>say(text_id)</c>: the subject says a <c>rule_text</c>, to the action's target.</summary>
[RulePrimitive(RuleHostKind.Any, 3, "say", Description = "The subject says a rule_text (its type picks say, yell, emote or whisper) to the target.")]
[RuleParam(1, "text_id", RuleParamKind.TextId, Description = "rule_text.id.")]
public sealed class SayAction : RuleAction
{
    public override RuleCapabilities Requires => RuleCapabilities.Speak;

    public override RuleActionResult Execute(in RuleActionContext context)
    {
        if (context.Subject is not IRuleSpeaker speaker || context.Action.Reference is not RuleText text)
        {
            return RuleActionResult.Unsupported;
        }

        speaker.Say(text, context.Target);
        return RuleActionResult.Done;
    }
}

/// <summary><c>cast(spell_id, flags)</c>: the subject casts a spell on the action's target.</summary>
[RulePrimitive(RuleHostKind.Any, 4, "cast", Description = "The subject casts a spell on the target.")]
[RuleParam(1, "spell_id", RuleParamKind.SpellId, Description = "The spell.")]
[RuleParam(2, "cast_flags", RuleParamKind.NonNegative, Description = "Host-defined cast flags (0 = a normal cast).")]
public sealed class CastAction : RuleAction
{
    public override RuleCapabilities Requires => RuleCapabilities.Cast;

    public override RuleActionResult Execute(in RuleActionContext context)
    {
        if (context.Subject is not IRuleCaster caster)
        {
            return RuleActionResult.Unsupported;
        }

        caster.Cast(context.Action.Param(1), context.Action.Param(2), context.Target);
        return RuleActionResult.Done;
    }
}

/// <summary><c>self</c>: the subject itself.</summary>
[RulePrimitive(RuleHostKind.Any, 1, "self", Description = "The subject the rule runs for.")]
public sealed class SelfTarget : RuleTarget
{
    public override IRuleSubject? Resolve(in RuleActionContext context) => context.Subject;
}

/// <summary><c>invoker</c>: whoever caused the event (none for events without one).</summary>
[RulePrimitive(RuleHostKind.Any, 2, "invoker", Description = "Whoever caused the event (the attacker, the user of an object); none when the event has no invoker.")]
public sealed class InvokerTarget : RuleTarget
{
    public override IRuleSubject? Resolve(in RuleActionContext context) => context.Invoker;
}

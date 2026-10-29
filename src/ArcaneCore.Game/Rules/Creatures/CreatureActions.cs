using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Rules.Creatures;

namespace ArcaneCore.Game.Rules.Creatures;

// The creature host's actions: what the EventAI action handlers of the same behaviour do (mangos-classic CreatureEventAI.cpp ProcessAction,
// :665-1380), on rule parameters. An action that cannot do its work (a refused cast, a missing target, a dead creature) returns false; the
// runner then applies the combat-action contract (the first action failing keeps the event's timer and skips the other actions). The target
// is the rule_action's target; the parameter lists say which parameters each action takes. This file holds the speech, cast, summon, threat,
// phase and combat actions; CreatureUnitActions.cs the unit-state, movement and script ones.

/// <summary>An action of the creature host. <see cref="Run"/> returns whether it succeeded; a failure is counted as <see cref="RuleActionResult.Unsupported"/>.</summary>
public abstract class CreatureAction : RuleAction
{
    internal abstract bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target);

    public override RuleActionResult Execute(in RuleActionContext context)
    {
        if (CreatureRuleRunner.Current is not { } runner)
        {
            return RuleActionResult.Unsupported;
        }

        bool done = Run(runner, context, runner.UnitOf(context.Target));
        if (!done)
        {
            runner.NoteFailure(context.Action.Seq);
        }

        return done ? RuleActionResult.Done : RuleActionResult.Unsupported;
    }
}

/// <summary>
/// <c>creature.row(flags, action_count)</c>: the first action of a converted EventAI row. It draws the firing's random value (cmangos
/// <c>rnd</c>, after the chance roll) and, for a random-action row, picks which of the <c>action_count</c> following actions runs. The flags
/// (<see cref="CreatureRuleIds.RowFlags"/>) are also read when the rule is bound, to gate the row before its chance is rolled.
/// </summary>
[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionRow, "creature.row",
    Description = "Row options of a converted EventAI row (first action): repeatable, random action, combat action, ranged/melee only, one shot.")]
[RuleParam(1, "flags", RuleParamKind.NonNegative, Description = "CreatureRuleIds.RowFlags: 1 repeatable, 2 random action, 4 combat action, 8 ranged only, 16 melee only, 32 one shot.")]
[RuleParam(2, "action_count", RuleParamKind.NonNegative, Description = "How many actions follow (the choices of a random-action row).")]
public sealed class CreatureRowAction : RuleAction
{
    public override RuleActionResult Execute(in RuleActionContext context)
    {
        if (CreatureRuleRunner.Current is not { } runner)
        {
            return RuleActionResult.Unsupported;
        }

        var flags = (CreatureRuleIds.RowFlags)context.Action.Param(1);
        int count = context.Action.Param(2);
        runner.SettleLivePhase();
        ref CreatureFireFrame frame = ref runner.Frame;
        frame.Random = runner.Context.NextRandom();
        frame.RandomDrawn = true;
        if ((flags & CreatureRuleIds.RowFlags.RandomAction) != 0 && count > 0)
        {
            frame.Picked = (int)(frame.Random % (uint)count);
            frame.Random = runner.Context.NextRandom(); // randomize again so a random event with a random action is not always the same
        }

        return RuleActionResult.Done;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSay, "creature.say",
    Description = "Say a rule_text; with a second (and third) text one is chosen at random. Pair with creature.text_audience for the EventAI text target.")]
[RuleParam(1, "text_id", RuleParamKind.TextId, Description = "rule_text.id.")]
[RuleParam(2, "text_id_2", RuleParamKind.NonNegative, Description = "Second choice (0 none).")]
[RuleParam(3, "text_id_3", RuleParamKind.NonNegative, Description = "Third choice (0 none).")]
public sealed class CreatureSayAction : CreatureAction
{
    public override RuleCapabilities Requires => RuleCapabilities.Speak;

    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        int first = context.Action.Param(1);
        int second = context.Action.Param(2);
        int third = context.Action.Param(3);
        uint random = runner.ActionRandom();
        int textId;
        if (second != 0 && third != 0)
        {
            textId = (random % 3) switch
            {
                0 => first,
                1 => second,
                _ => third,
            };
        }
        else if (second != 0 && random % 2 == 1)
        {
            textId = second;
        }
        else
        {
            textId = first;
        }

        if (runner.System is { } system && runner.Catalog.Texts.TryGetValue(textId, out RuleText? text))
        {
            system.Say(runner.Me, CreatureTexts.ToAiText(text), target);
        }

        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionPlaySound, "creature.play_sound", Description = "Play a sound to the players that see the creature.")]
[RuleParam(1, "sound_id", RuleParamKind.NonNegative)]
public sealed class CreaturePlaySoundAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        runner.System?.PlayDirectSound(runner.Me, (uint)context.Action.Param(1));
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionEmote, "creature.emote", Description = "Play an emote (a state emote is kept as the emote state).")]
[RuleParam(1, "emote_id", RuleParamKind.NonNegative)]
public sealed class CreatureEmoteAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        runner.System?.PlayEmote(runner.Me, (uint)context.Action.Param(1));
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionRandomSound, "creature.random_sound", Description = "Play one of three sounds by the firing's random value; -1 plays nothing.")]
[RuleParam(1, "sound_1", RuleParamKind.Int)]
[RuleParam(2, "sound_2", RuleParamKind.Int)]
[RuleParam(3, "sound_3", RuleParamKind.Int)]
public sealed class CreatureRandomSoundAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        int sound = CreatureActionRandom.Pick(runner, context);
        if (sound >= 0)
        {
            runner.System?.PlayDirectSound(runner.Me, (uint)sound);
        }

        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionRandomEmote, "creature.random_emote", Description = "Play one of three emotes by the firing's random value; -1 plays nothing.")]
[RuleParam(1, "emote_1", RuleParamKind.Int)]
[RuleParam(2, "emote_2", RuleParamKind.Int)]
[RuleParam(3, "emote_3", RuleParamKind.Int)]
public sealed class CreatureRandomEmoteAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        int emote = CreatureActionRandom.Pick(runner, context);
        if (emote >= 0)
        {
            runner.System?.PlayEmote(runner.Me, (uint)emote);
        }

        return true;
    }
}

/// <summary>cmangos GetRandActionParam: parameter 1, 2 or 3 by <c>rnd % 3</c>.</summary>
internal static class CreatureActionRandom
{
    public static int Pick(CreatureRuleRunner runner, in RuleActionContext context) => (runner.ActionRandom() % 3) switch
    {
        0 => context.Action.Param(1),
        1 => context.Action.Param(2),
        _ => context.Action.Param(3),
    };
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionCast, "creature.cast",
    Description = "Cast a spell on the target; fails when the creature is casting (unless triggered or interrupting), the target has the aura (aura-not-present) or the cast is refused.")]
[RuleParam(1, "spell_id", RuleParamKind.SpellId)]
[RuleParam(2, "cast_flags", RuleParamKind.NonNegative, Description = "1 interrupt the previous cast, 2 triggered, 0x20 only if the target lacks the aura.")]
public sealed class CreatureCastAction : CreatureAction
{
    public override RuleCapabilities Requires => RuleCapabilities.Cast;

    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
        => runner.CastSpell(context.Action.Param(1), context.Action.Param(2), target);
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSummon, "creature.summon",
    Description = "Summon a creature at the creature's position that attacks the target (unless the target is the creature itself).")]
[RuleParam(1, "entry", RuleParamKind.CreatureEntry)]
[RuleParam(2, "despawn_ms", RuleParamKind.NonNegative, Description = "Lifetime in ms out of combat (0: as soon as it is out of combat).")]
public sealed class CreatureSummonAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        runner.System?.Summon(runner.Me, (uint)context.Action.Param(1), context.Action.Target is CreatureSelfTarget ? null : target, (uint)context.Action.Param(2));
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSummonAt, "creature.summon_at",
    Description = "Summon a creature at a position that attacks the target (unless the target is the creature itself); the position is in param3-6.")]
[RuleParam(1, "entry", RuleParamKind.CreatureEntry)]
[RuleParam(2, "despawn_ms", RuleParamKind.NonNegative, Description = "Lifetime in ms.")]
[RuleParam(3, "x_bits", RuleParamKind.Int, Description = "X as the bits of a single-precision float (BitConverter.SingleToInt32Bits).")]
[RuleParam(4, "y_bits", RuleParamKind.Int, Description = "Y as float bits.")]
[RuleParam(5, "z_bits", RuleParamKind.Int, Description = "Z as float bits.")]
[RuleParam(6, "orientation_bits", RuleParamKind.Int, Description = "Orientation as float bits.")]
public sealed class CreatureSummonAtAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (runner.System is not { } system)
        {
            return false;
        }

        CompiledAction action = context.Action;
        system.SummonAt(runner.Me, (uint)action.Param(1), BitConverter.Int32BitsToSingle(action.Param(3)), BitConverter.Int32BitsToSingle(action.Param(4)),
            BitConverter.Int32BitsToSingle(action.Param(5)), BitConverter.Int32BitsToSingle(action.Param(6)),
            action.Target is CreatureSelfTarget ? null : target, (uint)action.Param(2));
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionThreatSingle, "creature.threat_single",
    Description = "Add threat to the target (direct), or change its entry by a percent (below -100 removes it).")]
[RuleParam(1, "threat", RuleParamKind.Int)]
[RuleParam(2, "is_direct", RuleParamKind.Bool, Description = "1: add the value; 0: a percent change.")]
public sealed class CreatureThreatSingleAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (target is null)
        {
            return true;
        }

        if (context.Action.Param(2) != 0)
        {
            runner.Me.Combat.Threat.AddThreat(target, context.Action.Param(1));
        }
        else
        {
            runner.Me.Combat.Threat.ModifyThreatPercent(target, context.Action.Param(1));
        }

        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionThreatAllPct, "creature.threat_all_pct", Description = "Change every entry of the threat list by a percent (below -100 removes it).")]
[RuleParam(1, "percent", RuleParamKind.Int)]
public sealed class CreatureThreatAllPctAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (!runner.Me.Combat.HasThreatList)
        {
            return true;
        }

        foreach (Unit unit in runner.Me.Combat.Threat.Entries.Select(static e => e.Target).ToArray())
        {
            runner.Me.Combat.Threat.ModifyThreatPercent(unit, context.Action.Param(1));
        }

        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionQuestEvent, "creature.quest_event",
    Description = "A player target completes the quest's exploration or event objective (with the reward group, its group near the creature too).")]
[RuleParam(1, "quest_id", RuleParamKind.NonNegative)]
[RuleParam(2, "reward_group", RuleParamKind.Bool)]
public sealed class CreatureQuestEventAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (target is Player player && runner.System?.AiServices.QuestEvents is { } quests)
        {
            quests.EventHappened(player, (uint)context.Action.Param(1), runner.Me, rewardGroup: context.Action.Param(2) != 0);
        }

        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionKillCredit, "creature.kill_credit",
    Description = "Kill credit for the entry goes to the player (and group) that tapped the creature, else to the target's player.")]
[RuleParam(1, "entry", RuleParamKind.NonNegative)]
public sealed class CreatureKillCreditAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (runner.System is not { } system || system.AiServices.QuestEvents is not { } quests)
        {
            return true;
        }

        Creature me = runner.Me;
        Player? player = !me.LootTapPlayerGuid.IsEmpty ? system.Map.FindPlayer(me.LootTapPlayerGuid) : null;
        player ??= target?.GetCharmerOrOwnerPlayerOrSelf();
        if (player is not null)
        {
            quests.KillCredit(player, (uint)context.Action.Param(1), me);
        }

        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionAutoAttack, "creature.auto_attack", Description = "Allow (1) or stop (0) the melee swing.")]
[RuleParam(1, "enabled", RuleParamKind.Bool)]
public sealed class CreatureAutoAttackAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        runner.Context.Ai.SetMeleeEnabled(context.Action.Param(1) != 0);
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionCombatMovement, "creature.combat_movement",
    Description = "Allow (1) or stop (0) running after the victim; no change, or a cast in progress, fails the action.")]
[RuleParam(1, "enabled", RuleParamKind.Bool)]
public sealed class CreatureCombatMovementAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        bool want = context.Action.Param(1) != 0;
        if (runner.Context.Ai.CombatMovement == want || runner.Context.IsCasting)
        {
            return false;
        }

        runner.Context.Ai.CombatMovement = want;
        runner.System?.ApplyCombatMovement(runner.Me);
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionIncPhase, "creature.inc_phase", Description = "Add a value to the phase (negative decrements); the result is kept within 0..31.")]
[RuleParam(1, "delta", RuleParamKind.Int)]
public sealed class CreatureIncPhaseAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        runner.Phase = Math.Clamp(runner.Phase + context.Action.Param(1), 0, CreatureEventAI.MaxPhase - 1);
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionRandomPhase, "creature.random_phase", Description = "The phase becomes one of three by the firing's random value.")]
[RuleParam(1, "phase_1", RuleParamKind.Int)]
[RuleParam(2, "phase_2", RuleParamKind.Int)]
[RuleParam(3, "phase_3", RuleParamKind.Int)]
public sealed class CreatureRandomPhaseAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        runner.Phase = Math.Clamp(CreatureActionRandom.Pick(runner, context), 0, CreatureEventAI.MaxPhase - 1);
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionRandomPhaseRange, "creature.random_phase_range",
    Description = "The phase becomes min + random % (max - min + 1); a maximum not above the minimum changes nothing.")]
[RuleParam(1, "phase_min", RuleParamKind.Int)]
[RuleParam(2, "phase_max", RuleParamKind.Int)]
public sealed class CreatureRandomPhaseRangeAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        int min = context.Action.Param(1);
        int max = context.Action.Param(2);
        if (max <= min || min < 0)
        {
            return true; // cmangos logs the row and leaves the phase (the action still counts as done)
        }

        uint span = (uint)(max - min + 1);
        runner.Phase = Math.Clamp((int)(runner.ActionRandom() % span) + min, 0, CreatureEventAI.MaxPhase - 1);
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionEvade, "creature.evade_now", Description = "The creature evades; with combat_only, combat just stops.")]
[RuleParam(1, "combat_only", RuleParamKind.Bool)]
public sealed class CreatureEvadeAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (context.Action.Param(1) != 0)
        {
            if (runner.System is { } system)
            {
                system.Map.Combat.CombatStop(runner.Me);
                if (runner.Me.Combat.HasThreatList)
                {
                    runner.Me.Combat.Threat.Clear();
                }
            }
        }
        else
        {
            runner.System?.EnterEvadeMode(runner.Me, CreatureAnomalies.ScriptedEvadeReason);
        }

        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionFleeForAssist, "creature.flee_for_assist", Description = "Flee to get help; fails without a victim to flee from.")]
public sealed class CreatureFleeForAssistAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (runner.System is not { } system || !runner.Me.IsAlive || runner.Context.IsEvading || runner.Context.Victim is null)
        {
            return false;
        }

        system.FleeForAssistance(runner.Me);
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionDie, "creature.die", Description = "The creature dies; fails on a dead creature.")]
public sealed class CreatureDieAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (runner.System is not { } system || !runner.Me.IsAlive)
        {
            return false;
        }

        system.KillCreature(runner.Me);
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionZoneCombatPulse, "creature.zone_combat_pulse",
    Description = "In a dungeon or raid every living player of the map enters the fight; a creature with no victim attacks the closest unit of its threat list.")]
public sealed class CreatureZoneCombatPulseAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (runner.System is { } system)
        {
            system.SetInCombatWithZone(runner.Me);
            system.AttackClosestEnemy(runner.Me);
        }

        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionCallForHelp, "creature.call_for_help", Description = "Call nearby creatures to help within a radius.")]
[RuleParam(1, "radius_yards", RuleParamKind.Int)]
public sealed class CreatureCallForHelpAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        runner.System?.CallForHelp(runner.Me, context.Action.Param(1));
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSendAiEvent, "creature.send_ai_event",
    Description = "Send an AI event to the creatures around, with the target as its invoker; fails when the target does not resolve.")]
[RuleParam(1, "event_type", RuleParamKind.NonNegative)]
[RuleParam(2, "radius_yards", RuleParamKind.Int)]
public sealed class CreatureSendAiEventAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (runner.System is not { } system || target is null)
        {
            return false;
        }

        system.SendAiEventAround(runner.Me, (uint)context.Action.Param(1), target, context.Action.Param(2));
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionAttackStart, "creature.attack_start", Description = "The creature attacks the target; a target that does not resolve or is dead fails.")]
public sealed class CreatureAttackStartAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (target is not { IsAlive: true })
        {
            return false;
        }

        runner.Context.Ai.AttackStart(target);
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionStartRelayScript, "creature.start_relay_script",
    Description = "Start a relay script with the target as its source and the creature as its target; a negative id is a relay template. Replaced by start_rule_list when the relays are converted.")]
[RuleParam(1, "relay_id", RuleParamKind.Int, Description = "dbscripts_on_relay id; negative: a relay template.")]
public sealed class CreatureStartRelayScriptAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (target is null || runner.System is not { } system)
        {
            return false;
        }

        int relay = context.Action.Param(1);
        uint relayId;
        if (relay < 0)
        {
            relayId = system.SelectRelayFromTemplate((uint)-relay);
            if (relayId == 0)
            {
                return true; // the cmangos `break`: nothing chosen, the action still counts as done
            }
        }
        else
        {
            relayId = (uint)relay;
        }

        system.StartRelayScript(relayId, target, runner.Me);
        return true;
    }
}

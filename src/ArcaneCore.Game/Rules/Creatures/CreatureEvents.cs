using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Rules.Creatures;

namespace ArcaneCore.Game.Rules.Creatures;

// The creature host's events. Each class carries what the EventAI handler of the same behaviour carried (mangos-classic
// src/game/AI/EventAI/CreatureEventAI.cpp CheckEvent :255-557, the timer sets IsTimerExecutedEvent :165-192 and IsTimerBasedEvent :209-242, and the
// per-type branches of JustRespawned / Reset / EnterCombat), expressed on a rule instead of a row:
//   event_param1..4  the type's own parameters (the initial timers for the timer events);
//   cooldown_min_ms/cooldown_max_ms  the repeat range of EventAI (the timer re-armed after the event ran);
//   phase_mask / chance_pct          the row's phase mask (inverted) and chance.
// The runner (CreatureRuleRunner) drives them; the engine's Matches entry point only accepts the rule the runner is processing.

/// <summary>Why a creature event is considered: the signal it listens to, its timer behaviour and what it checks.</summary>
public abstract class CreatureEvent : RuleEvent
{
    /// <summary>The game signal that makes the runner consider the rules of this type (<see cref="CreatureSignal.None"/> for periodic ones).</summary>
    internal virtual CreatureSignal Signal => CreatureSignal.None;

    /// <summary>Considered at every batch whatever its timer and armed state (cmangos special-cases TARGET_NOT_REACHABLE).</summary>
    internal virtual bool CheckedEveryBatch => false;

    /// <summary>cmangos IsRepeatableEvent: false for the events that happen once per spawn or fight, which the repeatable flag does not gate.</summary>
    internal virtual bool NaturallyRepeatable => true;

    /// <summary>cmangos IsTimerBasedEvent: finishing the event re-arms its timer from the repeat range.</summary>
    internal virtual bool TimerBased => false;

    /// <summary>cmangos IsTimerExecutedEvent: the periodic batch evaluates the event whenever its timer is 0.</summary>
    internal virtual bool TimerExecuted => false;

    /// <summary>Whether entering combat arms the rule and considers it at once (cmangos EVENT_T_AGGRO).</summary>
    internal virtual bool ArmsOnEnterCombat => false;

    /// <summary>The type-specific condition of cmangos <c>CheckEvent</c> (after the generic gates). May set the rule's event target.</summary>
    internal abstract bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker);

    /// <summary>Whether a rule of this type is considered at all when its signal fires (the SPAWNED condition).</summary>
    internal virtual bool AllowTrigger(CreatureRuleRunner runner, CompiledRule rule) => true;

    internal virtual bool MatchesSpell(CompiledRule rule, SpellInfo spell) => false;

    internal virtual bool MatchesUnit(CreatureRuleRunner runner, CompiledRule rule, Unit who) => false;

    internal virtual bool MatchesEmote(CompiledRule rule, uint textEmote) => false;

    internal virtual bool MatchesAiEvent(CompiledRule rule, uint eventType, Unit sender) => false;

    /// <summary>cmangos JustRespawned's per-type branch for a fresh rule (default: armed, timer 0).</summary>
    internal virtual void OnRespawn(CreatureRuleRunner runner, int slot, CompiledRule rule) => runner.ArmNow(slot);

    /// <summary>cmangos Reset's per-type branch (default: armed, timer 0).</summary>
    internal virtual void OnReset(CreatureRuleRunner runner, int slot, CompiledRule rule) => runner.ArmNow(slot);

    /// <summary>cmangos EnterCombat's per-type branch (default: nothing).</summary>
    internal virtual void OnEnterCombat(CreatureRuleRunner runner, int slot, CompiledRule rule)
    {
    }

    /// <summary>The engine's check: only the rule the runner is processing is accepted (the runner did the type's own check already).</summary>
    public override bool Matches(CompiledRule rule, in RuleEventArgs args)
        => CreatureRuleRunner.Current is { } runner && runner.Frame.RuleId == rule.Id;

    /// <summary>Event parameter <paramref name="index"/> as the unsigned value cmangos stores.</summary>
    protected static uint U(CompiledRule rule, int index) => unchecked((uint)rule.EventParam(index));
}

/// <summary>The timer events: initial timer in param1/param2, repeat range in the rule's cooldown.</summary>
public abstract class CreatureTimerEvent : CreatureEvent
{
    internal override bool TimerBased => true;

    internal override bool TimerExecuted => true;

    /// <summary>Arm the initial timer (cmangos UpdateRepeatTimer with the initial range; enabled when it set a timer).</summary>
    protected static void ArmInitial(CreatureRuleRunner runner, int slot, CompiledRule rule) => runner.Arm(slot, U(rule, 1), U(rule, 2));
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventTimerInCombat, "creature.timer_in_combat",
    Description = "While in combat, after an initial delay and then every repeat interval (the rule's cooldown range); the initial delay starts when combat starts.")]
[RuleParam(1, "initial_min_ms", RuleParamKind.Int, Description = "Initial delay minimum in ms.")]
[RuleParam(2, "initial_max_ms", RuleParamKind.Int, Description = "Initial delay maximum in ms (below the minimum: never fires).")]
public sealed class CreatureTimerInCombatEvent : CreatureTimerEvent
{
    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker) => runner.Context.InCombat;

    internal override void OnRespawn(CreatureRuleRunner runner, int slot, CompiledRule rule) => ArmInitial(runner, slot, rule);

    internal override void OnReset(CreatureRuleRunner runner, int slot, CompiledRule rule)
    {
    }

    internal override void OnEnterCombat(CreatureRuleRunner runner, int slot, CompiledRule rule) => ArmInitial(runner, slot, rule);
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventTimerOutOfCombat, "creature.timer_out_of_combat",
    Description = "While out of combat and not evading, after an initial delay (armed at spawn and on reset) and then every repeat interval.")]
[RuleParam(1, "initial_min_ms", RuleParamKind.Int, Description = "Initial delay minimum in ms.")]
[RuleParam(2, "initial_max_ms", RuleParamKind.Int, Description = "Initial delay maximum in ms.")]
public sealed class CreatureTimerOutOfCombatEvent : CreatureTimerEvent
{
    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker) => !runner.Context.InCombat && !runner.Context.IsEvading;

    internal override void OnRespawn(CreatureRuleRunner runner, int slot, CompiledRule rule) => ArmInitial(runner, slot, rule);

    internal override void OnReset(CreatureRuleRunner runner, int slot, CompiledRule rule) => ArmInitial(runner, slot, rule);
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventTimer, "creature.timer",
    Description = "In and out of combat, after an initial delay (armed at spawn only) and then every repeat interval.")]
[RuleParam(1, "initial_min_ms", RuleParamKind.Int, Description = "Initial delay minimum in ms.")]
[RuleParam(2, "initial_max_ms", RuleParamKind.Int, Description = "Initial delay maximum in ms.")]
public sealed class CreatureTimerGenericEvent : CreatureTimerEvent
{
    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker) => true;

    internal override void OnRespawn(CreatureRuleRunner runner, int slot, CompiledRule rule) => ArmInitial(runner, slot, rule);

    internal override void OnReset(CreatureRuleRunner runner, int slot, CompiledRule rule)
    {
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventHealthPct, "creature.health_pct",
    Description = "The creature's health percent is within [min, max]; in combat unless allow_out_of_combat is set.")]
[RuleParam(1, "max_pct", RuleParamKind.Int, Description = "Upper bound, percent.")]
[RuleParam(2, "min_pct", RuleParamKind.Int, Description = "Lower bound, percent.")]
[RuleParam(3, "allow_out_of_combat", RuleParamKind.Bool, Description = "1: also out of combat.")]
public sealed class CreatureHealthPctEvent : CreatureTimerEvent
{
    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker)
    {
        Creature me = runner.Me;
        if (me.MaxHealth == 0 || (U(rule, 3) == 0 && !runner.Context.InCombat))
        {
            return false;
        }

        uint percent = (uint)((ulong)me.Health * 100 / me.MaxHealth);
        return percent <= U(rule, 1) && percent >= U(rule, 2);
    }
}

/// <summary>The percent-range events (Max%, Min%): in combat, the percent of a resource of the creature or its victim inside [min, max].</summary>
public abstract class CreaturePercentRangeEvent : CreatureTimerEvent
{
    /// <summary>The percent of the observed resource, or null when it cannot be evaluated (no resource, not in combat).</summary>
    protected abstract uint? Percent(EventAiContext context);

    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker)
        => Percent(runner.Context) is { } percent && percent <= U(rule, 1) && percent >= U(rule, 2);

    protected static uint? Ratio(uint value, uint max) => max == 0 ? null : (uint)((ulong)value * 100 / max);
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventManaPct, "creature.mana_pct", Description = "In combat, the creature's mana percent is within [min, max].")]
[RuleParam(1, "max_pct", RuleParamKind.Int)]
[RuleParam(2, "min_pct", RuleParamKind.Int)]
public sealed class CreatureManaPctEvent : CreaturePercentRangeEvent
{
    protected override uint? Percent(EventAiContext context)
        => context.InCombat && context.Me.PowerType == PowerType.Mana
            ? Ratio(MapCombat.GetPower(context.Me, PowerType.Mana), MapCombat.GetMaxPower(context.Me, PowerType.Mana))
            : null;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventEnergyPct, "creature.energy_pct", Description = "In combat, the creature's energy percent is within [min, max].")]
[RuleParam(1, "max_pct", RuleParamKind.Int)]
[RuleParam(2, "min_pct", RuleParamKind.Int)]
public sealed class CreatureEnergyPctEvent : CreaturePercentRangeEvent
{
    protected override uint? Percent(EventAiContext context)
        => context.InCombat ? Ratio(MapCombat.GetPower(context.Me, PowerType.Energy), MapCombat.GetMaxPower(context.Me, PowerType.Energy)) : null;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventTargetHealthPct, "creature.target_health_pct", Description = "In combat, the victim's health percent is within [min, max].")]
[RuleParam(1, "max_pct", RuleParamKind.Int)]
[RuleParam(2, "min_pct", RuleParamKind.Int)]
public sealed class CreatureTargetHealthPctEvent : CreaturePercentRangeEvent
{
    protected override uint? Percent(EventAiContext context)
        => context.InCombat && context.Victim is { } victim ? Ratio(victim.Health, victim.MaxHealth) : null;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventTargetManaPct, "creature.target_mana_pct", Description = "In combat, the victim's mana percent is within [min, max] (the victim needs mana).")]
[RuleParam(1, "max_pct", RuleParamKind.Int)]
[RuleParam(2, "min_pct", RuleParamKind.Int)]
public sealed class CreatureTargetManaPctEvent : CreaturePercentRangeEvent
{
    protected override uint? Percent(EventAiContext context)
        => context.InCombat && context.Victim is { } victim && victim.PowerType == PowerType.Mana
            ? Ratio(MapCombat.GetPower(victim, PowerType.Mana), MapCombat.GetMaxPower(victim, PowerType.Mana))
            : null;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventTargetInRange, "creature.target_in_range",
    Description = "In combat, the victim is inside [min, max] yards measured between the bounding radii (min only when above 0).")]
[RuleParam(1, "min_yards", RuleParamKind.Int)]
[RuleParam(2, "max_yards", RuleParamKind.Int)]
public sealed class CreatureTargetInRangeEvent : CreatureTimerEvent
{
    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker)
    {
        EventAiContext context = runner.Context;
        if (!context.InCombat || context.Victim is not { } victim || !ReferenceEquals(victim.Map, context.Me.Map))
        {
            return false;
        }

        Creature me = context.Me;
        float dx = me.X - victim.X;
        float dy = me.Y - victim.Y;
        float dz = me.Z - victim.Z;
        float distanceSquared = (dx * dx) + (dy * dy) + (dz * dz);
        float sizeFactor = me.BoundingRadius + victim.BoundingRadius;
        float min = U(rule, 1);
        if (min > 0 && distanceSquared < (min + sizeFactor) * (min + sizeFactor))
        {
            return false;
        }

        float max = U(rule, 2) + sizeFactor;
        return distanceSquared < max * max;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventTargetCasting, "creature.target_casting", Description = "In combat, the victim is casting a non-melee spell.")]
public sealed class CreatureTargetCastingEvent : CreatureTimerEvent
{
    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker)
        => runner.Context.InCombat && runner.Context.Victim is { } victim && runner.Context.IsCastingNonMelee(victim);
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventFacingTarget, "creature.facing_target",
    Description = "In combat, the creature is within 5 yards of its victim and behind it (front 0) or in front of it (front 1).")]
[RuleParam(1, "front", RuleParamKind.Int, Description = "0 behind the victim, 1 in front of it.")]
public sealed class CreatureFacingTargetEvent : CreatureTimerEvent
{
    private const float MaxDistance = 5.0f;

    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker)
    {
        EventAiContext context = runner.Context;
        if (!context.InCombat || context.Victim is not { } victim || !ReferenceEquals(victim.Map, context.Me.Map))
        {
            return false;
        }

        Creature me = context.Me;
        float dx = me.X - victim.X;
        float dy = me.Y - victim.Y;
        float dz = me.Z - victim.Z;

        // GetDistance(x, y, z, DIST_CALC_COMBAT_REACH) subtracts only the measured object's own combat reach (Object.cpp:1296-1305, 1734-1747).
        float distance = MathF.Max(0f, MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz)) - me.GetFloat(UpdateFields.UnitFieldCombatreach));
        if (distance > MaxDistance)
        {
            return false;
        }

        bool inFrontOfVictim = MapCombat.HasInArc(victim, me, MathF.PI);
        return U(rule, 1) switch
        {
            0 => !inFrontOfVictim,
            1 => inFrontOfVictim,
            _ => true,
        };
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventTargetUnreachable, "creature.target_unreachable",
    Description = "The creature chases a victim that its movement reports unreachable; considered at every batch.")]
public sealed class CreatureTargetUnreachableEvent : CreatureEvent
{
    internal override bool CheckedEveryBatch => true;

    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker)
        => runner.Context.Victim is not null && runner.Context.Ai.CombatMovement && !runner.Me.Motion.IsReachable;
}

/// <summary>The aura events (spell, amount): the creature or its victim has at least (or fewer than) <c>amount</c> stacks.</summary>
public abstract class CreatureAuraEvent(bool onVictim, bool wantPresent) : CreatureTimerEvent
{
    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker)
    {
        EventAiContext context = runner.Context;
        Unit? unit = context.Me;
        if (onVictim)
        {
            if (!context.InCombat || context.Victim is not { } victim)
            {
                return false;
            }

            unit = victim;
        }

        int stacks = context.AuraStacks(unit, U(rule, 1));
        bool enough = stacks > 0 && stacks >= U(rule, 2);
        return enough == wantPresent;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventHasAura, "creature.has_aura", Description = "The creature has the aura with at least the given stacks.")]
[RuleParam(1, "spell_id", RuleParamKind.Int)]
[RuleParam(2, "stacks", RuleParamKind.Int)]
public sealed class CreatureHasAuraEvent() : CreatureAuraEvent(onVictim: false, wantPresent: true);

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventTargetHasAura, "creature.target_has_aura", Description = "In combat, the victim has the aura with at least the given stacks.")]
[RuleParam(1, "spell_id", RuleParamKind.Int)]
[RuleParam(2, "stacks", RuleParamKind.Int)]
public sealed class CreatureTargetHasAuraEvent() : CreatureAuraEvent(onVictim: true, wantPresent: true);

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventMissingAura, "creature.missing_aura", Description = "The creature has fewer than the given stacks of the aura (none counts).")]
[RuleParam(1, "spell_id", RuleParamKind.Int)]
[RuleParam(2, "stacks", RuleParamKind.Int)]
public sealed class CreatureMissingAuraEvent() : CreatureAuraEvent(onVictim: false, wantPresent: false);

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventTargetMissingAura, "creature.target_missing_aura", Description = "In combat, the victim has fewer than the given stacks of the aura.")]
[RuleParam(1, "spell_id", RuleParamKind.Int)]
[RuleParam(2, "stacks", RuleParamKind.Int)]
public sealed class CreatureTargetMissingAuraEvent() : CreatureAuraEvent(onVictim: true, wantPresent: false);

/// <summary>Base of the searches that fill the rule's event target: timer driven, armed with the repeat range when the fight starts.</summary>
public abstract class CreatureSearchEvent : CreatureTimerEvent
{
    /// <summary>cmangos EnterCombat: UpdateRepeatTimer(repeat range), enabled when it set a timer.</summary>
    internal override void OnEnterCombat(CreatureRuleRunner runner, int slot, CompiledRule rule)
        => runner.Arm(slot, (uint)rule.CooldownMinMs, (uint)rule.CooldownMaxMs);
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventFriendlyHealth, "creature.friendly_health",
    Description = "In combat, the friendly creature in radius that misses the most health (more than the deficit) becomes the event target.")]
[RuleParam(1, "deficit", RuleParamKind.Int, Description = "Health missing, points or percent.")]
[RuleParam(2, "radius_yards", RuleParamKind.Int)]
[RuleParam(3, "is_percent", RuleParamKind.Bool, Description = "1: the deficit is a percent.")]
public sealed class CreatureFriendlyHealthEvent : CreatureSearchEvent
{
    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker)
    {
        EventAiContext context = runner.Context;
        if (!context.InCombat)
        {
            return false;
        }

        bool percent = U(rule, 3) != 0;
        float best = U(rule, 1);
        Creature? chosen = null;
        foreach (Creature friend in EventAiSearch.Friends(context, U(rule, 2), includeSelf: TargetsSelf(context, rule)))
        {
            if (!friend.Combat.IsInCombat || friend.MaxHealth == 0)
            {
                continue;
            }

            float missing = percent ? 100f - (friend.Health * 100f / friend.MaxHealth) : friend.MaxHealth - friend.Health;
            if (missing > best)
            {
                best = missing;
                chosen = friend;
            }
        }

        runner.SetEventTarget(slot, chosen);
        return chosen is not null;
    }

    /// <summary>cmangos friendlyHp.targetSelf: false when a cast at the event target uses a spell that excludes its caster.</summary>
    private static bool TargetsSelf(EventAiContext context, CompiledRule rule)
    {
        if (context.System?.AiServices.UnitSpells is not { } spells)
        {
            return true;
        }

        foreach (CompiledAction action in rule.Actions)
        {
            if (action.Action is CreatureCastAction && action.Target is CreatureEventTargetTarget && spells.ExcludesCaster((uint)action.Param(1)))
            {
                return false;
            }
        }

        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventFriendlyCrowdControlled, "creature.friendly_crowd_controlled",
    Description = "In combat, a friendly creature in radius that is stunned, confused, fleeing or rooted becomes the event target.")]
[RuleParam(1, "radius_yards", RuleParamKind.Int)]
public sealed class CreatureFriendlyCrowdControlledEvent : CreatureSearchEvent
{
    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker)
    {
        if (!runner.Context.InCombat)
        {
            return false;
        }

        Creature? found = EventAiSearch.Friends(runner.Context, U(rule, 1), includeSelf: true)
            .FirstOrDefault(f => f.Combat.IsInCombat && EventAiSearch.IsCrowdControlled(f));
        runner.SetEventTarget(slot, found);
        return found is not null;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventFriendlyMissingBuff, "creature.friendly_missing_buff",
    Description = "A friendly creature in radius without the aura becomes the event target (flags: 0 in combat, 1 in or out, 2 out of combat only).")]
[RuleParam(1, "spell_id", RuleParamKind.Int)]
[RuleParam(2, "radius_yards", RuleParamKind.Int)]
[RuleParam(3, "flags", RuleParamKind.Int, Description = "0 only in combat, 1 in and out of combat, 2 only out of combat.")]
public sealed class CreatureFriendlyMissingBuffEvent : CreatureSearchEvent
{
    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker)
    {
        EventAiContext context = runner.Context;
        uint flags = U(rule, 3);
        bool friendsInCombat;
        if ((flags & 0x3) == 0)
        {
            if (!context.InCombat)
            {
                return false;
            }

            friendsInCombat = true;
        }
        else if ((flags & 0x1) != 0)
        {
            friendsInCombat = context.InCombat;
        }
        else
        {
            if (context.InCombat)
            {
                return false;
            }

            friendsInCombat = false;
        }

        uint spellId = U(rule, 1);
        Creature? found = EventAiSearch.Friends(context, U(rule, 2), includeSelf: true)
            .FirstOrDefault(f => f.Combat.IsInCombat == friendsInCombat && context.AuraStacks(f, spellId) == 0);
        runner.SetEventTarget(slot, found);
        return found is not null;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventSelectThreatTarget, "creature.select_threat_target",
    Description = "A random unit of the threat list between min and max yards becomes the event target.")]
[RuleParam(1, "min_yards", RuleParamKind.Int)]
[RuleParam(2, "max_yards", RuleParamKind.Int)]
public sealed class CreatureSelectThreatTargetEvent : CreatureSearchEvent
{
    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker)
    {
        EventAiContext context = runner.Context;
        float min = U(rule, 1);
        float max = U(rule, 2);
        Unit[] candidates = [.. context.Threat.Select(t => t.Target).Where(t => t.IsAlive && InRange(context.Me, t, min, max))];
        Unit? chosen = candidates.Length == 0 ? null : candidates[context.Random(0, candidates.Length - 1)];
        runner.SetEventTarget(slot, chosen);
        return chosen is not null;
    }

    private static bool InRange(Creature me, Unit target, float min, float max)
    {
        float distanceSquared = WorldGeometry.DistanceSq3D(me, target);
        return distanceSquared >= min * min && distanceSquared <= max * max;
    }
}

// --- signal events ---------------------------------------------------------------------------------------------------

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventAggro, "creature.aggro", Description = "The creature enters combat (armed and considered at once; a reset leaves it alone).")]
public sealed class CreatureAggroEvent : CreatureEvent
{
    internal override bool NaturallyRepeatable => false;

    internal override bool ArmsOnEnterCombat => true;

    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker) => true;

    internal override void OnReset(CreatureRuleRunner runner, int slot, CompiledRule rule)
    {
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventKill, "creature.kill", Description = "The creature killed a unit (only a player with player_only).")]
[RuleParam(1, "player_only", RuleParamKind.Int, Description = "1: only a killed player counts.")]
public sealed class CreatureKillEvent : CreatureEvent
{
    internal override CreatureSignal Signal => CreatureSignal.Kill;

    internal override bool TimerBased => true;

    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker) => U(rule, 1) != 1 || invoker is Player;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventDeath, "creature.death",
    Description = "The creature died; with a condition, only when the conditions row holds for the player controlling the killer (none without a killer).")]
[RuleParam(1, "condition_id", RuleParamKind.NonNegative, Description = "conditions.condition_entry; 0 none. An unknown row fails closed.")]
public sealed class CreatureDeathEvent : CreatureEvent
{
    internal override CreatureSignal Signal => CreatureSignal.Death;

    internal override bool NaturallyRepeatable => false;

    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker)
        => EventAiSearch.ConditionHolds(runner.Context, U(rule, 1), invoker);
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventEvade, "creature.evade", Description = "The creature starts evading.")]
public sealed class CreatureEvadeEvent : CreatureEvent
{
    internal override CreatureSignal Signal => CreatureSignal.Evade;

    internal override bool NaturallyRepeatable => false;

    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker) => true;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventSpellHit, "creature.spell_hit",
    Description = "A spell hit the creature: spell id 0 or equal, and the spell's school bit in the mask.")]
[RuleParam(1, "spell_id", RuleParamKind.Int, Description = "0: any spell.")]
[RuleParam(2, "school_mask", RuleParamKind.Int)]
public sealed class CreatureSpellHitEvent : CreatureEvent
{
    internal override CreatureSignal Signal => CreatureSignal.SpellHit;

    internal override bool TimerBased => true;

    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker) => true;

    internal override bool MatchesSpell(CompiledRule rule, SpellInfo spell)
        => (rule.EventParam(1) == 0 || U(rule, 1) == spell.Id) && (U(rule, 2) & (1u << (int)spell.School)) != 0;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventSpellHitTarget, "creature.spell_hit_target",
    Description = "A spell the creature cast landed on a unit (the invoker): spell id 0 or equal, and the school bit in the mask.")]
[RuleParam(1, "spell_id", RuleParamKind.Int, Description = "0: any spell.")]
[RuleParam(2, "school_mask", RuleParamKind.Int)]
public sealed class CreatureSpellHitTargetEvent : CreatureEvent
{
    internal override CreatureSignal Signal => CreatureSignal.SpellHitTarget;

    internal override bool TimerBased => true;

    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker) => true;

    internal override bool MatchesSpell(CompiledRule rule, SpellInfo spell)
        => (rule.EventParam(1) == 0 || U(rule, 1) == spell.Id) && (U(rule, 2) & (1u << (int)spell.School)) != 0;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventSpawned, "creature.spawned",
    Description = "The creature (re)spawned and the condition holds: 0 always, 1 map = value, 2 zone or area = value.")]
[RuleParam(1, "condition", RuleParamKind.Int, Description = "0 always, 1 on map value, 2 in zone or area value.")]
[RuleParam(2, "value", RuleParamKind.Int, Description = "The map, zone or area id.")]
public sealed class CreatureSpawnedEvent : CreatureEvent
{
    internal override CreatureSignal Signal => CreatureSignal.Respawn;

    internal override bool NaturallyRepeatable => false;

    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker) => true;

    internal override bool AllowTrigger(CreatureRuleRunner runner, CompiledRule rule)
    {
        switch ((EventAiSpawnedCondition)U(rule, 1))
        {
            case EventAiSpawnedCondition.Always:
                return true;
            case EventAiSpawnedCondition.Map:
                return runner.Me.MapId == U(rule, 2);
            case EventAiSpawnedCondition.Zone:
            {
                (uint zone, uint area) = runner.Context.ZoneAndArea;
                uint wanted = U(rule, 2);
                return zone == wanted || area == wanted;
            }

            default:
                return false;
        }
    }

    public override string? Validate(in RuleValidationContext context)
        => context.Parameters[0] is 0 or 1 or 2 ? null : $"spawned condition {context.Parameters[0]} is not 0 (always), 1 (map) or 2 (zone)";
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventReachedHome, "creature.reached_home", Description = "The creature reached home after evading.")]
public sealed class CreatureReachedHomeEvent : CreatureEvent
{
    internal override CreatureSignal Signal => CreatureSignal.ReachedHome;

    internal override bool NaturallyRepeatable => false;

    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker) => true;
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventReceiveEmote, "creature.receive_emote",
    Description = "A player aimed the text emote at the creature (the player is the invoker); with a condition, only when it holds for that player.")]
[RuleParam(1, "text_emote", RuleParamKind.Int, Description = "EmotesText.dbc id.")]
[RuleParam(2, "condition_id", RuleParamKind.NonNegative, Description = "conditions.condition_entry; 0 none.")]
public sealed class CreatureReceiveEmoteEvent : CreatureEvent
{
    internal override CreatureSignal Signal => CreatureSignal.ReceiveEmote;

    internal override bool MatchesEmote(CompiledRule rule, uint textEmote) => U(rule, 1) == textEmote;

    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker)
        => EventAiSearch.ConditionHolds(runner.Context, U(rule, 2), invoker);
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventOutOfCombatLineOfSight, "creature.ooc_line_of_sight",
    Description = "While the creature has no victim, a unit moves within range and in line of sight (hostile, or not hostile with no_hostile).")]
[RuleParam(1, "no_hostile", RuleParamKind.Int, Description = "0: a hostile unit triggers; otherwise a unit that is not hostile.")]
[RuleParam(2, "range_yards", RuleParamKind.Int)]
[RuleParam(3, "player_only", RuleParamKind.Int, Description = "Non-zero: only a player triggers.")]
[RuleParam(4, "condition_id", RuleParamKind.NonNegative, Description = "conditions.condition_entry for the unit's player; 0 none.")]
public sealed class CreatureOutOfCombatLineOfSightEvent : CreatureEvent
{
    internal override CreatureSignal Signal => CreatureSignal.OutOfCombatLineOfSight;

    internal override bool TimerBased => true;

    internal override bool MatchesUnit(CreatureRuleRunner runner, CompiledRule rule, Unit who)
    {
        EventAiContext context = runner.Context;
        if (ReferenceEquals(who, context.Me) || !who.IsAlive || context.System is not { } system)
        {
            return false;
        }

        if (U(rule, 3) != 0 && who is not Player)
        {
            return false;
        }

        bool hostile = system.AiServices.Hostility.IsHostile(context.Me, who); // cmangos IsEnemy
        if ((U(rule, 1) != 0) == hostile)
        {
            return false;
        }

        float range = U(rule, 2);
        return WorldGeometry.DistanceSq3D(context.Me, who) <= range * range && system.IsInLineOfSight(context.Me, who);
    }

    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker)
        => EventAiSearch.ConditionHolds(runner.Context, U(rule, 4), invoker);
}

/// <summary>The summon events: a creature of the entry that this creature summoned entered the world, died or left it (it is the invoker).</summary>
public abstract class CreatureSummonedEvent(CreatureSignal signal) : CreatureEvent
{
    internal override CreatureSignal Signal => signal;

    internal override bool TimerBased => true;

    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker)
        => invoker is Creature summoned && summoned.Entry == U(rule, 1);
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventSummoned, "creature.summoned", Description = "A creature of the entry that this creature summoned entered the world.")]
[RuleParam(1, "entry", RuleParamKind.Int, Description = "Creature entry.")]
public sealed class CreatureSummonedUnitEvent() : CreatureSummonedEvent(CreatureSignal.Summoned);

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventSummonedDied, "creature.summoned_died", Description = "A creature of the entry that this creature summoned died.")]
[RuleParam(1, "entry", RuleParamKind.Int, Description = "Creature entry.")]
public sealed class CreatureSummonedDiedEvent() : CreatureSummonedEvent(CreatureSignal.SummonedDied);

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventSummonedDespawned, "creature.summoned_despawned", Description = "A creature of the entry that this creature summoned left the world.")]
[RuleParam(1, "entry", RuleParamKind.Int, Description = "Creature entry.")]
public sealed class CreatureSummonedDespawnedEvent() : CreatureSummonedEvent(CreatureSignal.SummonedDespawned);

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.EventReceiveAiEvent, "creature.receive_ai_event",
    Description = "An AI event of the type reached the creature, from a sender of the entry (0: any sender); the sender is the event_sender target.")]
[RuleParam(1, "ai_event_type", RuleParamKind.Int)]
[RuleParam(2, "sender_entry", RuleParamKind.Int, Description = "0: any sender.")]
public sealed class CreatureReceiveAiEventEvent : CreatureEvent
{
    internal override CreatureSignal Signal => CreatureSignal.AiEvent;

    internal override bool MatchesAiEvent(CompiledRule rule, uint eventType, Unit sender)
        => U(rule, 1) == eventType && (rule.EventParam(2) == 0 || U(rule, 2) == sender.GetUInt32(UpdateFields.ObjectFieldEntry));

    internal override bool Check(CreatureRuleRunner runner, int slot, CompiledRule rule, Unit? invoker) => true;
}

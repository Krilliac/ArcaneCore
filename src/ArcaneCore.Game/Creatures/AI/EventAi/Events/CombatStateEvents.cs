using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures;

// The in-combat state events. Parameter layouts: mangos-classic src/game/AI/EventAI/CreatureEventAI.h:618-790;
// conditions: CreatureEventAI.cpp CheckEvent (:255-557). Timer sets: IsTimerExecutedEvent (:165-192) and
// IsTimerBasedEvent (:209-242) list every type below.

/// <summary>
/// Base for the percent-range events (HP-style layout: Max%, Min%, RepeatMin, RepeatMax): in combat, a power or health
/// percent inside [min, max] of the creature or its victim.
/// </summary>
public abstract class PercentRangeEventHandler : EventAiEventHandler
{
    public override bool TimerBased => true;

    public override bool TimerExecuted => true;

    /// <summary>The percent of the observed resource, or null when the event cannot be evaluated (no resource, not in combat).</summary>
    protected abstract uint? Percent(EventAiContext context);

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker)
        => Percent(context) is { } percent && percent <= holder.Param(0) && percent >= holder.Param(1);

    protected static uint? Ratio(uint value, uint max) => max == 0 ? null : (uint)((ulong)value * 100 / max);
}

/// <summary>EVENT_T_MANA (3): the creature's mana percent; needs a mana creature in combat (:280-290).</summary>
public sealed class ManaPercentEvent : PercentRangeEventHandler
{
    public override byte EventType => 3;

    protected override uint? Percent(EventAiContext context)
        => context.InCombat && context.Me.PowerType == PowerType.Mana
            ? Ratio(MapCombat.GetPower(context.Me, PowerType.Mana), MapCombat.GetMaxPower(context.Me, PowerType.Mana))
            : null;
}

/// <summary>EVENT_T_ENERGY (31): the creature's energy percent in combat (:488-499).</summary>
public sealed class EnergyPercentEvent : PercentRangeEventHandler
{
    public override byte EventType => 31;

    protected override uint? Percent(EventAiContext context)
        => context.InCombat ? Ratio(MapCombat.GetPower(context.Me, PowerType.Energy), MapCombat.GetMaxPower(context.Me, PowerType.Energy)) : null;
}

/// <summary>EVENT_T_TARGET_HP (12): the victim's health percent in combat (:343-352).</summary>
public sealed class TargetHealthPercentEvent : PercentRangeEventHandler
{
    public override byte EventType => 12;

    protected override uint? Percent(EventAiContext context)
        => context.InCombat && context.Victim is { } victim ? Ratio(victim.Health, victim.MaxHealth) : null;
}

/// <summary>EVENT_T_TARGET_MANA (18): the victim's mana percent; the victim needs mana (:446-456).</summary>
public sealed class TargetManaPercentEvent : PercentRangeEventHandler
{
    public override byte EventType => 18;

    protected override uint? Percent(EventAiContext context)
        => context.InCombat && context.Victim is { } victim && victim.PowerType == PowerType.Mana
            ? Ratio(MapCombat.GetPower(victim, PowerType.Mana), MapCombat.GetMaxPower(victim, PowerType.Mana))
            : null;
}

/// <summary>
/// EVENT_T_RANGE (9): MinDist, MaxDist, RepeatMin, RepeatMax. The victim is inside [min, max] yards measured between the
/// bounding radii (cmangos IsInRange with combat = true: distance² compared with (range + both bounding radii)², the
/// minimum only when above 0; :331-336, Object.cpp:1401-1420).
/// </summary>
public sealed class RangeEvent : EventAiEventHandler
{
    public override byte EventType => 9;

    public override bool TimerBased => true;

    public override bool TimerExecuted => true;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker)
    {
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
        float min = holder.Param(0);
        if (min > 0 && distanceSquared < (min + sizeFactor) * (min + sizeFactor))
        {
            return false;
        }

        float max = holder.Param(1) + sizeFactor;
        return distanceSquared < max * max;
    }
}

/// <summary>EVENT_T_TARGET_CASTING (13): RepeatMin, RepeatMax (parameters 0 and 1, GetRepeatTimers :559-566); the victim is casting (:353-355).</summary>
public sealed class TargetCastingEvent : EventAiEventHandler
{
    public override byte EventType => 13;

    public override bool TimerBased => true;

    public override bool TimerExecuted => true;

    public override int RepeatMinParam => 0;

    public override int RepeatMaxParam => 1;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker)
        => context.InCombat && context.Victim is { } victim && context.IsCastingNonMelee(victim);
}

/// <summary>
/// EVENT_T_FACING_TARGET (33): BackOrFront (0 the creature is behind its victim, 1 in front), unused, RepeatMin, RepeatMax.
/// The creature is within 5 yd of the victim (its own combat reach subtracted) and behind it (outside the victim's frontal
/// half circle) or in front of it (:518-542, Object.cpp:1572-1580).
/// </summary>
public sealed class FacingTargetEvent : EventAiEventHandler
{
    private const float MaxDistance = 5.0f;

    public override byte EventType => 33;

    public override bool TimerBased => true;

    public override bool TimerExecuted => true;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker)
    {
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
        return holder.Param(0) switch
        {
            0 => !inFrontOfVictim,
            1 => inFrontOfVictim,
            _ => true,
        };
    }
}

/// <summary>
/// EVENT_T_TARGET_NOT_REACHABLE (36): the creature has a victim, chases it, and the movement generator on top reports it
/// unreachable (:544-547). Considered at every batch (UpdateEventTimers :1937). The chase generator reports a victim unreachable
/// when the pathfinder found no path or only a partial one (<c>TargetedMovementGenerator.IsReachable</c>), so with navigation data
/// installed the row fires from the first batch after the chase got stuck until the creature gives the victim up
/// (<c>Creatures:UnreachableTargetEvadeMs</c>); without navigation data nothing is ever unreachable.
/// </summary>
public sealed class TargetNotReachableEvent : EventAiEventHandler
{
    public override byte EventType => 36;

    public override bool CheckedEveryBatch => true;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker)
        => context.Victim is not null && context.Ai.CombatMovement && !context.Me.Motion.IsReachable;
}

/// <summary>
/// The aura events: SpellId, Amount, RepeatMin, RepeatMax (CreatureEventAI.h:733-744). AURA (23) fires when the creature
/// has at least Amount stacks, MISSING_AURA (27) when it has fewer, TARGET_AURA (24) and TARGET_MISSING_AURA (28) the same
/// for the victim in combat (:458-491).
/// </summary>
public abstract class AuraStackEventHandler(bool onVictim, bool wantPresent) : EventAiEventHandler
{
    public override bool TimerBased => true;

    public override bool TimerExecuted => true;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker)
    {
        Unit? unit = context.Me;
        if (onVictim)
        {
            if (!context.InCombat || context.Victim is not { } victim)
            {
                return false;
            }

            unit = victim;
        }

        int stacks = context.AuraStacks(unit, holder.Param(0));
        bool enough = stacks > 0 && stacks >= holder.Param(1);
        return enough == wantPresent;
    }
}

/// <summary>EVENT_T_AURA (23): the creature has the aura with at least Amount stacks.</summary>
public sealed class AuraEvent() : AuraStackEventHandler(onVictim: false, wantPresent: true)
{
    public override byte EventType => 23;
}

/// <summary>EVENT_T_TARGET_AURA (24): the victim has the aura with at least Amount stacks.</summary>
public sealed class TargetAuraEvent() : AuraStackEventHandler(onVictim: true, wantPresent: true)
{
    public override byte EventType => 24;
}

/// <summary>EVENT_T_MISSING_AURA (27): the creature has fewer than Amount stacks (none counts).</summary>
public sealed class MissingAuraEvent() : AuraStackEventHandler(onVictim: false, wantPresent: false)
{
    public override byte EventType => 27;
}

/// <summary>EVENT_T_TARGET_MISSING_AURA (28): the victim has fewer than Amount stacks (none counts).</summary>
public sealed class TargetMissingAuraEvent() : AuraStackEventHandler(onVictim: true, wantPresent: false)
{
    public override byte EventType => 28;
}

/// <summary>
/// EVENT_T_TIMER_GENERIC (29): InitialMin, InitialMax, RepeatMin, RepeatMax; evaluated in and out of combat. The initial
/// timer is armed at (re)spawn only; reset and combat entry leave it alone (:1398-1405, :1439-1443).
/// </summary>
public sealed class TimerGenericEvent : EventAiEventHandler
{
    public override byte EventType => 29;

    public override bool TimerBased => true;

    public override bool TimerExecuted => true;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker) => true;

    public override void OnRespawn(EventAiContext context, EventAiHolder holder)
    {
        if (holder.UpdateRepeatTimer(context, holder.Param(0), holder.Param(1)))
        {
            holder.Enabled = true;
        }
    }

    public override void OnReset(EventAiContext context, EventAiHolder holder)
    {
    }
}

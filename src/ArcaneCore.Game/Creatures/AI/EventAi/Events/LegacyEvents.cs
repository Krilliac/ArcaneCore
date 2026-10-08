using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

// The ten event types ArcaneCore ran before the engine existed, ported to cmangos-classic semantics.
// Parameter layouts: src/game/AI/EventAI/CreatureEventAI.h:618-760 (the unions); conditions:
// CreatureEventAI.cpp CheckEvent (:255-557).

/// <summary>EVENT_T_TIMER_IN_COMBAT (0): InitialMin, InitialMax, RepeatMin, RepeatMax; only while in combat (:262-264).</summary>
public sealed class TimerInCombatEvent : EventAiEventHandler
{
    public override byte EventType => (byte)EventAiEventType.TimerInCombat;

    public override bool TimerBased => true;

    public override bool TimerExecuted => true;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker) => context.InCombat;

    // JustRespawned and EnterCombat arm the initial timer; Reset does not touch combat timers (:1398-1405, :1439-1443, :1609-1614).
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

    public override void OnEnterCombat(EventAiContext context, EventAiHolder holder)
    {
        if (holder.UpdateRepeatTimer(context, holder.Param(0), holder.Param(1)))
        {
            holder.Enabled = true;
        }
    }
}

/// <summary>EVENT_T_TIMER_OOC (1): InitialMin, InitialMax, RepeatMin, RepeatMax; only out of combat and not evading (:265-268).</summary>
public sealed class TimerOutOfCombatEvent : EventAiEventHandler
{
    public override byte EventType => (byte)EventAiEventType.TimerOutOfCombat;

    public override bool TimerBased => true;

    public override bool TimerExecuted => true;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker) => !context.InCombat && !context.IsEvading;

    // Both JustRespawned and Reset arm the initial timer (:1398-1405, :1445-1449).
    public override void OnRespawn(EventAiContext context, EventAiHolder holder) => Arm(context, holder);

    public override void OnReset(EventAiContext context, EventAiHolder holder) => Arm(context, holder);

    private static void Arm(EventAiContext context, EventAiHolder holder)
    {
        if (holder.UpdateRepeatTimer(context, holder.Param(0), holder.Param(1)))
        {
            holder.Enabled = true;
        }
    }
}

/// <summary>
/// EVENT_T_HP (2): HPMax%, HPMin%, RepeatMin, RepeatMax, AllowOutOfCombat. In combat (unless allowed
/// outside) with the health percent inside [min, max] (:269-279).
/// </summary>
public sealed class HealthPercentEvent : EventAiEventHandler
{
    public override byte EventType => (byte)EventAiEventType.HealthPercent;

    public override bool TimerBased => true;

    public override bool TimerExecuted => true;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker)
    {
        Creature me = context.Me;
        if (me.MaxHealth == 0)
        {
            return false;
        }

        if (holder.Param(4) == 0 && !context.InCombat)
        {
            return false;
        }

        uint percent = (uint)((ulong)me.Health * 100 / me.MaxHealth);
        return percent <= holder.Param(0) && percent >= holder.Param(1);
    }
}

/// <summary>EVENT_T_AGGRO (4): no parameters; armed and considered when the creature enters combat (:1597-1614).</summary>
public sealed class AggroEvent : EventAiEventHandler
{
    public override byte EventType => (byte)EventAiEventType.Aggro;

    public override bool Repeatable => false;

    public override bool ArmsOnEnterCombat => true;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker) => true;

    // Reset leaves aggro events alone; the next EnterCombat re-arms them (:1439-1443).
    public override void OnReset(EventAiContext context, EventAiHolder holder)
    {
    }
}

/// <summary>
/// EVENT_T_KILL (5): RepeatMin, RepeatMax, PlayerOnly (CreatureEventAI.h:648-654). The repeat timers are
/// parameters 0 and 1 (GetRepeatTimers, :559-572), not 2 and 3 as most types use; with PlayerOnly = 1 only
/// a killed player counts (:280-283).
/// </summary>
public sealed class KillEvent : EventAiEventHandler
{
    public override byte EventType => (byte)EventAiEventType.Kill;

    public override EventAiTrigger Trigger => EventAiTrigger.Kill;

    public override bool TimerBased => true;

    public override int RepeatMinParam => 0;

    public override int RepeatMaxParam => 1;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker)
        => holder.Param(2) != 1 || invoker is Player;
}

/// <summary>
/// EVENT_T_DEATH (6): ConditionId (CreatureEventAI.h:655-659). A row without a condition fires on every death; a row with one fires only when
/// there is a killer and the conditions table is satisfied for the player controlling it (CheckEvent, CreatureEventAI.cpp:327-339:
/// <c>actionInvoker-&gt;GetControllingPlayer()</c>, else the killer itself; a killer no player controls is not a player, so a player
/// condition fails for it). Without a conditions table such a row never fires. classic-db z2815: 2 rows (the Hazzali wasps summon their
/// parasites when killed by a player on quest 7734, condition 100).
/// </summary>
public sealed class DeathEvent : EventAiEventHandler
{
    public override byte EventType => (byte)EventAiEventType.Death;

    public override EventAiTrigger Trigger => EventAiTrigger.Death;

    public override bool Repeatable => false;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker)
        => EventAiSearch.ConditionHolds(context, holder.Param(0), invoker);
}

/// <summary>EVENT_T_EVADE (7): no parameters; fires when the creature starts evading (:1475-1489).</summary>
public sealed class EvadeEvent : EventAiEventHandler
{
    public override byte EventType => (byte)EventAiEventType.Evade;

    public override EventAiTrigger Trigger => EventAiTrigger.Evade;

    public override bool Repeatable => false;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker) => true;
}

/// <summary>
/// EVENT_T_SPELLHIT (8): SpellId, SchoolMask, RepeatMin, RepeatMax (CreatureEventAI.h:660-667). Matches when the
/// spell id is 0 or equal and the spell's school bit is in the mask (:1667-1678); the repeat timers are
/// parameters 2 and 3.
/// </summary>
public sealed class SpellHitEvent : EventAiEventHandler
{
    public override byte EventType => (byte)EventAiEventType.SpellHit;

    public override EventAiTrigger Trigger => EventAiTrigger.SpellHit;

    public override bool TimerBased => true;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker) => true;

    public override bool MatchesSpell(CreatureAiEvent row, SpellInfo spell)
        => (row.Param1 == 0 || (uint)row.Param1 == spell.Id) && ((uint)row.Param2 & (1u << (int)spell.School)) != 0;
}

/// <summary>
/// EVENT_T_SPAWNED (11): Condition, ConditionValue1 (CreatureEventAI.h:686-691). Considered at (re)spawn when the condition holds
/// (SpawnedEventConditionsCheck, CreatureEventAI.cpp:1902-1927): 0 always, 1 the creature is on map ConditionValue1, 2 the zone or the area it
/// stands in is ConditionValue1 (GetZoneAndAreaId; nothing matches where the terrain does not know the area). Any other condition never
/// fires and is listed as unsupported. classic-db z2815: 6 rows with the zone condition (the city revelers become neutral when they spawn in
/// Moonglade, zone 493).
/// </summary>
public sealed class SpawnedEvent : EventAiEventHandler
{
    public override byte EventType => (byte)EventAiEventType.Spawned;

    public override EventAiTrigger Trigger => EventAiTrigger.Respawn;

    public override bool Repeatable => false;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker) => true;

    public override bool AllowTrigger(EventAiContext context, EventAiHolder holder)
    {
        switch ((EventAiSpawnedCondition)holder.Param(0))
        {
            case EventAiSpawnedCondition.Always:
                return true;
            case EventAiSpawnedCondition.Map:
                return context.Me.MapId == holder.Param(1);
            case EventAiSpawnedCondition.Zone:
            {
                (uint zone, uint area) = context.ZoneAndArea;
                uint wanted = holder.Param(1);
                return zone == wanted || area == wanted;
            }

            default:
                return false;
        }
    }

    public override string? UnsupportedReason(CreatureAiEvent row)
        => row.Param1 is 0 or 1 or 2 ? null : $"spawned condition {row.Param1}";
}

/// <summary>EVENT_T_REACHED_HOME (21): no parameters; fires when the creature reaches home after evading (:1462-1473).</summary>
public sealed class ReachedHomeEvent : EventAiEventHandler
{
    public override byte EventType => (byte)EventAiEventType.ReachedHome;

    public override EventAiTrigger Trigger => EventAiTrigger.ReachedHome;

    public override bool Repeatable => false;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker) => true;
}

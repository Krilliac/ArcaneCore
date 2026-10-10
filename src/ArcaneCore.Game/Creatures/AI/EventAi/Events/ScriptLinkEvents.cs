using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

// The EventAI events that react to other units: line of sight, friends that need help, summons, AI events and the creature's own spell
// hits. Parameter layouts: mangos-classic src/game/AI/EventAI/CreatureEventAI.h:40-85 and the event unions (:618-790); conditions:
// CreatureEventAI.cpp CheckEvent (:255-557); timer sets: IsTimerExecutedEvent (:165-192), IsTimerBasedEvent (:209-242) and
// GetRepeatTimers (:559-573).

/// <summary>
/// EVENT_T_OOC_LOS (10): NoHostile, MaxRange, RepeatMin, RepeatMax, PlayerOnly, ConditionId. While the creature has no victim a unit that
/// moves within MaxRange and in line of sight readies the row (cmangos MoveInLineOfSight, :1621-1648): a hostile unit when NoHostile is 0, a
/// unit that is not hostile otherwise; with PlayerOnly only a player. A condition is checked for the unit's player (CheckEvent :354-358). The
/// unit is the invoker. Repeatable, timer based (repeat parameters 2 and 3).
/// </summary>
public sealed class OutOfCombatLineOfSightEvent : EventAiEventHandler
{
    public override byte EventType => 10;

    public override EventAiTrigger Trigger => EventAiTrigger.OutOfCombatLineOfSight;

    public override bool TimerBased => true;

    public override bool MatchesUnit(EventAiContext context, EventAiHolder holder, Unit who)
    {
        if (ReferenceEquals(who, context.Me) || !who.IsAlive || context.System is not { } system)
        {
            return false;
        }

        if (holder.Param(4) != 0 && who is not Player)
        {
            return false;
        }

        bool hostile = system.AiServices.Hostility.IsHostile(context.Me, who); // cmangos IsEnemy
        if ((holder.Param(0) != 0) == hostile)
        {
            return false;
        }

        float range = holder.Param(1);
        return EventAiSearch.DistanceSquared(context.Me, who) <= range * range && system.IsInLineOfSight(context.Me, who);
    }

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker)
        => EventAiSearch.ConditionHolds(context, holder.Param(5), invoker);
}

/// <summary>
/// EVENT_T_FRIENDLY_HP (14): HPDeficit, Radius, RepeatMin, RepeatMax, IsPercent. In combat, the friendly creature within Radius (itself
/// included) that misses the most health, more than HPDeficit points (or percent), becomes the event target (DoSelectLowestHpFriendly,
/// MostHPMissingInRangeCheck / MostHPPercentMissingInRangeCheck: alive, in combat, one it can assist). The creature itself is left out when
/// one of the row's actions casts at the event target (TARGET_T_EVENT_SPECIFIC) a spell that excludes its caster: cmangos computes
/// friendlyHp.targetSelf so at load (CreatureEventAIMgr.cpp:1082-1096). Entering combat arms it with the repeat timer (EnterCombat :1608-1615).
/// </summary>
public sealed class FriendlyHealthEvent : FriendlySearchEventHandler
{
    public override byte EventType => 14;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker)
    {
        if (!context.InCombat)
        {
            return false;
        }

        bool percent = holder.Param(4) != 0;
        float best = holder.Param(0);
        Creature? chosen = null;
        foreach (Creature friend in EventAiSearch.Friends(context, holder.Param(1), includeSelf: TargetsSelf(context, holder.Event)))
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

        holder.EventTarget = chosen;
        return chosen is not null;
    }

    /// <summary>cmangos friendlyHp.targetSelf: false when an ACTION_T_CAST at TARGET_T_EVENT_SPECIFIC (12) uses a SPELL_ATTR_EX_EXCLUDE_CASTER spell.</summary>
    private static bool TargetsSelf(EventAiContext context, CreatureAiEvent row)
        => context.System?.AiServices.UnitSpells is not { } spells
            || !row.Actions.Any(a => a.Type == (byte)EventAiActionType.Cast && a.Param2 == 12 && spells.ExcludesCaster((uint)a.Param1));
}

/// <summary>
/// EVENT_T_FRIENDLY_IS_CC (15): DispelType, Radius, RepeatMin, RepeatMax. In combat, a friendly creature in combat within Radius that is
/// crowd controlled (stunned, confused, fleeing or rooted; FriendlyEligibleDispelInRangeCheck) becomes the event target.
/// </summary>
public sealed class FriendlyCrowdControlledEvent : FriendlySearchEventHandler
{
    public override byte EventType => 15;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker)
    {
        if (!context.InCombat)
        {
            return false;
        }

        holder.EventTarget = EventAiSearch.Friends(context, holder.Param(1), includeSelf: true)
            .FirstOrDefault(f => f.Combat.IsInCombat && EventAiSearch.IsCrowdControlled(f));
        return holder.EventTarget is not null;
    }
}

/// <summary>
/// EVENT_T_FRIENDLY_MISSING_BUFF (16): SpellId, Radius, RepeatMin, RepeatMax, Flags. A friendly creature within Radius without the aura of
/// SpellId becomes the event target (CheckEvent :405-439): flags 0 only while the creature is in combat and among friends in combat; flag 1 in
/// and out of combat, among friends in the creature's own combat state; flag 2 only out of combat, among friends out of combat. The search
/// radius is the row's (cmangos measures the spell's range instead; classic-db radii are the buff ranges).
/// </summary>
public sealed class FriendlyMissingBuffEvent : FriendlySearchEventHandler
{
    public override byte EventType => 16;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker)
    {
        uint flags = holder.Param(4);
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

        uint spellId = holder.Param(0);
        holder.EventTarget = EventAiSearch.Friends(context, holder.Param(1), includeSelf: true)
            .FirstOrDefault(f => f.Combat.IsInCombat == friendsInCombat && context.AuraStacks(f, spellId) == 0);
        return holder.EventTarget is not null;
    }
}

/// <summary>Base of the friend-search events: timer driven and timer based, armed with the repeat timer when the fight starts.</summary>
public abstract class FriendlySearchEventHandler : EventAiEventHandler
{
    public override bool TimerBased => true;

    public override bool TimerExecuted => true;

    /// <summary>cmangos EnterCombat (:1608-1615): UpdateRepeatTimer(repeatMin, repeatMax), enabled when it set a timer.</summary>
    public override void OnEnterCombat(EventAiContext context, EventAiHolder holder)
    {
        if (holder.UpdateRepeatTimer(context, holder.Param(RepeatMinParam), holder.Param(RepeatMaxParam)))
        {
            holder.Enabled = true;
        }
    }
}

/// <summary>
/// EVENT_T_SELECT_ATTACKING_TARGET (32): MinRange, MaxRange, RepeatMin, RepeatMax. A random unit of the threat list between MinRange and
/// MaxRange yards becomes the event target (SelectAttackingTarget with SELECT_FLAG_RANGE_RANGE, :521-531). Armed like the friend searches.
/// </summary>
public sealed class SelectAttackingTargetEvent : FriendlySearchEventHandler
{
    public override byte EventType => 32;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker)
    {
        float min = holder.Param(0);
        float max = holder.Param(1);
        Unit[] candidates = [.. context.Threat.Select(t => t.Target).Where(t => t.IsAlive && InRange(context.Me, t, min, max))];
        holder.EventTarget = candidates.Length == 0 ? null : candidates[context.Random(0, candidates.Length - 1)];
        return holder.EventTarget is not null;
    }

    private static bool InRange(Creature me, Unit target, float min, float max)
    {
        float distanceSquared = EventAiSearch.DistanceSquared(me, target);
        return distanceSquared >= min * min && distanceSquared <= max * max;
    }
}

/// <summary>
/// EVENT_T_SUMMONED_UNIT (17), EVENT_T_SUMMONED_JUST_DIED (25), EVENT_T_SUMMONED_JUST_DESPAWN (26): CreatureId, RepeatMin, RepeatMax. A
/// creature the creature summoned of that entry entered the world, died or left it; it is the invoker (CheckEvent :441-452). Repeat timers are
/// parameters 1 and 2 (GetRepeatTimers :567-569).
/// </summary>
public abstract class SummonedUnitEventHandler(EventAiTrigger trigger) : EventAiEventHandler
{
    public override EventAiTrigger Trigger => trigger;

    public override bool TimerBased => true;

    public override int RepeatMinParam => 1;

    public override int RepeatMaxParam => 2;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker)
        => invoker is Creature summoned && summoned.Entry == holder.Param(0);
}

/// <summary>EVENT_T_SUMMONED_UNIT (17): a creature this one summoned entered the world (cmangos JustSummoned).</summary>
public sealed class SummonedUnitEvent() : SummonedUnitEventHandler(EventAiTrigger.Summoned)
{
    public override byte EventType => 17;
}

/// <summary>EVENT_T_SUMMONED_JUST_DIED (25): a creature this one summoned died.</summary>
public sealed class SummonedJustDiedEvent() : SummonedUnitEventHandler(EventAiTrigger.SummonedDied)
{
    public override byte EventType => 25;
}

/// <summary>EVENT_T_SUMMONED_JUST_DESPAWN (26): a creature this one summoned left the world.</summary>
public sealed class SummonedJustDespawnEvent() : SummonedUnitEventHandler(EventAiTrigger.SummonedDespawned)
{
    public override byte EventType => 26;
}

/// <summary>
/// EVENT_T_RECEIVE_AI_EVENT (30): AIEventType, SenderEntry. An AI event of the type (from a sender of the entry, or any sender with 0) reached
/// the creature (cmangos ReceiveAIEvent, :1562-1574; CheckEvent has no further condition for it, :508-509); the event's invoker is the
/// invoker and its sender is TARGET_T_EVENT_SENDER. The sender entry is compared with the sender's object entry (cmangos
/// <c>sender->GetEntry()</c>), so a player sender only matches a row that takes any sender. Repeatable, not timer based.
/// </summary>
public sealed class ReceiveAiEventEvent : EventAiEventHandler
{
    public override byte EventType => (byte)EventAiEventType.ReceiveAiEvent;

    public override EventAiTrigger Trigger => EventAiTrigger.AiEvent;

    public override bool MatchesAiEvent(CreatureAiEvent row, uint eventType, Unit sender)
        => (uint)row.Param1 == eventType && (row.Param2 == 0 || (uint)row.Param2 == sender.GetUInt32(UpdateFields.ObjectFieldEntry));

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker) => true;
}

/// <summary>
/// EVENT_T_SPELLHIT_TARGET (34): SpellId, SchoolMask, RepeatMin, RepeatMax. A spell the creature cast landed on a unit, matching like
/// EVENT_T_SPELLHIT (spell id 0 or equal, the school's bit in the mask; :1680-1691); the unit hit is the invoker.
/// </summary>
public sealed class SpellHitTargetEvent : EventAiEventHandler
{
    public override byte EventType => 34;

    public override EventAiTrigger Trigger => EventAiTrigger.SpellHitTarget;

    public override bool TimerBased => true;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker) => true;

    public override bool MatchesSpell(CreatureAiEvent row, SpellInfo spell)
        => (row.Param1 == 0 || (uint)row.Param1 == spell.Id) && ((uint)row.Param2 & (1u << (int)spell.School)) != 0;
}

/// <summary>Searches shared by the event and action handlers.</summary>
internal static class EventAiSearch
{
    public static float DistanceSquared(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    /// <summary>
    /// The living creatures of the map within <paramref name="radius"/> yards that the creature can assist (cmangos CanAssist: the same
    /// faction template, or a friendly one), itself included when asked.
    /// </summary>
    public static IEnumerable<Creature> Friends(EventAiContext context, float radius, bool includeSelf)
    {
        if (context.System is not { } system)
        {
            yield break;
        }

        Creature me = context.Me;
        ICreatureHostility hostility = system.AiServices.Hostility;
        float rangeSquared = radius * radius;
        foreach (Creature other in system.Creatures)
        {
            if (!other.IsAlive || (!includeSelf && ReferenceEquals(other, me)) || DistanceSquared(me, other) > rangeSquared)
            {
                continue;
            }

            if (ReferenceEquals(other, me) || hostility.CanAssist(other, me) || hostility.IsFriendly(me, other))
            {
                yield return other;
            }
        }
    }

    /// <summary>Stunned, confused, fleeing or rooted (cmangos IsImmobilizedState / IsCrowdControlled in FriendlyEligibleDispelInRangeCheck).</summary>
    public static bool IsCrowdControlled(Unit unit)
        => (unit.UnitFlags & (UnitFlags.Stunned | UnitFlags.Confused | UnitFlags.Fleeing)) != 0
            || unit.Movement.HasFlag(Protocol.MovementFlags.Root);

    /// <summary>A condition id of a row (0: none) checked for the invoker's player, as the receive-emote event does; without a conditions table it fails.</summary>
    public static bool ConditionHolds(EventAiContext context, uint conditionId, Unit? invoker)
    {
        if (conditionId == 0)
        {
            return true;
        }

        if (invoker?.GetCharmerOrOwnerPlayerOrSelf() is not { } player || context.System?.AiServices.Conditions is not { } conditions)
        {
            return false;
        }

        Creature me = context.Me;
        var source = new NpcInfo(me.Guid, me.Entry, me.Spawn?.Guid ?? me.Guid.Low, (NpcFlags)me.NpcFlags, me.MapId, me.X, me.Y, me.Z,
            me.BoundingRadius, me.IsAlive, IsHostile: false, me.Combat.IsInCombat, (me.UnitFlags & UnitFlags.NotSelectable) != 0,
            me.DefaultGossipMenuId);
        return conditions.IsSatisfied(conditionId, player, source);
    }
}

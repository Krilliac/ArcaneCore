using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

// EventAI actions for sound and emotes, phases, movement, despawns, summons, AI events, auras, attacks and quest credit.
// mangos-classic src/game/AI/EventAI/CreatureEventAI.cpp ProcessAction (:665-1380); parameters CreatureEventAI.h:89-159.

/// <summary>ACTION_T_SOUND (4): SoundId; SMSG_PLAY_SOUND to the players that see the creature (PlayDirectSound, :762-764).</summary>
public sealed class SoundAction : EventAiActionHandler
{
    public override byte ActionType => 4;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        context.System?.PlayDirectSound(context.Me, (uint)action.Param1);
        return true;
    }
}

/// <summary>ACTION_T_EMOTE (5): EmoteId; played once, or kept as the emote state for a state emote (HandleEmote, :765-767).</summary>
public sealed class EmoteAction : EventAiActionHandler
{
    public override byte ActionType => 5;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        context.System?.PlayEmote(context.Me, (uint)action.Param1);
        return true;
    }
}

/// <summary>ACTION_T_RANDOM_SOUND (9): SoundId1..3; one by <c>rnd % 3</c>, -1 plays nothing (:768-774).</summary>
public sealed class RandomSoundAction : EventAiActionHandler
{
    public override byte ActionType => 9;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        int sound = EventAiRandom.Pick(invocation.Random, action);
        if (sound >= 0)
        {
            context.System?.PlayDirectSound(context.Me, (uint)sound);
        }

        return true;
    }
}

/// <summary>ACTION_T_RANDOM_EMOTE (10): EmoteId1..3; one by <c>rnd % 3</c>, -1 plays nothing (:775-781).</summary>
public sealed class RandomEmoteAction : EventAiActionHandler
{
    public override byte ActionType => 10;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        int emote = EventAiRandom.Pick(invocation.Random, action);
        if (emote >= 0)
        {
            context.System?.PlayEmote(context.Me, (uint)emote);
        }

        return true;
    }
}

/// <summary>ACTION_T_RANDOM_PHASE (30): PhaseId1..3; the phase becomes one of them by <c>rnd % 3</c> (:993-996).</summary>
public sealed class RandomPhaseAction : EventAiActionHandler
{
    public override byte ActionType => 30;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        context.Phase = Math.Clamp(EventAiRandom.Pick(invocation.Random, action), 0, CreatureEventAI.MaxPhase - 1);
        return true;
    }
}

/// <summary>ACTION_T_RANDOM_PHASE_RANGE (31): PhaseMin, PhaseMax; <c>rnd % (max - min + 1) + min</c>; a maximum not above the minimum fails (:997-1002).</summary>
public sealed class RandomPhaseRangeAction : EventAiActionHandler
{
    public override byte ActionType => 31;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (action.Param2 <= action.Param1 || action.Param1 < 0)
        {
            return true; // cmangos logs the row and leaves the phase (the action still counts as done)
        }

        uint span = (uint)(action.Param2 - action.Param1 + 1);
        context.Phase = Math.Clamp((int)(invocation.Random % span) + action.Param1, 0, CreatureEventAI.MaxPhase - 1);
        return true;
    }
}

/// <summary>ACTION_T_RANGED_MOVEMENT (29): Distance, Angle; the creature chases at that distance from now on (m_attackDistance, :979-992). The angle is not modelled.</summary>
public sealed class RangedMovementAction : EventAiActionHandler
{
    public override byte ActionType => 29;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        context.Ai.AttackDistance = Math.Max(0, action.Param1);
        if (context.Victim is not null)
        {
            context.System?.ApplyCombatMovement(context.Me);
        }

        return true;
    }
}

/// <summary>ACTION_T_PAUSE_WAYPOINTS (51): DoPause; 1 pauses the waypoint movement, 0 resumes it (:1214-1221).</summary>
public sealed class PauseWaypointsAction : EventAiActionHandler
{
    public override byte ActionType => 51;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        context.System?.PauseWaypoints(context.Me, action.Param1 != 0);
        return true;
    }
}

/// <summary>
/// ACTION_T_SET_WALK (58): Type; 0 RUN_DEFAULT and 1 WALK_DEFAULT set how the creature's scripted moves go (SetWalk(.., asDefault), announced
/// with SMSG_SPLINE_MOVE_SET_RUN/WALK_MODE); 2 RUN_CHASE and 3 WALK_CHASE only change the chase speed, which this server always runs (:1333-1341).
/// </summary>
public sealed class SetWalkAction : EventAiActionHandler
{
    public override byte ActionType => 58;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (action.Param1 is 0 or 1)
        {
            context.System?.SetScriptRun(context.Me, run: action.Param1 == 0);
        }

        return true;
    }
}

/// <summary>
/// ACTION_T_SET_FACING (59): Target, Reset. Reset faces the reset orientation (the movement's reset position, else home); otherwise the
/// creature turns to the target, which must resolve (:1311-1332).
/// </summary>
public sealed class SetFacingAction : EventAiActionHandler
{
    public override byte ActionType => 59;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (context.System is not { } system)
        {
            return false;
        }

        Creature me = context.Me;
        if (action.Param2 != 0)
        {
            CreatureHome reset = me.Motion.Default.GetResetPosition(me) ?? me.Home;
            system.SetFacingTo(me, reset.Orientation);
            return true;
        }

        if (context.SelectTarget(action.Param1, invocation, out _) is not { } target)
        {
            return false;
        }

        system.SetFacingTo(me, MathF.Atan2(target.Y - me.Y, target.X - me.X));
        return true;
    }
}

/// <summary>ACTION_T_SET_IMMOBILIZED_STATE (61): Apply, CombatOnly; roots or frees the creature, a combat-only root ends at the next reset (:1347-1351).</summary>
public sealed class SetImmobilizedStateAction : EventAiActionHandler
{
    public override byte ActionType => 61;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        context.System?.SetAiImmobilized(context.Me, action.Param1 != 0, action.Param2 != 0);
        return true;
    }
}

/// <summary>ACTION_T_FORCE_DESPAWN (41): Delay (ms, 0 at once); the creature dies without a kill and its corpse goes (ForcedDespawn, :1114-1118).</summary>
public sealed class ForceDespawnAction : EventAiActionHandler
{
    public override byte ActionType => 41;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        context.System?.ForcedDespawn(context.Me, (uint)Math.Max(0, action.Param1));
        return true;
    }
}

/// <summary>ACTION_T_DESPAWN_GUARDIANS (56): Entry (0: every guardian); the creature's guardians go (:1285-1299).</summary>
public sealed class DespawnGuardiansAction : EventAiActionHandler
{
    public override byte ActionType => 56;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        context.System?.DespawnGuardians(context.Me, (uint)Math.Max(0, action.Param1));
        return true;
    }
}

/// <summary>ACTION_T_ATTACK_START (55): Target; the creature attacks it; a target that does not resolve fails (:1273-1284).</summary>
public sealed class AttackStartAction : EventAiActionHandler
{
    public override byte ActionType => 55;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (context.SelectTarget(action.Param1, invocation, out _) is not { IsAlive: true } target)
        {
            return false;
        }

        context.Ai.AttackStart(target);
        return true;
    }
}

/// <summary>
/// ACTION_T_ZONE_COMBAT_PULSE (38): in a dungeon or raid every living player of the map enters the creature's fight (SetInCombatWithZone),
/// then a creature with no victim attacks the closest unit of its threat list (AttackClosestEnemy, :1097-1103).
/// </summary>
public sealed class ZoneCombatPulseAction : EventAiActionHandler
{
    public override byte ActionType => 38;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (context.System is { } system)
        {
            system.SetInCombatWithZone(context.Me);
            system.AttackClosestEnemy(context.Me);
        }

        return true;
    }
}

/// <summary>ACTION_T_REMOVEAURASFROMSPELL (28): Target, SpellId; the target loses the auras of the spell (:973-978).</summary>
public sealed class RemoveAurasFromSpellAction : EventAiActionHandler
{
    public override byte ActionType => 28;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (context.SelectTarget(action.Param1, invocation, out _) is { } target)
        {
            context.System?.AiServices.UnitSpells?.RemoveAuras(target, (uint)action.Param2);
        }

        return true;
    }
}

/// <summary>
/// ACTION_T_SUMMON_ID (32): CreatureId, Target, SummonId. The creature_ai_summons row SummonId gives the position and the lifetime in
/// milliseconds (the column is named spawntimesecs, but cmangos passes it to SummonCreature as the despawn time, :1018-1021); 0 despawns
/// the summon as soon as it is out of combat. The summon attacks the target unless the target is the creature itself; a missing row
/// fails (:1003-1029).
/// </summary>
public sealed class SummonIdAction : EventAiActionHandler
{
    public override byte ActionType => 32;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (context.System is not { } system || context.Content.FindSummon((uint)action.Param3) is not { } at)
        {
            return false;
        }

        Unit? target = context.SelectTarget(action.Param2, invocation, out _);
        system.SummonAt(context.Me, (uint)action.Param1, at.X, at.Y, at.Z, at.Orientation, action.Param2 == (int)EventAiTarget.Self ? null : target,
            at.LifetimeMs);
        return true;
    }
}

/// <summary>
/// ACTION_T_THROW_AI_EVENT (45): EventType, Radius, Target. The AI event goes to the creatures around (SendAIEventAround with no delay) with
/// the target as its invoker; a target that does not resolve fails (:1142-1153).
/// </summary>
public sealed class ThrowAiEventAction : EventAiActionHandler
{
    public override byte ActionType => 45;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (context.System is not { } system || context.SelectTarget(action.Param3, invocation, out _) is not { } target)
        {
            return false;
        }

        system.SendAiEventAround(context.Me, (uint)action.Param1, target, action.Param2);
        return true;
    }
}

/// <summary>
/// ACTION_T_QUEST_EVENT (15): QuestId, Target, RewardGroup. A player target completes the quest's exploration or event objective, with
/// RewardGroup its group near the creature too (AreaExploredOrEventHappens / RewardPlayerAndGroupAtEventExplored, :849-865).
/// </summary>
public sealed class QuestEventAction : EventAiActionHandler
{
    public override byte ActionType => 15;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (context.SelectTarget(action.Param2, invocation, out _) is Player player && context.System?.AiServices.QuestEvents is { } quests)
        {
            quests.EventHappened(player, (uint)action.Param1, context.Me, rewardGroup: action.Param3 != 0);
        }

        return true;
    }
}

/// <summary>
/// ACTION_T_KILLED_MONSTER (33): CreatureId, Target. Kill credit for the entry goes to the player (and group) that tapped the creature, else to
/// the target's player (RewardPlayerAndGroupAtEventCredit, :1030-1045).
/// </summary>
public sealed class KilledMonsterAction : EventAiActionHandler
{
    public override byte ActionType => 33;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (context.System is not { } system || system.AiServices.QuestEvents is not { } quests)
        {
            return true;
        }

        Creature me = context.Me;
        Player? player = !me.LootTapPlayerGuid.IsEmpty ? system.Map.FindPlayer(me.LootTapPlayerGuid) : null;
        player ??= context.SelectTarget(action.Param2, invocation, out _)?.GetCharmerOrOwnerPlayerOrSelf();
        if (player is not null)
        {
            quests.KillCredit(player, (uint)action.Param1, me);
        }

        return true;
    }
}

/// <summary>cmangos GetRandActionParam: parameter 1, 2 or 3 by <c>rnd % 3</c>.</summary>
internal static class EventAiRandom
{
    public static int Pick(uint random, CreatureAiAction action) => (random % 3) switch
    {
        0 => action.Param1,
        1 => action.Param2,
        _ => action.Param3,
    };
}

/// <summary>
/// ACTION_T_SET_FOLLOW_MOVEMENT (64): State; 0 makes a follow movement hold still (UNIT_STAT_NO_FOLLOW_MOVEMENT, a following creature stops),
/// 1 lets it follow again (UnitAI::SetFollowMovement, BaseAI/UnitAI.cpp:320-328).
/// </summary>
public sealed class SetFollowMovementAction : EventAiActionHandler
{
    public override byte ActionType => 64;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        Creature me = context.Me;
        me.FollowMovementDisabled = action.Param1 == 0;
        if (me.FollowMovementDisabled && me.IsMoving && me.Motion.CurrentType == MovementGeneratorType.Follow)
        {
            context.System?.StopMoving(me);
        }

        return true;
    }
}

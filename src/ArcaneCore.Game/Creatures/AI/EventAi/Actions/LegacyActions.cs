using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

// The action types ArcaneCore ran before the engine existed, ported to cmangos-classic semantics
// (CreatureEventAI.cpp ProcessAction, :665-1380). Every action returns whether it succeeded: with the combat-action
// event flag (0x400) a failed first action leaves the event's timer alone so it retries at the next batch.

/// <summary>
/// ACTION_T_TEXT (1): TextId1, optionally TextId2, optionally TextId3. With all three set one is chosen by
/// <c>rnd % 3</c>; with two, TextId2 is used when <c>rnd % 2</c> is 1 (:676-697). Positive ids are broadcast
/// texts, negative ids <c>creature_ai_texts</c>. The line goes to the invoker when it is a player, else to the
/// victim when there is no invoker (:700-718).
/// </summary>
public sealed class TextAction : EventAiActionHandler
{
    public override byte ActionType => (byte)EventAiActionType.Text;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (action.Param1 == 0)
        {
            return false;
        }

        int textId;
        if (action.Param2 != 0 && action.Param3 != 0)
        {
            textId = (invocation.Random % 3) switch
            {
                0 => action.Param1,
                1 => action.Param2,
                _ => action.Param3,
            };
        }
        else if (action.Param2 != 0 && invocation.Random % 2 == 1)
        {
            textId = action.Param2;
        }
        else
        {
            textId = action.Param1;
        }

        Unit? target = invocation.Invoker is { } invoker ? (invoker is Player ? invoker : null) : context.Victim;
        if (context.System is { } system && context.Content.FindText(textId) is { } text)
        {
            system.Say(context.Me, text, target);
        }

        return true;
    }
}

/// <summary>
/// ACTION_T_CAST (11): SpellId, Target, CastFlags. A cast needs the target to resolve (:800-803), the aura-not-present
/// flag skips a target that already has the aura, and a creature that is casting only casts again when the spell is
/// triggered or interrupts the previous one (UnitAI::DoCastSpellIfCan, :160-190). It succeeds only when the cast was
/// accepted (:804-809); the other cast flags (force cast, caster mode, player only) arrive with the caster slice.
/// </summary>
public sealed class CastAction : EventAiActionHandler
{
    public const int InterruptPrevious = 0x01;
    public const int Triggered = 0x02;
    public const int AuraNotPresent = 0x20;

    public override byte ActionType => (byte)EventAiActionType.Cast;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (context.System is not { } system)
        {
            return false;
        }

        Unit? target = context.SelectTarget(action.Param2, invocation, out bool error);
        if (error)
        {
            return false;
        }

        int flags = action.Param3;
        if ((flags & AuraNotPresent) != 0 && system.HasAura(target ?? context.Me, (uint)action.Param1))
        {
            return false;
        }

        if (context.IsCasting && (flags & (Triggered | InterruptPrevious)) == 0)
        {
            return false;
        }

        if ((flags & InterruptPrevious) != 0)
        {
            system.InterruptCast(context.Me);
        }

        return system.CastSpell(context.Me, (uint)action.Param1, target, (flags & Triggered) != 0) == CreatureCastResult.Ok;
    }
}

/// <summary>
/// ACTION_T_SPAWN (12): CreatureId, Target, Duration (ms). A missing template is reported and summons nothing; the
/// summon attacks the target unless the target is the creature itself (:812-828). The action always counts as done.
/// </summary>
public sealed class SummonAction : EventAiActionHandler
{
    public override byte ActionType => (byte)EventAiActionType.Summon;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        Unit? target = context.SelectTarget(action.Param2, invocation, out _);
        context.System?.Summon(context.Me, (uint)action.Param1, action.Param2 == (int)EventAiTarget.Self ? null : target, (uint)Math.Max(0, action.Param3));
        return true;
    }
}

/// <summary>ACTION_T_AUTO_ATTACK (20): AllowAttackState; 0 stops the melee swing (:902-904).</summary>
public sealed class AutoAttackAction : EventAiActionHandler
{
    public override byte ActionType => (byte)EventAiActionType.AutoAttack;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        context.Ai.SetMeleeEnabled(action.Param1 != 0);
        return true;
    }
}

/// <summary>
/// ACTION_T_COMBAT_MOVEMENT (21): AllowCombatMovement. No change, or a cast in progress, fails the action
/// (:905-919). The melee-attack start/stop packet parameter is not sent.
/// </summary>
public sealed class CombatMovementAction : EventAiActionHandler
{
    public override byte ActionType => (byte)EventAiActionType.CombatMovement;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        bool want = action.Param1 != 0;
        if (context.Ai.CombatMovement == want || context.IsCasting)
        {
            return false;
        }

        context.Ai.CombatMovement = want;
        context.System?.ApplyCombatMovement(context.Me);
        return true;
    }
}

/// <summary>ACTION_T_SET_PHASE (22): Phase (:921-924).</summary>
public sealed class SetPhaseAction : EventAiActionHandler
{
    public override byte ActionType => (byte)EventAiActionType.SetPhase;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        context.Phase = Math.Clamp(action.Param1, 0, CreatureEventAI.MaxPhase - 1);
        return true;
    }
}

/// <summary>ACTION_T_INC_PHASE (23): Value, negative to decrement; below 0 gives 0 and above 31 gives 31 (:925-942).</summary>
public sealed class IncrementPhaseAction : EventAiActionHandler
{
    public override byte ActionType => (byte)EventAiActionType.IncrementPhase;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        context.Phase = Math.Clamp(context.Phase + action.Param1, 0, CreatureEventAI.MaxPhase - 1);
        return true;
    }
}

/// <summary>
/// ACTION_T_EVADE (24): CombatOnly. Without it the creature evades; with it combat just stops (CombatStopWithPets,
/// :944-949).
/// </summary>
public sealed class EvadeAction : EventAiActionHandler
{
    public override byte ActionType => (byte)EventAiActionType.Evade;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (action.Param1 != 0)
        {
            if (context.System is { } system)
            {
                system.Map.Combat.CombatStop(context.Me);
                if (context.Me.Combat.HasThreatList)
                {
                    context.Me.Combat.Threat.Clear();
                }
            }
        }
        else
        {
            context.Ai.EnterEvadeMode();
        }

        return true;
    }
}

/// <summary>ACTION_T_FLEE_FOR_ASSIST (25): no parameters; fails without a victim to flee from (:950-953).</summary>
public sealed class FleeForAssistAction : EventAiActionHandler
{
    public override byte ActionType => (byte)EventAiActionType.FleeForAssist;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (context.System is not { } system || !context.Me.IsAlive || context.IsEvading || context.Victim is null)
        {
            return false;
        }

        system.FleeForAssistance(context.Me);
        return true;
    }
}

/// <summary>ACTION_T_DIE (37): the creature dies; on a dead creature the action fails (:1087-1096).</summary>
public sealed class DieAction : EventAiActionHandler
{
    public override byte ActionType => (byte)EventAiActionType.Die;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (context.System is not { } system || !context.Me.IsAlive)
        {
            return false;
        }

        system.KillCreature(context.Me);
        return true;
    }
}

/// <summary>ACTION_T_CALL_FOR_HELP (39): Radius (:1104-1108).</summary>
public sealed class CallForHelpAction : EventAiActionHandler
{
    public override byte ActionType => (byte)EventAiActionType.CallForHelp;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        context.System?.CallForHelp(context.Me, action.Param1);
        return true;
    }
}

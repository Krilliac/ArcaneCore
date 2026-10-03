using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// ACTION_T_THREAT_SINGLE (13): Threat, Target, IsDirect. Direct adds the value to the target's threat (a new entry when it has
/// none); otherwise the value is a percent change of the target's entry (below -100 removes it). cmangos-classic
/// CreatureEventAI.cpp ProcessAction, ACTION_T_THREAT_SINGLE. A missing target fails the action.
/// </summary>
public sealed class ThreatSingleAction : EventAiActionHandler
{
    public override byte ActionType => (byte)EventAiActionType.ThreatSingle;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        Unit? target = context.SelectTarget(action.Param2, invocation, out bool error);
        if (error)
        {
            return false;
        }

        if (target is null)
        {
            return true;
        }

        if (action.Param3 != 0)
        {
            context.Me.Combat.Threat.AddThreat(target, action.Param1);
        }
        else
        {
            context.Me.Combat.Threat.ModifyThreatPercent(target, action.Param1);
        }

        return true;
    }
}

/// <summary>
/// ACTION_T_THREAT_ALL_PCT (14): Threat%. Every unit on the creature's threat list has its entry changed by that percent
/// (cmangos-classic CreatureEventAI.cpp ProcessAction, ACTION_T_THREAT_ALL_PCT; below -100 removes the entry).
/// </summary>
public sealed class ThreatAllPercentAction : EventAiActionHandler
{
    public override byte ActionType => (byte)EventAiActionType.ThreatAllPercent;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (!context.Me.Combat.HasThreatList)
        {
            return true;
        }

        foreach (Unit target in context.Me.Combat.Threat.Entries.Select(static e => e.Target).ToArray())
        {
            context.Me.Combat.Threat.ModifyThreatPercent(target, action.Param1);
        }

        return true;
    }
}

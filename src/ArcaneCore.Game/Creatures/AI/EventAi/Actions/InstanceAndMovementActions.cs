using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

// EventAI actions that reach the instance script and the motion master. mangos-classic src/game/AI/EventAI/CreatureEventAI.cpp ProcessAction;
// parameters CreatureEventAI.h:421-430 and 491-497.

/// <summary>
/// ACTION_T_SET_INST_DATA (34): Field, Data. The instance script of the creature's map stores <c>Data</c> under <c>Field</c>
/// (InstanceData::SetData; CreatureEventAI.cpp:1046-1057). Without an instance script on the map the action fails ("attempt to set instance
/// data without instance script"). classic-db z2815: 37 rows over eight dungeons (docs/areas/creature-ai.md, "Instance scripts").
/// </summary>
public sealed class SetInstanceDataAction : EventAiActionHandler
{
    public override byte ActionType => 34;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (context.InstanceData is not { } instance)
        {
            return false;
        }

        instance.SetData(unchecked((uint)action.Param1), unchecked((uint)action.Param2));
        return true;
    }
}

/// <summary>
/// ACTION_T_SET_INST_DATA64 (35): Field, Target. The instance script stores the target's GUID under <c>Field</c> (InstanceData::SetData64;
/// CreatureEventAI.cpp:1058-1077); a missing target or a map without an instance script fails the action. classic-db z2815 has no such row.
/// </summary>
public sealed class SetInstanceData64Action : EventAiActionHandler
{
    public override byte ActionType => 35;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (context.SelectTarget(action.Param2, invocation, out _) is not { } target || context.InstanceData is not { } instance)
        {
            return false;
        }

        instance.SetData64(unchecked((uint)action.Param1), target.Guid.Value);
        return true;
    }
}

/// <summary>
/// ACTION_T_CHANGE_MOVEMENT (48): MovementType, WanderDistance or PathId, Flags (CreatureEventAI.cpp:1164-1206): idle, a wander around where
/// the creature stands, or a waypoint path that replaces its movement (<see cref="CreatureMapSystem.ChangeMovement"/>). The path and linear
/// waypoint types and paths from <c>waypoint_path</c> fail the action. classic-db z2815: 3 rows (Forest Spirit and Alzzin start their path,
/// the Hakkari Minion wanders 15 yards).
/// </summary>
public sealed class ChangeMovementAction : EventAiActionHandler
{
    public override byte ActionType => 48;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
        => context.System is { } system
            && system.ChangeMovement(context.Me, unchecked((uint)action.Param1), unchecked((uint)action.Param2), unchecked((uint)action.Param3));
}

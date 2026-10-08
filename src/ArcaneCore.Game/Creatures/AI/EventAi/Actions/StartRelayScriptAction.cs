using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// ACTION_T_START_RELAY_SCRIPT (53): relay id, target type (cmangos CreatureEventAI.h:144 and the relayScript union, :525-531;
/// ProcessAction, CreatureEventAI.cpp:1227-1247). The target is resolved like any EventAI target; a negative relay id is a relay
/// template whose relay is chosen at random (ScriptMgr::GetRandomRelayDbscriptFromTemplate; nothing chosen is a successful no-op). The
/// relay starts with the <em>target</em> as its source and the creature as its target (<c>ScriptsStart(SCRIPT_TYPE_RELAY, id, target,
/// m_creature)</c>), which is why data_flags 2 (REVERSE_DIRECTION) is common in the scripts. vmangos has no such action (its EventAI rows
/// call generic scripts); classic-db z2815 carries 141 rows of it. What the relays can do: docs/areas/creature-ai.md, "Relay scripts".
/// </summary>
public sealed class StartRelayScriptAction : EventAiActionHandler
{
    public override byte ActionType => (byte)EventAiActionType.StartRelayScript;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        Entities.Unit? target = context.SelectTarget(action.Param2, invocation, out bool error);
        if (error || target is null || context.System is not { } system)
        {
            return false;
        }

        uint relayId;
        if (action.Param1 < 0)
        {
            relayId = system.SelectRelayFromTemplate((uint)-action.Param1);
            if (relayId == 0)
            {
                return true; // the cmangos `break`: nothing chosen, the action still counts as done
            }
        }
        else
        {
            relayId = (uint)action.Param1;
        }

        system.StartRelayScript(relayId, target, context.Me);
        return true;
    }
}

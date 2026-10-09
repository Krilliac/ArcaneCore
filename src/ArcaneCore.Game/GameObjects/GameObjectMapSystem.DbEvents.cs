using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.GameObjects;

public sealed partial class GameObjectMapSystem
{
    /// <summary>
    /// cmangos StartEvents_Event (DBScripts/ScriptMgr.cpp:3445-3478): a player using a chest or goober is the
    /// source and that object the target. GameObjectInfo::GetEventScriptId (vmangos GameObjectDefines.h:787-796)
    /// maps chest data6 and goober data2. Buttons have a linked trap (data3), not an event-id field.
    /// </summary>
    private void StartDbEvent(uint eventId, Player player, GameObject go)
    {
        if (eventId == 0 || Map.FindUpdater<Instances.Scripts.InstanceData>()?.OnSpellEvent(player, eventId) == true)
        {
            return;
        }

        Map.FindUpdater<CreatureMapSystem>()?.StartDbScript(DbScriptKind.Event, eventId, player, go);
    }
}

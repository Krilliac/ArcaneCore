using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;

namespace ArcaneCore.Game.GameObjects;

public sealed partial class GameObjectMapSystem
{
    /// <summary>
    /// cmangos StartEvents_Event (DBScripts/ScriptMgr.cpp:3445-3482) through <see cref="ScriptedEvents.Start"/>: a player using a chest
    /// or goober is the source and that object the target. A player is a unit, so the run is unique by the player
    /// (SCRIPT_EXEC_PARAM_UNIQUE_BY_SOURCE, ScriptMgr.cpp:3476-3481): another player's use starts its own copy, as in cmangos.
    /// GameObjectInfo::GetEventScriptId (vmangos GameObjectDefines.h:787-796) maps chest data6 and goober data2. Buttons have a linked
    /// trap (data3), not an event-id field.
    /// </summary>
    private void StartDbEvent(uint eventId, Player player, GameObject go)
    {
        if (eventId != 0)
        {
            ScriptedEvents.Start(Map, eventId, player, go);
        }
    }
}

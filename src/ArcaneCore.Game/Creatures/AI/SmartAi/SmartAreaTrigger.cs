using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// SMART_SCRIPT_TYPE_AREATRIGGER (AzerothCore SmartTrigger, SmartAI.cpp:1586-1601, re-implemented): a trigger's rows run in a script made for that one
/// trigger, with the invoking player as the invoker and as the reference point of the range targets. Dead players and game masters run
/// nothing (SmartTrigger is for an alive player only; MiscHandler.cpp:727 skips game masters). World thread only.
/// </summary>
public static class SmartAreaTrigger
{
    /// <summary>
    /// Run the rows of <paramref name="triggerId"/> for <paramref name="player"/>. True when a script ran (AzerothCore then ends the packet handler
    /// before quest exploration, tavern and teleport; ArcaneCore's listener seam cannot, a documented deviation); false when there was none to run.
    /// </summary>
    public static bool Run(SmartScriptCatalog catalog, Player player, uint triggerId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(player);
        if (!player.IsAlive || player.IsGameMaster)
        {
            return false;
        }

        IReadOnlyList<SmartScriptRow> rows = catalog.ForAreaTrigger(triggerId);
        if (rows.Count == 0)
        {
            return false;
        }

        var script = new SmartScript(SmartScriptOwner.ForAreaTrigger(player, catalog), rows);
        script.ProcessEventsFor(SmartEvent.AreaTriggerOnTrigger, player, triggerId);
        return true;
    }
}

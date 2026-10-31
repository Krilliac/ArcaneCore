using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>DB script commands that reach services outside the creature system through <see cref="CreatureAiServices"/> seams.</summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// SCRIPT_COMMAND_TELEPORT_TO (6, cmangos ScriptMgr.cpp:1925-1933): the player is the target, or the source when the target is not a
    /// player (GetPlayerTargetOrSourceAndLog); it is teleported to map <c>datalong</c>, point (x, y, z), facing o. Without a player
    /// nothing happens. The step is reported and skipped when the world bound no teleport service.
    /// </summary>
    private void RelayTeleportTo(RelayScriptStep step, WorldObject? source, WorldObject? target)
    {
        Player? player = target as Player ?? source as Player;
        if (player is null)
        {
            return;
        }

        if (_ai.Teleports is not { } teleports)
        {
            ReportRelay(step, "TELEPORT_TO without a teleport service");
            return;
        }

        teleports.Teleport(player, step.DataLong, step.X, step.Y, step.Z, step.Orientation);
    }
}

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

    /// <summary>
    /// SCRIPT_COMMAND_SEND_MAIL (38, cmangos ScriptMgr.cpp:2851-2866): the target must be a player and, without an alternative sender
    /// (<c>datalong2</c>), the source must be a creature; either failing does nothing. The player is mailed template <c>datalong</c> plus
    /// <c>datalong3</c> copper from creature <c>datalong2</c> (else the source), delayed by <c>dataint</c> seconds when positive. The step is
    /// reported and skipped when the world bound no mail sink.
    /// </summary>
    private void RelaySendMail(RelayScriptStep step, WorldObject? source, WorldObject? target)
    {
        if (target is not Player player)
        {
            return;
        }

        uint senderEntry = step.DataLong2;
        if (senderEntry == 0)
        {
            if (source is not Creature creature)
            {
                return;
            }

            senderEntry = creature.Entry;
        }

        if (_ai.ScriptMail is not { } mail)
        {
            ReportRelay(step, "SEND_MAIL without a mail service");
            return;
        }

        mail.Send(player, senderEntry, step.DataLong, step.DataLong3, step.DataInt > 0 ? (uint)step.DataInt : 0u);
    }
}

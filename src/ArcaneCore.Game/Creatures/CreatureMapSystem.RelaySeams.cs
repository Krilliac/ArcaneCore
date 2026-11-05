using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>DB script commands that reach services outside the creature system through <see cref="CreatureAiServices"/> seams.</summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>The <c>datalong2</c> teleport option bit this server honours: TELE_TO_NOT_LEAVE_COMBAT (vmangos UnitDefines.h:773).</summary>
    private const uint RelayTeleportKeepCombat = 0x04;

    /// <summary>
    /// SCRIPT_COMMAND_TELEPORT_TO (6, vmangos ScriptCommands.cpp:273-295; cmangos ScriptMgr.cpp:1925-1933): a player (the target, or the source
    /// when the target is not a player) is teleported to map <c>datalong</c>, point (x, y, z), facing o. Any other unit source is moved
    /// by Unit::NearTeleportTo (Unit.cpp:9801-9825): it stays on its own map, so <c>datalong</c> does not apply and a unit that is not on a map
    /// is skipped. <c>datalong2</c> is the teleport options mask: TELE_TO_NOT_LEAVE_COMBAT (0x04) keeps a player's combat on a same-map
    /// teleport (default: it leaves combat, Player.cpp:2016-2017); a creature never stops combat on a near teleport. Other set bits are
    /// reported once and ignored. Without a unit source nothing happens. The step is reported and skipped when the world bound no
    /// teleport service.
    /// </summary>
    private void RelayTeleportTo(RelayScriptStep step, WorldObject? source, WorldObject? target)
    {
        Unit? unit = target as Player ?? source as Unit;
        if (unit is null)
        {
            return;
        }

        if (_ai.Teleports is not { } teleports)
        {
            ReportRelay(step, "TELEPORT_TO without a teleport service");
            return;
        }

        uint unsupported = step.DataLong2 & ~RelayTeleportKeepCombat;
        if (unsupported != 0)
        {
            if (_reportedAi.Add($"relay:{step.Id}:TELEPORT_TO option bits 0x{unsupported:X}"))
            {
                _logger.LogWarning("relay script {Relay} sets TELEPORT_TO option bits 0x{Bits:X} that are ignored; the teleport still runs", step.Id, unsupported);
            }
        }

        bool keepCombat = (step.DataLong2 & RelayTeleportKeepCombat) != 0;
        if (unit is Player)
        {
            teleports.Teleport(unit, step.DataLong, step.X, step.Y, step.Z, step.Orientation, keepCombat);
            return;
        }

        if (unit.Map is null)
        {
            return;
        }

        // Unit::NearTeleportTo: a non-player never changes map, whatever datalong says.
        teleports.Teleport(unit, unit.MapId, step.X, step.Y, step.Z, step.Orientation, keepCombat);
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

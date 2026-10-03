using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Teleport;

/// <summary>
/// MSG_MOVE_TELEPORT_ACK, MSG_MOVE_WORLDPORT_ACK and CMSG_AREATRIGGER (vmangos
/// MovementHandler.cpp / MiscHandler.cpp).
/// </summary>
public sealed class TeleportHandlers : IOpcodeHandlerGroup
{
    /// <summary>mangos_string 49 (LANG_LEVEL_MINREQUIRED), cmangos-classic/vmangos world DB.</summary>
    public const string LevelRequiredText = "You must be at least level {0} to enter.";

    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.MsgMoveTeleportAck, HandleMoveTeleportAck);
        table.OnWorld(WorldOpcode.MsgMoveWorldportAck, HandleMoveWorldportAck);
        table.OnWorld(WorldOpcode.CmsgAreatrigger, HandleAreaTrigger);
    }

    // vmangos WorldSession::HandleMoveTeleportAckOpcode. The movement counter is only logged by
    // vmangos when it does not match a pending change; the teleport goes ahead either way.
    private static void HandleMoveTeleportAck(WorldSession session, Player player, byte[] payload)
    {
        (ulong guid, _, _) = TeleportPackets.ReadMoveTeleportAck(payload);
        Feature(session).Teleports.HandleTeleportAck(player, guid);
    }

    // vmangos WorldSession::HandleMoveWorldportAckOpcode (empty packet).
    private static void HandleMoveWorldportAck(WorldSession session, Player player, byte[] payload)
        => Feature(session).Teleports.HandleWorldportAck(player);

    // vmangos WorldSession::HandleAreaTriggerOpcode (the parts ArcaneCore has systems for:
    // the zone check, area trigger listeners such as quest exploration, then the teleport with
    // its level requirement).
    private static void HandleAreaTrigger(WorldSession session, Player player, byte[] payload)
    {
        uint triggerId = TeleportPackets.ReadAreaTrigger(payload);
        TeleportFeature feature = Feature(session);
        WorldMaps maps = feature.Maps;

        AreaTriggerTemplate? trigger = maps.FindAreaTrigger(triggerId);
        if (trigger is null)
        {
            return;
        }

        if (!AreaTriggerZone.Contains(trigger, player.MapId, player.X, player.Y, player.Z, AreaTriggerZone.ClientDelta))
        {
            return;
        }

        // vmangos handles the quest relation (areatrigger_involvedrelation) before teleports.
        foreach (IAreaTriggerListener listener in session.Services.GetServices<IAreaTriggerListener>())
        {
            listener.OnAreaTrigger(player, triggerId);
        }

        AreaTriggerTeleport? teleport = maps.FindAreaTriggerTeleport(triggerId);
        if (teleport is null || !maps.Registry.Contains(teleport.TargetMap))
        {
            return;
        }

        // vmangos: players in GM mode (.gm on) skip the level requirement.
        if (!player.IsGameMaster && player.Level < teleport.RequiredLevel)
        {
            string text = teleport.Message.Length > 0
                ? teleport.Message
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, LevelRequiredText, teleport.RequiredLevel);
            session.Send(WorldOpcode.SmsgAreaTriggerMessage, TeleportPackets.BuildAreaTriggerMessage(text));
            return;
        }

        feature.Teleports.TeleportTo(player, teleport.TargetMap, teleport.TargetX, teleport.TargetY, teleport.TargetZ, teleport.TargetOrientation);
    }

    private static TeleportFeature Feature(WorldSession session) => session.Services.GetRequiredService<TeleportFeature>();
}

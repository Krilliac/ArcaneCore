using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>Script entry points the ScriptDev world scripts need: an entry's own waypoint path and zone-wide yells.</summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// cmangos <c>MotionMaster::MoveWaypoint(pathId, PATH_FROM_ENTRY)</c> after <c>Clear(false, true)</c>: path
    /// <paramref name="pathId"/> of the creature's own entry (<c>creature_movement_template</c>), path 0 included, replaces
    /// whatever it was doing. A temporary summon has no spawn path, so this is how a script walks it. False, changing nothing,
    /// when the creature is not on this map or the entry has no such path.
    /// </summary>
    public bool StartEntryWaypointPath(Creature creature, uint pathId)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!_creatures.ContainsKey(creature.Guid)) return false;
        IReadOnlyList<CreatureWaypoint> path = _content.GetEntryWaypoints(creature.Template.Entry, pathId);
        if (path.Count == 0) return false;
        StopMoving(creature);
        creature.Motion.Initialize(new WaypointMovementGenerator(path), this, start: true);
        return true;
    }

    /// <summary>
    /// ScriptDev <c>DoBroadcastText(id, source, target, CHAT_TYPE_ZONE_YELL)</c>: the text as a monster yell to every player of
    /// this map standing in the speaker's zone (the text's own chat type is overridden). A missing text is logged once.
    /// Returns the number of players reached.
    /// </summary>
    public int ZoneYell(Creature creature, int textId, Unit? target = null)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (_content.Ai.FindText(textId) is not { } text)
        {
            SayText(creature, textId, target); // logs the missing text once
            return 0;
        }

        uint zone = ZoneAndAreaOf(creature).ZoneId;
        string content = text.Content;
        if (target is Player named)
            content = content.Replace("$N", named.Name, StringComparison.Ordinal).Replace("$n", named.Name, StringComparison.Ordinal);
        byte[] packet = CreatureChatPackets.BuildMonsterMessage(ChatType.MonsterYell, text.Language, creature.Guid,
            creature.Template.Name, target?.Guid ?? default, content);
        int reached = 0;
        foreach (Player player in Map.Players)
        {
            if (Map.GetZoneAndAreaId(player.X, player.Y, player.Z).ZoneId != zone) continue;
            player.Session.Send(WorldOpcode.SmsgMessagechat, packet);
            reached++;
        }

        if (text.Sound != 0) PlayDirectSound(creature, text.Sound);
        return reached;
    }
}

using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Playerbots.Groups;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// Which kills the bots remark on, and where they say it happened (the guild chat's boss topic and the conversations' boss news). A dungeon or raid
/// boss (the instance-bind flag or a world-boss rank, on a dungeon map) is remarked on as ever; on a continent only a creature of the world-boss
/// catalogue is (<see cref="PlayerbotWorldBossCatalogue.IsWorldBossEntry"/>, the coordinator's frozen snapshot): rank 3 alone is not enough, the faction
/// leaders and Archbishop Benedictus are rank 3 on the continents. Without a catalogue wired, a continent kill stays silent.
/// </summary>
internal static class PlayerbotBossKills
{
    /// <summary>
    /// The place to name for the kill of <paramref name="creature"/> on <paramref name="map"/>: the dungeon's name, or for a catalogued world boss
    /// the zone it stood in (<paramref name="zoneOf"/>), else the map's name. Null: nothing to remark on. Any thread that runs the map.
    /// </summary>
    internal static string? PlaceOf(Map map, Creature creature, Func<uint, bool>? isWorldBoss, Func<Map, Creature, string?>? zoneOf)
    {
        if (map.Template is { IsDungeon: true } dungeon)
            return (creature.Template.ExtraFlags & InstanceManager.CreatureFlagExtraInstanceBind) != 0 || creature.IsWorldBoss ? dungeon.Name : null;
        if (isWorldBoss?.Invoke(creature.Entry) != true) return null;
        if (zoneOf?.Invoke(map, creature) is { Length: > 0 } zone) return zone;
        return map.Template?.Name is { Length: > 0 } name ? name : "the open world";
    }
}

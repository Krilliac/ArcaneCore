using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Where a managed bot may move and stay. <see cref="PlayerbotOptions.AllowedMaps"/> limits open-world travel (an empty list
/// allows every map); in addition a bot may always move on, and stay on, the dungeon or raid instance it is in, when the map
/// registry (<c>map_template</c>) says its current map is one. Without that, a bot that entered a dungeon through its entrance
/// (or died in one and came back in as a ghost) could not take a step inside. A map the registry does not know gets the
/// <see cref="PlayerbotOptions.AllowedMaps"/> answer alone, exactly as before this policy. A battleground map is walkable only for a
/// participant of the match on that map instance (<see cref="IsParticipant"/>; the battlegrounds coordinator,
/// docs/areas/playbots-battlegrounds.md): any other bot there is one the match has let go, and the server takes it out.
/// <para>Thread affinity: world thread (reads the player's map).</para>
/// </summary>
internal static class PlayerbotMapPolicy
{
    /// <summary>
    /// The policy itself, free of world state: <paramref name="mapId"/> is allowed when the list is empty or names it, or when
    /// it is the bot's <paramref name="current"/> map and <paramref name="template"/> (null: unknown to the registry) is a dungeon
    /// or raid (vmangos <c>MapEntry::IsDungeon</c>), or a battleground (<c>MapEntry::IsBattleGround</c>) whose match the bot takes
    /// part in (<paramref name="participant"/>).
    /// </summary>
    internal static bool Allows(uint[]? allowedMaps, uint mapId, MapTemplate? template, bool current, bool participant = false)
    {
        if (allowedMaps is not { Length: > 0 } allowed || allowed.Contains(mapId)) return true;
        if (!current || template is null || template.Entry != mapId) return false;
        return template.IsDungeon || (template.IsBattleground && participant);
    }

    /// <summary>
    /// Whether <paramref name="player"/> may plan and walk on the map it is on now (navigation and route following). Besides the policy
    /// itself, the Deeprun Tram map (369) is walkable while the bot's tram trip is under way (<see cref="PlayerbotTram.AllowsWalkingOn"/>),
    /// and only then: the map stays off AllowedMaps, and a bot there without a trip goes nowhere but out (<see cref="PlayerbotTram.Drive"/>).
    /// </summary>
    internal static bool MayMoveOn(Player player, PlayerbotOptions options)
        => player.Map is { } map && (Allows(options.AllowedMaps, map.MapId, map.Template, current: true, IsParticipant(player, map))
            || PlayerbotTram.AllowsWalkingOn(player, map));

    /// <summary>
    /// Whether <paramref name="player"/> takes part in the battleground match of <paramref name="map"/>: the server binds it to a match
    /// (<see cref="BattlegroundFeature.BattlegroundOf"/>, vmangos <c>Player::GetBattleGround</c>) whose map and instance are these, and the
    /// match counts it on a team (<see cref="Game.Battlegrounds.Battleground.PlayerTeam"/>, vmangos <c>BattleGround::AddPlayer</c>).
    /// False on any other map and without a battleground feature.
    /// </summary>
    internal static bool IsParticipant(Player player, Map map)
    {
        if (map.Template is not { IsBattleground: true } || player.Session is not WorldSession session) return false;
        return session.Services.GetService<BattlegroundFeature>()?.BattlegroundOf(player.Guid) is { } match
            && match.MapId == map.MapId && match.InstanceId == map.InstanceId && match.PlayerTeam(player.Guid) is not null;
    }

    /// <summary>
    /// Whether a bot may take an area trigger whose <c>areatrigger_teleport</c> leads to <paramref name="targetMap"/> (null: the
    /// trigger teleports nowhere — a tavern, a quest exploration point — and is always reported). A teleport is only taken to a
    /// map in <paramref name="allowedMaps"/>, the maps the login gate of <see cref="ManagedPlayerbotFeature"/> lets a bot log back
    /// into (<c>AllowedMaps.Contains</c>); anything else (Deeprun Tram, map 369, is neither in the list nor a dungeon; a dungeon
    /// has no way back out for an autonomous bot) would leave a bot frozen, then refused at the next login and quarantined.
    /// Two exceptions: <paramref name="optedIn"/>, a controller that brings the bot back itself (a scenario, a party leader), and
    /// <paramref name="leadsToCorpse"/>, a ghost whose body lies on that map or in a dungeon nested in it (the corpse run through
    /// an entrance, <see cref="PlayerbotRecovery"/>; the server's ghost rules, <c>GhostEntryRules</c>, decide the rest).
    /// </summary>
    internal static bool AllowsTrigger(uint[]? allowedMaps, uint? targetMap, bool optedIn, bool leadsToCorpse)
        => targetMap is not { } target || optedIn || leadsToCorpse || (allowedMaps is { } allowed && allowed.Contains(target));

    /// <summary>
    /// Whether a teleport to <paramref name="targetMap"/> brings a ghost to its body on <paramref name="corpseMap"/>: the body's map
    /// itself, or a dungeon that map is nested in (the non-zero parent chain of <c>GhostEntryRules</c>, vmangos MiscHandler.cpp:712-756).
    /// </summary>
    internal static bool LeadsTo(MapRegistry registry, uint corpseMap, uint targetMap)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var seen = new HashSet<uint> { corpseMap };
        for (uint map = corpseMap; ;)
        {
            if (map == targetMap) return true;
            // A parent of 0 means none, as in GhostEntryRules (a dungeon is never nested in Eastern Kingdoms).
            map = registry.Find(map) is { IsDungeon: true } dungeon ? dungeon.Parent : 0;
            if (map == 0 || !seen.Add(map)) return false;
        }
    }

    /// <summary>
    /// Whether a bot may stay logged in on the map it is on (the login gate of <see cref="ManagedPlayerbotFeature"/>): its map is
    /// in <see cref="PlayerbotOptions.AllowedMaps"/>, or it is the dungeon or raid instance the bot is in. A bot saved inside a
    /// dungeon logs back into that instance (vmangos <c>Player::LoadFromDB</c> keeps the instance bind) and may carry on there. A login on
    /// a battleground map is moved to its entry point by the battleground feature first (vmangos Player.cpp:14775-14788).
    /// </summary>
    internal static bool MayStayOnMap(Player player, PlayerbotOptions options)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        return player.Map is Map map && (Allows(options.AllowedMaps, map.MapId, map.Template, current: true, IsParticipant(player, map))
            || MayLogInOnTram(player, map, options));
    }

    /// <summary>
    /// A bot saved on the Deeprun Tram map (on its way through) logs back in there, when the tram is how its continent's cities are joined
    /// (a continent of AllowedMaps, <see cref="TramNetwork.ContinentMap"/>): the brain takes it out by the nearest platform's exit at its
    /// first think (<see cref="PlayerbotTram.Drive"/>). Refused at the login gate, it was quarantined, and three refusals disabled it.
    /// </summary>
    internal static bool MayLogInOnTram(Player player, Map map, PlayerbotOptions options)
        => map.MapId == TramNetwork.TramMapId
            && options.AllowedMaps is { Length: > 0 } allowed
            && map.Template is { IsDungeon: false, IsBattleground: false }
            && player.Session is WorldSession session
            && TramNetwork.Of(WorldMaps.Of(session.World)) is { } network && allowed.Contains(network.ContinentMap);
}

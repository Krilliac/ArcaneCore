using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Where a managed bot may move and stay. <see cref="PlayerbotOptions.AllowedMaps"/> limits open-world travel (an empty list
/// allows every map); in addition a bot may always move on, and stay on, the dungeon or raid instance it is in, when the map
/// registry (<c>map_template</c>) says its current map is one. Without that, a bot that entered a dungeon through its entrance
/// (or died in one and came back in as a ghost) could not take a step inside. A map the registry does not know gets the
/// <see cref="PlayerbotOptions.AllowedMaps"/> answer alone, exactly as before this policy.
/// <para>Thread affinity: world thread (reads the player's map).</para>
/// </summary>
internal static class PlayerbotMapPolicy
{
    /// <summary>
    /// The policy itself, free of world state: <paramref name="mapId"/> is allowed when the list is empty or names it, or when
    /// it is the bot's <paramref name="current"/> map and <paramref name="template"/> (null: unknown to the registry) is a dungeon
    /// or raid (vmangos <c>MapEntry::IsDungeon</c>).
    /// </summary>
    internal static bool Allows(uint[]? allowedMaps, uint mapId, MapTemplate? template, bool current)
    {
        if (allowedMaps is not { Length: > 0 } allowed || allowed.Contains(mapId)) return true;
        return current && template is { IsDungeon: true } && template.Entry == mapId;
    }

    /// <summary>Whether <paramref name="player"/> may plan and walk on the map it is on now (navigation and route following).</summary>
    internal static bool MayMoveOn(Player player, PlayerbotOptions options)
        => player.Map is { } map && Allows(options.AllowedMaps, map.MapId, map.Template, current: true);

    /// <summary>
    /// Whether a bot may stay logged in on the map it is on (the login gate of <see cref="ManagedPlayerbotFeature"/>): its map is
    /// in <see cref="PlayerbotOptions.AllowedMaps"/>, or it is the dungeon or raid instance the bot is in. A bot saved inside a
    /// dungeon logs back into that instance (vmangos <c>Player::LoadFromDB</c> keeps the instance bind) and may carry on there.
    /// </summary>
    internal static bool MayStayOnMap(Player player, PlayerbotOptions options)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        return player.Map is Map map && Allows(options.AllowedMaps, map.MapId, map.Template, current: true);
    }
}

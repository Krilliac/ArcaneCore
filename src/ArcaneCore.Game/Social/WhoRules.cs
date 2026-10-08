namespace ArcaneCore.Game.Social;

/// <summary>
/// The /who filters of vmangos WhoListClientQueryTask (Handlers/MiscHandler.cpp:69-215) that need more than a field compare.
/// Pure; the caller lower-cases names, guilds, area names and search strings (vmangos wstrToLower).
/// </summary>
public static class WhoRules
{
    /// <summary>The battleground zones whose zone filter is limited to one's own instance: Alterac Valley, Warsong Gulch, Arathi Basin.</summary>
    public static readonly IReadOnlySet<uint> BattlegroundZones = new HashSet<uint> { 2597, 3277, 3358 };

    /// <summary>
    /// The zone filter (MiscHandler.cpp:158-176): no zones listed shows everyone; otherwise the player's zone must be listed,
    /// and when that zone is the asker's own battleground zone the player must also be in the asker's instance ("Using the /who
    /// command while in a Battleground instance will now only display players in your instance", client patch 1.7.0).
    /// </summary>
    public static bool ZoneFilterShows(
        IReadOnlyCollection<uint> zones, uint askerZone, uint askerMap, uint askerInstance, uint playerZone, uint playerMap, uint playerInstance)
    {
        ArgumentNullException.ThrowIfNull(zones);
        if (zones.Count == 0)
        {
            return true;
        }

        if (!zones.Contains(playerZone))
        {
            return false;
        }

        // vmangos compares GetInstanceId() alone; battleground instance ids are unique there. Here instance ids are per map,
        // so the map is compared too.
        return askerZone != playerZone || !BattlegroundZones.Contains(askerZone) || (askerMap == playerMap && askerInstance == playerInstance);
    }

    /// <summary>
    /// The search strings (MiscHandler.cpp:180-196): an empty string is skipped; the player shows when any non-empty string is
    /// part of the guild name, the player name or the area name of the zone (Utf8FitTo); with only empty strings, or none,
    /// everyone shows.
    /// </summary>
    public static bool MatchesSearchStrings(IReadOnlyList<string> strings, string name, string guild, string area)
    {
        ArgumentNullException.ThrowIfNull(strings);
        bool show = true;
        foreach (string term in strings)
        {
            if (term.Length == 0)
            {
                continue;
            }

            if (guild.Contains(term, StringComparison.Ordinal) || name.Contains(term, StringComparison.Ordinal)
                || (area.Length > 0 && area.Contains(term, StringComparison.Ordinal)))
            {
                return true;
            }

            show = false;
        }

        return show;
    }
}

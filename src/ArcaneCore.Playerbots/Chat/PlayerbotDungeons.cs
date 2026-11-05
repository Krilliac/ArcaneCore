using System.Text.RegularExpressions;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>One 1.12 dungeon as players named it in chat: its name, the short forms of the time, and the levels it is run at.</summary>
internal sealed record PlayerbotDungeon(string Name, string Short, IReadOnlyList<string> Tokens, byte MinLevel, byte MaxLevel, bool Raid = false, uint MapId = 0);

/// <summary>
/// The 1.12 dungeons and raids with the level ranges and the abbreviations players used in LFG/LFM lines ("lfm sfk", "lf2m dm",
/// "LFG ZF"). Well-known game data only (the instances of the original game); used to read world channel lines and to write a bot's
/// own LFG line for the dungeon that suits its level. "DM" means the Deadmines to a level 20 and Dire Maul to a level 58, as it did.
/// </summary>
internal static partial class PlayerbotDungeons
{
    internal static readonly IReadOnlyList<PlayerbotDungeon> All =
    [
        new("Ragefire Chasm", "RFC", ["rfc", "ragefire"], 13, 18, MapId: 389),
        new("Wailing Caverns", "WC", ["wc", "wailing"], 17, 24, MapId: 43),
        new("The Deadmines", "DM", ["dm", "deadmines", "vc"], 17, 26, MapId: 36),
        new("Shadowfang Keep", "SFK", ["sfk", "shadowfang"], 22, 30, MapId: 33),
        new("Blackfathom Deeps", "BFD", ["bfd", "blackfathom"], 24, 32, MapId: 48),
        new("The Stockade", "Stocks", ["stocks", "stockade", "stockades"], 24, 32, MapId: 34),
        new("Gnomeregan", "Gnomer", ["gnomer", "gnomeregan"], 29, 38, MapId: 90),
        new("Razorfen Kraul", "RFK", ["rfk", "kraul"], 30, 40, MapId: 47),
        new("Scarlet Monastery", "SM", ["sm", "scarlet", "gy", "lib", "library", "arm", "armory", "cath", "cathedral"], 30, 45, MapId: 189),
        new("Razorfen Downs", "RFD", ["rfd", "downs"], 40, 50, MapId: 129),
        new("Uldaman", "Uld", ["uld", "ulda", "uldaman"], 42, 52, MapId: 70),
        new("Zul'Farrak", "ZF", ["zf", "zulfarrak", "farrak"], 44, 54, MapId: 209),
        new("Maraudon", "Mara", ["mara", "maraudon"], 46, 55, MapId: 349),
        new("Sunken Temple", "ST", ["st", "temple", "sunken"], 50, 56, MapId: 109),
        new("Blackrock Depths", "BRD", ["brd", "depths"], 52, 60, MapId: 230),
        new("Lower Blackrock Spire", "LBRS", ["lbrs"], 55, 60, MapId: 229),
        new("Dire Maul", "DM", ["dm", "diremaul", "dme", "dmw", "dmn", "tribute"], 56, 60, MapId: 429),
        new("Upper Blackrock Spire", "UBRS", ["ubrs"], 58, 60, MapId: 229),
        new("Stratholme", "Strat", ["strat", "stratholme", "strath"], 58, 60, MapId: 329),
        new("Scholomance", "Scholo", ["scholo", "scholomance"], 58, 60, MapId: 289),
        new("Molten Core", "MC", ["mc"], 60, 60, Raid: true, MapId: 409),
        new("Onyxia's Lair", "Ony", ["ony", "onyxia"], 60, 60, Raid: true, MapId: 249),
        new("Zul'Gurub", "ZG", ["zg", "zulgurub"], 60, 60, Raid: true, MapId: 309),
        new("Blackwing Lair", "BWL", ["bwl"], 60, 60, Raid: true, MapId: 469),
    ];

    /// <summary>
    /// The dungeon a 2005 player of <paramref name="level"/> would go to: the five-player instance whose range holds the level, the
    /// lowest such (the one just reached), or null below 13 and at none.
    /// </summary>
    internal static PlayerbotDungeon? SuitedFor(int level)
    {
        PlayerbotDungeon? best = null;
        foreach (PlayerbotDungeon dungeon in All)
        {
            if (dungeon.Raid || level < dungeon.MinLevel || level > dungeon.MaxLevel) continue;
            if (best is null || dungeon.MinLevel > best.MinLevel) best = dungeon;
        }

        return best;
    }

    /// <summary>
    /// The dungeon a chat line names (a token of a dungeon, whole word, any case), the one that suits <paramref name="level"/> when a
    /// token is ambiguous ("dm"); null when it names none.
    /// </summary>
    internal static PlayerbotDungeon? Named(string text, int level)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        PlayerbotDungeon? found = null;
        foreach (Match word in Word().Matches(text.ToLowerInvariant()))
        {
            string token = word.Value.Replace("'", string.Empty, StringComparison.Ordinal);
            foreach (PlayerbotDungeon dungeon in All)
            {
                if (!dungeon.Tokens.Contains(token)) continue;
                bool fits = level >= dungeon.MinLevel - 2 && level <= dungeon.MaxLevel + 2;
                if (found is null || (fits && !(level >= found.MinLevel - 2 && level <= found.MaxLevel + 2))) found = dungeon;
            }

            if (found is not null) return found;
        }

        return found;
    }

    /// <summary>Whether a bot of <paramref name="level"/> may go to <paramref name="dungeon"/> (its range, two levels of slack either side).</summary>
    internal static bool Fits(PlayerbotDungeon dungeon, int level) => !dungeon.Raid && level >= dungeon.MinLevel - 2 && level <= dungeon.MaxLevel + 2;

    /// <summary>The recommended range of the five-player or raid instance on <paramref name="mapId"/> (the union of the entries sharing the map, e.g. LBRS and UBRS), or null.</summary>
    internal static (byte Min, byte Max)? RecommendedBand(uint mapId)
    {
        if (mapId == 0) return null;
        bool found = false;
        byte min = byte.MaxValue, max = 0;
        foreach (PlayerbotDungeon dungeon in All)
        {
            if (dungeon.MapId != mapId) continue;
            found = true;
            if (dungeon.MinLevel < min) min = dungeon.MinLevel;
            if (dungeon.MaxLevel > max) max = dungeon.MaxLevel;
        }

        return found ? (min, max) : null;
    }

    [GeneratedRegex(@"[a-z']+")] private static partial Regex Word();
}

using System.Text.RegularExpressions;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>One 1.12 dungeon as players named it in chat: its name, the short forms of the time, and the levels it is run at.</summary>
internal sealed record PlayerbotDungeon(string Name, string Short, IReadOnlyList<string> Tokens, byte MinLevel, byte MaxLevel, bool Raid = false);

/// <summary>
/// The 1.12 dungeons and raids with the level ranges and the abbreviations players used in LFG/LFM lines ("lfm sfk", "lf2m dm",
/// "LFG ZF"). Well-known game data only (the instances of the original game); used to read world channel lines and to write a bot's
/// own LFG line for the dungeon that suits its level. "DM" means the Deadmines to a level 20 and Dire Maul to a level 58, as it did.
/// </summary>
internal static partial class PlayerbotDungeons
{
    internal static readonly IReadOnlyList<PlayerbotDungeon> All =
    [
        new("Ragefire Chasm", "RFC", ["rfc", "ragefire"], 13, 18),
        new("Wailing Caverns", "WC", ["wc", "wailing"], 17, 24),
        new("The Deadmines", "DM", ["dm", "deadmines", "vc"], 17, 26),
        new("Shadowfang Keep", "SFK", ["sfk", "shadowfang"], 22, 30),
        new("Blackfathom Deeps", "BFD", ["bfd", "blackfathom"], 24, 32),
        new("The Stockade", "Stocks", ["stocks", "stockade", "stockades"], 24, 32),
        new("Gnomeregan", "Gnomer", ["gnomer", "gnomeregan"], 29, 38),
        new("Razorfen Kraul", "RFK", ["rfk", "kraul"], 30, 40),
        new("Scarlet Monastery", "SM", ["sm", "scarlet", "gy", "lib", "library", "arm", "armory", "cath", "cathedral"], 30, 45),
        new("Razorfen Downs", "RFD", ["rfd", "downs"], 40, 50),
        new("Uldaman", "Uld", ["uld", "ulda", "uldaman"], 42, 52),
        new("Zul'Farrak", "ZF", ["zf", "zulfarrak", "farrak"], 44, 54),
        new("Maraudon", "Mara", ["mara", "maraudon"], 46, 55),
        new("Sunken Temple", "ST", ["st", "temple", "sunken"], 50, 56),
        new("Blackrock Depths", "BRD", ["brd", "depths"], 52, 60),
        new("Lower Blackrock Spire", "LBRS", ["lbrs"], 55, 60),
        new("Dire Maul", "DM", ["dm", "diremaul", "dme", "dmw", "dmn", "tribute"], 56, 60),
        new("Upper Blackrock Spire", "UBRS", ["ubrs"], 58, 60),
        new("Stratholme", "Strat", ["strat", "stratholme", "strath"], 58, 60),
        new("Scholomance", "Scholo", ["scholo", "scholomance"], 58, 60),
        new("Molten Core", "MC", ["mc"], 60, 60, Raid: true),
        new("Onyxia's Lair", "Ony", ["ony", "onyxia"], 60, 60, Raid: true),
        new("Zul'Gurub", "ZG", ["zg", "zulgurub"], 60, 60, Raid: true),
        new("Blackwing Lair", "BWL", ["bwl"], 60, 60, Raid: true),
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

    [GeneratedRegex(@"[a-z']+")] private static partial Regex Word();
}

namespace ArcaneCore.Game.Reputation;

/// <summary>
/// en-US rank names (vmangos ReputationRankStrIndex / the client's FACTION_STANDING_LABEL, LANG_REP_*),
/// indexed by <see cref="ReputationRank"/>. Localisation is a documented limit: the GM commands print en-US only.
/// </summary>
public static class ReputationRankNames
{
    private static readonly string[] Names = ["Hated", "Hostile", "Unfriendly", "Neutral", "Friendly", "Honored", "Revered", "Exalted"];

    public static string Name(ReputationRank rank) => (int)rank < Names.Length ? Names[(int)rank] : "Unknown";

    /// <summary>
    /// Case-insensitive prefix match of a rank name (the `.modify rep` argument form, CharacterCommands.cpp:4362-4395:
    /// a non-empty prefix of the localised name). The first rank whose name starts with the text wins.
    /// </summary>
    public static bool TryParsePrefix(string text, out ReputationRank rank)
    {
        rank = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (int i = 0; i < Names.Length; i++)
        {
            if (Names[i].StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                rank = (ReputationRank)i;
                return true;
            }
        }

        return false;
    }
}

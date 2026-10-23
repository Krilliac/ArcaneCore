namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>Whether a line addressed to a bot is an apology. Pure; the line is read and dropped, never stored.</summary>
internal static class PlayerbotApology
{
    /// <summary>TEXTEMOTE_APOLOGIZE, the client's /sorry.</summary>
    internal const uint SorryEmote = 4;

    // Whole words ("sorrycake" and "my badass" are not apologies); "my bad" is the one two-word phrase.
    private static readonly string[] Words = ["sorry", "apologies", "apologize", "apologise"];

    // A negator in the three words before the keyword turns it round ("not sorry", "never even sorry", "I won't say sorry").
    private static readonly HashSet<string> Negators = new(StringComparer.OrdinalIgnoreCase)
    {
        "not", "never", "nor", "dont", "don't", "wont", "won't", "cant", "can't", "isnt", "isn't", "aint", "ain't", "refuse", "hardly",
    };

    private const int NegationReach = 3;

    internal static bool IsApology(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string[] words = text.Replace('\u2019', '\'').Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            bool hit = Array.Exists(Words, w => string.Equals(w, words[i], StringComparison.OrdinalIgnoreCase))
                || (string.Equals(words[i], "my", StringComparison.OrdinalIgnoreCase) && i + 1 < words.Length
                    && string.Equals(words[i + 1], "bad", StringComparison.OrdinalIgnoreCase));
            if (hit && !Negated(words, i)) return true;
        }

        return false;
    }

    private static bool Negated(string[] words, int at)
    {
        for (int j = Math.Max(0, at - NegationReach); j < at; j++)
            if (Negators.Contains(words[j])) return true;
        return false;
    }

    private static readonly char[] Separators = [' ', '\t', '\r', '\n', ',', '.', '!', '?', ';', ':', '"', '(', ')', '[', ']', '-', '*', '~', '/'];
}

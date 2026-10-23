namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>Whether a line addressed to a bot is an apology. Pure; the line is read and dropped, never stored.</summary>
internal static class PlayerbotApology
{
    /// <summary>TEXTEMOTE_APOLOGIZE, the client's /sorry.</summary>
    internal const uint SorryEmote = 4;

    private static readonly string[] Keywords = ["sorry", "my bad", "apologies", "apologize"];

    internal static bool IsApology(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (string keyword in Keywords)
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

using System.Globalization;

namespace ArcaneCore.World.Gm.Args;

/// <summary>vmangos <c>normalizePlayerName</c> (D:\refs\vmangos\src\game\ObjectMgr.cpp:64-82).</summary>
public static class PlayerNames
{
    /// <summary>MAX_PLAYER_NAME (ObjectMgr.h:402).</summary>
    public const int MaxLength = 12;

    /// <summary>First letter upper case, the rest lower case; false for an empty or overlong name.</summary>
    public static bool TryNormalize(string raw, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        var info = CultureInfo.InvariantCulture.TextInfo;
        var runes = raw.EnumerateRunes().ToList();
        if (runes.Count > MaxLength)
        {
            return false;
        }

        var builder = new System.Text.StringBuilder(raw.Length);
        for (int i = 0; i < runes.Count; i++)
        {
            builder.Append(i == 0 ? info.ToUpper(runes[i].ToString()) : info.ToLower(runes[i].ToString()));
        }

        name = builder.ToString();
        return true;
    }
}

/// <summary>
/// vmangos <c>ExtractPlayerTarget</c> (Chat.cpp:3717-3790) limited to online players: a name or
/// <c>|Hplayer:Name|</c> link when an argument is present, otherwise the selected player
/// (GetSelectedPlayer, Chat.cpp:2601: the invoker itself when nothing is selected).
/// </summary>
public static class PlayerTargetResolver
{
    /// <summary>
    /// True with the target and its normalised name; false when the argument is not a valid name,
    /// no such player is online or the selection is not an online player (the caller then answers
    /// "Player not found!" and treats the command as handled).
    /// </summary>
    /// <param name="selected">Returns the selected online player, the invoker when nothing is selected, or null for any other selection.</param>
    public static bool TryExtract<TPlayer>(
        CommandArgs args,
        Func<string, TPlayer?> findOnline,
        Func<TPlayer?> selected,
        out TPlayer? player,
        out string name)
        where TPlayer : class
    {
        player = null;
        name = string.Empty;
        if (!args.IsEmpty)
        {
            string? raw = args.ExtractKeyFromLink("Hplayer", out _, out _);
            if (raw is null || !PlayerNames.TryNormalize(raw, out string normalised))
            {
                return false;
            }

            player = findOnline(normalised);
            if (player is null)
            {
                return false;
            }

            name = normalised;
            return true;
        }

        player = selected();
        return player is not null;
    }
}

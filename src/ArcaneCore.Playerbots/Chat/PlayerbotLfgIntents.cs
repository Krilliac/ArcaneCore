using System.Text.RegularExpressions;
using ArcaneCore.World.Playerbots.BotSocial;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// Reading the role a player states when answering a bot's LFG or LFM call (docs/areas/playbots.md, Bot voice). Pure: no world, no clock. The
/// line is read on the text <see cref="PlayerbotWorldChatter.Normalize"/> makes (lower case, letters, digits and apostrophes).
/// </summary>
internal static partial class PlayerbotLfgIntents
{
    private const int MaxLineLength = 255;

    /// <summary>The role a line states ("i can tank", "inv, healer"): tank, healer or damage; null when it states none. Class words state no role.</summary>
    internal static BotChatRole? RoleOf(string text)
        => string.IsNullOrWhiteSpace(text) || text.Length > MaxLineLength ? null : StatedRole(PlayerbotWorldChatter.Normalize(text));

    private static BotChatRole? StatedRole(string line)
    {
        Match match = Role().Match(line);
        if (!match.Success) return null;
        if (match.Groups["tank"].Success) return BotChatRole.Tank;
        return match.Groups["heal"].Success ? BotChatRole.Healer : BotChatRole.Damage;
    }

    [GeneratedRegex(@"(?<tank>tank|mt)|(?<heal>heals?|healer)|(?<dps>dps|dd|damage)")]
    private static partial Regex Role();
}

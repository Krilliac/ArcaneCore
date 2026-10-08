using System.Globalization;
using ArcaneCore.Game.Battlegrounds;

namespace ArcaneCore.World.Battlegrounds;

/// <summary>
/// The texts of the battleground messages. The broadcast texts (<see cref="BattlegroundTexts"/>' BCT ids) come from the world's
/// <c>broadcast_text</c> when it has the row; the <c>mangos_string</c> texts (node names, node events, the queue and premature-finish
/// messages) are the English rows of cmangos-classic <c>mangos.sql</c> (lines 3964-4031), since this server has no <c>mangos_string</c>
/// table. A broadcast text missing from the database falls back to the English line recorded here, so a test world or a partial import still
/// reads sensibly. <c>$n</c> is the source player's name; <c>%s</c> and <c>%u</c> are the arguments in order.
/// </summary>
public static class BattlegroundStrings
{
    private static readonly Dictionary<uint, string> s_mangos = new()
    {
        [650] = "Alliance",
        [651] = "Horde",
        [652] = "stables",
        [653] = "blacksmith",
        [654] = "farm",
        [655] = "lumber mill",
        [656] = "mine",
        [657] = "The %s has taken the %s",
        [658] = "$n has defended the %s",
        [659] = "$n has assaulted the %s",
        [660] = "$n claims the %s! If left unchallenged, the %s will control it in 1 minute!",
        [715] = "You don't meet Battleground level requirements",
        [720] = "Your group is too large for this battleground. Please regroup to join.",
        [727] = "Your group has an offline member. Please remove him before joining.",
        [728] = "Your group has players from the opposing faction. You can't join the battleground as a group.",
        [729] = "Your group has players from different battleground brakets. You can't join as group.",
        [730] = "Someone in your party is already in this battleground queue. (S)he must leave it before joining as group.",
        [731] = "Someone in your party is Deserter. You can't join as group.",
        [732] = "Someone in your party is already in three battleground queues. You cannot join as group.",
        [750] = "Not enough players. This game will close in %u mins.",
        [751] = "Not enough players. This game will close in %u seconds.",
        [759] = "%s was destroyed by the %s!",
        [760] = "The %s is under attack! If left unchecked, the %s will destroy it!",
        [761] = "The %s was taken by the %s!",
        [762] = "The %s was taken by the %s!",
        [763] = "The %s was taken by the %s!",
        [764] = "The %s is under attack! If left unchecked, the %s will capture it!",
        [765] = "The %s has taken the %s! Its supplies will now be used for reinforcements!",
        [766] = "Irondeep Mine",
        [767] = "Coldtooth Mine",
        [768] = "Stormpike Aid Station",
        [769] = "Dun Baldar South Bunker",
        [770] = "Dun Baldar North Bunker",
        [771] = "Stormpike Graveyard",
        [772] = "Icewing Bunker",
        [773] = "Stonehearth Graveyard",
        [774] = "Stonehearth Bunker",
        [775] = "Snowfall Graveyard",
        [776] = "Iceblood Tower",
        [777] = "Iceblood Graveyard",
        [778] = "Tower Point",
        [779] = "Frostwolf Graveyard",
        [780] = "East Frostwolf Tower",
        [781] = "West Frostwolf Tower",
        [782] = "Frostwolf Relief Hut",
        [789] = "The Frostwolf General is Dead!",
        [790] = "The Stormpike General is Dead!",
    };

    /// <summary>English lines of the battleground broadcast texts, used only when the world's <c>broadcast_text</c> has no row.</summary>
    private static readonly Dictionary<uint, string> s_broadcastFallback = new()
    {
        [BattlegroundTexts.WsCapturedHordeFlag] = "$n captured the Horde flag!",
        [BattlegroundTexts.WsCapturedAllianceFlag] = "$n captured the Alliance flag!",
        [BattlegroundTexts.WsFlagsPlaced] = "The flags are now placed at their bases.",
        [BattlegroundTexts.WsPickedUpAllianceFlag] = "The Alliance Flag was picked up by $n!",
        [BattlegroundTexts.WsDroppedAllianceFlag] = "The Alliance Flag was dropped by $n!",
        [BattlegroundTexts.WsDroppedHordeFlag] = "The Horde flag was dropped by $n!",
        [BattlegroundTexts.WsPickedUpHordeFlag] = "The Horde flag was picked up by $n!",
        [BattlegroundTexts.WsReturnedAllianceFlag] = "The Alliance Flag was returned to its base by $n!",
        [BattlegroundTexts.WsReturnedHordeFlag] = "The Horde flag was returned to its base by $n!",
        [BattlegroundTexts.WsHordeWins] = "The Horde wins!",
        [BattlegroundTexts.WsAllianceWins] = "The Alliance wins!",
        [BattlegroundTexts.WsHasBegun] = "Let the battle for Warsong Gulch begin!",
        [BattlegroundTexts.WsStartOneMinute] = "The battle for Warsong Gulch begins in 1 minute.",
        [BattlegroundTexts.WsStartHalfMinute] = "The battle for Warsong Gulch begins in 30 seconds. Prepare yourselves!",
        [BattlegroundTexts.WsAllianceFlagRespawned] = "The Alliance Flag is now placed at its base.",
        [BattlegroundTexts.WsHordeFlagRespawned] = "The Horde flag is now placed at its base.",
        [BattlegroundTexts.AbStartOneMinute] = "The Battle for Arathi Basin will begin in 1 minute.",
        [BattlegroundTexts.AbStartHalfMinute] = "The Battle for Arathi Basin will begin in 30 seconds. Prepare yourselves!",
        [BattlegroundTexts.AbHasBegun] = "The Battle for Arathi Basin has begun!",
        [BattlegroundTexts.AbAllianceNearVictory] = "The Alliance has gathered enough resources, and is near victory!",
        [BattlegroundTexts.AbHordeNearVictory] = "The Horde has gathered enough resources, and is near victory!",
        [BattlegroundTexts.AbAllianceWins] = "The Alliance wins!",
        [BattlegroundTexts.AbHordeWins] = "The Horde wins!",
        [BattlegroundTexts.AvStartOneMinute] = "1 minute until the battle for Alterac Valley begins.",
        [BattlegroundTexts.AvStartHalfMinute] = "30 seconds until the battle for Alterac Valley begins. Prepare yourselves!",
        [BattlegroundTexts.AvHasBegun] = "The battle for Alterac Valley has begun!",
        [BattlegroundTexts.AvAllianceWins] = "The Alliance wins!",
        [BattlegroundTexts.AvHordeWins] = "The Horde wins!",
    };

    /// <summary>The English text of a <c>mangos_string</c> id, or null when it is not a battleground string.</summary>
    public static string? Mangos(uint id) => s_mangos.GetValueOrDefault(id);

    /// <summary>The recorded English line of a battleground broadcast text, or null.</summary>
    public static string? BroadcastFallback(uint id) => s_broadcastFallback.GetValueOrDefault(id);

    /// <summary>
    /// Fill a template the way vmangos <c>SendMessage2ToAll</c> does: <c>$n</c> becomes <paramref name="sourceName"/>, then each <c>%s</c> or
    /// <c>%u</c> takes the next argument (a string id argument is resolved through <see cref="Mangos"/>).
    /// </summary>
    public static string Format(string template, string? sourceName, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(template);
        string text = sourceName is null ? template : template.Replace("$n", sourceName, StringComparison.Ordinal);
        var result = new System.Text.StringBuilder(text.Length + 32);
        int next = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && i + 1 < text.Length && (text[i + 1] == 's' || text[i + 1] == 'u'))
            {
                object? arg = next < args.Length ? args[next++] : null;
                result.Append(arg switch
                {
                    null => string.Empty,
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => arg.ToString(),
                });
                i++;
                continue;
            }

            result.Append(text[i]);
        }

        return result.ToString();
    }
}

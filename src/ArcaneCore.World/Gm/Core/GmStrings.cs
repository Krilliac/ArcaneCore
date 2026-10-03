using System.Globalization;

namespace ArcaneCore.World.Gm.Core;

/// <summary>
/// The reply texts of GM commands, keyed by the vmangos <c>LANG_*</c> id
/// (D:\refs\vmangos\src\game\Language.h). vmangos' base <c>mangos_string</c> rows are not in the
/// reference tree, so each English text is the one mangos-classic ships at the same id
/// (D:\refs\mangos-classic\sql\base\mangos.sql:3423-3880); ids a vmangos migration overrides
/// are noted on the member. Format arguments use the C printf order.
/// </summary>
public static class GmStrings
{
    /// <summary>LANG_PLAYER_NOT_FOUND (499; mangos.sql:3851).</summary>
    public const string PlayerNotFound = "Player not found!";

    /// <summary>LANG_COMMAND_COULDNOTFIND (434; mangos.sql:3787): "Could not find '%s'".</summary>
    public static string CouldNotFind(string what) => $"Could not find '{what}'";

    /// <summary>LANG_COMMAND_ITEMIDINVALID (435; mangos.sql:3788): "Invalid item id: %u".</summary>
    public static string ItemIdInvalid(uint itemId) => string.Create(CultureInfo.InvariantCulture, $"Invalid item id: {itemId}");

    /// <summary>LANG_REMOVEITEM (496; mangos.sql:3848): "Removed itemID = %i, amount = %i from %s".</summary>
    public static string RemoveItem(uint itemId, uint amount, string targetLink)
        => string.Create(CultureInfo.InvariantCulture, $"Removed itemID = {itemId}, amount = {amount} from {targetLink}");

    /// <summary>LANG_ITEM_CANNOT_CREATE (497; mangos.sql:3849): "Cannot create item '%i' (amount: %i)".</summary>
    public static string ItemCannotCreate(uint itemId, uint amount)
        => string.Create(CultureInfo.InvariantCulture, $"Cannot create item '{itemId}' (amount: {amount})");

    /// <summary>vmangos CharacterCommands.cpp:3408 (literal in the source, English): "Cannot remove %u instances of %u - maximum value is %u".</summary>
    public static string CannotRemoveItems(uint count, uint itemId, uint held)
        => string.Create(CultureInfo.InvariantCulture, $"Cannot remove {count} instances of {itemId} - maximum value is {held}");

    /// <summary>vmangos <c>playerLink</c> (Chat.h:134): the clickable player name used in replies.</summary>
    public static string PlayerLink(string name) => $"|cffffffff|Hplayer:{name}|h[{name}]|h|r";
}

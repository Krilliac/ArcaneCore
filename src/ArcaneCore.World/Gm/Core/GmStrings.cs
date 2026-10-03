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

    /// <summary>LANG_COMMAND_TELE_NOTFOUND (164; mangos.sql:3550).</summary>
    public const string TeleNotFound = "Teleport location not found!";

    /// <summary>LANG_CANT_TELEPORT_SELF (171; mangos.sql:3556).</summary>
    public const string CantTeleportSelf = "You can't teleport self to self!";

    /// <summary>LANG_IS_TELEPORTED (102; mangos.sql:3490).</summary>
    public static string IsTeleported(string link) => $"{link} is already being teleported.";

    /// <summary>LANG_SUMMONING (108; mangos.sql:3496): "You are summoning %s%s.".</summary>
    public static string Summoning(string link, string suffix = "") => $"You are summoning {link}{suffix}.";

    /// <summary>LANG_SUMMONED_BY (109; mangos.sql:3497).</summary>
    public static string SummonedBy(string link) => $"You are being summoned by {link}.";

    /// <summary>LANG_TELEPORTING_TO (110; mangos.sql:3498): "You are teleporting %s%s to %s.".</summary>
    public static string TeleportingTo(string link, string suffix, string location) => $"You are teleporting {link}{suffix} to {location}.";

    /// <summary>LANG_TELEPORTED_TO_BY (111; mangos.sql:3499).</summary>
    public static string TeleportedToBy(string link) => $"You are being teleported by {link}.";

    /// <summary>LANG_APPEARING_AT_ONLINE (113; mangos.sql:3501): "Appearing at %s's location.".</summary>
    public static string AppearingAt(string link) => $"Appearing at {link}'s location.";

    /// <summary>LANG_APPEARING_TO (114; mangos.sql:3502).</summary>
    public static string AppearingTo(string link) => $"{link} is appearing to your location.";
}

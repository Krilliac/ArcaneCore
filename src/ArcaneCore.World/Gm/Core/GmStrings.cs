using System.Globalization;

namespace ArcaneCore.World.Gm.Core;

/// <summary>
/// The reply texts of GM commands, keyed by the vmangos <c>LANG_*</c> id
/// (vmangos@0e3ff01 src/game/Language.h). vmangos' base <c>mangos_string</c> rows are not in the
/// reference tree, so each English text is the one mangos-classic ships at the same id
/// (mangos-classic sql/base/mangos.sql:3423-3880); ids a vmangos migration overrides
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

    /// <summary>LANG_CANNOT_GO_TO_INST_PARTY (104; mangos.sql:3492; vmangos Language.h:107). The text takes no argument; vmangos passes the name link anyway.</summary>
    public const string CannotGoToInstParty = "You cannot go to the player's instance because you are in a party now.";

    /// <summary>LANG_CANNOT_GO_TO_BG_FROM_BG (736; mangos.sql:4001; vmangos Language.h:779).</summary>
    public const string CannotGoToBgFromBg = "You cannot teleport to a battleground from another battleground. Please leave the current battleground first.";

    /// <summary>LANG_COMMAND_NOITEMFOUND (436; mangos.sql:3789).</summary>
    public const string NoItemsFound = "No items found!";

    /// <summary>LANG_COMMAND_NOCREATUREFOUND (447; mangos.sql:3801).</summary>
    public const string NoCreaturesFound = "No creatures found!";

    /// <summary>LANG_COMMAND_NOGAMEOBJECTFOUND (448; mangos.sql:3802).</summary>
    public const string NoGameObjectsFound = "No gameobjects found!";

    /// <summary>LANG_COMMAND_ITEM_USABLE (vmangos 1152; mangos-classic mangos.sql:4129).</summary>
    public const string Usable = "[usable]";

    /// <summary>LANG_COMMAND_TELE_NOLOCATION (166; mangos.sql:3552).</summary>
    public const string TeleNoLocation = "There are no teleport locations matching your request.";

    /// <summary>The first line of LANG_COMMAND_TELE_LOCATION (168; mangos.sql:3553: "Locations found are:" then the list).</summary>
    public const string TeleLocationsFound = "Locations found are:";

    /// <summary>Not a vmangos text: printed when <c>GmCommands:LookupMaxResults</c> cut a lookup short.</summary>
    public const string LookupOmitted = "More results were omitted (World:GmCommands:LookupMaxResults).";

    /// <summary>LANG_MOTD_CURRENT (56; mangos.sql:3478): "Current Message of the day: \r\n%s".</summary>
    public static string MotdCurrent(string motd) => $"Current Message of the day: \r\n{motd}";

    /// <summary>LANG_MOTD_NEW (vmangos 1101; mangos-classic mangos.sql:4083): "Message of the day changed to:\r\n%s".</summary>
    public static string MotdChanged(string motd) => $"Message of the day changed to:\r\n{motd}";

    /// <summary>LANG_CONNECTED_USERS as vmangos prints it for .server info (ServerCommands.cpp:310).</summary>
    public static string PlayersOnline(int active, int queued, int maxActive, int maxQueued)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Players online: {active} ({queued} queued). Max online: {maxActive} ({maxQueued} queued).");

    /// <summary>LANG_UPTIME (13; mangos.sql:3435).</summary>
    public static string Uptime(string time) => $"Server uptime: {time}";

    /// <summary>LANG_NO_CHAR_SELECTED (116; mangos.sql: "No character selected.").</summary>
    public const string NoCharSelected = "No character selected.";

    /// <summary>LANG_BAD_VALUE (115): "Incorrect values.".</summary>
    public const string BadValue = "Incorrect values.";

    /// <summary>LANG_YOU_TAKE_ALL_MONEY (153).</summary>
    public static string YouTakeAllMoney(string link) => $"You take all copper of {link}.";

    /// <summary>LANG_YOURS_ALL_MONEY_GONE (154).</summary>
    public static string YoursAllMoneyGone(string link) => $"{link} took you all of your copper.";

    /// <summary>LANG_YOU_TAKE_MONEY (155).</summary>
    public static string YouTakeMoney(long copper, string link) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"You take {copper} copper from {link}.");

    /// <summary>LANG_YOURS_MONEY_TAKEN (156).</summary>
    public static string YoursMoneyTaken(string link, long copper) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{link} took {copper} copper from you.");

    /// <summary>LANG_YOU_GIVE_MONEY (157).</summary>
    public static string YouGiveMoney(long copper, string link) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"You give {copper} copper to {link}.");

    /// <summary>LANG_YOURS_MONEY_GIVEN (158).</summary>
    public static string YoursMoneyGiven(string link, long copper) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{link} gave you {copper} copper.");

    /// <summary>LANG_YOU_CHANGE_HP (118): "You changed HP of %s to %i/%i.".</summary>
    public static string YouChangeHp(string link, int hp, int max) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"You changed HP of {link} to {hp}/{max}.");

    /// <summary>LANG_YOURS_HP_CHANGED (119).</summary>
    public static string YoursHpChanged(string link, int hp, int max) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{link} changed your HP to {hp}/{max}.");

    /// <summary>LANG_YOU_CHANGE_MANA (120).</summary>
    public static string YouChangeMana(string link, int mana, int max) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"You changed MANA of {link} to {mana}/{max}.");

    /// <summary>LANG_YOURS_MANA_CHANGED (121).</summary>
    public static string YoursManaChanged(string link, int mana, int max) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{link} changed your MANA to {mana}/{max}.");

    /// <summary>LANG_YOU_CHANGE_ENERGY (122; mangos.sql).</summary>
    public static string YouChangeEnergy(string link, uint energy, uint max) => string.Create(CultureInfo.InvariantCulture, $"You changed ENERGY of {link} to {energy}/{max}.");

    /// <summary>LANG_YOURS_ENERGY_CHANGED (123; mangos.sql).</summary>
    public static string YoursEnergyChanged(string link, uint energy, uint max) => string.Create(CultureInfo.InvariantCulture, $"{link} changed your ENERGY to {energy}/{max}.");

    /// <summary>LANG_YOU_CHANGE_RAGE (125; mangos.sql).</summary>
    public static string YouChangeRage(string link, uint rage, uint max) => string.Create(CultureInfo.InvariantCulture, $"You changed rage of {link} to {rage}/{max}.");

    /// <summary>LANG_YOURS_RAGE_CHANGED (126; mangos.sql).</summary>
    public static string YoursRageChanged(string link, uint rage, uint max) => string.Create(CultureInfo.InvariantCulture, $"{link} changed your rage to {rage}/{max}.");

    /// <summary>LANG_YOU_CHANGE_LVL (127): "You changed level of %s to %i.".</summary>
    public static string YouChangeLevel(string link, int level) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"You changed level of {link} to {level}.");

    /// <summary>LANG_YOURS_LEVEL_UP (557).</summary>
    public static string YoursLevelUp(string link, int level) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{link} level up you to ({level})");

    /// <summary>LANG_YOURS_LEVEL_DOWN (558).</summary>
    public static string YoursLevelDown(string link, int level) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{link} level down you to ({level})");

    /// <summary>LANG_YOURS_LEVEL_PROGRESS_RESET (559).</summary>
    public static string YoursLevelProgressReset(string link) => $"{link} reset your level progress.";

    /// <summary>LANG_SELECT_CHAR_OR_CREATURE (1): "You should select a character or a creature.".</summary>
    public const string SelectCharOrCreature = "You should select a character or a creature.";

    /// <summary>Not a vmangos text: this server does not level creatures with .levelup (vmangos does).</summary>
    public const string LevelingCreaturesUnsupported = "Leveling creatures is not supported; select a player.";

    /// <summary>LANG_COMMAND_UNAVAILABLE (50): the vmangos migration 20240107103630_world.sql:14 text.</summary>
    public const string CommandUnavailable = "This command is not available to you.";

    /// <summary>LANG_NO_CMD (6; mangos.sql:3428), without a final period.</summary>
    public const string NoSuchCommand = "There is no such command";

    /// <summary>LANG_NO_SUBCMD (7; mangos.sql:3429).</summary>
    public const string NoSuchSubcommand = "There is no such subcommand";

    /// <summary>LANG_SUBCMDS_LIST (8; mangos.sql:3430): "Command %s have subcommands:".</summary>
    public static string SubcommandsList(string command) => $"Command {command} have subcommands:";

    /// <summary>LANG_AVIABLE_CMD (9; mangos.sql:3431).</summary>
    public const string CommandsAvailable = "Commands available to you:";

    /// <summary>LANG_CMD_SYNTAX (10; mangos.sql:3432).</summary>
    public const string CmdSyntax = "Incorrect syntax.";

    /// <summary>LANG_NO_HELP_CMD (5; mangos.sql:3427).</summary>
    public const string NoHelpForCommand = "There is no help for that command";

    /// <summary>LANG_YOURS_SECURITY_IS_LOW (403; mangos.sql:3756).</summary>
    public const string SecurityTooLow = "You have low security level for this.";

    /// <summary>Not a vmangos text: the reply when a console command needs the invoker's own character or selection.</summary>
    public const string ConsoleNeedsPlayer = "This command acts on your own character or your selection, which the console does not have; name a player instead (e.g. 'levelup Name 5').";

    /// <summary>LANG_USE_BOL (259; mangos.sql:3628).</summary>
    public const string UseOnOff = "Incorrect value, use on or off";

    /// <summary>LANG_PLAYER_SAVED (14; mangos.sql:3436).</summary>
    public const string PlayerSaved = "Player saved.";

    /// <summary>LANG_PLAYERS_SAVED (15; mangos.sql:3437).</summary>
    public const string PlayersSaved = "All players saved.";

    /// <summary>LANG_SYSTEMMESSAGE (3; mangos.sql:3425): "|cffff0000[System Message]: %s|r", what .announce sends.</summary>
    public static string SystemMessage(string text) => $"|cffff0000[System Message]: {text}|r";

    /// <summary>LANG_GLOBAL_NOTIFY (100; mangos.sql:3488): the prefix of every .notify.</summary>
    public const string GlobalNotifyPrefix = "Global notify: ";

    /// <summary>LANG_GM_ON (332; mangos.sql:3698).</summary>
    public const string GmOn = "GM mode is ON";

    /// <summary>LANG_GM_OFF (333; mangos.sql:3699).</summary>
    public const string GmOff = "GM mode is OFF";

    /// <summary>LANG_GM_CHAT_ON (334; mangos.sql:3700).</summary>
    public const string GmChatOn = "GM Chat Badge is ON";

    /// <summary>LANG_GM_CHAT_OFF (335; mangos.sql:3701).</summary>
    public const string GmChatOff = "GM Chat Badge is OFF";

    /// <summary>LANG_COMMAND_KICKSELF (281; mangos.sql:3649).</summary>
    public const string CommandKickSelf = "You can't kick self, logout instead";

    /// <summary>LANG_COMMAND_KICKMESSAGE (282; mangos.sql:3650): "Player %s kicked.".</summary>
    public static string CommandKickMessage(string link) => $"Player {link} kicked.";

    // The movement cheats. The base mangos_string rows are not in the reference tree: 351-352 are vmangos
    // sql/old_migrations/20190711162650_world.sql:15-16, 611-614 sql/migrations/20240611051049_world.sql:13-16 and
    // 615-618 sql/migrations/20240215110536_world.sql:38-41.

    /// <summary>LANG_YOU_SET_WALLCLIMB (351): "You set wallclimb mode to %s for %s.".</summary>
    public static string YouSetWallClimb(string state, string link) => $"You set wallclimb mode to {state} for {link}.";

    /// <summary>LANG_YOUR_WALLCLIMB_SET (352): "Your wallclimb mode has been turned %s by %s.".</summary>
    public static string YourWallClimbSet(string state, string link) => $"Your wallclimb mode has been turned {state} by {link}.";

    /// <summary>LANG_YOU_SET_FIXED_Z (611): "You set fixed z mode to %s for %s.".</summary>
    public static string YouSetFixedZ(string state, string link) => $"You set fixed z mode to {state} for {link}.";

    /// <summary>LANG_YOUR_FIXED_Z_SET (612): "Your fixed z mode has been turned %s by %s.".</summary>
    public static string YourFixedZSet(string state, string link) => $"Your fixed z mode has been turned {state} by {link}.";

    /// <summary>LANG_CHEAT_FIXED_Z_ON (613).</summary>
    public const string CheatFixedZOn = "FIXED Z cheat is ON";

    /// <summary>LANG_CHEAT_FIXED_Z_OFF (614).</summary>
    public const string CheatFixedZOff = "FIXED Z cheat is OFF";

    /// <summary>LANG_YOU_SET_FLY (615): "You set fly mode to %s for %s.".</summary>
    public static string YouSetFly(string state, string link) => $"You set fly mode to {state} for {link}.";

    /// <summary>LANG_YOUR_FLY_SET (616): "Your fly mode has been turned %s by %s.".</summary>
    public static string YourFlySet(string state, string link) => $"Your fly mode has been turned {state} by {link}.";

    /// <summary>LANG_CHEAT_FLY_ON (617).</summary>
    public const string CheatFlyOn = "FLY MODE cheat is ON";

    /// <summary>LANG_CHEAT_FLY_OFF (618).</summary>
    public const string CheatFlyOff = "FLY MODE cheat is OFF";

    /// <summary>The literal vmangos sends the target after <c>.cheat fly on</c> (CharacterCommands.cpp:116); no LANG id.</summary>
    public const string FlyWarning = "WARNING: Do not jump or flying mode will be removed.";
}

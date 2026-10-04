using System.Globalization;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;

namespace ArcaneCore.World.Gm.Audit;

/// <summary>
/// The reply texts of the GM audit lane. Where a text has a reference-core original it is cited by its mangos-zero
/// <c>Language.h</c> id (mangosserver/server/src/game/Tools/Language.h, evidence only); ArcaneCore words everything
/// the cores do not have, and says so on the member. Durations use <see cref="GmDuration.SecsToTimeString"/>.
/// </summary>
public static class GmAuditStrings
{
    /// <summary>The reason shown when a mute was given none.</summary>
    public const string NoReason = "No reason given";

    /// <summary>"3 Days 2 Hours 5 Minutes" (the vmangos time string without its trailing space and period).</summary>
    public static string Span(long seconds) => GmDuration.SecsToTimeString(Math.Max(0, seconds)).TrimEnd(' ', '.');

    /// <summary>"yyyy-MM-dd HH:mm:ss" in UTC for a unix time.</summary>
    public static string Stamp(long unix)
        => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    // ---- mute / unmute ----

    /// <summary>LANG_YOUR_CHAT_DISABLED (300): mangos-zero says "for %u minutes"; the by/reason tail is ArcaneCore's (acore 300 has it).</summary>
    public static string YourChatDisabled(string duration, string by, string reason) => $"Your chat has been disabled for {duration}. By: {by}, Reason: {reason}.";

    /// <summary>LANG_YOU_DISABLE_CHAT (301): "You have disabled %s's chat for %u minutes."</summary>
    public static string YouDisableChat(string link, string duration) => $"You have disabled {link}'s chat for {duration}.";

    /// <summary>LANG_CHAT_ALREADY_ENABLED (302).</summary>
    public const string ChatAlreadyEnabled = "Player's chat is already enabled.";

    /// <summary>LANG_YOUR_CHAT_ENABLED (303).</summary>
    public const string YourChatEnabled = "Your chat has been enabled.";

    /// <summary>LANG_YOU_ENABLE_CHAT (304): "You have enabled %s's chat."</summary>
    public static string YouEnableChat(string link) => $"You have enabled {link}'s chat.";

    /// <summary>Not a core text: a mute set by higher staff is not replaced or lifted by lower staff.</summary>
    public const string MuteSetByHigher = "That mute was set by a staff member of a higher level; you cannot change it.";

    /// <summary>Not a core text: the range <c>.mute</c> accepts.</summary>
    public const string MuteDurationLimit = "The mute time must be between 1 second and 365 days.";

    // ---- pinfo ----

    /// <summary>vmangos LANG_PINFO_ACCOUNT (548) without the e-mail, last IP and latency fields: ArcaneCore does not show an account's address or e-mail in chat.</summary>
    public static string PinfoOnline(string link, uint guid, int accountId, AccountSecurity security)
        => string.Create(CultureInfo.InvariantCulture, $"Player {link} (online, guid: {guid}) Account id: {accountId} Security: {security}");

    /// <summary>The same for a character that is not logged in (its account's security is not loaded).</summary>
    public static string PinfoOffline(string link, uint guid, int accountId)
        => string.Create(CultureInfo.InvariantCulture, $"Player {link} (offline, guid: {guid}) Account id: {accountId}");

    public static string PinfoOfflineLevel(int level, uint zone) => string.Create(CultureInfo.InvariantCulture, $"Level: {level} Zone: {zone}");

    public static string PinfoLevel(int level, string played, uint money)
        => string.Create(CultureInfo.InvariantCulture, $"Level: {level} Played time: {played} Money: {money / 10000}g {money / 100 % 100}s {money % 100}c");

    public static string PinfoPosition(uint map, uint zone, float x, float y, float z)
        => string.Create(CultureInfo.InvariantCulture, $"Map: {map} Zone: {zone} X: {x:F2} Y: {y:F2} Z: {z:F2}");

    public static string PinfoState(bool? gmMode, string mute, int? ticketId)
        => string.Create(CultureInfo.InvariantCulture, $"{(gmMode is { } on ? $"GM mode: {(on ? "on" : "off")} " : string.Empty)}Chat: {mute} Ticket: {(ticketId is { } id ? "#" + id : "none")}");

    public static string MuteState(AccountMuteRecord? mute, long now)
        => mute is null ? "enabled" : $"muted for {Span(mute.MutedUntil - now)} by {mute.MutedBy} ({mute.Reason})";

    // ---- tickets ----

    /// <summary>LANG_COMMAND_TICKETNEW (289), sent to online staff.</summary>
    public static string TicketNew(string link, int id) => string.Create(CultureInfo.InvariantCulture, $"New ticket from {link} (ID {id})");

    /// <summary>LANG_COMMAND_TICKETUPDATED (1440).</summary>
    public static string TicketUpdated(string link, int id) => string.Create(CultureInfo.InvariantCulture, $"Player {link} has updated his ticket (ID {id}).");

    /// <summary>LANG_COMMAND_TICKETNOTEXIST (293).</summary>
    public static string TicketNotExist(int id) => string.Create(CultureInfo.InvariantCulture, $"Ticket {id} doesn't exist");

    /// <summary>LANG_COMMAND_TICKETDEL (296).</summary>
    public const string TicketDeleted = "Ticket deleted.";

    /// <summary>LANG_COMMAND_TICKETCLOSED_NAME (1510).</summary>
    public static string TicketClosedBy(int id, string link, string gm) => string.Create(CultureInfo.InvariantCulture, $"Ticket {id} from {link} has been closed by <GM>{gm}");

    /// <summary>LANG_COMMAND_TICKETVIEW (290): "Ticket of %s (Last updated: %s): %s" (ArcaneCore adds the id).</summary>
    public static string TicketView(int id, string link, string updated, string text)
        => string.Create(CultureInfo.InvariantCulture, $"Ticket {id} of {link} (Last updated: {updated}): {text}");

    /// <summary>LANG_COMMAND_TICKETRESPONSE (373).</summary>
    public static string TicketResponse(string response) => $"Response: {response}";

    /// <summary>LANG_COMMAND_TICKET_BRIEF_INFO (1514): "ID %u from %s (%s), changed %s".</summary>
    public static string TicketBrief(int id, string link, bool online, string age)
        => string.Create(CultureInfo.InvariantCulture, $"ID {id} from {link} ({(online ? "online" : "offline")}), changed {age} ago");

    /// <summary>LANG_COMMAND_TICKETCOUNT (288) reduced to the count; the show-new-tickets switch does not exist here.</summary>
    public static string TicketCount(int count) => string.Create(CultureInfo.InvariantCulture, $"Open tickets: {count}");

    public const string NoTickets = "There are no open tickets.";

    public static string TicketsOmitted(int count) => string.Create(CultureInfo.InvariantCulture, $"... and {count} more (the list is capped).");

    /// <summary>Not a core text: what a player is told when staff close their ticket.</summary>
    public static string YourTicketClosed(string gm) => $"Your ticket has been closed by <GM>{gm}.";

    /// <summary>Not a core text: what a player is told when staff delete their ticket.</summary>
    public const string YourTicketDeleted = "Your ticket has been deleted by staff.";

    /// <summary>Not a core text: a staff answer delivered to the ticket's owner.</summary>
    public static string TicketAnswer(string gm, string text) => $"<GM>{gm} answers your ticket: {text}";

    /// <summary>Not a core text: the answer was stored, but its owner is offline and is not told later.</summary>
    public static string ResponseSavedOffline(string link) => $"Response saved. {link} is offline and was not told.";

    /// <summary>Not a core text: the answer reached its owner.</summary>
    public static string ResponseSent(string link) => $"Response sent to {link}.";

    // ---- gm list / ingame / announce ----

    /// <summary>LANG_GMS_ON_SRV (16).</summary>
    public const string GmsOnServer = "There are the following active GMs on this server:";

    /// <summary>LANG_GMS_NOT_LOGGED (17).</summary>
    public const string GmsNotLogged = "There are no GMs currently logged in on this server.";

    /// <summary>Not a core text: header of <c>.gm list</c>, which lists every online staff account whatever its GM mode.</summary>
    public const string StaffOnline = "Staff online:";

    public const string NoStaffOnline = "No staff are online.";

    public static string GmIngameLine(string link, bool acceptsWhispers) => $"{link} - {(acceptsWhispers ? "accepts whispers" : "does not accept whispers")}";

    public static string StaffLine(string link, AccountSecurity security, bool gmMode) => $"{link} - {security}{(gmMode ? " (GM mode on)" : string.Empty)}";

    /// <summary>Not a core text: the line a staff channel announcement is shown as.</summary>
    public static string GmAnnouncement(string from, string text) => $"|cff00ccff[GM] {from}:|r {text}";

    public const string GmNotifyPrefix = "GM notify: ";

    // ---- .arcane ----

    public const string NoAuditLines = "No audited commands.";

    public const string AuditTailOff = "The audit tail is off (World:GmCommands:AuditTailSize is 0).";

    public const string NoMutes = "No chat mutes are in force.";

    public const string BanStoreUnavailable = "The ban database is unavailable; see the server log.";

    public const string BanCheckSyntax = "Syntax: .arcane bancheck account|character|ip $value\nShow whether an account (by name), the account of a character, or an IP address is blocked from logging in right now.";
}

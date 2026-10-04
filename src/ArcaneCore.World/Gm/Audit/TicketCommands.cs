using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Audit;

/// <summary>
/// <c>.ticket</c> for staff: list, onlinelist, show, respond, close and delete the GM tickets players file from the
/// in-game help window (<see cref="GmTicketHandlers"/>). ArcaneCore's levels: everything but delete needs GameMaster
/// (mangos-zero 2, acore 2), delete needs Administrator (mangos-zero 3, acore 3). Only OPEN tickets are listed and
/// shown; a closed ticket stays in the database as history and is not loaded back.
/// <c>respond</c> answers and leaves the ticket open; <c>close</c> ends it (optionally with a final answer). An answer
/// reaches the owner only while they are online; there is no mail system to carry it (mangos-zero mails it, :1519-1521).
/// </summary>
public sealed class TicketCommands : ICommandGroup
{
    /// <summary>The most tickets one listing prints.</summary>
    public const int MaxListed = 50;

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("ticket", AccountSecurity.GameMaster, "Syntax: .ticket [$subcommand]\nWithout a subcommand, show how many tickets are open.", Count, Children:
        [
            new ChatCommand("list", AccountSecurity.GameMaster, "Syntax: .ticket list\nList the open tickets, oldest first (at most 50).", List),
            new ChatCommand("onlinelist", AccountSecurity.GameMaster, "Syntax: .ticket onlinelist\nList the open tickets of players who are online now.", OnlineList),
            new ChatCommand("show", AccountSecurity.GameMaster, "Syntax: .ticket show $id\nShow the text (and answer, if any) of an open ticket.", Show),
            new ChatCommand("respond", AccountSecurity.GameMaster, "Syntax: .ticket respond $id $text\nAnswer an open ticket; the player is told at once if online. The ticket stays open.", Respond),
            new ChatCommand("close", AccountSecurity.GameMaster, "Syntax: .ticket close $id [$text]\nClose an open ticket, with an optional final answer. The player is told if online.", Close),
            new ChatCommand("delete", AccountSecurity.Administrator, "Syntax: .ticket delete $id\nDelete an open ticket without trace (the row is removed).", Delete),
        ]),
    ];

    private static GmAuditFeature Audit(CommandContext context) => context.Session.Services.GetRequiredService<GmAuditFeature>();

    private static bool Count(CommandContext context, string text)
    {
        if (text.Length > 0)
        {
            return false;
        }

        context.Reply(GmAuditStrings.TicketCount(Audit(context).OpenTickets().Count));
        return true;
    }

    private static bool List(CommandContext context, string text) => text.Length == 0 && ListTickets(context, onlineOnly: false);

    private static bool OnlineList(CommandContext context, string text) => text.Length == 0 && ListTickets(context, onlineOnly: true);

    private static bool ListTickets(CommandContext context, bool onlineOnly)
    {
        GmAuditFeature audit = Audit(context);
        long now = audit.NowUnixSeconds;
        var rows = new List<(GmTicketRecord Ticket, string Name, bool Online)>();
        foreach (GmTicketRecord ticket in audit.OpenTickets())
        {
            (string name, bool online) = Owner(context, ticket.CharacterId);
            if (!onlineOnly || online)
            {
                rows.Add((ticket, name, online));
            }
        }

        if (rows.Count == 0)
        {
            context.Reply(GmAuditStrings.NoTickets);
            return true;
        }

        context.Reply(GmAuditStrings.TicketCount(rows.Count));
        foreach ((GmTicketRecord ticket, string name, bool online) in rows.Take(MaxListed))
        {
            context.Reply(GmAuditStrings.TicketBrief(ticket.Id, GmStrings.PlayerLink(name), online, GmAuditStrings.Span(now - ticket.UpdatedAt)));
        }

        if (rows.Count > MaxListed)
        {
            context.Reply(GmAuditStrings.TicketsOmitted(rows.Count - MaxListed));
        }

        return true;
    }

    private static bool Show(CommandContext context, string text)
    {
        if (!TryId(text, out int id, out _))
        {
            return false;
        }

        if (Audit(context).OpenTicket(id) is not { } ticket)
        {
            context.Reply(GmAuditStrings.TicketNotExist(id));
            return true;
        }

        (string name, _) = Owner(context, ticket.CharacterId);
        context.Reply(GmAuditStrings.TicketView(ticket.Id, GmStrings.PlayerLink(name), GmAuditStrings.Stamp(ticket.UpdatedAt), ticket.Text));
        if (ticket.Response.Length > 0)
        {
            context.Reply(GmAuditStrings.TicketResponse(ticket.Response));
        }

        return true;
    }

    private static bool Respond(CommandContext context, string text)
    {
        if (!TryId(text, out int id, out string rest) || rest.Length == 0)
        {
            return false;
        }

        string answer = AuditCommands.CleanReason(rest);
        if (Audit(context).RespondToTicket(id, answer) is not { } ticket)
        {
            context.Reply(GmAuditStrings.TicketNotExist(id));
            return true;
        }

        (string name, bool online) = Owner(context, ticket.CharacterId);
        string link = GmStrings.PlayerLink(name);
        if (online && context.World.FindOnlinePlayer(name) is { } owner)
        {
            owner.SendSystemMessage(GmAuditStrings.TicketAnswer(context.Player.Name, answer));
            context.Reply(GmAuditStrings.ResponseSent(link));
        }
        else
        {
            context.Reply(GmAuditStrings.ResponseSavedOffline(link));
        }

        return true;
    }

    private static bool Close(CommandContext context, string text)
    {
        if (!TryId(text, out int id, out string rest))
        {
            return false;
        }

        GmTicketRecord? ticket = Audit(context).CloseTicket(id, context.Player.Name, rest.Length == 0 ? null : AuditCommands.CleanReason(rest));
        if (ticket is null)
        {
            context.Reply(GmAuditStrings.TicketNotExist(id));
            return true;
        }

        (string name, bool online) = Owner(context, ticket.CharacterId);
        if (online && context.World.FindOnlinePlayer(name) is { } owner)
        {
            owner.SendSystemMessage(GmAuditStrings.YourTicketClosed(context.Player.Name));
            if (rest.Length > 0)
            {
                owner.SendSystemMessage(GmAuditStrings.TicketAnswer(context.Player.Name, ticket.Response));
            }

            owner.Session.Send(WorldOpcode.SmsgGmticketGetticket, MiscPackets.BuildNoGmTicket());
        }

        context.Reply(GmAuditStrings.TicketClosedBy(ticket.Id, GmStrings.PlayerLink(name), context.Player.Name));
        return true;
    }

    private static bool Delete(CommandContext context, string text)
    {
        if (!TryId(text, out int id, out string rest) || rest.Length > 0)
        {
            return false;
        }

        GmAuditFeature audit = Audit(context);
        GmTicketRecord? ticket = audit.OpenTicket(id);
        if (ticket is null || !audit.DeleteTicket(id))
        {
            context.Reply(GmAuditStrings.TicketNotExist(id));
            return true;
        }

        (string name, bool online) = Owner(context, ticket.CharacterId);
        if (online && context.World.FindOnlinePlayer(name) is { } owner)
        {
            owner.SendSystemMessage(GmAuditStrings.YourTicketDeleted);
            owner.Session.Send(WorldOpcode.SmsgGmticketGetticket, MiscPackets.BuildNoGmTicket());
        }

        context.Reply(GmAuditStrings.TicketDeleted);
        return true;
    }

    /// <summary>The ticket owner's name (from the character directory; "#id" when it is gone) and whether they are online.</summary>
    private static (string Name, bool Online) Owner(CommandContext context, int characterId)
    {
        CharacterIdentity? identity = context.Session.Services.GetRequiredService<CharacterDirectory>().Find(characterId);
        if (identity is null)
        {
            return ("#" + characterId.ToString(System.Globalization.CultureInfo.InvariantCulture), false);
        }

        return (identity.Name, context.World.FindOnlinePlayer(identity.Name) is not null);
    }

    private static bool TryId(string text, out int id, out string rest)
    {
        var args = new CommandArgs(text);
        rest = string.Empty;
        id = 0;
        if (!args.ExtractUInt32(out uint value) || value is 0 or > int.MaxValue)
        {
            return false;
        }

        id = (int)value;
        rest = args.Rest.Trim();
        return true;
    }
}

using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Character;

/// <summary>
/// The <c>.send</c> root (vmangos sendCommandTable, Chat.cpp; the root is SEC_MODERATOR, :1291): <c>.send mail</c>, <c>.send money</c>,
/// <c>.send items</c> and <c>.send message</c> (MiscCommands.cpp HandleSendMailCommand, HandleSendMoneyCommand, HandleSendItemsCommand,
/// HandleSendMessageCommand). The receiver of a letter may be offline. <c>.send mass ...</c> is not provided.
/// <para>
/// Differences: "Mail sent to" is answered once the letter is committed, not when it is queued, and a letter that could not be stored
/// is reported; the receiver is the selected player when the first argument is the quoted subject.
/// </para>
/// </summary>
public sealed class GmSendCommands : ICommandGroup
{
    /// <summary>MAX_MAIL_ITEMS (vmangos Mail.h:51).</summary>
    public const int MaxMailItems = 1;

    /// <summary>The header vmangos sends after the text of <c>.send message</c>.</summary>
    public const string AdministratorHeader = "|cffff0000[Message from administrator]:|r";

    public const string MailUnavailable = "The mail system is unavailable; nothing was sent. See the server log.";

    /// <summary>LANG_MAIL_SENT (mangos_string 169).</summary>
    public static string MailSent(string name) => $"Mail sent to {GmStrings.PlayerLink(name)}";

    /// <summary>LANG_SENDMESSAGE (mangos_string 1102).</summary>
    public static string MessageSent(string name, string text) => $"Message sent to {GmStrings.PlayerLink(name)}: {text}";

    /// <summary>LANG_COMMAND_INVALID_ITEM_COUNT (mangos_string 52, spelt "Invaid" there).</summary>
    public static string InvalidItemCount(uint count, uint item)
        => string.Create(CultureInfo.InvariantCulture, $"Invalid item count ({count}) for item {item}");

    /// <summary>LANG_COMMAND_MAIL_ITEMS_LIMIT (mangos_string 53).</summary>
    public static string ItemsLimit => string.Create(CultureInfo.InvariantCulture, $"Mail can't have more {MaxMailItems} item stacks");

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("send", AccountSecurity.Moderator, "Syntax: .send $subcommand\nType .send to see the list of possible subcommands or .help send $subcommand to see info on subcommands.", Children:
        [
            new ChatCommand("items", AccountSecurity.Administrator,
                "Syntax: .send items #playername \"#subject\" \"#text\" itemid[:count]\nSend a letter with one item stack to a player, online or not.",
                Items, RetailLevel: 6),
            new ChatCommand("mail", AccountSecurity.Moderator,
                "Syntax: .send mail #playername \"#subject\" \"#text\"\nSend a letter to a player, online or not.",
                Mail, RetailLevel: 1),
            new ChatCommand("message", AccountSecurity.Administrator,
                "Syntax: .send message $playername $message\nShow a message from the administrator in the middle of the player's screen.",
                Message, RetailLevel: 6),
            new ChatCommand("money", AccountSecurity.Administrator,
                "Syntax: .send money #playername \"#subject\" \"#text\" #money\nSend a letter with money (in copper) to a player, online or not.",
                Money, RetailLevel: 6),
        ], RetailLevel: 1),
    ];

    private readonly record struct Receiver(int Id, int AccountId, string Name, bool Online);

    private static bool Mail(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!TryReceiver(context, args, out Receiver? receiver)) return true;
        if (receiver is null || !TryText(args, out string subject, out string body) || !args.IsEmpty) return false;
        Send(context, receiver.Value, subject, body, 0, null);
        return true;
    }

    private static bool Money(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!TryReceiver(context, args, out Receiver? receiver)) return true;
        if (receiver is null || !TryText(args, out string subject, out string body) || !args.ExtractUInt32(out uint money) || money == 0)
        {
            return false;
        }

        Send(context, receiver.Value, subject, body, money, null);
        return true;
    }

    private static bool Items(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!TryReceiver(context, args, out Receiver? receiver)) return true;
        if (receiver is null || !TryText(args, out string subject, out string body)) return false;

        var stacks = new List<ServerMailItem>();
        while (args.ExtractArg() is { } itemText)
        {
            string[] parts = itemText.Split(':');
            uint count = 1;
            if (parts.Length > 2
                || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint itemId)
                || (parts.Length == 2 && !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out count)))
            {
                return false;
            }

            if (itemId == 0 || context.Player.Inventory.Templates.Find(itemId) is not { } template)
            {
                context.Reply(GmStrings.ItemIdInvalid(itemId));
                return true;
            }

            if (count < 1 || (template.MaxCount > 0 && count > template.MaxCount))
            {
                context.Reply(InvalidItemCount(count, itemId));
                return true;
            }

            uint stack = Math.Max(1u, template.Stackable);
            while (count > 0)
            {
                uint take = Math.Min(count, stack);
                stacks.Add(new ServerMailItem(itemId, take));
                count -= take;
                if (stacks.Count > MaxMailItems)
                {
                    context.Reply(ItemsLimit);
                    return true;
                }
            }
        }

        Send(context, receiver.Value, subject, body, 0, stacks.Count == 0 ? null : stacks[0]);
        return true;
    }

    private static bool Message(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? raw = args.ExtractKeyFromLink("Hplayer", out _, out _);
        if (raw is null || args.IsEmpty)
        {
            return false;
        }

        if (!PlayerNames.TryNormalize(raw, out string name) || context.World.FindOnlinePlayer(name) is not { } target)
        {
            context.Reply(GmStrings.PlayerNotFound);
            return true;
        }

        if (!context.CanActOn(target))
        {
            return true;
        }

        // Two area-trigger messages, the text first (the client shows the newest on top).
        string message = args.Rest;
        target.Session.Send(WorldOpcode.SmsgAreaTriggerMessage, TeleportPackets.BuildAreaTriggerMessage(message));
        target.Session.Send(WorldOpcode.SmsgAreaTriggerMessage, TeleportPackets.BuildAreaTriggerMessage(AdministratorHeader));
        context.Reply(MessageSent(target.Name, message));
        return true;
    }

    // HandleSendMailHelper: "subject" "text", both quoted.
    private static bool TryText(CommandArgs args, out string subject, out string body)
    {
        string? quotedSubject = args.ExtractQuoted();
        string? quotedBody = quotedSubject is null ? null : args.ExtractQuoted();
        subject = quotedSubject ?? string.Empty;
        body = quotedBody ?? string.Empty;
        return quotedBody is not null;
    }

    /// <summary>
    /// vmangos ExtractPlayerTarget for a letter: a name (online or stored) or <c>|Hplayer:|</c> link, or the selected player when the
    /// next argument is quoted. False when the command was answered (not found, low security); true with null for a syntax error.
    /// </summary>
    private static bool TryReceiver(CommandContext context, CommandArgs args, out Receiver? receiver)
    {
        receiver = null;
        if (args.IsEmpty)
        {
            return true;
        }

        if (args.Rest[0] is '"' or '\'' or '[')
        {
            if (context.SelectedPlayerOrSelf() is not { } selected)
            {
                context.Reply(GmStrings.PlayerNotFound);
                return false;
            }

            if (!context.CanActOn(selected)) return false;
            receiver = new Receiver((int)selected.Guid.Low, 0, selected.Name, true);
            return true;
        }

        string? raw = args.ExtractKeyFromLink("Hplayer", out _, out _);
        if (raw is null || !PlayerNames.TryNormalize(raw, out string name))
        {
            context.Reply(GmStrings.PlayerNotFound);
            return false;
        }

        if (context.World.FindOnlinePlayer(name) is { } online)
        {
            if (!context.CanActOn(online)) return false;
            receiver = new Receiver((int)online.Guid.Low, 0, online.Name, true);
            return true;
        }

        if (context.Session.Services.GetService<CharacterDirectory>()?.FindByName(name) is not { } identity)
        {
            context.Reply(GmStrings.PlayerNotFound);
            return false;
        }

        receiver = new Receiver(identity.Id, identity.AccountId, identity.Name, false);
        return true;
    }

    private static void Send(CommandContext context, Receiver receiver, string subject, string body, uint money, ServerMailItem? item)
    {
        if (context.Session.Services.GetService<EconomyFeature>() is not { Enabled: true } economy)
        {
            context.Reply(MailUnavailable);
            return;
        }

        uint sender = context.IsConsole ? 0 : context.Player.Guid.Low;
        var request = new GmMailRequest(receiver.Id, sender, subject, body, money, item);
        void Go() => economy.SendGmMail(request, sent => context.Reply(sent ? MailSent(receiver.Name) : MailUnavailable));

        if (receiver.Online)
        {
            Go();
        }
        else
        {
            // The owner's security decides, as ExtractPlayerTarget -> HasLowerSecurity(NULL, guid) does through the guid's account.
            GmTargets.ActOnOffline(context, receiver.AccountId, Go);
        }
    }
}
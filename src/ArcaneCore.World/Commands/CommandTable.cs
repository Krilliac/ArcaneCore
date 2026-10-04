using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Commands;

/// <summary>Runs a command; returns false when the arguments are wrong (the syntax help is then shown).</summary>
public delegate bool CommandHandler(CommandContext context, string args);

/// <summary>
/// One chat command or command group (vmangos/cmangos ChatCommand): the minimum account
/// security that can see and use it, help text, and either a handler, sub-commands, or both
/// (the handler then serves arguments that name no sub-command, like ".gm on").
/// <para>
/// <paramref name="RetailLevel"/> is the vmangos account level (0-7, Common.h:136-146) the command
/// needs; when null the level of <paramref name="Security"/> under <see cref="GmOptions.SecurityMap"/>
/// is used, so a command declared only with an <see cref="AccountSecurity"/> behaves as before.
/// </para>
/// </summary>
public sealed record ChatCommand(
    string Name,
    AccountSecurity Security,
    string Help,
    CommandHandler? Handler = null,
    IReadOnlyList<ChatCommand>? Children = null,
    byte? RetailLevel = null)
{
    public IReadOnlyList<ChatCommand> SubCommands => Children ?? [];

    /// <summary>The retail account level needed to run this command.</summary>
    public int RequiredLevel(GmOptions options) => RetailLevel ?? options.LevelOf(Security);
}

/// <summary>The invoker of a chat command (world thread).</summary>
public sealed class CommandContext(WorldSession session, Player player, CommandTable commands)
{
    public WorldSession Session { get; } = session;

    public Player Player { get; } = player;

    public CommandTable Commands { get; } = commands;

    public WorldRuntime World => Session.World;

    public AccountSecurity Security => Session.Security;

    /// <summary>Send CHAT_MSG_SYSTEM lines to the invoker ('\n' separates lines).</summary>
    public void Reply(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            Session.Send(WorldOpcode.SmsgMessagechat, ChatPackets.BuildSystemMessage(line));
        }
    }

    /// <summary>
    /// The selected player, or the invoker when nothing is selected (vmangos
    /// ChatHandler::GetSelectedPlayer); null when the selection is not an online player.
    /// </summary>
    public Player? SelectedPlayerOrSelf()
        => Player.Selection.IsEmpty ? Player : World.FindOnlinePlayer(Player.Selection);

    /// <summary>
    /// Whether the invoker may act on <paramref name="target"/>: always on itself, otherwise the
    /// vmangos HasLowerSecurity rule (<see cref="GmSecurity.HasLowerSecurity"/>); <paramref name="strong"/>
    /// also refuses accounts of equal security (mute/unmute). <see cref="GmOptions.LowerSecurity"/>
    /// defaults to on, stricter than the vmangos default (see docs/integration/gm-commands.md).
    /// </summary>
    public bool CanActOn(Player target, bool strong = false)
    {
        if (ReferenceEquals(target, Player) || !GmSecurity.HasLowerSecurity(Security, target.Security, strong, Commands.Gm))
        {
            return true;
        }

        Reply(GmStrings.SecurityTooLow);
        return false;
    }
}

/// <summary>How a command line resolved (vmangos ChatCommandSearchResult, Chat.h).</summary>
public enum CommandLookupResult
{
    /// <summary>A command with a handler was found.</summary>
    Ok,

    /// <summary>A command group was found but no sub-command (or handler) matches the rest.</summary>
    UnknownSubcommand,

    /// <summary>No command matches the first word.</summary>
    Unknown,
}

/// <summary>The outcome of <see cref="CommandTable.Lookup"/>.</summary>
/// <param name="Result">How the line resolved.</param>
/// <param name="Command">The command found (the group for <see cref="CommandLookupResult.UnknownSubcommand"/>); null when unknown.</param>
/// <param name="Path">The command's words, e.g. "server shutdown".</param>
/// <param name="Rest">The text after the command words (the handler's arguments).</param>
/// <param name="Available">Whether the account may run the command (always true for the other results).</param>
/// <param name="Table">The command list the search ended in.</param>
public sealed record CommandLookup(
    CommandLookupResult Result, ChatCommand? Command, string Path, string Rest, bool Available, IReadOnlyList<ChatCommand> Table);

/// <summary>
/// The chat command tree. A message is a command when it starts with '.' or '!' followed by
/// something other than another '.'/'!' (vmangos ChatHandler::ParseCommands). A command word
/// resolves to the first table entry whose name starts with it (vmangos FindCommand /
/// hasStringAbbr, Chat.cpp:1566-1600,1767-1860; the roots are in retail table order, see
/// <see cref="RetailCommandOrder"/>), and only then is the account checked: a known command above
/// the account answers "This command is not available to you." (Chat.cpp:1884-1888). Both are
/// switchable (<see cref="GmOptions.ExactNameFirst"/>, <see cref="GmOptions.HideUnavailable"/>).
/// </summary>
public sealed class CommandTable(IReadOnlyList<ChatCommand> roots, GmOptions? gm = null)
{
    public IReadOnlyList<ChatCommand> Roots { get; } = roots;

    /// <summary>The <c>World:GmCommands</c> options (retail defaults when none were bound).</summary>
    public GmOptions Gm { get; } = gm ?? new GmOptions();

    /// <summary>
    /// Whether <paramref name="message"/> is command syntax, and the command text without its
    /// prefix. A single '.' or '!' and messages starting with ".." or "!!" are ordinary chat.
    /// </summary>
    public static bool TryGetCommandText(string message, out string commandText)
    {
        commandText = string.Empty;
        if (message.Length < 2 || message[0] is not ('.' or '!') || message[1] is '.' or '!')
        {
            return false;
        }

        commandText = message[1..];
        return true;
    }

    /// <summary>Resolve a command line (without its prefix) for an account of <paramref name="security"/>; nothing runs.</summary>
    public CommandLookup Lookup(string commandText, AccountSecurity security) => Search(Roots, commandText, security);

    private CommandLookup Search(IReadOnlyList<ChatCommand> table, string commandText, AccountSecurity security)
    {
        IReadOnlyList<ChatCommand> level = table;
        string rest = commandText.Trim();
        ChatCommand? current = null;
        string path = string.Empty;

        while (true)
        {
            (string word, string remainder) = SplitFirst(rest);
            ChatCommand? next = word.Length == 0 ? null : Find(level, word, security);
            if (next is null)
            {
                break;
            }

            current = next;
            path = path.Length == 0 ? next.Name : $"{path} {next.Name}";
            rest = remainder;
            level = next.SubCommands;
        }

        if (current is null)
        {
            return new CommandLookup(CommandLookupResult.Unknown, null, string.Empty, rest, true, table);
        }

        return current.Handler is null
            ? new CommandLookup(CommandLookupResult.UnknownSubcommand, current, path, rest, true, current.SubCommands)
            : new CommandLookup(CommandLookupResult.Ok, current, path, rest, IsAvailable(current, security), level);
    }

    /// <summary>Resolve and run a command line (without its prefix) for the invoker (vmangos ChatHandler::ExecuteCommand, Chat.cpp:1873-1970).</summary>
    public void Execute(CommandContext context, string commandText)
    {
        CommandLookup found = Lookup(commandText, context.Security);
        switch (found.Result)
        {
            case CommandLookupResult.Unknown:
                context.Reply(GmStrings.NoSuchCommand);
                return;
            case CommandLookupResult.UnknownSubcommand:
                context.Reply(GmStrings.NoSuchSubcommand);
                ShowHelpForCommand(context, found.Table, found.Rest);
                return;
        }

        ChatCommand command = found.Command!;
        if (!found.Available)
        {
            context.Reply(GmStrings.CommandUnavailable);
            return;
        }

        if (GmCommandLog.ShouldLog(command, Gm))
        {
            Player invoker = context.Player;
            string selection = invoker.Selection.IsEmpty ? "none" : invoker.Selection.ToString();
            ILogger log = context.Session.Services.GetService<ILoggerFactory>()?.CreateLogger(GmCommandLog.Category) ?? context.Session.Logger;
            string commandLine = found.Rest.Length == 0 ? found.Path : $"{found.Path} {found.Rest}";
            string line = GmCommandLog.Describe(commandLine, context.Session.AccountId, invoker.Name, invoker.MapId, invoker.X, invoker.Y, invoker.Z, selection);
            log.LogInformation("{Line}", line);
            context.Session.Services.GetService<GmAuditFeature>()?.RecordCommand(context.Session.AccountId, invoker.Name, commandLine, line);
        }

        if (!command.Handler!(context, found.Rest))
        {
            // Chat.cpp:1941-1950: the command's help text (or the generic syntax line), then its sub-commands.
            context.Reply(command.Help.Length > 0 ? command.Help : GmStrings.CmdSyntax);
            if (command.SubCommands.Count > 0)
            {
                ShowHelpForSubCommands(context, command.SubCommands, command.Name);
            }
        }
    }

    /// <summary>
    /// vmangos ShowHelpForCommand (Chat.cpp:2118-2163): the help of the command <paramref name="text"/>
    /// names in <paramref name="table"/> and its sub-commands; for an unknown word in a nested table
    /// (or no word), that table's commands. Returns false when nothing was shown.
    /// </summary>
    public bool ShowHelpForCommand(CommandContext context, IReadOnlyList<ChatCommand> table, string text)
    {
        CommandLookup found = Search(table, text, context.Security);
        ChatCommand? command = found.Command;
        IReadOnlyList<ChatCommand>? children;
        if (found.Result == CommandLookupResult.Unknown)
        {
            // No command list for an unknown first-level word.
            children = !ReferenceEquals(table, Roots) || text.Trim().Length == 0 ? table : null;
            command = null;
        }
        else
        {
            children = command!.SubCommands;
        }

        if (command is not null && command.Help.Length > 0)
        {
            context.Reply(command.Help);
        }

        if (children is not null && ShowHelpForSubCommands(context, children, command?.Name ?? string.Empty))
        {
            return true;
        }

        if (command is not null && command.Help.Length == 0)
        {
            context.Reply(GmStrings.NoHelpForCommand);
        }

        return command is not null || children is not null;
    }

    /// <summary>
    /// vmangos ShowHelpForSubCommands (Chat.cpp:2081-2116): the commands of <paramref name="table"/>
    /// the invoker may use (a group also counts when one of its sub-commands is usable), one indented
    /// line each, " ..." marking a group; "Commands available to you:" for the root table,
    /// "Command %s have subcommands:" otherwise. Returns false when there is nothing to list.
    /// </summary>
    public bool ShowHelpForSubCommands(CommandContext context, IReadOnlyList<ChatCommand> table, string commandName)
    {
        List<string> lines = [.. table.Where(c => IsListed(c, context.Security)).Select(c => "    " + c.Name + (c.SubCommands.Count > 0 ? " ..." : string.Empty))];
        if (lines.Count == 0)
        {
            return false;
        }

        context.Reply(ReferenceEquals(table, Roots) ? GmStrings.CommandsAvailable : GmStrings.SubcommandsList(commandName));
        foreach (string line in lines)
        {
            context.Reply(line);
        }

        return true;
    }

    /// <summary>The command at <paramref name="commandPath"/> (words separated by spaces), if visible to <paramref name="security"/>.</summary>
    public ChatCommand? Resolve(string commandPath, AccountSecurity security)
    {
        IReadOnlyList<ChatCommand> level = Roots;
        ChatCommand? current = null;
        foreach (string word in commandPath.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Find(level, word, security, filterByLevel: true);
            if (current is null)
            {
                return null;
            }

            level = current.SubCommands;
        }

        return current;
    }

    /// <summary>The command at <paramref name="commandPath"/> by exact names, whatever the account level (for inspection).</summary>
    public ChatCommand? Find(string commandPath)
    {
        IReadOnlyList<ChatCommand> level = Roots;
        ChatCommand? current = null;
        foreach (string word in commandPath.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            current = level.FirstOrDefault(c => c.Name.Equals(word, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                return null;
            }

            level = current.SubCommands;
        }

        return current;
    }

    /// <summary>Whether an account of <paramref name="security"/> reaches <paramref name="command"/> (retail level compare).</summary>
    public bool IsAvailable(ChatCommand command, AccountSecurity security)
        => Gm.LevelOf(security) >= command.RequiredLevel(Gm);

    // A group is listed when it, or any command below it, is available (vmangos lists a node by its
    // own level, and its group levels are the lowest of their commands).
    private bool IsListed(ChatCommand command, AccountSecurity security)
        => IsAvailable(command, security) || command.SubCommands.Any(c => IsListed(c, security));

    private ChatCommand? Find(IReadOnlyList<ChatCommand> level, string word, AccountSecurity security, bool filterByLevel = false)
    {
        bool filter = filterByLevel || Gm.HideUnavailable;
        ChatCommand? abbreviation = null;
        foreach (ChatCommand command in level)
        {
            if (filter && !IsAvailable(command, security))
            {
                continue;
            }

            if (!command.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Gm.ExactNameFirst)
            {
                return command;   // hasStringAbbr: the first entry in table order
            }

            if (command.Name.Length == word.Length)
            {
                return command;
            }

            abbreviation ??= command;
        }

        return abbreviation;
    }

    private static (string Word, string Remainder) SplitFirst(string text)
    {
        text = text.TrimStart();
        int space = text.IndexOf(' ');
        return space < 0 ? (text, string.Empty) : (text[..space], text[(space + 1)..].Trim());
    }
}

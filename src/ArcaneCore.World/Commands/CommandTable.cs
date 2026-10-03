using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
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

        Reply("Your security level is too low for that.");
        return false;
    }
}

/// <summary>
/// The chat command tree. A message is a command when it starts with '.' or '!' followed by
/// something other than another '.'/'!' (vmangos ChatHandler::ParseCommands). Command words
/// match by exact name first, then by abbreviation in table order (vmangos FindCommand /
/// hasStringAbbr); commands above the invoker's security behave as if they did not exist.
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

    /// <summary>Resolve and run a command line (without its prefix) for the invoker.</summary>
    public void Execute(CommandContext context, string commandText)
    {
        IReadOnlyList<ChatCommand> level = Roots;
        string rest = commandText.Trim();
        ChatCommand? current = null;
        string path = string.Empty;

        while (true)
        {
            (string word, string remainder) = SplitFirst(rest);
            ChatCommand? next = word.Length == 0 ? null : Find(level, word, context.Security);
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
            context.Reply("There is no such command.");
            return;
        }

        if (current.Handler is null)
        {
            context.Reply(rest.Length == 0
                ? $"Subcommands of .{path}: {ListNames(current.SubCommands, context.Security)}"
                : $"There is no such subcommand. Subcommands of .{path}: {ListNames(current.SubCommands, context.Security)}");
            return;
        }

        if (GmCommandLog.ShouldLog(current, Gm))
        {
            Player invoker = context.Player;
            string selection = invoker.Selection.IsEmpty ? "none" : invoker.Selection.ToString();
            ILogger log = context.Session.Services.GetService<ILoggerFactory>()?.CreateLogger(GmCommandLog.Category) ?? context.Session.Logger;
            log.LogInformation("{Line}", GmCommandLog.Describe(rest.Length == 0 ? path : $"{path} {rest}", context.Session.AccountId, invoker.Name, invoker.MapId, invoker.X, invoker.Y, invoker.Z, selection));
        }

        if (!current.Handler(context, rest))
        {
            context.Reply($"Incorrect syntax. .{path}: {current.Help}");
        }
    }

    /// <summary>The command at <paramref name="commandPath"/> (words separated by spaces), if visible to <paramref name="security"/>.</summary>
    public ChatCommand? Resolve(string commandPath, AccountSecurity security)
    {
        IReadOnlyList<ChatCommand> level = Roots;
        ChatCommand? current = null;
        foreach (string word in commandPath.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Find(level, word, security);
            if (current is null)
            {
                return null;
            }

            level = current.SubCommands;
        }

        return current;
    }

    /// <summary>Comma-separated names of the commands in <paramref name="commands"/> visible to <paramref name="security"/>.</summary>
    public string ListNames(IEnumerable<ChatCommand> commands, AccountSecurity security)
        => string.Join(", ", commands.Where(c => IsAvailable(c, security)).Select(c => c.Name));

    /// <summary>Whether an account of <paramref name="security"/> reaches <paramref name="command"/> (retail level compare).</summary>
    public bool IsAvailable(ChatCommand command, AccountSecurity security)
        => Gm.LevelOf(security) >= command.RequiredLevel(Gm);

    private ChatCommand? Find(IReadOnlyList<ChatCommand> level, string word, AccountSecurity security)
    {
        ChatCommand? abbreviation = null;
        foreach (ChatCommand command in level)
        {
            if (!IsAvailable(command, security))
            {
                continue;
            }

            if (command.Name.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                return command;
            }

            abbreviation ??= command.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase) ? command : null;
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

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Ops.ServerConsole;
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
/// <para>
/// <paramref name="IsWrite"/> marks a command that changes state, sends a client-visible packet or starts or stops a process
/// (debug-commands plan 1.5): its handler must call <c>DebugWrite.Record</c> once per changed target, and Record refuses (strict mode)
/// or complains (production) when the running command does not carry the flag.
/// </para>
/// </summary>
public sealed record ChatCommand(
    string Name,
    AccountSecurity Security,
    string Help,
    CommandHandler? Handler = null,
    IReadOnlyList<ChatCommand>? Children = null,
    byte? RetailLevel = null,
    bool IsWrite = false)
{
    public IReadOnlyList<ChatCommand> SubCommands => Children ?? [];

    /// <summary>The retail account level needed to run this command.</summary>
    public int RequiredLevel(GmOptions options) => RetailLevel ?? options.LevelOf(Security);
}

/// <summary>
/// The reply of a console command that needs the invoker's own character or selection, which the console does not have
/// (<see cref="GmStrings.ConsoleNeedsPlayer"/>). Thrown by <see cref="CommandContext.Player"/> in console mode and caught by
/// the console executor, so such a command fails closed instead of dereferencing a missing player.
/// </summary>
public sealed class ConsoleInvokerException() : InvalidOperationException(GmStrings.ConsoleNeedsPlayer);

/// <summary>
/// The invoker of a chat command (world thread): a player, or the server console
/// (<see cref="ForConsole"/>: Administrator authority, no player, replies to a text sink).
/// </summary>
public sealed class CommandContext
{
    /// <summary>The name the console runs commands under (audit lines, ban authors).</summary>
    public const string ConsoleName = "Console";

    private readonly Player? _player;
    private readonly Action<string>? _output;
    private readonly AccountSecurity? _remoteSecurity;
    private readonly string? _remoteName;
    private readonly object _settleGate = new();
    private int _pending = 1; // the handler itself until Complete
    private TaskCompletionSource? _settled;

    public CommandContext(WorldSession session, Player player, CommandTable commands)
    {
        Session = session;
        _player = player;
        Commands = commands;
    }

    private CommandContext(WorldSession session, CommandTable commands, Action<string> output)
    {
        Session = session;
        Commands = commands;
        _output = output;
    }

    /// <summary>
    /// A console invoker: <paramref name="consoleSession"/> is the socketless console session (account 0, Administrator),
    /// every reply line goes to <paramref name="output"/> (which must be thread-safe: async handlers reply later).
    /// </summary>
    public static CommandContext ForConsole(WorldSession consoleSession, CommandTable commands, Action<string> output)
    {
        ArgumentNullException.ThrowIfNull(consoleSession);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(output);
        return new CommandContext(consoleSession, commands, output);
    }

    /// <summary>
    /// A remote administration invoker (RA, docs/ops/remote-admin.md): console-like (no player, replies to
    /// <paramref name="output"/>, which must be thread-safe) but with the logged-in account's <paramref name="security"/>, audited as
    /// <paramref name="invokerName"/>. <paramref name="lifetime"/> is cancelled when the connection closes, so long-running commands
    /// (waits, scripts, event streams) stop with it.
    /// </summary>
    public static CommandContext ForRemote(WorldSession consoleSession, CommandTable commands, Action<string> output,
        AccountSecurity security, string invokerName, CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(consoleSession);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(invokerName);
        return new CommandContext(consoleSession, commands, output, security, invokerName, lifetime);
    }

    private CommandContext(WorldSession session, CommandTable commands, Action<string> output, AccountSecurity security,
        string invokerName, CancellationToken lifetime)
        : this(session, commands, output)
    {
        _remoteSecurity = security;
        _remoteName = invokerName;
        Lifetime = lifetime;
    }

    /// <summary>Whether the invoker is a remote administration (RA) connection (console-like, with its account's security).</summary>
    public bool IsRemote => _remoteName is not null;

    /// <summary>
    /// Cancelled when the invoker can no longer receive replies (an RA connection closed). <see cref="CancellationToken.None"/> for
    /// the console and for players (use <see cref="CanReplyLater"/>).
    /// </summary>
    public CancellationToken Lifetime { get; } = CancellationToken.None;

    /// <summary>
    /// Why the command failed, for machine callers (RA's <c>#done error &lt;reason&gt;</c>), or null when it succeeded. Set by the
    /// table (unknown command, security, syntax) and by handlers that report a machine-readable outcome (<see cref="Fail"/>).
    /// </summary>
    public string? Failure { get; private set; }

    /// <summary>The command the table is running for this context (set by <see cref="CommandTable.Execute"/> before the handler), or null outside a table run.</summary>
    public ChatCommand? Command { get; private set; }

    /// <summary>The words of <see cref="Command"/> as resolved ("alter speed"), or empty outside a table run.</summary>
    public string CommandPath { get; private set; } = string.Empty;

    /// <summary>Record the command that is about to run (the table, and tests that call a handler directly).</summary>
    internal void Bind(ChatCommand command, string path)
    {
        Command = command;
        CommandPath = path;
    }

    /// <summary>Mark the command failed (the first reason wins); the reply text is still the handler's.</summary>
    public void Fail(string reason) => Failure ??= string.IsNullOrWhiteSpace(reason) ? "failed" : reason.Trim();

    /// <summary>
    /// Hold the command open while asynchronous work runs (waits, scripts): the caller that tracks completion
    /// (<see cref="WhenSettled"/>, RA and the control pipe) waits until every hold is disposed before it reports the command done.
    /// Any thread.
    /// </summary>
    public IDisposable Defer()
    {
        lock (_settleGate) _pending++;
        return new Hold(this);
    }

    /// <summary>Completes once the handler returned (<see cref="Complete"/>) and every <see cref="Defer"/> hold was released.</summary>
    public Task WhenSettled
    {
        get
        {
            lock (_settleGate)
            {
                if (_pending == 0) return Task.CompletedTask;
                return (_settled ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }
    }

    /// <summary>The handler returned (called once by the executor that ran it).</summary>
    public void Complete() => Release();

    private void Release()
    {
        TaskCompletionSource? done = null;
        lock (_settleGate)
        {
            if (_pending == 0) return;
            if (--_pending == 0) done = _settled;
        }

        done?.TrySetResult();
    }

    private sealed class Hold(CommandContext owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release();
        }
    }

    public WorldSession Session { get; }

    /// <summary>Whether the invoker is the server console.</summary>
    public bool IsConsole => _output is not null;

    /// <summary>The invoking player; in console mode throws <see cref="ConsoleInvokerException"/> (the console has no character).</summary>
    public Player Player => _player ?? throw new ConsoleInvokerException();

    /// <summary>The invoker's name for audit lines and authorship: the player, or <see cref="ConsoleName"/>.</summary>
    public string InvokerName => _player?.Name ?? _remoteName ?? ConsoleName;

    /// <summary>Whether a reply sent later (after an asynchronous step) still has a recipient: the console always, a player while in the world.</summary>
    public bool CanReplyLater => _player is null || _player.IsInWorld;

    /// <summary>Whether <paramref name="unit"/> is the invoking player (always false for the console); never throws.</summary>
    public bool IsInvoker(Unit? unit) => unit is not null && ReferenceEquals(unit, _player);

    public CommandTable Commands { get; }

    public WorldRuntime World => Session.World;

    public AccountSecurity Security => IsConsole ? _remoteSecurity ?? AccountSecurity.Administrator : Session.Security;

    /// <summary>Send CHAT_MSG_SYSTEM lines to the invoker ('\n' separates lines); the console prints them.</summary>
    public void Reply(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            if (_output is not null)
            {
                _output(line);
            }
            else
            {
                Session.Send(WorldOpcode.SmsgMessagechat, ChatPackets.BuildSystemMessage(line));
            }
        }
    }

    /// <summary>
    /// The selected player, or the invoker when nothing is selected (vmangos
    /// ChatHandler::GetSelectedPlayer); null when the selection is not an online player. The console has neither
    /// (throws <see cref="ConsoleInvokerException"/>).
    /// </summary>
    public Player? SelectedPlayerOrSelf()
        => Player.Selection.IsEmpty ? Player : World.FindOnlinePlayer(Player.Selection);

    /// <summary>
    /// Whether the invoker may act on <paramref name="target"/>: always on itself, otherwise the
    /// vmangos HasLowerSecurity rule (<see cref="GmSecurity.HasLowerSecurity"/>); <paramref name="strong"/>
    /// also refuses accounts of equal security (mute/unmute). <see cref="GmOptions.LowerSecurity"/>
    /// defaults to on, stricter than the vmangos default (see docs/integration/gm-commands.md). The console
    /// outranks every account (vmangos SEC_CONSOLE) and may act on anyone.
    /// </summary>
    public bool CanActOn(Player target, bool strong = false)
    {
        if ((IsConsole && !IsRemote) || IsInvoker(target) || !GmSecurity.HasLowerSecurity(Security, target.Security, strong, Commands.Gm))
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
                context.Fail("unknown-command");
                context.Reply(GmStrings.NoSuchCommand);
                return;
            case CommandLookupResult.UnknownSubcommand:
                context.Fail("unknown-subcommand");
                context.Reply(GmStrings.NoSuchSubcommand);
                ShowHelpForCommand(context, found.Table, found.Rest, 1, found.Path);
                return;
        }

        ChatCommand command = found.Command!;
        if (!found.Available)
        {
            context.Fail("security");
            context.Reply(GmStrings.CommandUnavailable);
            return;
        }

        if (GmCommandLog.ShouldLog(command, Gm))
        {
            string commandLine = found.Rest.Length == 0 ? found.Path : $"{found.Path} {found.Rest}";
            ILogger log = context.Session.Services.GetService<ILoggerFactory>()?.CreateLogger(GmCommandLog.Category) ?? context.Session.Logger;
            string line;
            if (context.IsConsole)
            {
                commandLine = ConsoleCommandRedactor.Redact(commandLine);
                line = GmCommandLog.DescribeConsole(commandLine);
                if (context.IsRemote) line += " [RA " + context.InvokerName + "]";
                log.LogInformation("{Line}", line);
                context.Session.Services.GetService<GmAuditFeature>()?.RecordCommand(0, context.InvokerName, commandLine, line);
            }
            else
            {
                Player invoker = context.Player;
                string selection = invoker.Selection.IsEmpty ? "none" : invoker.Selection.ToString();
                line = GmCommandLog.Describe(commandLine, context.Session.AccountId, invoker.Name, invoker.MapId, invoker.X, invoker.Y, invoker.Z, selection);
                log.LogInformation("{Line}", line);
                context.Session.Services.GetService<GmAuditFeature>()?.RecordCommand(context.Session.AccountId, invoker.Name, commandLine, line);
            }
        }

        context.Bind(command, found.Path);
        if (!command.Handler!(context, found.Rest))
        {
            context.Fail("syntax");
            // Chat.cpp:1941-1950: the command's help text (or the generic syntax line), then its sub-commands.
            context.Reply(command.Help.Length > 0 ? command.Help : GmStrings.CmdSyntax);
            if (command.SubCommands.Count > 0)
            {
                ShowHelpForSubCommands(context, command.SubCommands, found.Path);
            }
        }
    }

    /// <summary>
    /// vmangos ShowHelpForCommand (Chat.cpp:2118-2163): the help of the command <paramref name="text"/>
    /// names in <paramref name="table"/> and its sub-commands; for an unknown word in a nested table
    /// (or no word), that table's commands. Returns false when nothing was shown.
    /// </summary>
    public bool ShowHelpForCommand(CommandContext context, IReadOnlyList<ChatCommand> table, string text, int page = 1, string pathPrefix = "")
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

        if (children is not null && ShowHelpForSubCommands(context, children, JoinPath(pathPrefix, found.Path), page))
        {
            return true;
        }

        if (command is not null && command.Help.Length == 0)
        {
            context.Reply(GmStrings.NoHelpForCommand);
        }

        return command is not null || children is not null;
    }

    private static string JoinPath(string prefix, string path)
        => prefix.Length == 0 ? path : path.Length == 0 ? prefix : prefix + " " + path;

    /// <summary>
    /// vmangos ShowHelpForSubCommands (Chat.cpp:2081-2116): the commands of <paramref name="table"/>
    /// the invoker may use (a group also counts when one of its sub-commands is usable), one indented
    /// line each, " ..." marking a group; "Commands available to you:" for the root table,
    /// "Command %s have subcommands:" otherwise. Returns false when there is nothing to list.
    /// <para>
    /// Players and GMs get <see cref="CommandListing"/>'s packed page <paramref name="page"/> instead of the vertical
    /// list (it no longer fits the client's chat scrollback); the console keeps the full vertical list.
    /// </para>
    /// </summary>
    public bool ShowHelpForSubCommands(CommandContext context, IReadOnlyList<ChatCommand> table, string commandPath, int page = 1)
    {
        if (!context.IsConsole)
        {
            if (!table.Any(c => IsListed(c, context.Security)))
            {
                return false;
            }

            context.Reply(string.Join('\n', ReferenceEquals(table, Roots)
                ? CommandListing.Overview(this, context.Security, page)
                : CommandListing.SubCommands(this, table, commandPath, context.Security, page)));
            return true;
        }

        List<string> lines = [.. table.Where(c => IsListed(c, context.Security)).Select(c => "    " + c.Name + (c.SubCommands.Count > 0 ? " ..." : string.Empty))];
        if (lines.Count == 0)
        {
            return false;
        }

        context.Reply(ReferenceEquals(table, Roots) ? GmStrings.CommandsAvailable : GmStrings.SubcommandsList(commandPath[(commandPath.LastIndexOf(' ') + 1)..]));
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
    internal bool IsListed(ChatCommand command, AccountSecurity security)
        => IsAvailable(command, security) || command.SubCommands.Any(c => IsListed(c, security));

    private ChatCommand? Find(IReadOnlyList<ChatCommand> level, string word, AccountSecurity security, bool filterByLevel = false)
    {
        bool filter = filterByLevel || Gm.HideUnavailable;
        ChatCommand? abbreviation = null;
        foreach (ChatCommand command in level)
        {
            // A parent can have a more restricted default handler than a child (.learn is level 5,
            // .learn all_lang is level 1 in vmangos Chat.cpp:492-507). Keep such a parent traversable.
            if (filter && !IsListed(command, security))
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

using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Ops.ServerConsole;

/// <summary>
/// Runs one console line through the GM command dispatcher as the console invoker (<see cref="CommandContext.ForConsole"/>:
/// Administrator authority, no player). World thread only: every GM handler assumes it. The mangosd equivalent is
/// <c>World::ProcessCliCommands</c> (cmangos mangos-classic World.cpp:2221) running a queued <c>CliCommandHolder</c>.
/// <para>
/// Ownership: holds the console session and the sink; the creator owns the DI scope the session lives in. Output lines go
/// to the sink prefixed <see cref="Prefix"/> and are never routed through the logger, so a one-time secret in a reply
/// never reaches a log file. The sink must be thread-safe (async handlers reply from other threads).
/// </para>
/// </summary>
public sealed class WorldConsoleExecutor
{
    /// <summary>The prefix of every output line.</summary>
    public const string Prefix = "[console] ";

    private readonly WorldSession _session;
    private readonly CommandTableSource _commands;
    private readonly TextWriter _output;
    private readonly ILogger _logger;
    private readonly string _prefix = Prefix;
    private readonly AccountSecurity? _remoteSecurity;
    private readonly string? _remoteName;
    private readonly CancellationToken _lifetime;

    internal WorldConsoleExecutor(WorldSession consoleSession, CommandTableSource commands, TextWriter output, ILogger logger)
    {
        _session = consoleSession;
        _commands = commands;
        _output = output;
        _logger = logger;
    }

    private WorldConsoleExecutor(WorldSession consoleSession, CommandTableSource commands, TextWriter output, ILogger logger,
        AccountSecurity security, string invokerName, CancellationToken lifetime)
        : this(consoleSession, commands, output, logger)
    {
        _prefix = string.Empty;
        _remoteSecurity = security;
        _remoteName = invokerName;
        _lifetime = lifetime;
    }

    /// <summary>
    /// An executor for a remote administration connection (RA, docs/ops/remote-admin.md): like <see cref="Create"/>, but every
    /// command runs with <paramref name="security"/> (the logged-in account's level), is audited as <paramref name="invokerName"/>,
    /// prints its lines without the console prefix and sees <paramref name="lifetime"/> (cancelled when the connection closes).
    /// </summary>
    internal static WorldConsoleExecutor CreateRemote(
        IServiceProvider scopeServices, OpcodeTable opcodes, WorldRuntime world, SessionRegistry registry,
        WorldSessionOptions sessionOptions, ILogger logger, TextWriter output, AccountSecurity security, string invokerName,
        CancellationToken lifetime)
    {
        WorldSession session = WorldSession.CreateConsole(scopeServices, opcodes, world, registry, sessionOptions, logger);
        return new WorldConsoleExecutor(session, scopeServices.GetRequiredService<CommandTableSource>(), output, logger,
            security, invokerName, lifetime);
    }

    /// <summary>
    /// Build an executor over <paramref name="scopeServices"/> (the console-owned DI scope: the GM handlers resolve their
    /// stores from it). <paramref name="output"/> must be a synchronized writer.
    /// </summary>
    internal static WorldConsoleExecutor Create(
        IServiceProvider scopeServices, OpcodeTable opcodes, WorldRuntime world, SessionRegistry registry,
        WorldSessionOptions sessionOptions, ILogger logger, TextWriter output)
    {
        WorldSession session = WorldSession.CreateConsole(scopeServices, opcodes, world, registry, sessionOptions, logger);
        return new WorldConsoleExecutor(session, scopeServices.GetRequiredService<CommandTableSource>(), output, logger);
    }

    /// <summary>Print one reply line.</summary>
    public void WriteLine(string line) => _output.WriteLine(_prefix + line);

    /// <summary>
    /// Rewrite the console built-ins (<c>quit</c>, <c>exit</c>, <c>shutdown [n|cancel]</c>) to the <c>.server shutdown</c> command, so
    /// they run through the normal dispatcher with its logging and security. Other lines pass unchanged (one leading '.' or '!'
    /// is dropped: the prefix is optional at the console).
    /// </summary>
    internal static string Normalize(string line)
    {
        string text = line.Trim();
        if (text.Length > 0 && text[0] is '.' or '!')
        {
            text = text[1..].TrimStart();
        }

        int space = text.IndexOf(' ');
        string word = space < 0 ? text : text[..space];
        string rest = space < 0 ? string.Empty : text[(space + 1)..].Trim();
        if (rest.Length == 0 && (word.Equals("quit", StringComparison.OrdinalIgnoreCase) || word.Equals("exit", StringComparison.OrdinalIgnoreCase)))
        {
            return "server shutdown 0";
        }

        if (word.Equals("shutdown", StringComparison.OrdinalIgnoreCase))
        {
            return rest.Length == 0 ? "server shutdown 0" : "server shutdown " + rest;
        }

        return text;
    }

    /// <summary>Run <paramref name="line"/> as the console. World thread only; never throws (a failure prints a message).</summary>
    public void Execute(string line) => Run(line);

    /// <summary>
    /// Run <paramref name="line"/> like <see cref="Execute"/> and return its context: <see cref="CommandContext.Failure"/> says whether it
    /// failed and <see cref="CommandContext.WhenSettled"/> completes once its asynchronous work (a <c>.botctl wait</c>) ended. Null for
    /// an empty line. World thread only; never throws.
    /// </summary>
    public CommandContext? Run(string line)
    {
        Invariant.Assert(_session.World.IsWorldThread, "console commands run on the world thread");
        string text = Normalize(line);
        if (text.Length == 0)
        {
            return null;
        }

        CommandContext context = _remoteName is { } name
            ? CommandContext.ForRemote(_session, _commands.Current, WriteLine, _remoteSecurity ?? AccountSecurity.Player, name, _lifetime)
            : CommandContext.ForConsole(_session, _commands.Current, WriteLine);
        if (context.IsRemote && ConsoleOnlyCommands.IsConsoleOnly(context.Commands.Lookup(text, context.Security).Path))
        {
            context.Fail(CommandContextConsole.ConsoleOnlyFailure);
            WriteLine(CommandContextConsole.ConsoleOnlyText);
            context.Complete();
            return context;
        }

        try
        {
            context.Commands.Execute(context, text);
        }
        catch (ConsoleInvokerException)
        {
            context.Fail("needs-player");
            WriteLine(GmStrings.ConsoleNeedsPlayer);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("ops.console-executor", ex);
            context.Fail("exception");
            _logger.LogError(ex, "console command failed: {Command}", ArcaneCore.Kernel.Ops.ServerConsole.ConsoleCommandRedactor.Redact(text));
            WriteLine("The command failed; see the server log.");
        }
        finally
        {
            context.Complete();
        }

        return context;
    }
}

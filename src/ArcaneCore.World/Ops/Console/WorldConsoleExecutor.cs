using ArcaneCore.Game.Maps;
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

    internal WorldConsoleExecutor(WorldSession consoleSession, CommandTableSource commands, TextWriter output, ILogger logger)
    {
        _session = consoleSession;
        _commands = commands;
        _output = output;
        _logger = logger;
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
    public void WriteLine(string line) => _output.WriteLine(Prefix + line);

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
    public void Execute(string line)
    {
        Invariant.Assert(_session.World.IsWorldThread, "console commands run on the world thread");
        string text = Normalize(line);
        if (text.Length == 0)
        {
            return;
        }

        var context = CommandContext.ForConsole(_session, _commands.Current, WriteLine);
        try
        {
            context.Commands.Execute(context, text);
        }
        catch (ConsoleInvokerException)
        {
            WriteLine(GmStrings.ConsoleNeedsPlayer);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "console command failed: {Command}", ArcaneCore.Kernel.Ops.ServerConsole.ConsoleCommandRedactor.Redact(text));
            WriteLine("The command failed; see the server log.");
        }
    }
}

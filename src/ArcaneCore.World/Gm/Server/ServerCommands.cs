using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Server;

/// <summary>
/// <c>.server shutdown|restart|idleshutdown|idlerestart [cancel | #delay [#exitcode]]</c> and
/// <c>.server set motd</c> (vmangos ServerCommands.cpp:322-323,370-495; all SEC_ADMINISTRATOR,
/// Chat.cpp:944-998), added under the <c>.server</c> root of the built-in commands.
/// <c>.server info</c> and <c>.server motd</c> are in <see cref="BuiltinCommands"/>.
/// <c>.server plimit</c>, <c>corpses</c>, <c>resetallraids</c>, <c>log</c> and <c>exit</c> are not
/// provided (no player limit, corpse expiry or raid-reset scheduler to drive; log and exit are
/// console-only in vmangos).
/// </summary>
public sealed class ServerCommands : ICommandExtension
{
    private const string DelaySyntax = "Syntax: .server {0} #delay [#exit_code]\nSchedule the server {1} after #delay seconds (0 = at once); #exit_code is 0-125.";

    public string Path => "server";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        Group("shutdown", "shuts down", ShutdownMask.None, ShutdownScheduler.ShutdownExitCode),
        Group("restart", "restarts", ShutdownMask.Restart, ShutdownScheduler.RestartExitCode),
        Group("idleshutdown", "shuts down once nobody is online", ShutdownMask.Idle, ShutdownScheduler.ShutdownExitCode),
        Group("idlerestart", "restarts once nobody is online", ShutdownMask.Restart | ShutdownMask.Idle, ShutdownScheduler.RestartExitCode),
        new ChatCommand("set", AccountSecurity.Administrator, "Syntax: .server set $subcommand", Children:
        [
            new ChatCommand("motd", AccountSecurity.Administrator, "Syntax: .server set motd $MOTD\nSet the server message of the day.", SetMotd, RetailLevel: 6),
        ], RetailLevel: 6),
    ];

    private static ChatCommand Group(string name, string what, ShutdownMask mask, byte defaultExitCode)
        => new(name, AccountSecurity.Administrator, string.Format(DelaySyntax, name, what),
            (context, args) => Schedule(context, args, mask, defaultExitCode),
            [new ChatCommand("cancel", AccountSecurity.Administrator, $"Syntax: .server {name} cancel\nCancel the scheduled {name}.", Cancel, RetailLevel: 6)],
            RetailLevel: 6);

    private static bool Schedule(CommandContext context, string text, ShutdownMask mask, byte defaultExitCode)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint delay) || !args.ExtractOptUInt32(out uint exitCode, defaultExitCode) || exitCode > 125)
        {
            return false;   // 126-255 are shell-reserved (ServerCommands.cpp:412-415)
        }

        context.Session.Services.GetRequiredService<ShutdownFeature>().Request(delay, mask, (byte)exitCode);
        return true;
    }

    private static bool Cancel(CommandContext context, string args)
    {
        context.Session.Services.GetRequiredService<ShutdownFeature>().Cancel();
        return true;
    }

    private static bool SetMotd(CommandContext context, string args)
    {
        context.World.Options.Motd = args;
        context.Reply(GmStrings.MotdChanged(args));
        return true;
    }
}

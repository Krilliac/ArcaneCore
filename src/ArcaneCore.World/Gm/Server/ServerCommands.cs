using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Server;

/// <summary>
/// <c>.server set motd</c> (vmangos ServerCommands.cpp:322-323; SEC_ADMINISTRATOR, Chat.cpp:944-998), added under the
/// <c>.server</c> root of the built-in commands. <c>.server shutdown|restart|idleshutdown|idlerestart</c> are the ops
/// lane's <c>Ops.Lifecycle.ServerLifecycleCommands</c> (wave-2 integration: one shutdown implementation, which also owns
/// the process exit code and the supervisor contract).
/// <c>.server info</c> and <c>.server motd</c> are in <see cref="BuiltinCommands"/>.
/// <c>.server plimit</c>, <c>corpses</c>, <c>resetallraids</c>, <c>log</c> and <c>exit</c> are not
/// provided (no player limit, corpse expiry or raid-reset scheduler to drive; log and exit are
/// console-only in vmangos).
/// </summary>
public sealed class ServerCommands : ICommandExtension
{
    public string Path => "server";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("set", AccountSecurity.Administrator, "Syntax: .server set $subcommand", Children:
        [
            new ChatCommand("motd", AccountSecurity.Administrator, "Syntax: .server set motd $MOTD\nSet the server message of the day.", SetMotd, RetailLevel: 6),
        ], RetailLevel: 6),
    ];

    private static bool SetMotd(CommandContext context, string args)
    {
        context.World.Options.Motd = args;
        context.Reply(GmStrings.MotdChanged(args));
        return true;
    }
}

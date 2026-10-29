using System.Globalization;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.World.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Ops.Lifecycle;

/// <summary>
/// <c>.server shutdown|restart|idleshutdown|idlerestart [cancel | delay [exitcode]]</c>; shutdown and restart also take a trailing realm id
/// (<c>delay exitcode realmid</c>) that stops or restarts another world server through the cluster host supervisor. Names,
/// arguments and the exit-code range follow vmangos ServerCommands.cpp:409-502; every command is
/// SEC_ADMINISTRATOR there (Chat.cpp:946-997), which is ArcaneCore's highest level. vmangos'
/// console-only <c>server exit</c> has no in-game equivalent and is not offered.
/// These children are appended to the builtin <c>server</c> group
/// (BuiltinCommands: <c>.. ServerLifecycleCommands.Children</c>).
/// </summary>
public static class ServerLifecycleCommands
{
    public static IReadOnlyList<ChatCommand> Children { get; } =
    [
        Command("shutdown", "Syntax: .server shutdown [cancel | #delay [#exitcode [#realmid]]] — stop the server after #delay seconds. With #realmid, ask the cluster host supervisor to stop that world server instead (same host only; needs the supervisor and its realm ids, otherwise nothing is done).", ShutdownMask.None, ExitCodes.Success, allowRealm: true),
        Command("restart", "Syntax: .server restart [cancel | #delay [#exitcode [#realmid]]] — restart the server after #delay seconds (exit code 2 for a supervisor). With #realmid, ask the cluster host supervisor to restart that world server instead (same host only; needs the supervisor and its realm ids, otherwise nothing is done).", ShutdownMask.Restart, ExitCodes.Restart, allowRealm: true),
        Command("idleshutdown", "Syntax: .server idleshutdown [cancel | #delay [#exitcode]] — stop when no sessions remain.", ShutdownMask.Idle, ExitCodes.Success),
        Command("idlerestart", "Syntax: .server idlerestart [cancel | #delay [#exitcode]] — restart when no sessions remain.", ShutdownMask.Restart | ShutdownMask.Idle, ExitCodes.Restart),
    ];

    private static ChatCommand Command(string name, string help, ShutdownMask mask, int defaultExitCode, bool allowRealm = false) => new(
        name,
        AccountSecurity.Administrator,
        help,
        (context, args) => Run(context, args, mask, defaultExitCode, allowRealm),
        [new ChatCommand("cancel", AccountSecurity.Administrator, "Syntax: .server " + name + " cancel — abort the pending countdown.", Cancel)]);

    private static bool Cancel(CommandContext context, string args)
    {
        Feature(context).Cancel(context.InvokerName);
        return true;
    }

    // vmangos: ExtractUInt32(delay) then ExtractOptUInt32(exitcode, default); more than 125 is a usage error.
    private static bool Run(CommandContext context, string args, ShutdownMask mask, int defaultExitCode, bool allowRealm)
    {
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || parts.Length > (allowRealm ? 3 : 2) || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint delay))
        {
            return false;
        }

        int exitCode = defaultExitCode;
        if (parts.Length >= 2 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out exitCode))
        {
            return false;
        }

        if (exitCode > ExitCodes.MaxRequested)
        {
            return false;
        }

        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int realmId) || realmId < 1)
            {
                return false;
            }

            SupervisorProtocolChannel channel = context.Session.Services.GetService<SupervisorProtocolChannel>() ?? SupervisorProtocolChannel.Process;
            if (!channel.IsSupervised)
            {
                context.Reply($"This world server is not running under a cluster host supervisor, so it cannot reach realm {realmId}; nothing was done.");
                return true;
            }

            if (realmId != channel.OwnRealmId)
            {
                var request = new SupervisorLifecycleRequest(
                    channel.Token!, realmId, mask.HasFlag(ShutdownMask.Restart) ? SupervisorLifecycleAction.Restart : SupervisorLifecycleAction.Shutdown, delay, exitCode, context.InvokerName);
                channel.Send(SupervisorProtocol.FormatLifecycleRequest(request));
                context.Reply($"Asked the supervisor to {(mask.HasFlag(ShutdownMask.Restart) ? "restart" : "stop")} realm {realmId} in {delay} s (exit code {exitCode}); it reports back to the staff.");
                return true;
            }
        }

        Feature(context).Request(delay, mask, (byte)exitCode, context.InvokerName);
        return true;
    }

    private static ServerLifecycleFeature Feature(CommandContext context)
        => context.Session.Services.GetRequiredService<ServerLifecycleFeature>();
}

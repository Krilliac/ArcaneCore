using System.Globalization;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Ops.Lifecycle;

/// <summary>
/// <c>.server shutdown|restart|idleshutdown|idlerestart [cancel | delay [exitcode]]</c>. Names,
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
        Command("shutdown", "Syntax: .server shutdown [cancel | #delay [#exitcode]] — stop the server after #delay seconds.", ShutdownMask.None, ExitCodes.Success),
        Command("restart", "Syntax: .server restart [cancel | #delay [#exitcode]] — restart the server after #delay seconds (exit code 2 for a supervisor).", ShutdownMask.Restart, ExitCodes.Restart),
        Command("idleshutdown", "Syntax: .server idleshutdown [cancel | #delay [#exitcode]] — stop when no sessions remain.", ShutdownMask.Idle, ExitCodes.Success),
        Command("idlerestart", "Syntax: .server idlerestart [cancel | #delay [#exitcode]] — restart when no sessions remain.", ShutdownMask.Restart | ShutdownMask.Idle, ExitCodes.Restart),
    ];

    private static ChatCommand Command(string name, string help, ShutdownMask mask, int defaultExitCode) => new(
        name,
        AccountSecurity.Administrator,
        help,
        (context, args) => Run(context, args, mask, defaultExitCode),
        [new ChatCommand("cancel", AccountSecurity.Administrator, "Syntax: .server " + name + " cancel — abort the pending countdown.", Cancel)]);

    private static bool Cancel(CommandContext context, string args)
    {
        Feature(context).Cancel();
        return true;
    }

    // vmangos: ExtractUInt32(delay) then ExtractOptUInt32(exitcode, default); more than 125 is a usage error.
    private static bool Run(CommandContext context, string args, ShutdownMask mask, int defaultExitCode)
    {
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint delay))
        {
            return false;
        }

        int exitCode = defaultExitCode;
        if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out exitCode))
        {
            return false;
        }

        if (exitCode > ExitCodes.MaxRequested)
        {
            return false;
        }

        Feature(context).Request(delay, mask, (byte)exitCode);
        return true;
    }

    private static ServerLifecycleFeature Feature(CommandContext context)
        => context.Session.Services.GetRequiredService<ServerLifecycleFeature>();
}

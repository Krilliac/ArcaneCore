using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Commands;

/// <summary>
/// Logs an exception a fire-and-forget command handler caught (they answer the invoker with a message and would otherwise lose the
/// exception). The logger comes from the invoker's session services, so the line goes through the host's logging pipeline (console and
/// file) at Error with the full exception, and the fault is counted in the <see cref="FaultLedger"/> (<c>.server faults</c>). Runs only
/// when an exception actually happened; any thread.
/// </summary>
internal static class CommandFaultLogging
{
    internal const string Category = "ArcaneCore.World.Commands";

    /// <summary>Record <paramref name="error"/> at <paramref name="site"/> and log it with <paramref name="what"/> (a short name of the command).</summary>
    internal static void Log(CommandContext context, string site, Exception error, string what)
    {
        ILogger logger = context.Session.Services.GetService<ILoggerFactory>()?.CreateLogger(Category) ?? NullLogger.Instance;
        logger.LogFault(site, error, "Command {Command} failed", what);
    }
}

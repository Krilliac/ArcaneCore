using ArcaneCore.Kernel.Ops;
using Microsoft.Extensions.Hosting;

namespace ArcaneCore.Kernel.Configuration.Conf;

/// <summary>
/// The command-line and .conf steps both daemons run before they build anything, so the World and the Realm cannot drift apart:
/// answer <c>--help</c> and usage errors without starting, then register the daemon's .conf file as a configuration source.
/// Stateless; called once from the main thread of the process.
/// </summary>
public static class ConfStartup
{
    /// <summary>
    /// True when the command line is fully answered (help text, or a usage error message) and the process must exit with <paramref name="exitCode"/>
    /// without building the host. Help goes to <paramref name="standardOutput"/>, errors to <paramref name="standardError"/>; both exit with <see cref="ExitCodes.Usage"/>.
    /// </summary>
    public static bool TryAnswerEarly(DaemonCommand command, ConfigDaemon daemon, TextWriter standardOutput, TextWriter standardError, out int exitCode)
    {
        switch (command.Kind)
        {
            case DaemonCommandKind.Help:
                standardOutput.Write(DaemonCommandLine.Usage(daemon));
                exitCode = ExitCodes.Usage;
                return true;
            case DaemonCommandKind.UsageError:
                standardError.WriteLine(command.Message);
                standardError.WriteLine("Run with --help for the usage.");
                exitCode = ExitCodes.Usage;
                return true;
            default:
                exitCode = ExitCodes.Success;
                return false;
        }
    }

    /// <summary>
    /// Registers the daemon's .conf file on <paramref name="builder"/> (see <see cref="ConfFiles.Attach"/>). A malformed file or an unusable location
    /// is reported on <paramref name="standardError"/> and returns null with <see cref="ExitCodes.InvalidConfiguration"/>; nothing has been registered then.
    /// </summary>
    public static ConfAttachment? TryAttach(IHostApplicationBuilder builder, ConfigDaemon daemon, DaemonCommand command, TextWriter standardError, out int exitCode)
    {
        exitCode = ExitCodes.Success;
        try
        {
            return ConfFiles.Attach(builder, daemon, command.ConfigPath);
        }
        catch (ConfParseException ex)
        {
            foreach (string error in ex.Errors)
            {
                standardError.WriteLine(error);
            }
        }
        catch (ConfLocationException ex)
        {
            standardError.WriteLine(ex.Message);
        }

        exitCode = ExitCodes.InvalidConfiguration;
        return null;
    }
}

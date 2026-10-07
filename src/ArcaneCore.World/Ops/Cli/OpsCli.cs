using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Ops.Validation;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Ops.Cli;

/// <summary>
/// Operations verbs run instead of the daemon: <c>ArcaneCore.World check-config</c>. They run
/// before the host is built, so no listener binds and no schema is touched.
/// </summary>
public static class OpsCli
{
    public static IReadOnlyList<string> Verbs { get; } = ["check-config"];

    /// <summary>
    /// Run the verb named by the first argument, if it is one. A bare first word that is not a
    /// verb is a usage error (exit 64, never 2, which means restart); options (<c>--x</c>,
    /// <c>key=value</c>) are left to the host.
    /// </summary>
    public static bool TryRun(string[] args, IConfiguration configuration, TextWriter output, out int exitCode)
    {
        exitCode = ExitCodes.Success;
        if (args.Length == 0 || args[0].StartsWith('-') || args[0].StartsWith('/') || args[0].Contains('=', StringComparison.Ordinal))
        {
            return false;
        }

        if (args[0] == "check-config")
        {
            exitCode = CheckConfig(configuration, output);
            return true;
        }

        output.WriteLine($"Unknown command '{args[0]}'. Commands: {string.Join(", ", Verbs)}.");
        exitCode = ExitCodes.Usage;
        return true;
    }

    /// <summary>Validate the configuration; 78 when invalid, 0 otherwise.</summary>
    public static int CheckConfig(IConfiguration configuration, TextWriter output)
    {
        ConfigReport report = Validate(configuration);
        report.Write(output);
        return report.IsInvalid ? ExitCodes.InvalidConfiguration : ExitCodes.Success;
    }

    public static ConfigReport Validate(IConfiguration configuration)
        => ConfigReport.Run(configuration, [new WorldConfigChecks(), new DiagnosticsConfigChecks(), new LoggingConfigChecks(), new ResilienceConfigChecks(), new NetProtectionConfigChecks()]);
}

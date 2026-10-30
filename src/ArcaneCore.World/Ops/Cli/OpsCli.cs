using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Kernel.Ops;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Ops.Cli;

/// <summary>
/// Operations verbs run instead of the daemon: <c>ArcaneCore.World check-config</c> and <c>write-config</c>. They run
/// before the host is built, so no listener binds and no schema is touched.
/// </summary>
public static class OpsCli
{
    public static IReadOnlyList<string> Verbs { get; } = [DaemonCommandLine.CheckConfig, DaemonCommandLine.WriteConfig];

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

        if (args[0] == DaemonCommandLine.CheckConfig)
        {
            exitCode = CheckConfig(configuration, output);
            return true;
        }

        if (args[0] == DaemonCommandLine.WriteConfig)
        {
            DaemonCommand command = DaemonCommandLine.Parse(args, ConfigDaemon.World, ConfCatalog.Embedded);
            if (command.Kind != DaemonCommandKind.Verb)
            {
                output.WriteLine(command.Message);
                exitCode = ExitCodes.Usage;
                return true;
            }

            IConfigurationRoot root = configuration as IConfigurationRoot ?? new ConfigurationBuilder().AddConfiguration(configuration).Build();
            exitCode = WriteConfigVerb.Run(command, ConfigDaemon.World, ConfCatalog.Embedded, root, output, output);
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

    /// <summary>
    /// The assemblies whose <see cref="IConfigCheck"/> classes the world daemon runs: the Kernel's (every check under ArcaneCore.Kernel.Configuration.Validation),
    /// then the world's startup code set (this assembly, then the built-in packs such as Playerbots, <see cref="Features.WorldCodeAssemblies.Ordered"/>).
    /// </summary>
    public static IReadOnlyList<System.Reflection.Assembly> CheckAssemblies { get; } = [typeof(IConfigCheck).Assembly, .. Features.WorldCodeAssemblies.Ordered];

    /// <summary>
    /// Every check the world daemon runs: the .conf file check (it needs the catalog and the daemon, so it is supplied here) first, then
    /// every other <see cref="IConfigCheck"/> of <see cref="CheckAssemblies"/> found by reflection (<see cref="ConfigCheckDiscovery"/>).
    /// </summary>
    public static IReadOnlyList<IConfigCheck> CreateChecks()
        => [new ConfKeyCheck(ConfCatalog.Embedded, ConfigDaemon.World), .. ConfigCheckDiscovery.Create(CheckAssemblies)];

    public static ConfigReport Validate(IConfiguration configuration)
        => ConfigReport.Run(configuration, CreateChecks());
}

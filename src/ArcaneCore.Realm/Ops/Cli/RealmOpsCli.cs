using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Kernel.Ops;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.Realm.Ops.Cli;

/// <summary>
/// The realm daemon's operations verbs, run instead of the daemon before the host is built (nothing binds, no database is touched):
/// <c>check-config</c> and <c>write-config</c>. Stateless; called once from the main thread.
/// </summary>
public static class RealmOpsCli
{
    /// <summary>The assemblies whose <see cref="IConfigCheck"/> classes the realm runs: the Kernel's (every check under ArcaneCore.Kernel.Configuration.Validation) and this one's.</summary>
    public static IReadOnlyList<System.Reflection.Assembly> CheckAssemblies { get; } = [typeof(IConfigCheck).Assembly, typeof(RealmOpsCli).Assembly];

    /// <summary>The .conf file check (it needs the catalog and the daemon, so it is supplied here) first, then every other <see cref="IConfigCheck"/> of <see cref="CheckAssemblies"/> found by reflection.</summary>
    public static IReadOnlyList<IConfigCheck> CreateChecks()
        => [new ConfKeyCheck(ConfCatalog.Embedded, ConfigDaemon.Realm), .. ConfigCheckDiscovery.Create(CheckAssemblies)];

    /// <summary>Every problem of the realm's configuration, from every check <see cref="CreateChecks"/> returns.</summary>
    public static ConfigReport Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return ConfigReport.Run(configuration, CreateChecks());
    }

    /// <summary>Runs the verb of <paramref name="command"/>; the exit code is 78 when check-config finds an error, otherwise the verb's own code.</summary>
    public static int Run(DaemonCommand command, IConfigurationRoot configuration, TextWriter standardOutput, TextWriter standardError)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        if (command.Verb == DaemonCommandLine.WriteConfig)
        {
            return WriteConfigVerb.Run(command, ConfigDaemon.Realm, ConfCatalog.Embedded, configuration, standardOutput, standardError);
        }

        ConfigReport report = Validate(configuration);
        report.Write(standardOutput);
        return report.IsInvalid ? ExitCodes.InvalidConfiguration : ExitCodes.Success;
    }
}

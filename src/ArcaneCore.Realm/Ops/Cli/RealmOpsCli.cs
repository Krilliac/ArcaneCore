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
    /// <summary>Every problem of the realm's configuration: the .conf file's keys and values, logging, resilience, watchdog and metrics.</summary>
    public static ConfigReport Validate(IConfiguration configuration)
        => ConfigReport.Run(configuration,
        [
            new ConfKeyCheck(ConfCatalog.Embedded, ConfigDaemon.Realm),
            new LoggingConfigChecks(),
            new ResilienceConfigChecks(),
            new Kernel.Ops.Watchdog.WatchdogOptionsValidation(),
            new Kernel.Ops.Metrics.MetricsOptionsValidation(),
        ]);

    /// <summary>Runs the verb of <paramref name="command"/>; the exit code is 78 when check-config finds an error, otherwise the verb's own code.</summary>
    public static int Run(DaemonCommand command, IConfigurationRoot configuration, TextWriter standardOutput, TextWriter standardError)
    {
        if (command.Verb == DaemonCommandLine.WriteConfig)
        {
            return WriteConfigVerb.Run(command, ConfigDaemon.Realm, ConfCatalog.Embedded, configuration, standardOutput, standardError);
        }

        ConfigReport report = Validate(configuration);
        report.Write(standardOutput);
        return report.IsInvalid ? ExitCodes.InvalidConfiguration : ExitCodes.Success;
    }
}

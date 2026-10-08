using ArcaneCore.Data.ClientData;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.ClientData;

/// <summary>
/// The world daemon's client data start-up (docs/areas/client-data.md). <see cref="Apply"/> runs right after the host builder
/// exists, before anything binds: it resolves every DBC consumer key (<see cref="ClientDbcConsumers"/>) and adds the keys
/// <c>ClientData:DbcDirectory</c> fills as the last configuration source, so a key that is set anywhere else wins and an unset one
/// reads the directory's file. Every feature then binds its options as it always did.
/// </summary>
public static class ClientDataStartup
{
    /// <summary>The logger category of the start-up lines.</summary>
    public const string LogCategory = "ArcaneCore.ClientData";

    /// <summary>Resolve <paramref name="configuration"/> and add the directory overlay to it.</summary>
    public static ClientDataReport Apply(IConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ClientDataReport report = ClientDataReport.Build(configuration);
        if (report.Overlay.Count > 0)
        {
            configuration.AddInMemoryCollection(report.Overlay);
        }

        return report;
    }

    /// <summary>Log the report: one line per DBC file, warnings for what is missing, mismatched or not used.</summary>
    public static void Log(ClientDataReport report, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(logger);
        foreach (ClientDataLine line in report.Lines())
        {
            if (line.Warning)
            {
                logger.LogWarning("{ClientData}", line.Text);
            }
            else
            {
                logger.LogInformation("{ClientData}", line.Text);
            }
        }
    }
}

/// <summary>
/// The client data problems as configuration issues (<c>check-config</c> and every start): a warning each, an error with
/// <c>ClientData:Strict</c>, which then refuses the start (exit code 78).
/// </summary>
public sealed class ClientDataConfigChecks : IConfigCheck
{
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        ClientDataReport report = ClientDataReport.Build(configuration);
        ConfigSeverity severity = report.Options.Strict ? ConfigSeverity.Error : ConfigSeverity.Warning;
        return report.Problems.Select(p => new ConfigIssue(severity, p.Key, p.Problem, p.Fix
            + (report.Options.Strict ? $" ({ClientDataOptions.SectionName}:Strict makes this fatal)" : string.Empty)));
    }
}

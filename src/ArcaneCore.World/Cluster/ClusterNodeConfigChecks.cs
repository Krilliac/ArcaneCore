using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Cluster;

/// <summary>
/// Startup check of the <c>Cluster:Node</c> section (<c>docs/areas/gateway.md</c>): a role this World process cannot host, a MapWorker
/// without worker mode, or lease timings out of range are errors (exit 78), so the daemon never starts as a node it cannot be.
/// </summary>
public sealed class ClusterNodeConfigChecks : IConfigCheck
{
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        var options = new ClusterNodeOptions();
        try
        {
            configuration.GetSection(ClusterNodeOptions.SectionName).Bind(options);
        }
        catch (InvalidOperationException ex)
        {
            return [new ConfigIssue(ConfigSeverity.Error, ClusterNodeOptions.SectionName, $"a value does not fit its key ({ex.Message}).",
                "use the role names Combined or MapWorker and whole numbers of seconds")];
        }

        bool workerEnabled = bool.TryParse(configuration[$"{GatewayWorkerOptions.SectionName}:Enabled"], out bool enabled) && enabled;
        return [.. options.Validate(workerEnabled).Select(problem => new ConfigIssue(
            ConfigSeverity.Error, ClusterNodeOptions.SectionName, problem, "see the node role section of docs/areas/gateway.md"))];
    }
}

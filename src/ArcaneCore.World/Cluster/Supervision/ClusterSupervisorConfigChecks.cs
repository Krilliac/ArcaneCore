using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.World.Cluster.Gateway;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Cluster.Supervision;

/// <summary>
/// Startup check of the <c>Cluster:Supervisor</c> section (docs/CLUSTERING_DESIGN.md M15.4): a value that does not bind or that
/// <see cref="ClusterSupervisorOptions.Validate"/> refuses is an error (exit 78), and so is running the supervisor mode and worker
/// mode in one process (the supervisor builds no world; a worker is a world).
/// </summary>
public sealed class ClusterSupervisorConfigChecks : IConfigCheck
{
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        var options = new ClusterSupervisorOptions();
        try
        {
            configuration.GetSection(ClusterSupervisorOptions.SectionName).Bind(options);
        }
        catch (InvalidOperationException ex)
        {
            return [new ConfigIssue(ConfigSeverity.Error, ClusterSupervisorOptions.SectionName, $"a value does not fit its key ({ex.Message}).",
                "use true/false, whole numbers, and the console modes Merged, Window or None")];
        }

        var issues = new List<ConfigIssue>();
        foreach (string problem in options.Validate())
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, ClusterSupervisorOptions.SectionName, problem, "see the M15.4 section of docs/CLUSTERING_DESIGN.md"));
        }

        foreach (string warning in options.Warnings())
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Warning, $"{ClusterSupervisorOptions.SectionName}:Processes", warning, "lower the DrainTimeoutMs values or split the children across supervisors"));
        }

        if (options.Enabled && configuration.GetSection(GatewayWorkerOptions.SectionName).GetValue<bool>(nameof(GatewayWorkerOptions.Enabled)))
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, $"{ClusterSupervisorOptions.SectionName}:Enabled",
                $"the supervisor and {GatewayWorkerOptions.SectionName}:Enabled (worker mode) are both on, but the supervisor builds no world.",
                $"turn one off: the supervisor launches workers as child processes with their own {GatewayWorkerOptions.SectionName}:Enabled"));
        }

        return issues;
    }
}

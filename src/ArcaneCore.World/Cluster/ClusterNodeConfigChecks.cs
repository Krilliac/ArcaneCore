using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Clustering;
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
        var issues = options.Validate(workerEnabled).Select(problem => new ConfigIssue(
            ConfigSeverity.Error, ClusterNodeOptions.SectionName, problem, "see the node role section of docs/areas/gateway.md")).ToList();

        // A role other than Combined means another world process shares the characters database: fencing is what keeps the writers apart and
        // primes the durable id allocators (an unfenced world collides on its in-memory id counters). A SQLite characters database with such a
        // role is refused by ClusterSqliteConfigChecks.
        if (issues.Count == 0 && options.Role != ClusterNodeRole.Combined
            && !(bool.TryParse(configuration[$"{CharacterFencingOptions.SectionName}:Enabled"], out bool fenced) && fenced))
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, $"{CharacterFencingOptions.SectionName}:Enabled",
                $"{ClusterNodeOptions.SectionName}:Role is {options.Role}, but character fencing is off: another world shares the characters database, and an unfenced world skips the durable id allocator priming and collides on in-memory id counters.",
                $"set {CharacterFencingOptions.SectionName}:Enabled=true, with a distinct {CharacterFencingOptions.SectionName}:OwnerNode, on a PostgreSQL or MariaDB characters database"));
        }

        return issues;
    }
}

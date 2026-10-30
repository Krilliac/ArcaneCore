using ArcaneCore.Data;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Cluster.Supervision;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Cluster;

/// <summary>
/// Startup check that a clustered realm does not keep its characters on SQLite (docs/areas/gateway.md, docs/guide/operations.md). SQLite allows one writer process and the world
/// processes of a cluster write the characters database concurrently, so a SQLite characters database is an error (exit 78, whatever <c>Startup:Strict</c> says) when more than one
/// world process can run against it: <c>Cluster:Worker:Enabled</c> (a gateway worker, a standby included), <c>Cluster:Node:Role = MapWorker</c>, or a cluster supervisor whose
/// <c>Cluster:Supervisor:Processes</c> list more than one world-type child (role <c>combined</c> or <c>worker</c>). A supervisor with exactly one world child has one writer and is
/// allowed, with a warning that the realm cannot grow beyond one world on SQLite. The provider is the one <c>Database:Characters</c> resolves to in this process's configuration
/// (the supervisor's own, for a supervisor; children are expected to share it).
/// </summary>
public sealed class ClusterSqliteConfigChecks : IConfigCheck
{
    private const string RunbookHeading = "Moving from SQLite to PostgreSQL or MariaDB";

    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        DatabaseProvider provider;
        ClusterSupervisorOptions supervisor = new();
        ClusterNodeOptions node = new();
        try
        {
            var database = new DatabaseOptions();
            configuration.GetSection(DatabaseOptions.SectionName).Bind(database);
            provider = database.Resolve(DatabaseComponent.Characters).Provider;
            configuration.GetSection(ClusterSupervisorOptions.SectionName).Bind(supervisor);
            configuration.GetSection(ClusterNodeOptions.SectionName).Bind(node);
        }
        catch (InvalidOperationException)
        {
            return []; // a value that does not bind is reported by the check of its own section
        }

        if (provider != DatabaseProvider.Sqlite)
        {
            return [];
        }

        bool workerEnabled = bool.TryParse(configuration[$"{GatewayWorkerOptions.SectionName}:Enabled"], out bool enabled) && enabled;
        int worldChildren = supervisor.Enabled
            ? supervisor.Processes.Values.Count(p => string.Equals(p.Role, SupervisedRoles.Combined, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Role, SupervisedRoles.Worker, StringComparison.OrdinalIgnoreCase))
            : 0;

        string? cause = null;
        string? key = null;
        if (workerEnabled)
        {
            key = $"{GatewayWorkerOptions.SectionName}:Enabled";
            cause = $"{key} is true (a gateway worker{(bool.TryParse(configuration[$"{GatewayWorkerOptions.SectionName}:Standby"], out bool standby) && standby ? ", here a standby" : string.Empty)}: the realm runs more than one world process)";
        }
        else if (node.Role == ClusterNodeRole.MapWorker)
        {
            key = $"{ClusterNodeOptions.SectionName}:Role";
            cause = $"{key} is MapWorker (a map worker is one of several world processes)";
        }
        else if (worldChildren > 1)
        {
            key = $"{ClusterSupervisorOptions.SectionName}:Processes";
            cause = $"{key} lists {worldChildren} world-type children (role combined or worker) under {ClusterSupervisorOptions.SectionName}:Enabled";
        }

        if (cause is not null)
        {
            return [new ConfigIssue(ConfigSeverity.Error, key!,
                $"{cause}, but the characters database is on the Sqlite provider (Database:Characters). SQLite allows one writer process, and the world processes of a cluster write the characters database concurrently.",
                $"use PostgreSQL or MariaDB for Database:Characters; see the runbook \"{RunbookHeading}\" in docs/ops/database-upgrade.md")];
        }

        if (supervisor.Enabled && worldChildren == 1)
        {
            return [new ConfigIssue(ConfigSeverity.Warning, $"{ClusterSupervisorOptions.SectionName}:Processes",
                "the supervisor runs one world on a SQLite characters database: this realm cannot grow beyond one world on SQLite.",
                $"to add a second world, move Database:Characters to PostgreSQL or MariaDB first (\"{RunbookHeading}\" in docs/ops/database-upgrade.md)")];
        }

        return [];
    }
}

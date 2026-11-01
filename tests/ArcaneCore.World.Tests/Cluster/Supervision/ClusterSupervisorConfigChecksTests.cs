using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Supervision;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

/// <summary>
/// A supervisor with more than one world child (or a child with a Cluster:Node:Role other than Combined) needs fencing on every world child, a distinct
/// Cluster:Fencing:OwnerNode and Cluster:Worker:NodeId for each, and a server database (the SQLite refusal is <see cref="ClusterSqliteConfigChecks"/>).
/// </summary>
public sealed class ClusterSupervisorConfigChecksTests
{
    private static Dictionary<string, string?> Base(string provider = "PostgreSql")
    {
        var values = new Dictionary<string, string?>
        {
            ["Database:Characters:Provider"] = provider,
            ["Database:Characters:ConnectionString"] = provider == "Sqlite" ? "Data Source=characters.db" : "Host=127.0.0.1;Database=chars;Username=u;Password=p",
            ["Cluster:Supervisor:Enabled"] = "true",
            ["Cluster:Supervisor:MinClusterProcesses"] = "1",
            ["Cluster:Supervisor:Processes:realm:Name"] = "realm",
            ["Cluster:Supervisor:Processes:realm:Role"] = "realm",
            ["Cluster:Supervisor:Processes:realm:ExecutablePath"] = "ArcaneCore.Realm",
        };
        return values;
    }

    private static void AddWorld(Dictionary<string, string?> values, string key, string role, string arguments)
    {
        string prefix = $"Cluster:Supervisor:Processes:{key}:";
        values[prefix + "Name"] = key;
        values[prefix + "Role"] = role;
        values[prefix + "ExecutablePath"] = "ArcaneCore.World";
        values[prefix + "Arguments"] = arguments;
    }

    private static ConfigIssue[] Errors(Dictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return [.. new ClusterSupervisorConfigChecks().Check(configuration).Concat(new ClusterSqliteConfigChecks().Check(configuration))
            .Where(i => i.Severity == ConfigSeverity.Error)];
    }

    private const string FencedA = "--Cluster:Fencing:Enabled=true --Cluster:Fencing:OwnerNode=world-a --Cluster:Worker:NodeId=node-a";
    private const string FencedB = "--Cluster:Fencing:Enabled=true --Cluster:Fencing:OwnerNode=world-b --Cluster:Worker:NodeId=node-b";

    [Fact]
    public void TwoWorldChildren_WithoutFencing_AreRefused()
    {
        Dictionary<string, string?> values = Base();
        AddWorld(values, "a", "worker", "--Cluster:Fencing:OwnerNode=world-a --Cluster:Worker:NodeId=node-a");
        AddWorld(values, "b", "worker", "--Cluster:Fencing:OwnerNode=world-b --Cluster:Worker:NodeId=node-b");

        ConfigIssue[] errors = Errors(values);

        Assert.Equal(2, errors.Length);
        Assert.All(errors, e => Assert.Contains("Cluster:Fencing:Enabled=true", e.Problem, StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Key == "Cluster:Supervisor:Processes:a:Arguments");
        Assert.Contains(errors, e => e.Key == "Cluster:Supervisor:Processes:b:Arguments");
    }

    [Fact]
    public void TwoWorldChildren_FencedByTheSharedConfiguration_AreAccepted()
    {
        Dictionary<string, string?> values = Base();
        values["Cluster:Fencing:Enabled"] = "true";
        AddWorld(values, "a", "worker", "--Cluster:Fencing:OwnerNode=world-a --Cluster:Worker:NodeId=node-a");
        AddWorld(values, "b", "worker", "--Cluster:Fencing:OwnerNode=world-b --Cluster:Worker:NodeId=node-b");

        Assert.Empty(Errors(values));
    }

    [Fact]
    public void ValidFencedPostgreSqlCluster_IsAccepted()
    {
        Dictionary<string, string?> values = Base();
        AddWorld(values, "a", "worker", FencedA);
        AddWorld(values, "b", "worker", FencedB);

        Assert.Empty(Errors(values));
    }

    [Fact]
    public void TwoWorkerScopeChildren_WhoseShardIdsDifferOnlyInCase_AreRefused_ButDistinctShardsAreAccepted()
    {
        const string scope = " --Cluster:Fencing:Scope=Worker";
        Dictionary<string, string?> values = Base();
        AddWorld(values, "a", "worker", "--Cluster:Fencing:Enabled=true --Cluster:Fencing:OwnerNode=world-a --Cluster:Worker:NodeId=Node-A" + scope);
        AddWorld(values, "b", "worker", "--Cluster:Fencing:Enabled=true --Cluster:Fencing:OwnerNode=world-b --Cluster:Worker:NodeId=node-a" + scope);

        ConfigIssue error = Assert.Single(Errors(values));
        Assert.Contains("shard", error.Problem, StringComparison.Ordinal);
        Assert.Equal("Cluster:Supervisor:Processes:b:Arguments", error.Key);

        // An explicit Shard that differs makes them distinct; without Worker scope the same NodeIds are not a shard clash.
        Dictionary<string, string?> distinct = Base();
        AddWorld(distinct, "a", "worker", "--Cluster:Fencing:Enabled=true --Cluster:Fencing:OwnerNode=world-a --Cluster:Worker:NodeId=Node-A --Cluster:Fencing:Shard=s1" + scope);
        AddWorld(distinct, "b", "worker", "--Cluster:Fencing:Enabled=true --Cluster:Fencing:OwnerNode=world-b --Cluster:Worker:NodeId=node-a --Cluster:Fencing:Shard=s2" + scope);
        Assert.Empty(Errors(distinct));

        Dictionary<string, string?> realmScope = Base();
        AddWorld(realmScope, "a", "worker", "--Cluster:Fencing:Enabled=true --Cluster:Fencing:OwnerNode=world-a --Cluster:Worker:NodeId=Node-A");
        AddWorld(realmScope, "b", "worker", "--Cluster:Fencing:Enabled=true --Cluster:Fencing:OwnerNode=world-b --Cluster:Worker:NodeId=node-a");
        Assert.Empty(Errors(realmScope));
    }

    [Fact]
    public void ANumericWorkerScope_IsParsedLikeTheName_SoCaseOnlyShardClashesAreStillRefused()
    {
        // CharacterFencingScope.Worker = 1: the configuration binder accepts the number, so the check must too.
        const string scope = " --Cluster:Fencing:Scope=1";
        Dictionary<string, string?> values = Base();
        AddWorld(values, "a", "worker", "--Cluster:Fencing:Enabled=true --Cluster:Fencing:OwnerNode=world-a --Cluster:Worker:NodeId=Node-A" + scope);
        AddWorld(values, "b", "worker", "--Cluster:Fencing:Enabled=true --Cluster:Fencing:OwnerNode=world-b --Cluster:Worker:NodeId=node-a" + scope);

        ConfigIssue error = Assert.Single(Errors(values));
        Assert.Contains("shard", error.Problem, StringComparison.Ordinal);
        Assert.Equal("Cluster:Supervisor:Processes:b:Arguments", error.Key);
    }

    [Fact]
    public void TwoWorldChildren_OnSqlite_AreRefused()
    {
        Dictionary<string, string?> values = Base("Sqlite");
        AddWorld(values, "a", "worker", FencedA);
        AddWorld(values, "b", "worker", FencedB);

        Assert.Contains(Errors(values), e => e.Problem.Contains("Sqlite", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateOwnerNode_IsRefused()
    {
        Dictionary<string, string?> values = Base();
        AddWorld(values, "a", "worker", FencedA);
        AddWorld(values, "b", "worker", "--Cluster:Fencing:Enabled=true --Cluster:Fencing:OwnerNode=world-a --Cluster:Worker:NodeId=node-b");

        ConfigIssue issue = Assert.Single(Errors(values));

        Assert.Equal("Cluster:Supervisor:Processes:b:Arguments", issue.Key);
        Assert.Contains("OwnerNode 'world-a'", issue.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void WorldChildrenThatLeaveOwnerNodeAndNodeIdUnset_AreRefusedAsDuplicates()
    {
        Dictionary<string, string?> values = Base();
        AddWorld(values, "a", "worker", "--Cluster:Fencing:Enabled=true");
        AddWorld(values, "b", "worker", "--Cluster:Fencing:Enabled=true");

        ConfigIssue[] errors = Errors(values);

        Assert.Contains(errors, e => e.Problem.Contains("Cluster:Fencing:OwnerNode", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Problem.Contains("Cluster:Worker:NodeId", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateNodeId_IsRefused()
    {
        Dictionary<string, string?> values = Base();
        AddWorld(values, "a", "worker", FencedA);
        AddWorld(values, "b", "worker", "--Cluster:Fencing:Enabled=true --Cluster:Fencing:OwnerNode=world-b --Cluster:Worker:NodeId=node-a");

        ConfigIssue issue = Assert.Single(Errors(values));

        Assert.Contains("NodeId 'node-a'", issue.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleCombinedWorldChild_NeedsNoFencing()
    {
        Dictionary<string, string?> values = Base();
        AddWorld(values, "world", "combined", string.Empty);

        Assert.Empty(Errors(values));
    }

    [Fact]
    public void SingleWorldChild_WithNodeRoleMapWorker_NeedsFencing()
    {
        Dictionary<string, string?> values = Base();
        AddWorld(values, "world", "worker", "--Cluster:Node:Role=MapWorker");
        Assert.Single(Errors(values));

        AddWorld(values, "world", "worker", "--Cluster:Node:Role=MapWorker --Cluster:Fencing:Enabled=true");
        Assert.Empty(Errors(values));
    }
}

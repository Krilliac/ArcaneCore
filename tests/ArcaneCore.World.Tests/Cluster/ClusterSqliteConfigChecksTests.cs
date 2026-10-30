using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Tests.Ops;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>A clustered realm must not keep its characters on SQLite (one writer process): refused (exit 78) for a worker, a map worker and a supervisor with several world children.</summary>
public sealed class ClusterSqliteConfigChecksTests
{
    private const string SqliteString = "Data Source=characters.db";

    private static ConfigIssue[] Run(string provider, params (string Key, string? Value)[] values)
    {
        var all = new Dictionary<string, string?>
        {
            ["Database:Characters:Provider"] = provider,
            ["Database:Characters:ConnectionString"] = provider == "Sqlite" ? SqliteString : "Server=127.0.0.1;Database=chars;User=u;Password=p;",
        };
        foreach ((string key, string? value) in values) all[key] = value;
        return [.. new ClusterSqliteConfigChecks().Check(new ConfigurationBuilder().AddInMemoryCollection(all).Build())];
    }

    private static (string, string?)[] Worker() => [("Cluster:Worker:Enabled", "true")];

    private static (string, string?)[] StandbyWorker() => [("Cluster:Worker:Enabled", "true"), ("Cluster:Worker:Standby", "true")];

    private static (string, string?)[] MapWorker() => [("Cluster:Node:Role", "MapWorker")]; // alone: with Cluster:Worker:Enabled too, the worker setting is the one named

    private static (string, string?)[] Supervisor(params string[] roles)
    {
        var values = new List<(string, string?)> { ("Cluster:Supervisor:Enabled", "true") };
        for (int i = 0; i < roles.Length; i++) values.Add(($"Cluster:Supervisor:Processes:p{i}:Role", roles[i]));
        return [.. values];
    }

    public static IEnumerable<object[]> ClusteredModes() =>
    [
        ["worker", "Cluster:Worker:Enabled", Worker()],
        ["standby", "Cluster:Worker:Enabled", StandbyWorker()],
        ["mapworker", "Cluster:Node:Role", MapWorker()],
        ["supervisor", "Cluster:Supervisor:Processes", Supervisor("realm", "worker", "worker")],
        ["supervisor-combined", "Cluster:Supervisor:Processes", Supervisor("combined", "Worker")],
    ];

    [Theory]
    [MemberData(nameof(ClusteredModes))]
    public void SqliteCharacters_AreRefused_InEveryModeWithSeveralWorldProcesses(string mode, string key, (string, string?)[] settings)
    {
        ConfigIssue issue = Assert.Single(Run("Sqlite", settings));

        Assert.Equal(ConfigSeverity.Error, issue.Severity);
        Assert.Equal(key, issue.Key);
        Assert.Contains("Sqlite", issue.Problem, StringComparison.Ordinal);
        Assert.Contains("one writer process", issue.Problem, StringComparison.Ordinal);
        Assert.Contains("concurrently", issue.Problem, StringComparison.Ordinal);
        Assert.Contains("PostgreSQL or MariaDB", issue.Fix, StringComparison.Ordinal);
        Assert.Contains("Moving from SQLite to PostgreSQL or MariaDB", issue.Fix, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(mode));
    }

    [Theory]
    [MemberData(nameof(ClusteredModes))]
    public void ServerCharacters_AreAllowed_InEveryMode(string mode, string key, (string, string?)[] settings)
    {
        Assert.Empty(Run("PostgreSql", settings));
        Assert.Empty(Run("MariaDb", settings));
        Assert.False(string.IsNullOrEmpty(mode + key));
    }

    [Fact]
    public void ASupervisorWithOneWorldChild_IsAllowedOnSqlite_WithAWarning()
    {
        ConfigIssue warning = Assert.Single(Run("Sqlite", Supervisor("realm", "combined", "gateway")));

        Assert.Equal(ConfigSeverity.Warning, warning.Severity);
        Assert.Contains("cannot grow beyond one world on SQLite", warning.Problem, StringComparison.Ordinal);
        Assert.Empty(Run("PostgreSql", Supervisor("realm", "combined", "gateway")));
    }

    [Fact]
    public void ASingleCombinedWorld_OnSqlite_HasNoIssue()
    {
        Assert.Empty(Run("Sqlite"));
        Assert.Empty(Run("Sqlite", Supervisor("realm", "gateway"))); // no world child at all
    }

    [Fact]
    public void ASupervisorThatIsOff_DoesNotCountItsChildren()
        => Assert.Empty(Run("Sqlite", ("Cluster:Supervisor:Processes:a:Role", "worker"), ("Cluster:Supervisor:Processes:b:Role", "worker")));

    [Fact]
    public async Task CheckConfig_ReportsTheError_AndExits78()
    {
        string[] database =
        [
            "--Database:Characters:Provider=Sqlite", "--Database:Characters:ConnectionString=Data Source=characters.db",
            "--Database:Auth:Provider=Sqlite", "--Database:Auth:ConnectionString=Data Source=auth.db",
            "--Database:World:Provider=Sqlite", "--Database:World:ConnectionString=Data Source=world.db",
        ];

        ConfProcess.Result control = await ConfProcess.RunWorldAsync(["check-config", .. database]);
        Assert.Equal(ExitCodes.Success, control.ExitCode);

        ConfProcess.Result refused = await ConfProcess.RunWorldAsync(["check-config", .. database, "--Cluster:Node:Role=MapWorker", "--Cluster:Worker:Enabled=true", "--Cluster:Worker:LinkSecret=check-config-link-secret-0123456789"]);
        Assert.Equal(ExitCodes.InvalidConfiguration, refused.ExitCode);
        Assert.Contains("SQLite allows one writer process", refused.Output, StringComparison.Ordinal);
    }
}

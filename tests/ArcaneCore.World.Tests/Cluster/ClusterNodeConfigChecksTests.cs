using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Tests.Ops;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>The Cluster:Node startup check: roles a World process cannot host, a map worker without worker mode, and lease timings are refused (exit 78).</summary>
public sealed class ClusterNodeConfigChecksTests
{
    private static ConfigIssue[] Run(params (string Key, string? Value)[] values)
        => [.. new ClusterNodeConfigChecks().Check(new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build())];

    [Fact]
    public void Defaults_NoIssues()
    {
        Assert.Empty(Run());
        Assert.Empty(new ClusterNodeOptions().Validate(workerEnabled: false));
        Assert.Equal(ClusterNodeRole.Combined, new ClusterNodeOptions().Role);
        Assert.Equal(30, new ClusterNodeOptions().AuthorityLeaseTtlSeconds);
        Assert.Equal(10, new ClusterNodeOptions().AuthorityLeaseRenewSeconds);
    }

    [Fact]
    public void MapWorker_WithWorkerMode_IsValid()
        => Assert.Empty(Run(("Cluster:Node:Role", "MapWorker"), ("Cluster:Worker:Enabled", "true")));

    [Theory]
    [InlineData("Social", "AutoBroadcast")]
    [InlineData("Economy", "EconomyFeature")]
    [InlineData("Gateway", "ArcaneCore.Gateway")]
    public void RefusedRoles_AreErrors(string role, string reasonMentions)
    {
        ConfigIssue issue = Assert.Single(Run(("Cluster:Node:Role", role), ("Cluster:Worker:Enabled", "true")));

        Assert.Equal(ConfigSeverity.Error, issue.Severity);
        Assert.Equal("Cluster:Node", issue.Key);
        Assert.Contains(reasonMentions, issue.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void MapWorker_WithoutWorkerMode_IsError()
    {
        ConfigIssue issue = Assert.Single(Run(("Cluster:Node:Role", "MapWorker")));

        Assert.Equal(ConfigSeverity.Error, issue.Severity);
        Assert.Contains("Cluster:Worker:Enabled", issue.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("4", "1")]
    [InlineData("601", "10")]
    public void TtlOutOfRange_IsError(string ttl, string renew)
        => Assert.Contains(Run(("Cluster:Node:AuthorityLeaseTtlSeconds", ttl), ("Cluster:Node:AuthorityLeaseRenewSeconds", renew)),
            i => i.Problem.Contains("AuthorityLeaseTtlSeconds", StringComparison.Ordinal));

    [Theory]
    [InlineData("30", "11")]
    [InlineData("30", "0")]
    public void RenewNotBelowTtlThird_IsError(string ttl, string renew)
        => Assert.Contains(Run(("Cluster:Node:AuthorityLeaseTtlSeconds", ttl), ("Cluster:Node:AuthorityLeaseRenewSeconds", renew)),
            i => i.Problem.Contains("AuthorityLeaseRenewSeconds", StringComparison.Ordinal));

    [Fact]
    public void RenewAtExactlyATtlThird_IsValid()
        => Assert.Empty(Run(("Cluster:Node:AuthorityLeaseTtlSeconds", "30"), ("Cluster:Node:AuthorityLeaseRenewSeconds", "10")));

    [Theory]
    [InlineData("Primary")]
    [InlineData("7")]
    public void UnknownRoleText_IsError(string role)
    {
        ConfigIssue issue = Assert.Single(Run(("Cluster:Node:Role", role)));

        Assert.Equal(ConfigSeverity.Error, issue.Severity);
        Assert.Equal("Cluster:Node", issue.Key);
    }

    [Fact]
    public async Task CheckConfig_ExitsInvalidConfiguration_ForARoleThisProcessCannotHost_AndPassesWithout()
    {
        string[] database =
        [
            "--Database:Auth:Provider=MariaDb", "--Database:Auth:ConnectionString=Server=127.0.0.1;Port=3306;Database=x;User=arcane;Password=Sup3rSecret!;",
            "--Database:Characters:Provider=MariaDb", "--Database:Characters:ConnectionString=Server=127.0.0.1;Port=3306;Database=x;User=arcane;Password=Sup3rSecret!;",
            "--Database:World:Provider=MariaDb", "--Database:World:ConnectionString=Server=127.0.0.1;Port=3306;Database=x;User=arcane;Password=Sup3rSecret!;",
        ];

        ConfProcess.Result control = await ConfProcess.RunWorldAsync(["check-config", .. database]);
        Assert.Equal(ExitCodes.Success, control.ExitCode);

        ConfProcess.Result refused = await ConfProcess.RunWorldAsync(["check-config", .. database, "--Cluster:Node:Role=Economy"]);
        Assert.Equal(ExitCodes.InvalidConfiguration, refused.ExitCode);
        Assert.Contains("Cluster:Node", refused.Output, StringComparison.Ordinal);
    }
}

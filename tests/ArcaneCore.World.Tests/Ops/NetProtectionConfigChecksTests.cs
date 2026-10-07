using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.World.Ops.Cli;
using ArcaneCore.World.Ops.Validation;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>check-config covers the Net:Protection section: bad values are errors, weakening ones are warnings, the defaults are clean.</summary>
public sealed class NetProtectionConfigChecksTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value)).Build();

    private static List<ConfigIssue> Check(params (string Key, string Value)[] pairs) => [.. new NetProtectionConfigChecks().Check(Config(pairs))];

    [Fact]
    public void Defaults_AreClean()
    {
        Assert.Empty(Check());
        Assert.Empty(Check(("Net:Protection:MaxConnectionsPerIp", "16"), ("Net:Protection:FrameReadTimeout", "00:00:30"), ("Net:Protection:MaxTrackedAddresses", "4096")));
    }

    [Fact]
    public void BadValues_AreErrorsNamingTheKey()
    {
        List<ConfigIssue> issues = Check(
            ("Net:Protection:MaxConnectionsPerIp", "-1"),
            ("Net:Protection:MaxTrackedAddresses", "0"),
            ("Net:Protection:FrameReadTimeout", "soon"),
            ("Net:Protection:AddressIdleEviction", "00:00:00"),
            ("Net:Protection:AuthFailureBurstPerIp", "ten"));

        Assert.Equal(5, issues.Count(i => i.Severity == ConfigSeverity.Error));
        Assert.Contains(issues, i => i.Key == "Net:Protection:MaxConnectionsPerIp" && i.Problem.Contains("out of range", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Key == "Net:Protection:MaxTrackedAddresses");
        Assert.Contains(issues, i => i.Key == "Net:Protection:FrameReadTimeout" && i.Problem.Contains("not a duration", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Key == "Net:Protection:AddressIdleEviction");
        Assert.Contains(issues, i => i.Key == "Net:Protection:AuthFailureBurstPerIp" && i.Problem.Contains("not a whole number", StringComparison.Ordinal));
        Assert.All(issues, i => Assert.Equal(i.Key.Replace(":", "__", StringComparison.Ordinal), i.EnvironmentVariable));
    }

    [Fact]
    public void WeakeningValues_AreWarnings()
    {
        List<ConfigIssue> noRefill = Check(("Net:Protection:ConnectionsPerMinutePerIp", "0"), ("Net:Protection:AuthFailuresPerMinutePerIp", "0"));
        Assert.Equal(2, noRefill.Count);
        Assert.All(noRefill, i => Assert.Equal(ConfigSeverity.Warning, i.Severity));

        List<ConfigIssue> shortIdle = Check(("Net:Protection:AddressIdleEviction", "00:00:30"));
        ConfigIssue idle = Assert.Single(shortIdle);
        Assert.Equal("Net:Protection:AddressIdleEviction", idle.Key);
        Assert.Contains("start over", idle.Problem, StringComparison.Ordinal);

        List<ConfigIssue> open = Check(("Net:Protection:MaxConnectionsPerIp", "0"), ("Net:Protection:ConnectionBurstPerIp", "0"));
        ConfigIssue both = Assert.Single(open);
        Assert.Equal(ConfigSeverity.Warning, both.Severity);
    }

    [Fact]
    public void OpsCli_RunsTheSectionsChecks()
    {
        ConfigReport report = OpsCli.Validate(Config(("Net:Protection:MaxTrackedAddresses", "-5"), ("Database:ConnectionString", "Server=127.0.0.1;User=arcane;Password=arcane;")));
        Assert.Contains(report.Issues, i => i.Key == "Net:Protection:MaxTrackedAddresses" && i.Severity == ConfigSeverity.Error);
        Assert.True(report.IsInvalid);
    }
}

using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Configuration.Validation;

/// <summary>Pins the <c>Diagnostics:CrashDump</c> start-up checks (docs/ops/invariants.md), the ones every daemon runs.</summary>
public sealed class CrashDumpConfigChecksTests
{
    private static ConfigIssue[] Run(params (string Key, string Value)[] values)
        => [.. new CrashDumpConfigChecks().Check(
            new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build())];

    [Fact]
    public void Defaults_AndTheEdgeValues_Pass()
    {
        Assert.Empty(Run());
        Assert.Empty(Run(
            ("Diagnostics:CrashDump:Mode", "heap"),
            ("Diagnostics:CrashDump:MaxFiles", "0"),
            ("Diagnostics:CrashDump:FullMinFreeGB", "0"),
            ("Diagnostics:CrashDump:TimeoutSeconds", "1")));
        Assert.Empty(Run(("Diagnostics:CrashDump:TimeoutSeconds", "3600")));
    }

    [Theory]
    [InlineData("Diagnostics:CrashDump:Mode", "Everything")]
    [InlineData("Diagnostics:CrashDump:MaxFiles", "-1")]
    [InlineData("Diagnostics:CrashDump:FullMinFreeGB", "-5")]
    [InlineData("Diagnostics:CrashDump:FullMinFreeGB", "lots")]
    [InlineData("Diagnostics:CrashDump:TimeoutSeconds", "0")]
    [InlineData("Diagnostics:CrashDump:TimeoutSeconds", "-3")]
    [InlineData("Diagnostics:CrashDump:TimeoutSeconds", "3601")]
    [InlineData("Diagnostics:CrashDump:TimeoutSeconds", "2000000000")]
    public void ABadValue_IsOneError_NamingTheKey(string key, string value)
    {
        ConfigIssue issue = Assert.Single(Run((key, value)));
        Assert.Equal(ConfigSeverity.Error, issue.Severity);
        Assert.Equal(key, issue.Key);
        Assert.Contains(value, issue.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLargestAcceptedTimeout_GivesTheChainsMaximum_WithoutOverflow()
        => Assert.Equal(3 * CrashDumpConfigChecks.MaxTimeoutSeconds,
            ArcaneCore.Kernel.Diagnostics.CrashDumpService.MaxWaitSeconds(new ArcaneCore.Kernel.Configuration.CrashDumpOptions { TimeoutSeconds = CrashDumpConfigChecks.MaxTimeoutSeconds }));
}

using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Realm.Ops.Cli;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.Realm.Tests;

/// <summary>
/// Realm start-up validation through the same path as <c>check-config</c> (<see cref="RealmOpsCli.Validate"/>). The reconnect
/// pre-auth lifetime needs no test here: the logon session arms Net:Protection:LogonUnauthenticatedLifetime for the whole connection
/// (reconnect challenge included) and disarms it only at a good proof, covered by LogonReconnectTests.
/// </summary>
public sealed class RealmStartupValidationTests
{
    private static ConfigReport Validate(params (string Key, string? Value)[] pairs)
        => RealmOpsCli.Validate(new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value)).Build());

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void EmptyAuthPort_FailsCheckConfig(string value)
    {
        ConfigReport report = Validate(("Auth:Port", value));
        Assert.True(report.IsInvalid);
        Assert.Contains(report.Issues, i => i.Key == "Auth:Port" && i.Severity == ConfigSeverity.Error);
    }

    [Fact]
    public void EmptyAuthPort_ThrowsWhenBoundAtStart()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Port"] = "" }).Build();
        Assert.Throws<InvalidOperationException>(() => configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>());
    }

    [Fact]
    public void MissingAuthPort_FallsBackToTheDefault()
        => Assert.DoesNotContain(Validate().Issues, i => i.Key == "Auth:Port");

    [Theory]
    [InlineData("localhost")]
    [InlineData("")]
    [InlineData("999.1.1.1")]
    public void InvalidAuthBindAddress_FailsCheckConfig(string value)
    {
        ConfigReport report = Validate(("Auth:BindAddress", value));
        Assert.True(report.IsInvalid);
        Assert.Contains(report.Issues, i => i.Key == "Auth:BindAddress" && i.Severity == ConfigSeverity.Error);
    }

    [Fact]
    public void ValidAuthListener_Passes()
        => Assert.DoesNotContain(Validate(("Auth:BindAddress", "::1"), ("Auth:Port", "3724")).Issues, i => i.Key.StartsWith("Auth:", StringComparison.Ordinal));
}

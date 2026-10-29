using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Configuration.Validation;

/// <summary>Pins the realm-liveness start-up checks (docs/ops/realm-liveness.md): ranges, the interval/stale/hang relations and the display name.</summary>
public sealed class RealmLivenessConfigChecksTests
{
    private const string Interval = "World:Realm:HeartbeatIntervalSeconds";
    private const string Stale = "Realm:Liveness:HeartbeatStaleSeconds";
    private const string Restarting = "Realm:Liveness:RestartingAs";
    private const string Hang = "Ops:Watchdog:TickMonitor:HangMs";

    private static ConfigIssue[] Run(params (string Key, string Value)[] values)
        => [.. new RealmLivenessConfigChecks().Check(
            new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build())];

    [Fact]
    public void Defaults_Pass()
    {
        Assert.Empty(Run());
        Assert.Empty(Run((Interval, "10"), (Stale, "60"), (Restarting, "Invalid"), (Hang, "2000")));
    }

    [Fact]
    public void StaleBelowThreeBeatIntervals_IsRefused()
    {
        ConfigIssue issue = Assert.Single(Run((Interval, "10"), (Stale, "29")));
        Assert.Equal(ConfigSeverity.Error, issue.Severity);
        Assert.Equal(Stale, issue.Key);
        Assert.Contains("three times", issue.Problem, StringComparison.Ordinal);
        Assert.Contains("30", issue.Fix, StringComparison.Ordinal);

        Assert.Empty(Run((Interval, "10"), (Stale, "30"))); // exactly three intervals is allowed
        Assert.Single(Run((Interval, "30"), (Stale, "60"))); // the default stale against a slow beat
    }

    [Fact]
    public void StaleNotAboveFiveTimesTheHangThreshold_IsRefused()
    {
        ConfigIssue issue = Assert.Single(Run((Stale, "60"), (Hang, "12000")));
        Assert.Equal(Stale, issue.Key);
        Assert.Contains("hang threshold", issue.Problem, StringComparison.Ordinal);

        Assert.Single(Run((Stale, "60"), (Hang, "15000"))); // 60 s is not above 5 x 15 s
        Assert.Empty(Run((Stale, "60"), (Hang, "11999")));
        Assert.Empty(Run((Stale, "60"), (Hang, "0"))); // hang detection off: nothing to relate to
    }

    [Fact]
    public void ZeroStale_TurnsTheHeartbeatCheckOff_AndSkipsTheRelations()
    {
        Assert.Empty(Run((Stale, "0"), (Interval, "3600"), (Hang, "60000")));
    }

    [Fact]
    public void Ranges_AreEnforced()
    {
        Assert.Equal("0 is outside 1..3600.", Assert.Single(Run((Interval, "0"))).Problem);
        Assert.Equal("3601 is outside 1..3600.", Assert.Single(Run((Interval, "3601"))).Problem);
        Assert.Equal("86401 is outside 0..86400.", Assert.Single(Run((Stale, "86401"))).Problem);
        Assert.Equal("-1 is outside 0..86400.", Assert.Single(Run((Stale, "-1"))).Problem);
        Assert.Equal("'soon' is not a whole number.", Assert.Single(Run((Interval, "soon"))).Problem);
    }

    [Theory]
    [InlineData("Invalid")]
    [InlineData("Offline")]
    [InlineData("offline")]
    [InlineData("")]
    public void RestartingAs_ValidNames_Pass(string value) => Assert.Empty(Run((Restarting, value)));

    [Theory]
    [InlineData("Red")]
    [InlineData("7")]
    public void RestartingAs_InvalidNames_AreRefused(string value)
    {
        ConfigIssue issue = Assert.Single(Run((Restarting, value)));
        Assert.Equal(Restarting, issue.Key);
        Assert.Equal($"'{value}' is not a display.", issue.Problem);
        Assert.Equal("use Invalid, Offline", issue.Fix);
    }
}

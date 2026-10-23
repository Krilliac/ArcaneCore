using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Configuration.Validation;

/// <summary>Pins the key, problem and fix text and the range boundaries of the Announce and Resilience start-up checks.</summary>
public sealed class AnnounceAndResilienceConfigChecksTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static ConfigIssue Single(IConfigCheck check, params (string Key, string Value)[] values)
        => Assert.Single(check.Check(Config(values)));

    [Fact]
    public void Announce_BadBoolean_NamesKeyAndFix()
    {
        ConfigIssue i = Single(new AnnounceConfigChecks(), ("World:Announce:Enabled", "maybe"));
        Assert.Equal(ConfigSeverity.Error, i.Severity);
        Assert.Equal("World:Announce:Enabled", i.Key);
        Assert.Equal("'maybe' is not a boolean.", i.Problem);
        Assert.Equal("use true or false", i.Fix);
    }

    [Fact]
    public void Announce_Ranges_BoundariesAndMessages()
    {
        var check = new AnnounceConfigChecks();
        Assert.Empty(check.Check(Config(("World:Announce:Faults:RateBurst", "0"), ("World:Announce:Faults:RatePerMinute", "100000"), ("World:Announce:Faults:DedupSeconds", "86400"))));
        Assert.Empty(check.Check(Config(("World:Announce:Faults:RateBurst", "10000"))));

        ConfigIssue over = Single(check, ("World:Announce:Faults:RateBurst", "10001"));
        Assert.Equal("World:Announce:Faults:RateBurst", over.Key);
        Assert.Equal("10001 is outside 0..10000.", over.Problem);
        Assert.Equal("use 0..10000", over.Fix);

        Assert.Equal("-1 is outside 0..100000.", Single(check, ("World:Announce:Saves:RatePerMinute", "-1")).Problem);
        Assert.Equal("86401 is outside 0..86400.", Single(check, ("World:Announce:Saves:DedupSeconds", "86401")).Problem);

        ConfigIssue nan = Single(check, ("World:Announce:Wiring:RateBurst", "ten"));
        Assert.Equal("'ten' is not a whole number.", nan.Problem);
        Assert.Equal("use 0..10000", nan.Fix);
    }

    [Fact]
    public void Announce_MinimumSeverity_RejectsUnknownNamesAndNumbers()
    {
        var check = new AnnounceConfigChecks();
        Assert.Empty(check.Check(Config(("World:Announce:Faults:MinimumSeverity", "warning"))));

        ConfigIssue bad = Single(check, ("World:Announce:Faults:MinimumSeverity", "Loud"));
        Assert.Equal("World:Announce:Faults:MinimumSeverity", bad.Key);
        Assert.Equal("'Loud' is not a severity.", bad.Problem);
        Assert.Equal("use Info, Warning, Error or Critical", bad.Fix);

        // A bare number parses as an enum value, so the second branch names it.
        ConfigIssue number = Single(check, ("World:Announce:Faults:MinimumSeverity", "99"));
        Assert.Equal("'99' is a number, not a severity name.", number.Problem);
        Assert.Equal("use Info, Warning, Error or Critical", number.Fix);
    }

    [Fact]
    public void Announce_HotSwapLanes_ReportsEachUnknownLane()
    {
        var check = new AnnounceConfigChecks();
        Assert.Empty(check.Check(Config(("World:Announce:HotSwap:Lanes:0", "data"), ("World:Announce:HotSwap:Lanes:1", "CODE"))));

        ConfigIssue lane = Single(check, ("World:Announce:HotSwap:Lanes:0", "Data"), ("World:Announce:HotSwap:Lanes:1", "Shader"));
        Assert.Equal("World:Announce:HotSwap:Lanes:1", lane.Key);
        Assert.Equal("'Shader' is not a hot swap lane.", lane.Problem);
        Assert.Equal("use Data, Module, Code", lane.Fix);
    }

    [Fact]
    public void Announce_HotSwapBooleans_AreChecked()
    {
        var check = new AnnounceConfigChecks();
        Assert.Equal("World:Announce:HotSwap:WorldMessageOnFailure", Single(check, ("World:Announce:HotSwap:WorldMessageOnFailure", "1")).Key);
        Assert.Equal("'x' is not a boolean.", Single(check, ("World:Announce:HotSwap:WorldMessageIncludeReason", "x")).Problem);
    }

    [Fact]
    public void Announce_ReportsEveryBadKeyAtOnce()
    {
        ConfigIssue[] issues = [.. new AnnounceConfigChecks().Check(Config(("World:Announce:Enabled", "no"), ("World:Announce:Faults:RateBurst", "-5"), ("World:Announce:Faults:GmNotify", "?")))];
        Assert.Equal(3, issues.Length);
    }

    [Fact]
    public void Resilience_BadBoolean_And_QueryTimeoutRange()
    {
        var check = new ResilienceConfigChecks();
        ConfigIssue b = Single(check, ("Resilience:Database:Enabled", "nah"));
        Assert.Equal("'nah' is not a boolean.", b.Problem);
        Assert.Equal("use true or false", b.Fix);

        Assert.Empty(check.Check(Config(("Resilience:Database:QueryTimeoutMs", "0"))));
        Assert.Empty(check.Check(Config(("Resilience:Database:QueryTimeoutMs", "600000"))));
        ConfigIssue over = Single(check, ("Resilience:Database:QueryTimeoutMs", "600001"));
        Assert.Equal("Resilience:Database:QueryTimeoutMs", over.Key);
        Assert.Equal("600001 is outside 0..600000.", over.Problem);
        Assert.Equal("0 to disable, or 1..600000 milliseconds (default 5000)", over.Fix);

        ConfigIssue nan = Single(check, ("Resilience:Database:QueryTimeoutMs", "soon"));
        Assert.Equal("'soon' is not a whole number.", nan.Problem);
        Assert.Equal("0 to disable, or 1..600000 milliseconds (default 5000)", nan.Fix);
    }

    [Fact]
    public void Resilience_MinimumThroughput_LowerBoundIsOne()
    {
        var check = new ResilienceConfigChecks();
        Assert.Empty(check.Check(Config(("Resilience:Database:Breaker:MinimumThroughput", "1"))));
        ConfigIssue i = Single(check, ("Resilience:Database:Breaker:MinimumThroughput", "0"));
        Assert.Equal($"0 is outside 1..{int.MaxValue}.", i.Problem);
        Assert.Equal("at least 1 call (default 10)", i.Fix);
    }

    [Fact]
    public void Resilience_BreakerWithNoTripCondition_IsAnError()
    {
        ConfigIssue i = Single(new ResilienceConfigChecks(), ("Resilience:Database:Breaker:FailureThreshold", "0"), ("Resilience:Database:Breaker:FailureRateThreshold", "0"));
        Assert.Equal("Resilience:Database:Breaker:FailureThreshold", i.Key);
        Assert.Equal("FailureThreshold and FailureRateThreshold are both 0, so the breaker could never open.", i.Problem);
        Assert.Equal("enable at least one trip condition", i.Fix);
    }

    [Fact]
    public void Resilience_FailureRateThreshold_FractionBoundariesAndMessages()
    {
        var check = new ResilienceConfigChecks();
        Assert.Empty(check.Check(Config(("Resilience:Database:Breaker:FailureRateThreshold", "1"))));
        Assert.Empty(check.Check(Config(("Resilience:Database:Breaker:FailureRateThreshold", "0.5"))));
        const string fix = "use a fraction between 0 (disabled) and 1, e.g. 0.5";

        ConfigIssue high = Single(check, ("Resilience:Database:Breaker:FailureRateThreshold", "1.5"));
        Assert.Equal("1.5 is outside 0..1.", high.Problem);
        Assert.Equal(fix, high.Fix);
        Assert.Equal("-0.1 is outside 0..1.", Single(check, ("Resilience:Database:Breaker:FailureRateThreshold", "-0.1")).Problem);

        ConfigIssue nan = Single(check, ("Resilience:Database:Breaker:FailureRateThreshold", "half"));
        Assert.Equal("'half' is not a number.", nan.Problem);
        Assert.Equal(fix, nan.Fix);
        Assert.Equal("'NaN' is not a number.", Single(check, ("Resilience:Database:Breaker:FailureRateThreshold", "NaN")).Problem);
    }

    [Fact]
    public void Resilience_BootstrapMaxDelayBelowBaseDelay_IsAnError_AndEqualIsFine()
    {
        var check = new ResilienceConfigChecks();
        Assert.Empty(check.Check(Config(("Resilience:Database:Bootstrap:BaseDelayMs", "500"), ("Resilience:Database:Bootstrap:MaxDelayMs", "500"))));
        ConfigIssue i = Single(check, ("Resilience:Database:Bootstrap:BaseDelayMs", "500"), ("Resilience:Database:Bootstrap:MaxDelayMs", "499"));
        Assert.Equal("Resilience:Database:Bootstrap:MaxDelayMs", i.Key);
        Assert.Equal("499 is below BaseDelayMs (500).", i.Problem);
        Assert.Equal("use a cap at least as large as the base delay", i.Fix);
    }

    [Fact]
    public void Resilience_EmptyConfiguration_ReportsNothing()
        => Assert.Empty(new ResilienceConfigChecks().Check(Config()));

    [Fact]
    public void NullConfiguration_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AnnounceConfigChecks().Check(null!).ToList());
        Assert.Throws<ArgumentNullException>(() => new ResilienceConfigChecks().Check(null!).ToList());
    }
}

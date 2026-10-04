using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Resilience;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.Realm.Tests.Resilience;

public sealed class ResilienceOptionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    [Fact]
    public void Defaults_AreValid_AndMatchTheDocumentedValues()
    {
        var options = new ResilienceOptions();
        Assert.Empty(options.Validate());
        Assert.True(options.Database.Enabled);
        Assert.Equal(5000, options.Database.QueryTimeoutMs);
        Assert.Equal(5, options.Database.Breaker.FailureThreshold);
        Assert.Equal(0.5, options.Database.Breaker.FailureRateThreshold);
        Assert.Equal(10, options.Database.Breaker.MinimumThroughput);
        Assert.Equal(10_000, options.Database.Breaker.SamplingWindowMs);
        Assert.Equal(10_000, options.Database.Breaker.OpenDurationMs);
        Assert.Equal(1, options.Database.Breaker.HalfOpenMaxProbes);
        Assert.Equal(0, options.Database.Bulkhead.MaxConcurrency);
        Assert.Equal(64, options.Database.Bulkhead.MaxQueue);
        Assert.Equal(5, options.Database.Bootstrap.MaxAttempts);
        Assert.Equal(500, options.Database.Bootstrap.BaseDelayMs);
        Assert.Equal(5000, options.Database.Bootstrap.MaxDelayMs);
        Assert.Equal(0, options.Database.Bootstrap.MaxTotalDurationMs);

        // The runtime options built from them are accepted by the primitives.
        _ = new CircuitBreaker(options.Database.Breaker.ToOptions("x"));
        _ = new RetryPolicy(options.Database.Bootstrap.ToOptions());
        Assert.Null(options.Database.Bulkhead.ToOptions("x"));
    }

    [Fact]
    public void Validate_NamesEveryBadKey()
    {
        var options = new ResilienceOptions();
        options.Database.QueryTimeoutMs = -1;
        options.Database.Breaker.FailureThreshold = 0;
        options.Database.Breaker.FailureRateThreshold = 0;
        options.Database.Breaker.OpenDurationMs = 0;
        options.Database.Bulkhead.MaxQueue = -5;
        options.Database.Bootstrap.MaxAttempts = 0;
        options.Database.Bootstrap.BaseDelayMs = 1000;
        options.Database.Bootstrap.MaxDelayMs = 10;

        IReadOnlyList<string> problems = options.Validate();
        Assert.Contains(problems, p => p.StartsWith("Resilience:Database:QueryTimeoutMs", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Resilience:Database:Breaker:FailureThreshold", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Resilience:Database:Breaker:OpenDurationMs", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Resilience:Database:Bulkhead:MaxQueue", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Resilience:Database:Bootstrap:MaxAttempts", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Resilience:Database:Bootstrap:MaxDelayMs", StringComparison.Ordinal));
        Assert.Equal(6, problems.Count);
    }

    [Fact]
    public void ConfigChecks_EmptySectionHasNoIssues()
    {
        Assert.Empty(new ResilienceConfigChecks().Check(Config()));
    }

    [Fact]
    public void ConfigChecks_ReportEveryBadRawValueWithItsKey()
    {
        IConfiguration config = Config(
            ("Resilience:Database:Enabled", "maybe"),
            ("Resilience:Database:QueryTimeoutMs", "abc"),
            ("Resilience:Database:Breaker:FailureThreshold", "0"),
            ("Resilience:Database:Breaker:FailureRateThreshold", "0"),
            ("Resilience:Database:Breaker:MinimumThroughput", "0"),
            ("Resilience:Database:Breaker:HalfOpenMaxProbes", "-1"),
            ("Resilience:Database:Bootstrap:BaseDelayMs", "2000"),
            ("Resilience:Database:Bootstrap:MaxDelayMs", "100"));

        List<ConfigIssue> issues = [.. new ResilienceConfigChecks().Check(config)];
        Assert.All(issues, i => Assert.Equal(ConfigSeverity.Error, i.Severity));
        Assert.Equal(
            [
                "Resilience:Database:Bootstrap:MaxDelayMs",
                "Resilience:Database:Breaker:FailureThreshold",
                "Resilience:Database:Breaker:HalfOpenMaxProbes",
                "Resilience:Database:Breaker:MinimumThroughput",
                "Resilience:Database:Enabled",
                "Resilience:Database:QueryTimeoutMs",
            ],
            issues.Select(i => i.Key).Order(StringComparer.Ordinal));
        Assert.Contains(issues, i => i.EnvironmentVariable == "Resilience__Database__QueryTimeoutMs");
    }

    [Fact]
    public void ConfigChecks_AcceptAFullyValidSection()
    {
        IConfiguration config = Config(
            ("Resilience:Database:Enabled", "true"),
            ("Resilience:Database:QueryTimeoutMs", "2000"),
            ("Resilience:Database:Breaker:FailureThreshold", "3"),
            ("Resilience:Database:Breaker:FailureRateThreshold", "0.25"),
            ("Resilience:Database:Breaker:MinimumThroughput", "20"),
            ("Resilience:Database:Breaker:SamplingWindowMs", "30000"),
            ("Resilience:Database:Breaker:OpenDurationMs", "15000"),
            ("Resilience:Database:Breaker:HalfOpenMaxProbes", "2"),
            ("Resilience:Database:Bulkhead:MaxConcurrency", "16"),
            ("Resilience:Database:Bulkhead:MaxQueue", "32"),
            ("Resilience:Database:Bootstrap:MaxAttempts", "10"),
            ("Resilience:Database:Bootstrap:BaseDelayMs", "250"),
            ("Resilience:Database:Bootstrap:MaxDelayMs", "4000"),
            ("Resilience:Database:Bootstrap:MaxTotalDurationMs", "60000"));
        Assert.Empty(new ResilienceConfigChecks().Check(config));
    }
}

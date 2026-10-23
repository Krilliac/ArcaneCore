using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.Kernel.Ops.Watchdog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Configuration.Validation;

/// <summary>Pins the problem and fix text, severities and boundaries of the Ops:Metrics and Ops:Watchdog validators.</summary>
public sealed class OpsOptionsValidationMessageTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static ConfigIssue Single(IConfigCheck check, params (string Key, string Value)[] values)
        => Assert.Single(check.Check(Config(values)));

    private static void AssertSingleFailureStartsWith(ValidateOptionsResult result, string expectedPrefix)
    {
        Assert.True(result.Failed);
        Assert.StartsWith(expectedPrefix, Assert.Single(result.Failures!), StringComparison.Ordinal);
    }

    [Fact]
    public void Metrics_Interval_BoundariesAndMessages()
    {
        foreach (int ok in new[] { 1, 3600 })
        {
            Assert.Empty(MetricsOptionsValidation.Problems(new MetricsOptions { Enabled = false, OtlpIntervalSeconds = ok, MapSampleIntervalSeconds = ok }));
        }

        foreach (int bad in new[] { 0, 3601 })
        {
            ConfigIssue i = Assert.Single(MetricsOptionsValidation.Problems(new MetricsOptions { Enabled = false, OtlpIntervalSeconds = bad }));
            Assert.Equal(ConfigSeverity.Error, i.Severity);
            Assert.Equal("Ops:Metrics:OtlpIntervalSeconds", i.Key);
            Assert.Equal($"{bad} is out of range.", i.Problem);
            Assert.Equal("use 1-3600 seconds", i.Fix);
        }

        Assert.Equal("Ops:Metrics:MapSampleIntervalSeconds", Assert.Single(MetricsOptionsValidation.Problems(new MetricsOptions { Enabled = false, MapSampleIntervalSeconds = 0 })).Key);
    }

    [Fact]
    public void Metrics_UndefinedExporter_IsReported()
    {
        ConfigIssue i = Assert.Single(MetricsOptionsValidation.Problems(new MetricsOptions { Enabled = false, Exporter = (MetricsExporter)99 }));
        Assert.Equal("Ops:Metrics:Exporter", i.Key);
        Assert.Equal("'99' is not an exporter.", i.Problem);
        Assert.Equal("use Prometheus, Otlp, Both", i.Fix);
    }

    [Fact]
    public void Metrics_BadPrefix_And_BadOtlpEndpoint_OnlyWhenEnabled()
    {
        var disabled = new MetricsOptions { Enabled = false, Exporter = MetricsExporter.Both, PrometheusPrefix = "nope", OtlpEndpoint = "ftp://x" };
        Assert.Empty(MetricsOptionsValidation.Problems(disabled));

        var enabled = new MetricsOptions { Enabled = true, Exporter = MetricsExporter.Both, PrometheusPrefix = "http://+:9464/metrics", OtlpEndpoint = "ftp://x" };
        List<ConfigIssue> issues = MetricsOptionsValidation.Problems(enabled);
        Assert.Equal(2, issues.Count);
        ConfigIssue prefix = issues.Single(i => i.Key == "Ops:Metrics:PrometheusPrefix");
        Assert.Equal("'http://+:9464/metrics' is not a listener prefix.", prefix.Problem);
        Assert.Equal("use an http:// prefix ending in /, for example http://+:9464/metrics/", prefix.Fix);
        ConfigIssue otlp = issues.Single(i => i.Key == "Ops:Metrics:OtlpEndpoint");
        Assert.Equal("'ftp://x' is not an http(s) URL.", otlp.Problem);
        Assert.Equal("use the collector's OTLP/HTTP metrics URL, for example http://otel-collector:4318/v1/metrics", otlp.Fix);
    }

    [Fact]
    public void Metrics_Check_ParsesRawStrings_AndAbsentSectionIsEmpty()
    {
        var check = new MetricsOptionsValidation();
        Assert.Empty(check.Check(Config()));

        ConfigIssue[] issues = [.. check.Check(Config(("Ops:Metrics:Enabled", "yes"), ("Ops:Metrics:OtlpIntervalSeconds", "soon"), ("Ops:Metrics:Exporter", "Smoke")))];
        Assert.Equal(3, issues.Length);
        Assert.Contains(issues, i => i.Key == "Ops:Metrics:Enabled" && i.Problem == "'yes' is not true or false." && i.Fix == "use true or false");
        Assert.Contains(issues, i => i.Key == "Ops:Metrics:OtlpIntervalSeconds" && i.Problem == "'soon' is not a whole number." && i.Fix == "use a whole number");
        Assert.Contains(issues, i => i.Key == "Ops:Metrics:Exporter" && i.Problem == "'Smoke' is not one of the allowed values." && i.Fix == "use Prometheus, Otlp, Both");

        // Parseable values flow through the same rules as the bound options.
        Assert.Equal("0 is out of range.", Single(check, ("Ops:Metrics:OtlpIntervalSeconds", "0")).Problem);
    }

    [Fact]
    public void Metrics_Validate_ReturnsFormattedErrors()
    {
        var validation = new MetricsOptionsValidation();
        AssertSingleFailureStartsWith(
            validation.Validate(null, new MetricsOptions { Enabled = false, OtlpIntervalSeconds = 0 }),
            "ERROR Ops:Metrics:OtlpIntervalSeconds: 0 is out of range. Fix: use 1-3600 seconds (");
        Assert.True(validation.Validate(null, new MetricsOptions { Enabled = false }).Succeeded);
    }

    [Fact]
    public void Watchdog_CheckInterval_BoundariesAndMessages()
    {
        var check = new WatchdogOptionsValidation();
        Assert.Empty(check.Check(Config(("Ops:Watchdog:CheckIntervalMs", "100"))));
        Assert.Empty(check.Check(Config(("Ops:Watchdog:CheckIntervalMs", "60000"))));
        ConfigIssue low = Single(check, ("Ops:Watchdog:CheckIntervalMs", "99"));
        Assert.Equal(ConfigSeverity.Error, low.Severity);
        Assert.Equal("Ops:Watchdog:CheckIntervalMs", low.Key);
        Assert.Equal("99 is out of range.", low.Problem);
        Assert.Equal("use 100-60000 milliseconds", low.Fix);
        Assert.Equal("60001 is out of range.", Single(check, ("Ops:Watchdog:CheckIntervalMs", "60001")).Problem);
    }

    [Fact]
    public void Watchdog_NonNegative_Message()
    {
        var check = new WatchdogOptionsValidation();
        ConfigIssue i = Single(check, ("Ops:Watchdog:TickMonitor:BudgetMs", "-1"));
        Assert.Equal("Ops:Watchdog:TickMonitor:BudgetMs", i.Key);
        Assert.Equal("-1 is negative.", i.Problem);
        Assert.Equal("use 0 to disable, or a positive value", i.Fix);
        Assert.Empty(check.Check(Config(("Ops:Watchdog:TickMonitor:BudgetMs", "0"))));
    }

    [Fact]
    public void Watchdog_HangMsMustExceedBudgetMs_WhenBothSet()
    {
        var check = new WatchdogOptionsValidation();
        ConfigIssue i = Single(check, ("Ops:Watchdog:TickMonitor:BudgetMs", "500"), ("Ops:Watchdog:TickMonitor:HangMs", "500"));
        Assert.Equal("Ops:Watchdog:TickMonitor:HangMs", i.Key);
        Assert.Equal("500 is not above BudgetMs (500).", i.Problem);
        Assert.Equal("a hang must be longer than an overrun; use a larger HangMs or 0 to disable hang detection", i.Fix);
        Assert.Empty(check.Check(Config(("Ops:Watchdog:TickMonitor:BudgetMs", "500"), ("Ops:Watchdog:TickMonitor:HangMs", "501"))));
        Assert.Empty(check.Check(Config(("Ops:Watchdog:TickMonitor:BudgetMs", "500"), ("Ops:Watchdog:TickMonitor:HangMs", "0"))));
    }

    [Fact]
    public void Watchdog_ActionHangMsBelowHangMs_IsAnError_AndEqualIsFine()
    {
        var check = new WatchdogOptionsValidation();
        ConfigIssue i = Single(check, ("Ops:Watchdog:TickMonitor:BudgetMs", "0"), ("Ops:Watchdog:TickMonitor:HangMs", "500"), ("Ops:Watchdog:TickMonitor:ActionHangMs", "499"));
        Assert.Equal("Ops:Watchdog:TickMonitor:ActionHangMs", i.Key);
        Assert.Equal("499 is below HangMs (500).", i.Problem);
        Assert.Equal("the fatal hang action threshold must be at least HangMs, or 0 to disable the action", i.Fix);
        Assert.Empty(check.Check(Config(("Ops:Watchdog:TickMonitor:BudgetMs", "0"), ("Ops:Watchdog:TickMonitor:HangMs", "500"), ("Ops:Watchdog:TickMonitor:ActionHangMs", "500"))));
    }

    [Fact]
    public void Watchdog_ThreadPoolCriticalMustExceedWarn()
    {
        ConfigIssue i = Single(new WatchdogOptionsValidation(), ("Ops:Watchdog:ThreadPool:WarnDelayMs", "200"), ("Ops:Watchdog:ThreadPool:CriticalDelayMs", "200"));
        Assert.Equal("Ops:Watchdog:ThreadPool:CriticalDelayMs", i.Key);
        Assert.Equal("200 is not above WarnDelayMs (200).", i.Problem);
        Assert.Equal("use a larger critical delay, or 0 to disable the critical line", i.Fix);
    }

    [Fact]
    public void Watchdog_ActionWithoutThreshold_IsAWarning_NotAnError()
    {
        ConfigIssue i = Single(new WatchdogOptionsValidation(), ("Ops:Watchdog:Memory:Action", "collect"));
        Assert.Equal(ConfigSeverity.Warning, i.Severity);
        Assert.Equal("Ops:Watchdog:Memory:ActionHeapBytes", i.Key);
        Assert.Equal("Memory:Action is Collect but ActionHeapBytes is 0, so the action never fires.", i.Problem);
        Assert.Equal("set the heap size in bytes at which it fires, or set Action to None", i.Fix);

        // The options validator fails on a warning too (it does not filter by severity).
        var options = new WatchdogOptions();
        options.Memory.Action = MemoryPressureAction.Collect;
        AssertSingleFailureStartsWith(new WatchdogOptionsValidation().Validate(null, options), "WARNING Ops:Watchdog:Memory:ActionHeapBytes: ");
    }

    [Fact]
    public void Watchdog_Check_ParsesRawStrings_AndReportsEveryBadValue()
    {
        var check = new WatchdogOptionsValidation();
        ConfigIssue[] issues = [.. check.Check(Config(
            ("Ops:Watchdog:Enabled", "yes"),
            ("Ops:Watchdog:CheckIntervalMs", "fast"),
            ("Ops:Watchdog:Memory:GrowthLogBytes", "lots"),
            ("Ops:Watchdog:Memory:Action", "Explode")))];
        Assert.Equal(4, issues.Length);
        Assert.Contains(issues, i => i.Key == "Ops:Watchdog:Enabled" && i.Problem == "'yes' is not true or false." && i.Fix == "use true or false");
        Assert.Contains(issues, i => i.Key == "Ops:Watchdog:CheckIntervalMs" && i.Problem == "'fast' is not a whole number." && i.Fix == "use a whole number");
        Assert.Contains(issues, i => i.Key == "Ops:Watchdog:Memory:GrowthLogBytes" && i.Problem == "'lots' is not a whole number." && i.Fix == "use a whole number of bytes");
        Assert.Contains(issues, i => i.Key == "Ops:Watchdog:Memory:Action" && i.Problem == "'Explode' is not one of the allowed values." && i.Fix == "use None, Log, Collect, Stop");
        Assert.Empty(check.Check(Config()));
    }
}

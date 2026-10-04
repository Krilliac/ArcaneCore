using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Ops.Watchdog;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using ArcaneCore.Kernel.Ops.Watchdog.Heartbeat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Realm.Tests.Watchdog;

public sealed class WatchdogHostTests
{
    private readonly FakeClock _clock = new();
    private readonly RecordingLogger<WatchdogHost> _log = new();
    private readonly CounterRegistry _counters = WatchdogTestSupport.NewRegistry();

    private sealed class Monitor(string name, Action<long>? check = null) : IWatchdogMonitor
    {
        public List<string> Events { get; } = [];

        public string Name => name;

        public void Start() => Events.Add("start");

        public void Check(long nowMicros)
        {
            Events.Add("check");
            check?.Invoke(nowMicros);
        }

        public void Stop() => Events.Add("stop");
    }

    [Fact]
    public void RunOnce_RunsEveryMonitor_AndAFaultingOneIsLoggedRateLimitedWithoutStoppingTheOthers()
    {
        var first = new Monitor("first", _ => throw new InvalidOperationException("broken"));
        var second = new Monitor("second");
        var host = new WatchdogHost(new WatchdogOptions(), [first, second], _clock, _counters, _log);
        for (int i = 0; i < 5; i++)
        {
            host.RunOnce(_clock.NowMicros);
            _clock.AdvanceMs(1_000);
        }

        Assert.Equal(5, second.Events.Count(e => e == "check"));
        Assert.Equal(5, host.Checks);
        Assert.Equal(5, _counters.Find("watchdog.monitor_faults")!.Value);
        LogLine fault = Assert.Single(_log.Of(WatchdogEvents.MonitorFault));
        Assert.Equal(LogLevel.Error, fault.Level);
        Assert.Contains("first failed", fault.Message);
        Assert.IsType<InvalidOperationException>(fault.Exception);
        _clock.AdvanceMs(60_000);
        host.RunOnce(_clock.NowMicros);
        Assert.Equal(2, _log.CountOf(WatchdogEvents.MonitorFault));
        Assert.Contains("(4 suppressed)", _log.Of(WatchdogEvents.MonitorFault).Last().Message);
    }

    [Fact]
    public async Task StartAndStop_RunTheThread_AndCallStartAndStopOnEveryMonitor()
    {
        var monitor = new Monitor("m");
        var options = new WatchdogOptions { CheckIntervalMs = 100 };
        var host = new WatchdogHost(options, [monitor], WatchdogClock.System, _counters, _log);
        await host.StartAsync(CancellationToken.None);
        Assert.True(host.IsRunning);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.Checks < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await host.StopAsync(CancellationToken.None);
        Assert.False(host.IsRunning);
        Assert.True(host.Checks >= 2, $"only {host.Checks} checks ran");
        Assert.Equal("start", monitor.Events[0]);
        Assert.Equal("stop", monitor.Events[^1]);
        Assert.Contains(_log.Lines, l => l.Event.Id == WatchdogEvents.Started.Id && l.Message.Contains("watchdog started: m every 100 ms", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disabled_StartsNoThread_AndRunsNoMonitor()
    {
        var monitor = new Monitor("m");
        var host = new WatchdogHost(new WatchdogOptions { Enabled = false, CheckIntervalMs = 100 }, [monitor], WatchdogClock.System, _counters, _log);
        await host.StartAsync(CancellationToken.None);
        await Task.Delay(250);
        await host.StopAsync(CancellationToken.None);
        Assert.False(host.IsRunning);
        Assert.Empty(monitor.Events);
        Assert.Equal(0, host.Checks);
        Assert.Contains(_log.Lines, l => l.Message.Contains("watchdog disabled", StringComparison.Ordinal));
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    [Fact]
    public void AddRealmWatchdog_RegistersTheHostAndTheFourSharedMonitors_Once()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRealmWatchdog(Config(("Ops:Watchdog:CheckIntervalMs", "250")));
        services.AddRealmWatchdog(Config()); // idempotent
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(250, provider.GetRequiredService<WatchdogOptions>().CheckIntervalMs);
        IWatchdogMonitor[] monitors = [.. provider.GetServices<IWatchdogMonitor>()];
        Assert.Equal(["memory", "threadpool", "heartbeat", "counters"], monitors.Select(m => m.Name));
        Assert.Single(provider.GetServices<IHostedService>().OfType<WatchdogHost>());
        Assert.Same(CounterRegistry.Default, provider.GetRequiredService<CounterRegistry>());
        Assert.Same(WatchdogClock.System, provider.GetRequiredService<WatchdogClock>());
        Assert.Empty(provider.GetServices<ILivenessSource>());
    }

    [Fact]
    public void AddTickWatchdog_AddsTheTickMonitorAsMonitorAndLivenessSource()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<WatchdogClock>(_clock);
        services.AddTickWatchdog();
        using ServiceProvider provider = services.BuildServiceProvider();
        TickMonitor tick = provider.GetRequiredService<TickMonitor>();
        Assert.Contains(tick, provider.GetServices<IWatchdogMonitor>());
        Assert.Same(tick, Assert.Single(provider.GetServices<ILivenessSource>()));
        Assert.Same(_clock, provider.GetRequiredService<WatchdogClock>());
        Assert.Equal(5, provider.GetServices<IWatchdogMonitor>().Count());
        Assert.IsType<HeartbeatWriter>(provider.GetRequiredService<HeartbeatWriter>());
    }

    [Fact]
    public void InvalidOptions_FailValidation_AtResolveTime()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRealmWatchdog(Config(("Ops:Watchdog:CheckIntervalMs", "5")));
        using ServiceProvider provider = services.BuildServiceProvider();
        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<WatchdogOptions>>().Value);
        Assert.Contains(ex.Failures, f => f.Contains("Ops:Watchdog:CheckIntervalMs", StringComparison.Ordinal) && f.Contains("Ops__Watchdog__CheckIntervalMs", StringComparison.Ordinal));
    }

    [Fact]
    public void Validation_Defaults_PassAndTheRulesCatchEachBadValue()
    {
        Assert.Empty(WatchdogOptionsValidation.Problems(new WatchdogOptions()));

        var bad = new WatchdogOptions { CheckIntervalMs = 50 };
        bad.TickMonitor.RingCapacity = 1;
        bad.TickMonitor.BudgetMs = 500;
        bad.TickMonitor.HangMs = 400;
        bad.Memory.WarnLoadPercent = 120;
        bad.Memory.Action = MemoryPressureAction.Stop; // with ActionHeapBytes 0: a warning
        bad.ThreadPool.WarnDelayMs = 300;
        bad.ThreadPool.CriticalDelayMs = 300;
        bad.Heartbeat.Mode = HeartbeatMode.File;
        bad.Counters.DumpIntervalSeconds = -1;
        List<ConfigIssue> issues = WatchdogOptionsValidation.Problems(bad);
        string[] keys = [.. issues.Select(i => i.Key)];
        Assert.Contains("Ops:Watchdog:CheckIntervalMs", keys);
        Assert.Contains("Ops:Watchdog:TickMonitor:RingCapacity", keys);
        Assert.Contains("Ops:Watchdog:TickMonitor:HangMs", keys);
        Assert.Contains("Ops:Watchdog:Memory:WarnLoadPercent", keys);
        Assert.Contains("Ops:Watchdog:ThreadPool:CriticalDelayMs", keys);
        Assert.Contains("Ops:Watchdog:Heartbeat:FilePath", keys);
        Assert.Contains("Ops:Watchdog:Counters:DumpIntervalSeconds", keys);
        Assert.Equal(ConfigSeverity.Warning, Assert.Single(issues, i => i.Key == "Ops:Watchdog:Memory:ActionHeapBytes").Severity);
        Assert.Equal(8, issues.Count);
    }

    [Fact]
    public void ConfigCheck_ParsesRawStrings_ListsEveryBadValue_AndAcceptsAnAbsentSection()
    {
        var check = new WatchdogOptionsValidation();
        Assert.Empty(check.Check(Config()));
        Assert.Empty(check.Check(Config(("Ops:Watchdog:Enabled", "true"), ("Ops:Watchdog:Memory:Action", "collect"), ("Ops:Watchdog:Memory:ActionHeapBytes", "1073741824"))));

        ConfigIssue[] issues = [.. check.Check(Config(
            ("Ops:Watchdog:Enabled", "yes"),
            ("Ops:Watchdog:TickMonitor:HangMs", "two"),
            ("Ops:Watchdog:Memory:Action", "Explode"),
            ("Ops:Watchdog:Memory:GrowthLogBytes", "lots"),
            ("Ops:Watchdog:Heartbeat:Mode", "Pigeon")))];
        Assert.Equal(5, issues.Length);
        Assert.All(issues, i => Assert.Equal(ConfigSeverity.Error, i.Severity));
        Assert.Contains(issues, i => i.Key == "Ops:Watchdog:Enabled" && i.Problem.Contains("'yes' is not true or false", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Key == "Ops:Watchdog:Heartbeat:Mode" && i.Fix.Contains("Systemd", StringComparison.Ordinal));

        // Values that parse but break a rule go through the same rules as the bound options.
        ConfigIssue issue = Assert.Single(check.Check(Config(("Ops:Watchdog:ThreadPool:ProbeIntervalSeconds", "0"))));
        Assert.Equal("Ops:Watchdog:ThreadPool:ProbeIntervalSeconds", issue.Key);
    }
}

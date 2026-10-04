using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Realm.Tests.Logging;

/// <summary>Configuration checks (fail closed), binding through the service collection, and the live reload of the console mode.</summary>
public sealed class LoggingConfigTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void Check_ListsEveryBadValueWithKeyAndFix()
    {
        IConfiguration configuration = Config(
            ("Logging:ArcaneCore:Console:Mode", "Rainbow"),
            ("Logging:ArcaneCore:Console:QueueCapacity", "0"),
            ("Logging:ArcaneCore:Timestamps", "Mars"),
            ("Logging:ArcaneCore:IncludeScopes", "maybe"),
            ("Logging:ArcaneCore:File:Enabled", "true"),
            ("Logging:ArcaneCore:File:Path", ""),
            ("Logging:ArcaneCore:File:RollSizeMb", "-1"),
            ("Logging:ArcaneCore:File:Retain", "many"),
            ("Logging:ArcaneCore:Json:QueueCapacity", "99999999"));

        List<ConfigIssue> issues = [.. new LoggingConfigChecks().Check(configuration)];

        string[] keys = [.. issues.Select(i => i.Key).Order(StringComparer.Ordinal)];
        Assert.Equal(
            [
                "Logging:ArcaneCore:Console:Mode",
                "Logging:ArcaneCore:Console:QueueCapacity",
                "Logging:ArcaneCore:File:Path",
                "Logging:ArcaneCore:File:Retain",
                "Logging:ArcaneCore:File:RollSizeMb",
                "Logging:ArcaneCore:IncludeScopes",
                "Logging:ArcaneCore:Json:QueueCapacity",
                "Logging:ArcaneCore:Timestamps",
            ],
            keys);
        Assert.All(issues, i => Assert.Equal(ConfigSeverity.Error, i.Severity));
        Assert.Contains(issues, i => i.Key == "Logging:ArcaneCore:Console:Mode" && i.Fix.Contains("Color, Plain or Off", StringComparison.Ordinal));
        Assert.Equal("Logging__ArcaneCore__Console__Mode", issues.Single(i => i.Key == "Logging:ArcaneCore:Console:Mode").EnvironmentVariable);
    }

    [Fact]
    public void Check_AcceptsTheShippedDefaults_AndADisabledSinkWithAnEmptyPath()
    {
        Assert.Empty(new LoggingConfigChecks().Check(Config()));
        Assert.Empty(new LoggingConfigChecks().Check(Config(
            ("Logging:ArcaneCore:Console:Mode", "plain"),
            ("Logging:ArcaneCore:File:Enabled", "false"),
            ("Logging:ArcaneCore:File:Path", ""),
            ("Logging:ArcaneCore:Json:Enabled", "true"),
            ("Logging:ArcaneCore:Json:Path", "logs/x.jsonl"),
            ("Logging:ArcaneCore:Timestamps", "Local"))));
    }

    [Fact]
    public void ThrowIfInvalid_RejectsBoundOptions_WithEveryFailure()
    {
        var options = new ArcaneLoggingOptions();
        options.File.Enabled = true;
        options.File.Path = "logs/same.log";
        options.Json.Enabled = true;
        options.Json.Path = "logs/same.log";
        options.Json.Retain = -3;
        options.Console.QueueCapacity = 0;

        var ex = Assert.Throws<OptionsValidationException>(() => LoggingConfigChecks.ThrowIfInvalid(options));
        Assert.Equal(3, ex.Failures.Count());
        Assert.Contains(ex.Failures, f => f.Contains("must differ", StringComparison.Ordinal));
        Assert.Contains(ex.Failures, f => f.Contains("Json:Retain", StringComparison.Ordinal));
        Assert.Contains(ex.Failures, f => f.Contains("Console:QueueCapacity", StringComparison.Ordinal));
        LoggingConfigChecks.ThrowIfInvalid(new ArcaneLoggingOptions());
    }

    [Fact]
    public void AddArcaneCoreLogging_RefusesAnInvalidSection_BeforeTheHostIsBuilt()
    {
        var services = new ServiceCollection();
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddArcaneCoreLogging(Config(("Logging:ArcaneCore:Console:Mode", "Loud"))));
        Assert.Contains("Logging:ArcaneCore:Console:Mode", ex.Message, StringComparison.Ordinal);
        Assert.Contains("refuses to start", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddArcaneCoreLogging_ReplacesOtherProviders_BindsOptions_AndHonoursLogLevelRules()
    {
        IConfiguration configuration = Config(
            ("Logging:LogLevel:Default", "Warning"),
            ("Logging:LogLevel:Chatty", "None"),
            ("Logging:ArcaneCore:Console:Mode", "Off"),
            ("Logging:ArcaneCore:Timestamps", "Local"),
            ("Logging:ArcaneCore:IncludeScopes", "false"));
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConfiguration(configuration.GetSection("Logging")).AddProvider(new CountingProvider()));
        services.AddArcaneCoreLogging(configuration);
        using ServiceProvider sp = services.BuildServiceProvider();

        ILoggerProvider[] providers = [.. sp.GetServices<ILoggerProvider>()];
        ArcaneLoggerProvider provider = Assert.IsType<ArcaneLoggerProvider>(Assert.Single(providers));
        ArcaneLoggingOptions options = sp.GetRequiredService<IOptionsMonitor<ArcaneLoggingOptions>>().CurrentValue;
        Assert.Equal(ConsoleMode.Off, options.Console.Mode);
        Assert.Equal(TimestampKind.Local, options.Timestamps);
        Assert.False(options.IncludeScopes);
        Assert.False(provider.IncludeScopes);
        Assert.Equal(DateTimeKind.Local, provider.Now().Kind);

        // the factory's rules still decide: an Information line to a Warning-default category never reaches the provider
        ILoggerFactory factory = sp.GetRequiredService<ILoggerFactory>();
        Assert.False(factory.CreateLogger("Any").IsEnabled(LogLevel.Information));
        Assert.True(factory.CreateLogger("Any").IsEnabled(LogLevel.Warning));
        Assert.False(factory.CreateLogger("Chatty").IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void Reload_AppliesConsoleModeTimestampsAndScopes_AndReportsRestartOnlyKeys()
    {
        var output = new StringWriter();
        var initial = new ArcaneLoggingOptions();
        initial.Console.Mode = ConsoleMode.Color;
        var monitor = new FakeMonitor(initial);
        LogSink[] sinks = [new TextLogSink("console", new TextWriterLineWriter(output), color: false)];
        using var provider = new ArcaneLoggerProvider(initial, colorAllowed: true, sinks, monitor);
        Assert.True(provider.ConsoleColor);

        var next = new ArcaneLoggingOptions { Timestamps = TimestampKind.Local, IncludeScopes = false };
        next.Console.Mode = ConsoleMode.Plain;
        next.File.Path = "elsewhere/world.log";
        next.File.Enabled = true;
        monitor.Change(next);

        Assert.False(provider.ConsoleColor);
        Assert.True(sinks[0].Enabled);
        Assert.False(provider.IncludeScopes);
        Assert.Equal(DateTimeKind.Local, provider.Now().Kind);
        string text = output.ToString();
        Assert.Contains("Logging:ArcaneCore:Console:Mode changed to Plain", text, StringComparison.Ordinal);
        Assert.Contains("Logging:ArcaneCore:File:Path option can't be changed at reload; still logs/arcanecore.log", text, StringComparison.Ordinal);
        Assert.Contains("Logging:ArcaneCore:File:Enabled option can't be changed at reload; still False", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Json:", text, StringComparison.Ordinal);

        var off = new ArcaneLoggingOptions();
        off.Console.Mode = ConsoleMode.Off;
        monitor.Change(off);
        Assert.False(sinks[0].Enabled);
        Assert.False(provider.ConsoleColor);

        // an invalid reload is rejected as a whole and the running settings stay
        var bad = new ArcaneLoggingOptions();
        bad.Console.Mode = ConsoleMode.Color;
        bad.Console.QueueCapacity = 0;
        monitor.Change(bad);
        Assert.False(sinks[0].Enabled);
    }

    private sealed class FakeMonitor(ArcaneLoggingOptions current) : IOptionsMonitor<ArcaneLoggingOptions>
    {
        private Action<ArcaneLoggingOptions, string?>? _listener;

        public ArcaneLoggingOptions CurrentValue { get; private set; } = current;

        public ArcaneLoggingOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcaneLoggingOptions, string?> listener)
        {
            _listener = listener;
            return null;
        }

        public void Change(ArcaneLoggingOptions next)
        {
            CurrentValue = next;
            _listener?.Invoke(next, Options.DefaultName);
        }
    }

    private sealed class CountingProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose()
        {
        }
    }
}

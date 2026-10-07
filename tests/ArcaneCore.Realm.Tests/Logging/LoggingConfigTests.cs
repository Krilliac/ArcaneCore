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

    /// <summary>
    /// The IConfiguration check (check-config, exit 78) and the bound-options check (provider construction and reload) are one rule
    /// set: a configuration that one rejects, the other rejects too. The text and JSON sinks sharing a file is the rule that used to
    /// live only in <see cref="LoggingConfigChecks.ThrowIfInvalid"/>, so check-config passed and the host then crashed building the
    /// provider; here both paths of the same path are tried, one spelled out in config and one from the sink's default.
    /// </summary>
    [Fact]
    public void Check_AndThrowIfInvalid_BothRejectTheTextAndJsonSinksOnOneFile()
    {
        (string Key, string Value)[] explicitSame =
        [
            ("Logging:ArcaneCore:File:Enabled", "true"),
            ("Logging:ArcaneCore:File:Path", "logs/world.log"),
            ("Logging:ArcaneCore:Json:Enabled", "true"),
            ("Logging:ArcaneCore:Json:Path", "logs/world.log"),
        ];
        (string Key, string Value)[] defaultSame =
        [
            ("Logging:ArcaneCore:File:Enabled", "true"),
            ("Logging:ArcaneCore:Json:Enabled", "true"),
            ("Logging:ArcaneCore:Json:Path", "logs/arcanecore.log"), // the text sink's default path
        ];
        (string Key, string Value)[] differ =
        [
            ("Logging:ArcaneCore:File:Enabled", "true"),
            ("Logging:ArcaneCore:File:Path", "logs/world.log"),
            ("Logging:ArcaneCore:Json:Enabled", "true"),
            ("Logging:ArcaneCore:Json:Path", "logs/world.jsonl"),
        ];
        (string Key, string Value)[] sameButOneDisabled =
        [
            ("Logging:ArcaneCore:File:Enabled", "false"),
            ("Logging:ArcaneCore:File:Path", "logs/world.log"),
            ("Logging:ArcaneCore:Json:Enabled", "true"),
            ("Logging:ArcaneCore:Json:Path", "logs/world.log"),
        ];

        foreach ((string Key, string Value)[] rejected in new[] { explicitSame, defaultSame })
        {
            IConfiguration configuration = Config(rejected);
            ConfigIssue issue = Assert.Single(new LoggingConfigChecks().Check(configuration));
            Assert.Equal(ConfigSeverity.Error, issue.Severity);
            Assert.Equal("Logging:ArcaneCore:Json:Path", issue.Key);
            Assert.Contains("must differ", issue.Problem, StringComparison.Ordinal);

            var ex = Assert.Throws<OptionsValidationException>(() => LoggingConfigChecks.ThrowIfInvalid(Bind(configuration)));
            string failure = Assert.Single(ex.Failures);
            Assert.Contains("Logging:ArcaneCore:Json:Path", failure, StringComparison.Ordinal);
            Assert.Contains("must differ", failure, StringComparison.Ordinal);
        }

        foreach ((string Key, string Value)[] accepted in new[] { differ, sameButOneDisabled })
        {
            IConfiguration configuration = Config(accepted);
            Assert.Empty(new LoggingConfigChecks().Check(configuration));
            LoggingConfigChecks.ThrowIfInvalid(Bind(configuration));
        }
    }

    /// <summary>Every rule the bound-options check knows is reported by the raw-configuration check with the same key, and vice versa.</summary>
    [Fact]
    public void Check_AndThrowIfInvalid_AgreeOnEveryRule()
    {
        IConfiguration configuration = Config(
            ("Logging:ArcaneCore:Console:QueueCapacity", "0"),
            ("Logging:ArcaneCore:File:Enabled", "true"),
            ("Logging:ArcaneCore:File:Path", "logs/"),
            ("Logging:ArcaneCore:File:RollSizeMb", "-1"),
            ("Logging:ArcaneCore:File:Retain", "-3"),
            ("Logging:ArcaneCore:File:QueueCapacity", "1000001"),
            ("Logging:ArcaneCore:Json:Enabled", "true"),
            ("Logging:ArcaneCore:Json:Path", " "),
            ("Logging:ArcaneCore:Json:QueueCapacity", "0"));

        string[] checkKeys = [.. new LoggingConfigChecks().Check(configuration).Select(i => i.Key).Order(StringComparer.Ordinal)];
        var ex = Assert.Throws<OptionsValidationException>(() => LoggingConfigChecks.ThrowIfInvalid(Bind(configuration)));
        string[] throwKeys = [.. ex.Failures.Select(f => f[..f.IndexOf(' ', StringComparison.Ordinal)]).Order(StringComparer.Ordinal)];

        Assert.Equal(7, checkKeys.Length);
        Assert.Equal(checkKeys, throwKeys);
    }

    private static ArcaneLoggingOptions Bind(IConfiguration configuration)
    {
        var options = new ArcaneLoggingOptions();
        configuration.GetSection(ArcaneLoggingOptions.SectionName).Bind(options);
        return options;
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

    /// <summary>
    /// The terminal's capability (a tty, NO_COLOR unset, VT accepted) is independent of the configured mode: a daemon started with
    /// <c>Console:Mode=Plain</c> or <c>Off</c> on a colour-capable terminal must render colour after a reload to <c>Color</c>, as the
    /// options' "live" promise says. The capability is probed once, and only when colour is first asked for.
    /// </summary>
    [Theory]
    [InlineData(ConsoleMode.Plain)]
    [InlineData(ConsoleMode.Off)]
    public void Reload_ToColor_RendersColour_WhenTheProcessStartedPlainOrOff(ConsoleMode start)
    {
        var output = new StringWriter();
        var initial = new ArcaneLoggingOptions();
        initial.Console.Mode = start;
        var monitor = new FakeMonitor(initial);
        LogSink[] sinks = [new TextLogSink("console", new TextWriterLineWriter(output), color: false)];
        int probes = 0;
        using var provider = new ArcaneLoggerProvider(monitor, () => { probes++; return true; }, sinks);
        Assert.False(provider.ConsoleColor);
        Assert.Equal(0, probes);

        var color = new ArcaneLoggingOptions();
        color.Console.Mode = ConsoleMode.Color;
        monitor.Change(color);

        Assert.True(provider.ConsoleColor);
        Assert.True(sinks[0].Enabled);
        Assert.Equal(1, probes);
        Assert.Contains("Logging:ArcaneCore:Console:Mode changed to Color.", output.ToString(), StringComparison.Ordinal);
        Assert.Contains('\u001b', output.ToString());

        // Color -> Plain -> Color: the verdict is reused, not probed again
        var plain = new ArcaneLoggingOptions();
        plain.Console.Mode = ConsoleMode.Plain;
        monitor.Change(plain);
        Assert.False(provider.ConsoleColor);
        monitor.Change(color);
        Assert.True(provider.ConsoleColor);
        Assert.Equal(1, probes);
    }

    [Fact]
    public void Reload_ToColor_StaysPlain_WhenTheTerminalRefusesColour()
    {
        var output = new StringWriter();
        var initial = new ArcaneLoggingOptions();
        initial.Console.Mode = ConsoleMode.Plain;
        var monitor = new FakeMonitor(initial);
        LogSink[] sinks = [new TextLogSink("console", new TextWriterLineWriter(output), color: false)];
        using var provider = new ArcaneLoggerProvider(monitor, static () => false, sinks);

        var color = new ArcaneLoggingOptions();
        color.Console.Mode = ConsoleMode.Color;
        monitor.Change(color);

        Assert.False(provider.ConsoleColor);
        Assert.Contains("Logging:ArcaneCore:Console:Mode changed to Color.", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', output.ToString());
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

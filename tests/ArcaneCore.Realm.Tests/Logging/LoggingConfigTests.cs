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
            ("Logging:ArcaneCore:File:Path", "   "),
            ("Logging:ArcaneCore:File:RollSizeMb", "-1"),
            ("Logging:ArcaneCore:File:Retain", "many"),
            ("Logging:ArcaneCore:File:QueueCapacity", "99999999"));

        List<ConfigIssue> issues = [.. new LoggingConfigChecks().Check(configuration)];

        string[] keys = [.. issues.Select(i => i.Key).Order(StringComparer.Ordinal)];
        Assert.Equal(
            [
                "Logging:ArcaneCore:Console:Mode",
                "Logging:ArcaneCore:Console:QueueCapacity",
                "Logging:ArcaneCore:File:Path",
                "Logging:ArcaneCore:File:QueueCapacity",
                "Logging:ArcaneCore:File:Retain",
                "Logging:ArcaneCore:File:RollSizeMb",
                "Logging:ArcaneCore:IncludeScopes",
                "Logging:ArcaneCore:Timestamps",
            ],
            keys);
        Assert.Contains(issues, i => i.Key == "Logging:ArcaneCore:File:Path" && i.Problem.Contains("is empty", StringComparison.Ordinal));
        Assert.All(issues, i => Assert.Equal(ConfigSeverity.Error, i.Severity));
        Assert.Contains(issues, i => i.Key == "Logging:ArcaneCore:Console:Mode" && i.Fix.Contains("Color, Plain or Off", StringComparison.Ordinal));
        Assert.Equal("Logging__ArcaneCore__Console__Mode", issues.Single(i => i.Key == "Logging:ArcaneCore:Console:Mode").EnvironmentVariable);
    }

    [Fact]
    public void Check_AcceptsTheShippedDefaults_AndADisabledSinkWithAnEmptyPath()
    {
        Assert.Empty(new LoggingConfigChecks().Check(Config()));
        Assert.Empty(Errors(new LoggingConfigChecks().Check(Config(
            ("Logging:ArcaneCore:Console:Mode", "plain"),
            ("Logging:ArcaneCore:File:Enabled", "false"),
            ("Logging:ArcaneCore:File:Path", ""),
            ("Logging:ArcaneCore:Json:Enabled", "true"),
            ("Logging:ArcaneCore:Json:Path", "logs/x.jsonl"),
            ("Logging:ArcaneCore:Timestamps", "Local")))));
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
        // Format unset and both enabled: the Json file is the one written, so its tuning (Json:Retain) is in force; sharing a path is no longer an error
        Assert.Equal(2, ex.Failures.Count());
        Assert.Contains(ex.Failures, f => f.Contains("Json:Retain", StringComparison.Ordinal));
        Assert.Contains(ex.Failures, f => f.Contains("Console:QueueCapacity", StringComparison.Ordinal));
        LoggingConfigChecks.ThrowIfInvalid(new ArcaneLoggingOptions());
    }

    /// <summary>
    /// The text and JSON sinks used to be rejected for sharing one file. A daemon now writes ONE log file, so enabling both on the
    /// same path is not an error: both checks accept it and the plan holds a single file (Json, at the Json path).
    /// </summary>
    [Fact]
    public void Check_AndThrowIfInvalid_AcceptBothEnabledOnOnePath_AsASingleFile()
    {
        IConfiguration configuration = Config(
            ("Logging:ArcaneCore:File:Enabled", "true"),
            ("Logging:ArcaneCore:File:Path", "logs/world.log"),
            ("Logging:ArcaneCore:Json:Enabled", "true"),
            ("Logging:ArcaneCore:Json:Path", "logs/world.log"));

        Assert.Empty(Errors(new LoggingConfigChecks().Check(configuration))); // the legacy-both warning is pinned in LoggingFormatWarningTests
        LoggingConfigChecks.ThrowIfInvalid(Bind(configuration));

        LogFileSinkPlan plan = LogFileSinkPlan.Resolve(Bind(configuration));
        Assert.True(plan.Enabled);
        Assert.Equal(LogFileFormat.Json, plan.Format);
        Assert.Equal("logs/world.log", plan.Path);
    }

    /// <summary>Every rule the bound-options check knows is reported by the raw-configuration check with the same key, and vice versa; only the keys in force are checked.</summary>
    [Fact]
    public void Check_AndThrowIfInvalid_AgreeOnEveryRule()
    {
        // Format=Json: the keys in force are File:*; the legacy Json:* values are ignored (only warned about), so they are not reported.
        IConfiguration viaFile = Config(
            ("Logging:ArcaneCore:Format", "Json"),
            ("Logging:ArcaneCore:Console:QueueCapacity", "0"),
            ("Logging:ArcaneCore:File:Enabled", "true"),
            ("Logging:ArcaneCore:File:Path", "logs/"),
            ("Logging:ArcaneCore:File:RollSizeMb", "-1"),
            ("Logging:ArcaneCore:File:Retain", "-3"),
            ("Logging:ArcaneCore:File:QueueCapacity", "1000001"),
            ("Logging:ArcaneCore:Json:Path", " "),
            ("Logging:ArcaneCore:Json:QueueCapacity", "0"));
        // Format unset, Json only: the legacy Json:* keys are the ones in force.
        IConfiguration viaLegacyJson = Config(
            ("Logging:ArcaneCore:Json:Enabled", "true"),
            ("Logging:ArcaneCore:Json:Path", " "),
            ("Logging:ArcaneCore:Json:RollSizeMb", "-2"),
            ("Logging:ArcaneCore:Json:Retain", "-3"),
            ("Logging:ArcaneCore:Json:QueueCapacity", "0"),
            ("Logging:ArcaneCore:File:Retain", "-9"));

        foreach ((IConfiguration configuration, int expected) in new[] { (viaFile, 5), (viaLegacyJson, 4) })
        {
            string[] checkKeys = [.. Errors(new LoggingConfigChecks().Check(configuration)).Select(i => i.Key).Order(StringComparer.Ordinal)];
            var ex = Assert.Throws<OptionsValidationException>(() => LoggingConfigChecks.ThrowIfInvalid(Bind(configuration)));
            string[] throwKeys = [.. ex.Failures.Select(f => f[..f.IndexOf(' ', StringComparison.Ordinal)]).Order(StringComparer.Ordinal)];

            Assert.Equal(expected, checkKeys.Length);
            Assert.Equal(checkKeys, throwKeys);
        }

        Assert.DoesNotContain(Errors(new LoggingConfigChecks().Check(viaFile)), i => i.Key.Contains(":Json:", StringComparison.Ordinal));
        Assert.DoesNotContain(Errors(new LoggingConfigChecks().Check(viaLegacyJson)), i => i.Key.Contains(":File:", StringComparison.Ordinal));
    }

    /// <summary>An enabled file whose path is only white space is an error under every format; a disabled one is not looked at.</summary>
    [Theory]
    [InlineData(null, "File", "Logging:ArcaneCore:File:Path")]
    [InlineData("Text", "File", "Logging:ArcaneCore:File:Path")]
    [InlineData("Json", "File", "Logging:ArcaneCore:File:Path")]
    [InlineData(null, "Json", "Logging:ArcaneCore:Json:Path")]
    public void Check_AndThrowIfInvalid_RejectAnAllWhitespacePath_OfTheEnabledFile(string? format, string section, string key)
    {
        var pairs = new List<(string, string)> { ($"Logging:ArcaneCore:{section}:Enabled", "true"), ($"Logging:ArcaneCore:{section}:Path", "  \t ") };
        if (format is not null)
        {
            pairs.Add(("Logging:ArcaneCore:Format", format));
        }

        IConfiguration configuration = Config([.. pairs]);
        ConfigIssue issue = Assert.Single(Errors(new LoggingConfigChecks().Check(configuration)));
        Assert.Equal(key, issue.Key);
        Assert.Contains("is empty", issue.Problem, StringComparison.Ordinal);
        var ex = Assert.Throws<OptionsValidationException>(() => LoggingConfigChecks.ThrowIfInvalid(Bind(configuration)));
        Assert.StartsWith(key + " ", Assert.Single(ex.Failures), StringComparison.Ordinal);

        pairs[0] = ($"Logging:ArcaneCore:{section}:Enabled", "false");
        Assert.Empty(Errors(new LoggingConfigChecks().Check(Config([.. pairs]))));
    }

    private static IEnumerable<ConfigIssue> Errors(IEnumerable<ConfigIssue> issues) => issues.Where(static i => i.Severity == ConfigSeverity.Error);

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
        Assert.Contains("Logging:ArcaneCore:File:Path option can't be changed at reload;", text, StringComparison.Ordinal);
        Assert.Contains("restart to apply elsewhere/world.log.", text, StringComparison.Ordinal);
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

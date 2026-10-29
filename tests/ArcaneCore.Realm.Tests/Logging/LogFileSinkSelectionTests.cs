using System.Text.Json;
using ArcaneCore.Kernel.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Realm.Tests.Logging;

/// <summary>
/// A daemon writes ONE log file, text or JSON lines, chosen by <c>Logging:ArcaneCore:Format</c> (Text|Json): which sinks
/// <see cref="ArcaneLoggerProvider.BuildSinks"/> builds, what each file holds, which path and tuning win, the startup warning for the
/// ambiguous legacy configuration, and the lock fallback of a path another writer owns.
/// </summary>
public sealed class LogFileSinkSelectionTests : IDisposable
{
    private const string Root = "Logging:ArcaneCore";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcanecore-sinkselect-" + Guid.NewGuid().ToString("N"));

    public LogFileSinkSelectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    /// <summary>Options as the host binds them, with the console switched off so a test run prints nothing.</summary>
    private static ArcaneLoggingOptions Bind(params (string Key, string Value)[] pairs)
    {
        var options = new ArcaneLoggingOptions();
        Config(pairs).GetSection(Root).Bind(options);
        options.Console.Mode = ConsoleMode.Off;
        return options;
    }

    /// <summary>Builds the provider the host builds, logs <paramref name="messages"/> as Information, drains and disposes it.</summary>
    private static void RunHost(ArcaneLoggingOptions options, params string[] messages)
    {
        using var provider = new ArcaneLoggerProvider(new FixedMonitor(options));
        ILogger logger = provider.CreateLogger("Test.Category");
        foreach (string message in messages)
        {
            logger.LogInformation("{Message}", message);
        }

        Assert.True(provider.Flush(TimeSpan.FromSeconds(30)));
    }

    private static string[] Lines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string[] DisposeAll(LogSink[] sinks)
    {
        string[] names = [.. sinks.Select(s => s.Name)];
        foreach (LogSink sink in sinks)
        {
            sink.Dispose();
        }

        return names;
    }

    // ---- (a)/(b) sink selection and file content ----

    [Fact]
    public void FormatText_BuildsConsoleAndOneFileSink_AndWritesPlainText()
    {
        string path = P("world.log");
        ArcaneLoggingOptions options = Bind(($"{Root}:Format", "Text"), ($"{Root}:File:Enabled", "true"), ($"{Root}:File:Path", path));

        LogSink[] sinks = ArcaneLoggerProvider.BuildSinks(options);
        Assert.IsType<TextLogSink>(sinks[1]);
        Assert.Equal(["console", "file"], DisposeAll(sinks));

        RunHost(options, "hello text");
        string[] lines = Lines(path);
        Assert.Contains(lines, l => l.Contains("hello text", StringComparison.Ordinal));
        Assert.All(lines, l => Assert.False(l.StartsWith('{'), "a Text file holds plain lines, not JSON objects: " + l));
        Assert.Equal([path], Directory.GetFiles(_dir, "*.log"));
    }

    [Fact]
    public void FormatJson_BuildsConsoleAndOneJsonSink_AndWritesJsonLines()
    {
        string path = P("world.jsonl");
        ArcaneLoggingOptions options = Bind(($"{Root}:Format", "Json"), ($"{Root}:File:Enabled", "true"), ($"{Root}:File:Path", path));

        LogSink[] sinks = ArcaneLoggerProvider.BuildSinks(options);
        Assert.IsType<JsonLogSink>(sinks[1]);
        Assert.Equal(["console", "json"], DisposeAll(sinks));

        RunHost(options, "hello json");
        string[] lines = Lines(path);
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => JsonDocument.Parse(l).RootElement.GetProperty("message").GetString() == "hello json");
        Assert.All(lines, l => Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(l).RootElement.ValueKind));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    /// <summary>Format set: either switch turns the file on (the deprecated Json:Enabled keeps working), and neither means no file sink.</summary>
    [Theory]
    [InlineData("File", true)]
    [InlineData("Json", true)]
    [InlineData("None", false)]
    public void FormatSet_FileOrJsonEnabled_SwitchesTheFileOn(string enabledKey, bool expectFile)
    {
        var pairs = new List<(string, string)> { ($"{Root}:Format", "Json"), ($"{Root}:File:Path", P("on.jsonl")) };
        if (enabledKey != "None")
        {
            pairs.Add(($"{Root}:{enabledKey}:Enabled", "true"));
        }

        string[] names = DisposeAll(ArcaneLoggerProvider.BuildSinks(Bind([.. pairs])));
        Assert.Equal(expectFile ? ["console", "json"] : ["console"], names);
        if (!expectFile)
        {
            Assert.Empty(Directory.GetFiles(_dir));
        }
    }

    // ---- (c) legacy: both enabled, Format unset ----

    [Fact]
    public void LegacyBothEnabled_FormatUnset_WritesOnlyTheJsonFile_AndWarnsNamingBothPaths()
    {
        string textPath = P("legacy.log");
        string jsonPath = P("legacy.jsonl");
        ArcaneLoggingOptions options = Bind(
            ($"{Root}:File:Enabled", "true"), ($"{Root}:File:Path", textPath),
            ($"{Root}:Json:Enabled", "true"), ($"{Root}:Json:Path", jsonPath));

        LogSink[] sinks = ArcaneLoggerProvider.BuildSinks(options);
        Assert.IsType<JsonLogSink>(sinks[1]);
        Assert.Equal(["console", "json"], DisposeAll(sinks));

        RunHost(options, "after the warning");

        Assert.False(File.Exists(textPath), "the text sink's file must never be created");
        Assert.Equal(Path.GetFileName(jsonPath), Assert.Single(Directory.GetFiles(_dir, "*.jsonl").Select(Path.GetFileName)));
        Assert.Empty(Directory.GetFiles(_dir, "*.log"));

        JsonElement[] events = [.. Lines(jsonPath).Select(l => JsonDocument.Parse(l).RootElement.Clone())];
        JsonElement warning = Assert.Single(events, e => e.GetProperty("level").GetString() == "Warning");
        string message = warning.GetProperty("message").GetString()!;
        Assert.Contains(textPath, message, StringComparison.Ordinal);
        Assert.Contains(jsonPath, message, StringComparison.Ordinal);
        Assert.Contains("NOT being written", message, StringComparison.Ordinal);
        Assert.Contains(events, e => e.GetProperty("message").GetString() == "after the warning");
    }

    // ---- (d) legacy single sinks ----

    [Fact]
    public void LegacyFileOnly_FormatUnset_IsText()
    {
        string path = P("only.log");
        ArcaneLoggingOptions options = Bind(($"{Root}:File:Enabled", "true"), ($"{Root}:File:Path", path), ($"{Root}:Json:Path", P("ignored.jsonl")));

        Assert.Equal(["console", "file"], DisposeAll(ArcaneLoggerProvider.BuildSinks(options)));
        RunHost(options, "legacy text");

        Assert.DoesNotContain(Lines(path), l => l.StartsWith('{'));
        Assert.Contains(Lines(path), l => l.Contains("legacy text", StringComparison.Ordinal));
        Assert.False(File.Exists(P("ignored.jsonl")));
        Assert.Empty(LogFileSinkPlan.Resolve(options).Warnings);
    }

    [Fact]
    public void LegacyJsonOnly_FormatUnset_IsJsonAtTheJsonPath()
    {
        string path = P("only.jsonl");
        ArcaneLoggingOptions options = Bind(($"{Root}:Json:Enabled", "true"), ($"{Root}:Json:Path", path), ($"{Root}:File:Path", P("ignored.log")));

        Assert.Equal(["console", "json"], DisposeAll(ArcaneLoggerProvider.BuildSinks(options)));
        RunHost(options, "legacy json");

        Assert.Contains(Lines(path), l => JsonDocument.Parse(l).RootElement.GetProperty("message").GetString() == "legacy json");
        Assert.False(File.Exists(P("ignored.log")));
        Assert.NotEmpty(LogFileSinkPlan.Resolve(options).Warnings); // Json:Enabled without Format is deprecated
    }

    [Fact]
    public void NothingEnabled_BuildsTheConsoleOnly_AndNoFileExists()
    {
        Assert.Equal(["console"], DisposeAll(ArcaneLoggerProvider.BuildSinks(Bind(($"{Root}:File:Path", P("never.log")), ($"{Root}:Json:Path", P("never.jsonl"))))));
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.False(LogFileSinkPlan.Resolve(new ArcaneLoggingOptions()).Enabled);
    }

    // ---- (e) path resolution ----

    /// <summary>File:Path is the file written and beats the legacy Json:Path, which is only a fallback for a Json file and is reported as deprecated.</summary>
    [Fact]
    public void FilePath_BeatsLegacyJsonPath_ForAJsonFormat()
    {
        string filePath = P("from-file.jsonl");
        ArcaneLoggingOptions options = Bind(
            ($"{Root}:Format", "Json"), ($"{Root}:File:Enabled", "true"),
            ($"{Root}:File:Path", filePath), ($"{Root}:Json:Path", P("from-json.jsonl")));

        RunHost(options, "which file");

        Assert.Equal(filePath, LogFileSinkPlan.Resolve(options).Path);
        Assert.Contains(Lines(filePath), l => l.Contains("which file", StringComparison.Ordinal));
        Assert.False(File.Exists(P("from-json.jsonl")));
        Assert.Contains("Json:Path", Assert.Single(LogFileSinkPlan.Resolve(options).Warnings).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyJsonPath_IsUsedWhenFilePathIsUnset_ForJsonAndText_WithAWarning()
    {
        string jsonPath = P("legacy-only.jsonl");
        ArcaneLoggingOptions json = Bind(($"{Root}:Format", "Json"), ($"{Root}:File:Enabled", "true"), ($"{Root}:Json:Path", jsonPath));
        Assert.Equal(jsonPath, LogFileSinkPlan.Resolve(json).Path);
        RunHost(json, "legacy path");
        Assert.True(File.Exists(jsonPath));

        // Precedence is File:Path > Json:Path > the format default for every format, so a Text daemon whose launcher only set the
        // deprecated Json:Path still writes where that launcher said.
        string textPath = P("legacy-text.jsonl");
        LogFileSinkPlan text = LogFileSinkPlan.Resolve(Bind(($"{Root}:Format", "Text"), ($"{Root}:File:Enabled", "true"), ($"{Root}:Json:Path", textPath)));
        Assert.Equal(LogFileFormat.Text, text.Format);
        Assert.Equal(textPath, text.Path);
        Assert.Equal(Root + ":Json:Path", Assert.Single(text.Warnings).Key);
    }

    [Theory]
    [InlineData("Text", "logs/arcanecore.log")]
    [InlineData("Json", "logs/arcanecore.jsonl")]
    public void UnsetPath_UsesThePerFormatDefault(string format, string expected)
    {
        LogFileSinkPlan plan = LogFileSinkPlan.Resolve(Bind(($"{Root}:Format", format), ($"{Root}:File:Enabled", "true")));
        Assert.Equal(expected, plan.Path);
        Assert.Equal(expected, LogFileSinkPlan.DefaultPath(Enum.Parse<LogFileFormat>(format)));
        Assert.Equal(Root + ":File:Path", plan.PathKey);

        // an empty path is "unset", not a path
        Assert.Equal(expected, LogFileSinkPlan.Resolve(Bind(($"{Root}:Format", format), ($"{Root}:File:Enabled", "true"), ($"{Root}:File:Path", ""))).Path);
    }

    [Fact]
    public void UnsetPath_LegacyDefaults_FollowTheLegacyFormat()
    {
        Assert.Equal("logs/arcanecore.log", LogFileSinkPlan.Resolve(Bind(($"{Root}:File:Enabled", "true"))).Path);
        Assert.Equal("logs/arcanecore.jsonl", LogFileSinkPlan.Resolve(Bind(($"{Root}:Json:Enabled", "true"))).Path);
    }

    // ---- (f) tuning from File:* applies to the chosen Json sink ----

    [Fact]
    public void FileTuning_AppliesToTheJsonSink_AndBeatsLegacyJsonTuning()
    {
        string path = P("roll.jsonl");
        // Json:RollSizeMb=0 / Json:Retain=99 would never roll or prune; they must be ignored because Format is set.
        ArcaneLoggingOptions options = Bind(
            ($"{Root}:Format", "Json"), ($"{Root}:File:Enabled", "true"), ($"{Root}:File:Path", path),
            ($"{Root}:File:RollSizeMb", "1"), ($"{Root}:File:RollDaily", "false"), ($"{Root}:File:Retain", "1"), ($"{Root}:File:QueueCapacity", "5000"),
            ($"{Root}:Json:RollSizeMb", "0"), ($"{Root}:Json:Retain", "99"), ($"{Root}:Json:QueueCapacity", "7"));

        LogFileSinkPlan plan = LogFileSinkPlan.Resolve(options);
        Assert.Equal((1, false, 1, 5000), (plan.RollSizeMb, plan.RollDaily, plan.Retain, plan.QueueCapacity));
        Assert.Equal(Root + ":File", plan.Section);

        string big = new('x', 2000);
        string[] messages = [.. Enumerable.Range(0, 2400).Select(i => $"{i:0000} {big}")]; // about 4.6 MiB: at least three 1 MiB rolls
        RunHost(options, messages);

        string[] segments = [.. Directory.GetFiles(_dir, "roll-*.jsonl").Select(f => Path.GetFileName(f))];
        string segment = Assert.Single(segments); // Retain=1 pruned the older ones
        Assert.Matches(@"^roll-\d{8}-\d{3}\.jsonl$", segment);
        Assert.NotEqual("roll-" + DateTime.UtcNow.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + "-001.jsonl", segment); // a later roll than the first survived
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length <= 1024 * 1024 + 8192);
        Assert.All(Lines(path), l => JsonDocument.Parse(l).Dispose());
    }

    [Fact]
    public void LegacyJsonTuning_StillAppliesWhenFormatIsUnset()
    {
        LogFileSinkPlan plan = LogFileSinkPlan.Resolve(Bind(
            ($"{Root}:Json:Enabled", "true"), ($"{Root}:Json:RollSizeMb", "3"), ($"{Root}:Json:Retain", "4"), ($"{Root}:Json:QueueCapacity", "11"),
            ($"{Root}:File:RollSizeMb", "9"), ($"{Root}:File:Retain", "8")));
        Assert.Equal((3, 4, 11, Root + ":Json"), (plan.RollSizeMb, plan.Retain, plan.QueueCapacity, plan.Section));
    }

    // ---- (g) lock fallback ----

    [Theory]
    [InlineData("held.jsonl")]
    [InlineData("held.log")]
    public void PathHeldByAnotherWriter_FallsBackToAPerProcessName_KeepingTheExtension(string name)
    {
        string path = P(name);
        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        var policy = new RollingFilePolicy(0, false, 0);
        var diagnostics = new StringWriter();

        using var owner = new RollingFileWriter(path, policy, diagnostics: TextWriter.Null);
        owner.Write("owner\n");
        using var second = new RollingFileWriter(path, policy, diagnostics: diagnostics);
        second.Write("second\n");
        second.Flush();

        Assert.Equal(path, owner.ActivePath);
        Assert.NotEqual(path, second.ActivePath);
        Assert.Equal(_dir, Path.GetDirectoryName(second.ActivePath));
        string fallback = Path.GetFileName(second.ActivePath);
        Assert.StartsWith(stem + ".", fallback, StringComparison.Ordinal);
        Assert.EndsWith(extension, fallback, StringComparison.Ordinal);
        Assert.Contains("in use by another writer", diagnostics.ToString(), StringComparison.Ordinal);
        Assert.Equal("second\n", string.Concat(Lines(second.ActivePath).Select(l => l + "\n")));
    }

    /// <summary>Two hosts configured with the same file: the second provider's sink lands on the fallback file, so no line is lost or interleaved.</summary>
    [Fact]
    public void TwoProvidersOnOnePath_BothWriteAJsonFile_NeitherSharesIt()
    {
        string path = P("shared.jsonl");
        ArcaneLoggingOptions options = Bind(($"{Root}:Format", "Json"), ($"{Root}:File:Enabled", "true"), ($"{Root}:File:Path", path));

        using var first = new ArcaneLoggerProvider(new FixedMonitor(options));
        using var second = new ArcaneLoggerProvider(new FixedMonitor(options));
        first.CreateLogger("A").LogInformation("{Message}", "from first");
        second.CreateLogger("B").LogInformation("{Message}", "from second");
        Assert.True(first.Flush(TimeSpan.FromSeconds(30)));
        Assert.True(second.Flush(TimeSpan.FromSeconds(30)));

        string[] files = Directory.GetFiles(_dir, "*.jsonl");
        Assert.Equal(2, files.Length);
        foreach (string file in files)
        {
            string[] messages = [.. Lines(file).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("message").GetString()!)];
            Assert.Single(messages, m => m.StartsWith("from ", StringComparison.Ordinal));
        }
    }

    // ---- (h) Resolve(IConfiguration) matches Resolve(options) ----

    [Theory]
    [MemberData(nameof(ResolutionCases))]
    public void ResolveFromConfiguration_MatchesResolveFromOptions(string description, (string Key, string Value)[] pairs)
    {
        IConfiguration configuration = Config(pairs);
        var options = new ArcaneLoggingOptions();
        configuration.GetSection(Root).Bind(options);

        LogFileSinkPlan fromConfiguration = LogFileSinkPlan.Resolve(configuration);
        LogFileSinkPlan fromOptions = LogFileSinkPlan.Resolve(options);

        Assert.True(
            (fromConfiguration.Enabled, fromConfiguration.Format, fromConfiguration.Path, fromConfiguration.RollSizeMb, fromConfiguration.RollDaily, fromConfiguration.Retain, fromConfiguration.QueueCapacity, fromConfiguration.Section, fromConfiguration.PathKey)
            == (fromOptions.Enabled, fromOptions.Format, fromOptions.Path, fromOptions.RollSizeMb, fromOptions.RollDaily, fromOptions.Retain, fromOptions.QueueCapacity, fromOptions.Section, fromOptions.PathKey),
            description);
        Assert.Equal(fromOptions.Warnings, fromConfiguration.Warnings);
    }

    public static TheoryData<string, (string Key, string Value)[]> ResolutionCases() => new()
    {
        { "nothing configured", [] },
        { "Format Text", [($"{Root}:Format", "Text"), ($"{Root}:File:Enabled", "true"), ($"{Root}:File:Path", "a/b.log"), ($"{Root}:File:Retain", "3")] },
        { "Format json (case-insensitive) via Json:Enabled and Json:Path", [($"{Root}:Format", "json"), ($"{Root}:Json:Enabled", "true"), ($"{Root}:Json:Path", "a/b.jsonl")] },
        { "legacy both enabled", [($"{Root}:File:Enabled", "true"), ($"{Root}:Json:Enabled", "true"), ($"{Root}:File:Path", "t.log"), ($"{Root}:Json:Path", "j.jsonl")] },
        { "legacy file only", [($"{Root}:File:Enabled", "true"), ($"{Root}:File:RollSizeMb", "5")] },
        { "legacy json only", [($"{Root}:Json:Enabled", "true"), ($"{Root}:Json:QueueCapacity", "99")] },
    };

    private sealed class FixedMonitor(ArcaneLoggingOptions current) : IOptionsMonitor<ArcaneLoggingOptions>
    {
        public ArcaneLoggingOptions CurrentValue { get; } = current;

        public ArcaneLoggingOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcaneLoggingOptions, string?> listener) => null;
    }
}

using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Logging;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Logging;

/// <summary>
/// The single-log-file format selection reports legacy and deprecated keys as <see cref="ConfigSeverity.Warning"/> issues from
/// <see cref="LoggingConfigChecks.Check"/> (never as startup failures), and the realm and world, which share one profile
/// appsettings.json and differ only by environment overrides, each resolve to their own file path.
/// </summary>
public sealed class LoggingFormatWarningTests : IDisposable
{
    private const string Root = "Logging:ArcaneCore";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcanecore-fmtwarn-" + Guid.NewGuid().ToString("N"));

    public LoggingFormatWarningTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    /// <summary>An in-memory profile layer, then an in-memory environment layer added after it (the order AddEnvironmentVariables has; keys use ':').</summary>
    private static IConfiguration Layered((string Key, string Value)[] profile, params (string Key, string Value)[] env)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(profile.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .AddInMemoryCollection(env.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static ArcaneLoggingOptions Bind(IConfiguration configuration)
    {
        var options = new ArcaneLoggingOptions();
        configuration.GetSection(Root).Bind(options);
        options.Console.Mode = ConsoleMode.Off;
        return options;
    }

    private static ConfigIssue[] Warnings(IConfiguration configuration)
        => [.. new LoggingConfigChecks().Check(configuration).Where(static i => i.Severity == ConfigSeverity.Warning)];

    private static void AssertWarn(ConfigIssue issue, string key)
    {
        Assert.Equal(ConfigSeverity.Warning, issue.Severity);
        Assert.Equal(key, issue.Key);
        Assert.False(string.IsNullOrWhiteSpace(issue.Problem));
        Assert.False(string.IsNullOrWhiteSpace(issue.Fix));
    }

    // ---- 1. warnings surfaced by Check ----

    [Fact]
    public void Check_FormatUnset_BothEnabled_WarnsOnFormatKey_NamingBothPaths_AndSayingOnlyJsonIsWritten()
    {
        string text = P("legacy.log");
        string json = P("legacy.jsonl");
        ConfigIssue issue = Assert.Single(Warnings(Config(
            (Root + ":File:Enabled", "true"), (Root + ":File:Path", text),
            (Root + ":Json:Enabled", "true"), (Root + ":Json:Path", json))));

        AssertWarn(issue, Root + ":Format");
        Assert.Contains(text, issue.Problem, StringComparison.Ordinal);
        Assert.Contains(json, issue.Problem, StringComparison.Ordinal);
        Assert.Contains("JSON", issue.Problem, StringComparison.Ordinal);
        Assert.Contains("Format", issue.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_FormatUnset_BothEnabled_WithDefaultPaths_NamesTheDefaultPaths()
    {
        ConfigIssue issue = Assert.Single(Warnings(Config((Root + ":File:Enabled", "true"), (Root + ":Json:Enabled", "true"))));

        AssertWarn(issue, Root + ":Format");
        Assert.Contains("logs/arcanecore.log", issue.Problem, StringComparison.Ordinal);
        Assert.Contains("logs/arcanecore.jsonl", issue.Problem, StringComparison.Ordinal);
        Assert.Contains("JSON", issue.Problem, StringComparison.Ordinal);
        Assert.Contains("Format", issue.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_FormatUnset_JsonOnly_WarnsThatJsonEnabledIsADeprecatedAlias()
    {
        IConfiguration configuration = Config((Root + ":Json:Enabled", "true"), (Root + ":Json:Path", P("only.jsonl")));

        ConfigIssue issue = Assert.Single(Warnings(configuration));
        AssertWarn(issue, Root + ":Json:Enabled");
        Assert.Contains("Format=Json", issue.Fix, StringComparison.Ordinal);
        Assert.Contains("File", issue.Fix, StringComparison.Ordinal);
        Assert.Equal(ConfigSeverity.Warning, Assert.Single(new LoggingConfigChecks().Check(configuration)).Severity);
    }

    [Fact]
    public void Resolve_FormatUnset_JsonOnly_NowCarriesAWarning_ButStillResolvesToJsonAtTheJsonPath()
    {
        string path = P("only.jsonl");
        LogFileSinkPlan plan = LogFileSinkPlan.Resolve(Config((Root + ":Json:Enabled", "true"), (Root + ":Json:Path", path)));

        Assert.NotEmpty(plan.Warnings);
        Assert.True(plan.Enabled);
        Assert.Equal(LogFileFormat.Json, plan.Format);
        Assert.Equal(path, plan.Path);
    }

    [Theory]
    [InlineData("Json")]
    [InlineData("Text")]
    public void Check_FormatSet_JsonEnabled_WarnsOnJsonEnabled(string format)
    {
        ConfigIssue issue = Assert.Single(Warnings(Config(
            (Root + ":Format", format), (Root + ":File:Path", P("f.out")), (Root + ":Json:Enabled", "true"))));

        AssertWarn(issue, Root + ":Json:Enabled");
    }

    [Fact]
    public void Check_FormatSet_OnlyJsonPath_WarnsOnJsonPath()
    {
        ConfigIssue issue = Assert.Single(Warnings(Config(
            (Root + ":Format", "Json"), (Root + ":File:Enabled", "true"), (Root + ":Json:Path", P("j.jsonl")))));

        AssertWarn(issue, Root + ":Json:Path");
    }

    [Fact]
    public void Check_FormatSet_JsonEnabledAndJsonPath_IsOneWarning_OnJsonEnabled()
    {
        ConfigIssue issue = Assert.Single(Warnings(Config(
            (Root + ":Format", "Json"), (Root + ":Json:Enabled", "true"), (Root + ":Json:Path", P("j.jsonl")))));

        AssertWarn(issue, Root + ":Json:Enabled");
    }

    public static TheoryData<string, (string Key, string Value)[]> QuietCases() => new()
    {
        { "Format=Json + File:Enabled", [(Root + ":Format", "Json"), (Root + ":File:Enabled", "true"), (Root + ":File:Path", "logs/a.jsonl")] },
        { "Format=Text + File:Enabled", [(Root + ":Format", "Text"), (Root + ":File:Enabled", "true"), (Root + ":File:Path", "logs/a.log")] },
        { "Format unset + File:Enabled only", [(Root + ":File:Enabled", "true"), (Root + ":File:Path", "logs/a.log")] },
        { "nothing configured", [] },
    };

    [Theory]
    [MemberData(nameof(QuietCases))]
    public void Check_NoLegacyKeys_YieldsNoWarnings(string description, (string Key, string Value)[] pairs)
    {
        Assert.True(Warnings(Config(pairs)).Length == 0, description);
        Assert.Empty(new LoggingConfigChecks().Check(Config(pairs)));
        Assert.Empty(LogFileSinkPlan.Resolve(Config(pairs)).Warnings);
    }

    public static TheoryData<string, string> WarningConfigs() => new()
    {
        { "legacy both", "both" },
        { "legacy json only", "json-only" },
        { "format json + Json:Enabled", "format-json-enabled" },
        { "format text + Json:Enabled", "format-text-enabled" },
        { "format json + Json:Path only", "format-json-path" },
        { "format json + Json:Enabled and Json:Path", "format-json-both" },
    };

    private (string Key, string Value)[] WarningConfig(string name) => name switch
    {
        "both" => [(Root + ":File:Enabled", "true"), (Root + ":File:Path", P("a.log")), (Root + ":Json:Enabled", "true"), (Root + ":Json:Path", P("a.jsonl"))],
        "json-only" => [(Root + ":Json:Enabled", "true"), (Root + ":Json:Path", P("b.jsonl"))],
        "format-json-enabled" => [(Root + ":Format", "Json"), (Root + ":File:Path", P("c.jsonl")), (Root + ":Json:Enabled", "true")],
        "format-text-enabled" => [(Root + ":Format", "Text"), (Root + ":File:Path", P("d.log")), (Root + ":Json:Enabled", "true")],
        "format-json-path" => [(Root + ":Format", "Json"), (Root + ":File:Enabled", "true"), (Root + ":Json:Path", P("e.jsonl"))],
        "format-json-both" => [(Root + ":Format", "Json"), (Root + ":Json:Enabled", "true"), (Root + ":Json:Path", P("f.jsonl"))],
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>Warnings never fail startup: the bound-options check passes and the sinks build (the console is off, files land in the temp dir).</summary>
    [Theory]
    [MemberData(nameof(WarningConfigs))]
    public void Warnings_NeverFailStartup(string description, string name)
    {
        IConfiguration configuration = Config(WarningConfig(name));
        Assert.True(Warnings(configuration).Length >= 1, description);

        ArcaneLoggingOptions options = Bind(configuration);
        LoggingConfigChecks.ThrowIfInvalid(options);

        LogSink[] sinks = ArcaneLoggerProvider.BuildSinks(options);
        try
        {
            Assert.NotEmpty(sinks);
        }
        finally
        {
            foreach (LogSink sink in sinks)
            {
                sink.Dispose();
            }
        }
    }

    // ---- 3. realm/world distinct paths (regression guards; layered profile + environment) ----

    private string World(string ext) => P("world.debug." + ext);

    private string Realm(string ext) => P("realm.debug." + ext);

    [Fact]
    public void Guard_LiveShape_LegacyProfileWithPerRoleEnvPaths_GivesRealmAndWorldDistinctJsonFiles()
    {
        (string, string)[] profile =
        [
            (Root + ":File:Enabled", "true"), (Root + ":File:Path", World("log")),
            (Root + ":Json:Enabled", "true"), (Root + ":Json:Path", World("jsonl")),
        ];

        LogFileSinkPlan realm = LogFileSinkPlan.Resolve(Layered(profile, (Root + ":File:Path", Realm("log")), (Root + ":Json:Path", Realm("jsonl"))));
        LogFileSinkPlan world = LogFileSinkPlan.Resolve(Layered(profile));

        Assert.Equal(LogFileFormat.Json, realm.Format);
        Assert.Equal(Realm("jsonl"), realm.Path);
        Assert.Equal(LogFileFormat.Json, world.Format);
        Assert.Equal(World("jsonl"), world.Path);
        Assert.NotEqual(realm.Path, world.Path);
    }

    [Fact]
    public void Guard_NewShape_FormatJsonProfileWithRealmEnvFilePath_GivesDistinctJsonFiles()
    {
        (string, string)[] profile = [(Root + ":Format", "Json"), (Root + ":File:Enabled", "true"), (Root + ":File:Path", World("jsonl"))];

        LogFileSinkPlan realm = LogFileSinkPlan.Resolve(Layered(profile, (Root + ":File:Path", Realm("jsonl"))));
        LogFileSinkPlan world = LogFileSinkPlan.Resolve(Layered(profile));

        Assert.Equal(LogFileFormat.Json, realm.Format);
        Assert.Equal(LogFileFormat.Json, world.Format);
        Assert.Equal(Realm("jsonl"), realm.Path);
        Assert.Equal(World("jsonl"), world.Path);
        Assert.NotEqual(realm.Path, world.Path);
    }

    /// <summary>
    /// Guard for an environment that carries File:Path and Json:Path both set to the role path (an older launcher's shape, or an operator's
    /// shell): the resolution still lands on the role path. The current Set-LiveDaemonLogEnvironment shapes are exercised by LiveLogPathTests.
    /// </summary>
    [Theory]
    [InlineData("format-json-profile")]
    [InlineData("legacy-profile")]
    public void Guard_LiveEnvScriptShape_BothPathKeysSet_ResolvesToTheRolePath(string profileKind)
    {
        (string, string)[] profile = profileKind == "format-json-profile"
            ? [(Root + ":Format", "Json"), (Root + ":File:Enabled", "true"), (Root + ":File:Path", World("jsonl"))]
            : [(Root + ":File:Enabled", "true"), (Root + ":File:Path", World("log")), (Root + ":Json:Enabled", "true"), (Root + ":Json:Path", World("jsonl"))];

        string realmPath = Realm("jsonl");
        string worldPath = World("jsonl");
        LogFileSinkPlan realm = LogFileSinkPlan.Resolve(Layered(profile, (Root + ":File:Path", realmPath), (Root + ":Json:Path", realmPath)));
        LogFileSinkPlan world = LogFileSinkPlan.Resolve(Layered(profile, (Root + ":File:Path", worldPath), (Root + ":Json:Path", worldPath)));

        Assert.Equal(realmPath, realm.Path);
        Assert.Equal(worldPath, world.Path);
        Assert.NotEqual(realm.Path, world.Path);
        Assert.Equal(LogFileFormat.Json, realm.Format);
        Assert.Equal(LogFileFormat.Json, world.Format);
    }

    // ---- 4. path precedence File:Path > legacy Json:Path > format default, for Text and Json ----

    [Fact]
    public void Resolve_FormatText_FilePathUnset_UsesTheLegacyJsonPath_WithOneJsonPathWarning()
    {
        string path = Realm("jsonl");
        LogFileSinkPlan plan = LogFileSinkPlan.Resolve(Config(
            (Root + ":Format", "Text"), (Root + ":File:Enabled", "true"), (Root + ":Json:Path", path)));

        Assert.True(plan.Enabled);
        Assert.Equal(LogFileFormat.Text, plan.Format);
        Assert.Equal(path, plan.Path);
        Assert.Equal(Root + ":Json:Path", Assert.Single(plan.Warnings).Key);
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("Json")]
    public void Resolve_FilePath_BeatsLegacyJsonPath_ForEveryFormat(string format)
    {
        string filePath = P("from-file.out");
        LogFileSinkPlan plan = LogFileSinkPlan.Resolve(Config(
            (Root + ":Format", format), (Root + ":File:Enabled", "true"), (Root + ":File:Path", filePath), (Root + ":Json:Path", P("from-json.jsonl"))));

        Assert.Equal(filePath, plan.Path);
    }

    /// <summary>Shared profile that names no File:Path, per-role environment carrying only the legacy Json:Path: the roles still get distinct files.</summary>
    [Theory]
    [InlineData("Text")]
    [InlineData("Json")]
    public void Resolve_SharedProfile_PerRoleEnvJsonPathOnly_GivesRealmAndWorldDistinctPaths(string format)
    {
        (string, string)[] profile = [(Root + ":Format", format), (Root + ":File:Enabled", "true")];

        LogFileSinkPlan realm = LogFileSinkPlan.Resolve(Layered(profile, (Root + ":Json:Path", Realm("jsonl"))));
        LogFileSinkPlan world = LogFileSinkPlan.Resolve(Layered(profile, (Root + ":Json:Path", World("jsonl"))));

        Assert.Equal(Realm("jsonl"), realm.Path);
        Assert.Equal(World("jsonl"), world.Path);
        Assert.NotEqual(realm.Path, world.Path);
        Assert.Equal(Enum.Parse<LogFileFormat>(format), realm.Format);
        Assert.Equal(Enum.Parse<LogFileFormat>(format), world.Format);
    }

    // ---- 5. Json:Path equal to File:Path is not worth a warning ----

    [Fact]
    public void Check_FormatSet_JsonDisabled_JsonPathEqualToFilePath_YieldsNoIssues()
    {
        string path = P("same.jsonl");
        IConfiguration configuration = Config(
            (Root + ":Format", "Json"), (Root + ":File:Enabled", "true"), (Root + ":File:Path", path),
            (Root + ":Json:Enabled", "false"), (Root + ":Json:Path", path));

        Assert.Empty(new LoggingConfigChecks().Check(configuration));
        Assert.Empty(LogFileSinkPlan.Resolve(configuration).Warnings);
    }

    [Fact]
    public void Check_FormatSet_JsonDisabled_JsonPathDifferentFromFilePath_WarnsOnJsonPath()
    {
        ConfigIssue issue = Assert.Single(new LoggingConfigChecks().Check(Config(
            (Root + ":Format", "Json"), (Root + ":File:Enabled", "true"), (Root + ":File:Path", P("a.jsonl")),
            (Root + ":Json:Enabled", "false"), (Root + ":Json:Path", P("b.jsonl")))));

        AssertWarn(issue, Root + ":Json:Path");
    }

    [Fact]
    public void Check_FormatSet_JsonEnabledTrue_WarnsOnJsonEnabled_EvenWithEqualPaths()
    {
        string path = P("same.jsonl");
        ConfigIssue issue = Assert.Single(new LoggingConfigChecks().Check(Config(
            (Root + ":Format", "Json"), (Root + ":File:Enabled", "true"), (Root + ":File:Path", path),
            (Root + ":Json:Enabled", "true"), (Root + ":Json:Path", path))));

        AssertWarn(issue, Root + ":Json:Enabled");
    }

    // ---- 6. launcher shapes against a migrated profile ----

    private (string, string)[] MigratedProfile() =>
        [(Root + ":Format", "Json"), (Root + ":File:Enabled", "true"), (Root + ":File:Path", World("jsonl"))];

    /// <summary>(a) LiveEnv.ps1 on a migrated profile sets File:Path only. (b) deploy-live.py visible consoles pin Text. (c) server-status.ps1 supervised restart.</summary>
    [Fact]
    public void LauncherShape_LiveEnv_FilePathOnly_YieldsNoIssues()
    {
        IConfiguration configuration = Layered(MigratedProfile(), (Root + ":File:Path", Realm("jsonl")));

        Assert.Empty(new LoggingConfigChecks().Check(configuration));
        LogFileSinkPlan plan = LogFileSinkPlan.Resolve(configuration);
        Assert.Equal(LogFileFormat.Json, plan.Format);
        Assert.Equal(Realm("jsonl"), plan.Path);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void LauncherShape_DeployLiveVisibleConsoles_TextFormatAndFilePath_YieldsNoIssues()
    {
        IConfiguration configuration = Layered(
            MigratedProfile(), (Root + ":Format", "Text"), (Root + ":File:Enabled", "true"), (Root + ":File:Path", Realm("log")));

        Assert.Empty(new LoggingConfigChecks().Check(configuration));
        LogFileSinkPlan plan = LogFileSinkPlan.Resolve(configuration);
        Assert.Equal(LogFileFormat.Text, plan.Format);
        Assert.Equal(Realm("log"), plan.Path);
    }

    [Fact]
    public void LauncherShape_ServerStatusSupervisedRestart_JsonFormatAndFilePath_YieldsNoIssues()
    {
        string supervised = P("realm-supervised.jsonl");
        IConfiguration configuration = Layered(
            MigratedProfile(), (Root + ":Format", "Json"), (Root + ":File:Enabled", "true"), (Root + ":File:Path", supervised));

        Assert.Empty(new LoggingConfigChecks().Check(configuration));
        LogFileSinkPlan plan = LogFileSinkPlan.Resolve(configuration);
        Assert.Equal(LogFileFormat.Json, plan.Format);
        Assert.Equal(supervised, plan.Path);
    }
}

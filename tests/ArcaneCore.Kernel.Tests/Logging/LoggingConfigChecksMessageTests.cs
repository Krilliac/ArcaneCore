using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Logging;

/// <summary>Pins the exact key, problem and fix text of <see cref="LoggingConfigChecks"/> in its raw-string and bound-options forms, and the capacity boundaries.</summary>
public sealed class LoggingConfigChecksMessageTests
{
    private const string Root = "Logging:ArcaneCore";
    private const string PathFix = "use a file path such as logs/arcanecore.jsonl (relative paths resolve against the working directory)";

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static ConfigIssue Single(params (string Key, string Value)[] values)
        => Assert.Single(new LoggingConfigChecks().Check(Config(values)), static i => i.Severity == ConfigSeverity.Error); // legacy/deprecation warnings are pinned in LoggingFormatWarningTests

    private static IEnumerable<ConfigIssue> Errors(IEnumerable<ConfigIssue> issues) => issues.Where(static i => i.Severity == ConfigSeverity.Error);

    private static void AssertError(ConfigIssue issue, string key, string problem, string fix)
    {
        Assert.Equal(ConfigSeverity.Error, issue.Severity);
        Assert.Equal(key, issue.Key);
        Assert.Equal(problem, issue.Problem);
        Assert.Equal(fix, issue.Fix);
    }

    [Fact]
    public void Check_UnparsableValues_AreReportedOnceWithTheirOwnWording()
    {
        AssertError(Single((Root + ":Console:Mode", "Rainbow")), Root + ":Console:Mode", "'Rainbow' is not one of Color, Plain or Off.", "use Color, Plain or Off");
        AssertError(Single((Root + ":Timestamps", "Mars")), Root + ":Timestamps", "'Mars' is not one of Utc or Local.", "use Utc or Local");
        AssertError(Single((Root + ":IncludeScopes", "maybe")), Root + ":IncludeScopes", "'maybe' is not true or false.", "use true or false");
        AssertError(Single((Root + ":Console:QueueCapacity", "lots")), Root + ":Console:QueueCapacity", "'lots' is not a whole number.", "use 1 to 1000000 lines");
        AssertError(Single((Root + ":File:RollSizeMb", "big")), Root + ":File:RollSizeMb", "'big' is not a whole number.", "use 0 (never roll by size) or a positive number of MiB");
        AssertError(Single((Root + ":Json:Retain", "many")), Root + ":Json:Retain", "'many' is not a whole number.", "use 0 (keep all) or the number of rolled segments to keep");
    }

    [Fact]
    public void Check_NumericEnumText_IsRejected_AndNamesAreCaseInsensitive()
    {
        AssertError(Single((Root + ":Console:Mode", "1")), Root + ":Console:Mode", "'1' is not one of Color, Plain or Off.", "use Color, Plain or Off");
        Assert.Empty(new LoggingConfigChecks().Check(Config((Root + ":Console:Mode", "PLAIN"))));
    }

    [Fact]
    public void Check_RuleMessages_ForTypedValues()
    {
        AssertError(Single((Root + ":Console:QueueCapacity", "0")), Root + ":Console:QueueCapacity", "must be 1 to 1000000 lines; 0 is out of range.", "use 1 to 1000000 lines");
        AssertError(Single((Root + ":File:QueueCapacity", "1000001")), Root + ":File:QueueCapacity", "must be 1 to 1000000 lines; 1000001 is out of range.", "use 1 to 1000000 lines");
        AssertError(Single((Root + ":File:RollSizeMb", "-1")), Root + ":File:RollSizeMb", "must be 0 (never roll by size) or a positive number of MiB; -1 is out of range.", "use 0 or a positive number of MiB");
        AssertError(Single((Root + ":File:Retain", "-2")), Root + ":File:Retain", "must be 0 (keep all) or the number of rolled segments to keep; -2 is out of range.", "use 0 or the number of segments to keep");

        // Format unset and only the legacy Json file on: the Json:* tuning keys are the ones in force and the ones reported.
        AssertError(Single((Root + ":Json:Enabled", "true"), (Root + ":Json:Retain", "-2")), Root + ":Json:Retain", "must be 0 (keep all) or the number of rolled segments to keep; -2 is out of range.", "use 0 or the number of segments to keep");
        // ...and while File is the file in force, a bad Json:* tuning key is no error; it is reported as one warning on its own key.
        ConfigIssue[] unused = [.. new LoggingConfigChecks().Check(Config((Root + ":Format", "Text"), (Root + ":File:Enabled", "true"), (Root + ":Json:Retain", "-2")))];
        Assert.Empty(Errors(unused));
        ConfigIssue warning = Assert.Single(unused);
        Assert.Equal(ConfigSeverity.Warning, warning.Severity);
        Assert.Equal(Root + ":Json:Retain", warning.Key);
    }

    private static ArcaneLoggingOptions Bind(IConfiguration configuration)
    {
        var options = new ArcaneLoggingOptions();
        configuration.GetSection(Root).Bind(options);
        return options;
    }

    /// <summary>Out-of-range tuning in the section that is NOT in force is a warning on that section's key (never an error, never a startup failure).</summary>
    [Fact]
    public void Check_UnusedFileSectionTuning_IsAWarning_NamingTheRuleAndThatItIsNotInForce()
    {
        IConfiguration configuration = Config(
            (Root + ":File:Enabled", "true"), (Root + ":Json:Enabled", "true"), (Root + ":File:RollSizeMb", "-5")); // Format unset: Json is in force

        ConfigIssue[] issues = [.. new LoggingConfigChecks().Check(configuration)];
        Assert.Empty(Errors(issues));
        ConfigIssue issue = Assert.Single(issues, i => i.Key == Root + ":File:RollSizeMb");
        Assert.Equal(ConfigSeverity.Warning, issue.Severity);
        Assert.StartsWith("must be 0 (never roll by size) or a positive number of MiB; -5 is out of range.", issue.Problem, StringComparison.Ordinal);
        Assert.Contains("not in force", issue.Problem, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(issue.Fix));

        LoggingConfigChecks.ThrowIfInvalid(Bind(configuration));
    }

    [Theory]
    [InlineData("Retain", "-1")]
    [InlineData("QueueCapacity", "0")]
    public void Check_UnusedJsonSectionTuning_UnderFormatJson_IsAWarning_NotAnError(string name, string value)
    {
        IConfiguration configuration = Config((Root + ":Format", "Json"), (Root + ":File:Enabled", "true"), (Root + ":Json:" + name, value));

        ConfigIssue[] issues = [.. new LoggingConfigChecks().Check(configuration)];
        Assert.Empty(Errors(issues));
        ConfigIssue issue = Assert.Single(issues, i => i.Key == Root + ":Json:" + name);
        Assert.Equal(ConfigSeverity.Warning, issue.Severity);
        Assert.Contains("not in force", issue.Problem, StringComparison.Ordinal);

        LoggingConfigChecks.ThrowIfInvalid(Bind(configuration));
    }

    /// <summary>The section in force keeps reporting the same bad value as an Error.</summary>
    [Fact]
    public void Check_SameBadTuning_IsAnError_WhenItsSectionIsInForce()
    {
        ConfigIssue[] issues = [.. new LoggingConfigChecks().Check(Config((Root + ":Format", "Json"), (Root + ":File:Enabled", "true"), (Root + ":File:Retain", "-1")))];
        ConfigIssue issue = Assert.Single(issues);
        Assert.Equal(ConfigSeverity.Error, issue.Severity);
        Assert.Equal(Root + ":File:Retain", issue.Key);
    }

    [Fact]
    public void QueueCapacity_Boundaries_AreInclusive()
    {
        Assert.Equal(1_000_000, LoggingConfigChecks.MaxQueueCapacity);
        foreach (string ok in new[] { "1", "1000000" })
        {
            Assert.Empty(new LoggingConfigChecks().Check(Config((Root + ":Console:QueueCapacity", ok), (Root + ":File:QueueCapacity", ok), (Root + ":Json:QueueCapacity", ok))));
        }

        Assert.Single(new LoggingConfigChecks().Check(Config((Root + ":File:QueueCapacity", "0"))));
        Assert.Single(new LoggingConfigChecks().Check(Config((Root + ":File:QueueCapacity", "1000001"))));
        Assert.Empty(new LoggingConfigChecks().Check(Config((Root + ":File:RollSizeMb", "0"), (Root + ":File:Retain", "0"))));
    }

    [Fact]
    public void Check_EnabledSinkPath_Problems()
    {
        AssertError(Single((Root + ":File:Enabled", "true"), (Root + ":File:Path", "   ")), Root + ":File:Path", "'   ' is empty.", PathFix);
        AssertError(Single((Root + ":Format", "Json"), (Root + ":File:Enabled", "true"), (Root + ":File:Path", "	")), Root + ":File:Path", "'	' is empty.", PathFix);
        // An empty (not white space) path is "unset" and falls back to the per-format default, so it is no error.
        Assert.Empty(new LoggingConfigChecks().Check(Config((Root + ":File:Enabled", "true"), (Root + ":File:Path", ""))));
        AssertError(Single((Root + ":Json:Enabled", "true"), (Root + ":Json:Path", "  ")), Root + ":Json:Path", "'  ' is empty.", PathFix);
        AssertError(Single((Root + ":File:Enabled", "true"), (Root + ":File:Path", "logs/")), Root + ":File:Path", "'logs/' names a directory, not a file.", PathFix);
        string bad = "a\0b.log";
        AssertError(Single((Root + ":File:Enabled", "true"), (Root + ":File:Path", bad)), Root + ":File:Path", $"'{bad}' contains characters that are not allowed in a path.", PathFix);

        // A disabled sink's path is not inspected.
        Assert.Empty(new LoggingConfigChecks().Check(Config((Root + ":File:Path", "logs/"))));
    }

    [Fact]
    public void Check_SameFileForTextAndJson_IsNoLongerAnError_BecauseOnlyOneFileIsWritten()
    {
        Assert.Empty(Errors(new LoggingConfigChecks().Check(Config((Root + ":File:Enabled", "true"), (Root + ":Json:Enabled", "true"), (Root + ":File:Path", "logs/same.log"), (Root + ":Json:Path", "logs/same.log")))));
        Assert.Empty(Errors(new LoggingConfigChecks().Check(Config((Root + ":File:Enabled", "true"), (Root + ":Json:Enabled", "true")))));
        Assert.Empty(Errors(new LoggingConfigChecks().Check(Config((Root + ":File:Enabled", "true"), (Root + ":File:Path", "logs/same.log"), (Root + ":Json:Path", "logs/same.log")))));
    }

    [Fact]
    public void ThrowIfInvalid_FailureText_IsKeySpaceProblem_InRuleOrder()
    {
        var options = new ArcaneLoggingOptions { Timestamps = (TimestampKind)9 };
        options.Console.Mode = (ConsoleMode)7;
        options.Console.QueueCapacity = 0;
        options.File.Retain = -1;
        options.Json.RollSizeMb = -5; // Format unset, Json off: not the file in force, so not reported

        var ex = Assert.Throws<OptionsValidationException>(() => LoggingConfigChecks.ThrowIfInvalid(options));
        Assert.Equal(
            [
                Root + ":Console:Mode must be Color, Plain or Off; 7 is not one of them.",
                Root + ":Console:QueueCapacity must be 1 to 1000000 lines; 0 is out of range.",
                Root + ":Timestamps must be Utc or Local; 9 is not one of them.",
                Root + ":File:Retain must be 0 (keep all) or the number of rolled segments to keep; -1 is out of range.",
            ],
            ex.Failures.ToArray());

        // the same bad value is reported once its file is the one in force
        options.Json.Enabled = true;
        ex = Assert.Throws<OptionsValidationException>(() => LoggingConfigChecks.ThrowIfInvalid(options));
        Assert.Contains(Root + ":Json:RollSizeMb must be 0 (never roll by size) or a positive number of MiB; -5 is out of range.", ex.Failures);
        Assert.DoesNotContain(ex.Failures, f => f.StartsWith(Root + ":File:Retain", StringComparison.Ordinal));
    }

    [Fact]
    public void ThrowIfInvalid_NullOptions_Throws_AndDefaultsPass_AndExceptionCarriesOptionsType()
    {
        Assert.Throws<ArgumentNullException>(() => LoggingConfigChecks.ThrowIfInvalid(null!));
        LoggingConfigChecks.ThrowIfInvalid(new ArcaneLoggingOptions());
        var ex = Assert.Throws<OptionsValidationException>(() => LoggingConfigChecks.ThrowIfInvalid(new ArcaneLoggingOptions { Timestamps = (TimestampKind)3 }));
        Assert.Equal(typeof(ArcaneLoggingOptions), ex.OptionsType);
        Assert.Equal(Options.DefaultName, ex.OptionsName);
    }
}

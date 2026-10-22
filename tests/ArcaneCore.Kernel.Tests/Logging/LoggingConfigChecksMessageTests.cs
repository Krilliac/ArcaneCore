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
    private const string PathFix = "use a file path such as logs/arcanecore.log (relative paths resolve against the working directory)";

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static ConfigIssue Single(params (string Key, string Value)[] values)
        => Assert.Single(new LoggingConfigChecks().Check(Config(values)));

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
        AssertError(Single((Root + ":Json:QueueCapacity", "1000001")), Root + ":Json:QueueCapacity", "must be 1 to 1000000 lines; 1000001 is out of range.", "use 1 to 1000000 lines");
        AssertError(Single((Root + ":File:RollSizeMb", "-1")), Root + ":File:RollSizeMb", "must be 0 (never roll by size) or a positive number of MiB; -1 is out of range.", "use 0 or a positive number of MiB");
        AssertError(Single((Root + ":Json:Retain", "-2")), Root + ":Json:Retain", "must be 0 (keep all) or the number of rolled segments to keep; -2 is out of range.", "use 0 or the number of segments to keep");
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
        AssertError(Single((Root + ":File:Enabled", "true"), (Root + ":File:Path", "")), Root + ":File:Path", "'' is empty.", PathFix);
        AssertError(Single((Root + ":Json:Enabled", "true"), (Root + ":Json:Path", "  ")), Root + ":Json:Path", "'  ' is empty.", PathFix);
        AssertError(Single((Root + ":File:Enabled", "true"), (Root + ":File:Path", "logs/")), Root + ":File:Path", "'logs/' names a directory, not a file.", PathFix);
        string bad = "a\0b.log";
        AssertError(Single((Root + ":File:Enabled", "true"), (Root + ":File:Path", bad)), Root + ":File:Path", $"'{bad}' contains characters that are not allowed in a path.", PathFix);

        // A disabled sink's path is not inspected.
        Assert.Empty(new LoggingConfigChecks().Check(Config((Root + ":File:Path", "logs/"))));
    }

    [Fact]
    public void Check_SameFileForTextAndJson_IsReportedOnTheJsonPath()
    {
        ConfigIssue issue = Single((Root + ":File:Enabled", "true"), (Root + ":Json:Enabled", "true"), (Root + ":File:Path", "logs/same.log"), (Root + ":Json:Path", "logs/same.log"));
        AssertError(issue, Root + ":Json:Path", $"must differ from {Root}:File:Path when both sinks are enabled; both are 'logs/same.log'.", "give the JSON-lines sink its own file, such as logs/arcanecore.jsonl");
        Assert.Empty(new LoggingConfigChecks().Check(Config((Root + ":File:Enabled", "true"), (Root + ":Json:Enabled", "true"))));
        Assert.Empty(new LoggingConfigChecks().Check(Config((Root + ":File:Enabled", "true"), (Root + ":File:Path", "logs/same.log"), (Root + ":Json:Path", "logs/same.log"))));
    }

    [Fact]
    public void ThrowIfInvalid_FailureText_IsKeySpaceProblem_InRuleOrder()
    {
        var options = new ArcaneLoggingOptions { Timestamps = (TimestampKind)9 };
        options.Console.Mode = (ConsoleMode)7;
        options.Console.QueueCapacity = 0;
        options.File.Retain = -1;
        options.Json.RollSizeMb = -5;

        var ex = Assert.Throws<OptionsValidationException>(() => LoggingConfigChecks.ThrowIfInvalid(options));
        Assert.Equal(
            [
                Root + ":Console:Mode must be Color, Plain or Off; 7 is not one of them.",
                Root + ":Console:QueueCapacity must be 1 to 1000000 lines; 0 is out of range.",
                Root + ":Timestamps must be Utc or Local; 9 is not one of them.",
                Root + ":File:Retain must be 0 (keep all) or the number of rolled segments to keep; -1 is out of range.",
                Root + ":Json:RollSizeMb must be 0 (never roll by size) or a positive number of MiB; -5 is out of range.",
            ],
            ex.Failures.ToArray());
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

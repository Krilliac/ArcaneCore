using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Ops.Cli;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

public sealed class ConfigValidationTests
{
    private const string Valid = "Server=127.0.0.1;Port=3306;Database=x;User=arcane;Password=Sup3rSecret!;";

    private static IConfiguration Config(params (string Key, string? Value)[] values)
    {
        var all = new Dictionary<string, string?>
        {
            ["Database:Auth:Provider"] = "MariaDb",
            ["Database:Auth:ConnectionString"] = Valid,
            ["Database:Characters:Provider"] = "MariaDb",
            ["Database:Characters:ConnectionString"] = Valid,
            ["Database:World:Provider"] = "MariaDb",
            ["Database:World:ConnectionString"] = Valid,
        };
        foreach ((string key, string? value) in values)
        {
            all[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(all).Build();
    }

    private static ConfigReport Run(IConfiguration configuration) => OpsCli.Validate(configuration);

    [Fact]
    public void ShippedAppsettings_HasNoErrors()
    {
        IConfiguration shipped = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build();
        Assert.Equal(0, Run(shipped).ErrorCount);
    }

    [Fact]
    public void BindAddress_NotAnIp_ReportsKeyValueAndFix()
    {
        ConfigIssue issue = Assert.Single(Run(Config(("World:BindAddress", "localhots"))).Issues);
        Assert.Equal("World:BindAddress", issue.Key);
        Assert.Contains("localhots", issue.Problem);
        Assert.Contains("World__BindAddress", issue.ToString());
        Assert.Equal(ConfigSeverity.Error, issue.Severity);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("eighty")]
    public void Port_Invalid_IsAnError(string port)
        => Assert.Contains(Run(Config(("World:Port", port))).Issues, i => i.Key == "World:Port" && i.Severity == ConfigSeverity.Error);

    [Fact]
    public void TickInterval_Zero_IsAnError_AndSlowIsAWarning()
    {
        Assert.Contains(Run(Config(("World:TickIntervalMs", "0"))).Issues, i => i.Key == "World:TickIntervalMs" && i.Severity == ConfigSeverity.Error);
        Assert.Contains(Run(Config(("World:TickIntervalMs", "150"))).Issues, i => i.Key == "World:TickIntervalMs" && i.Severity == ConfigSeverity.Warning);
    }

    [Fact]
    public void SlowWorldUpdateMeasure_UnknownValue_IsAnError_AndKnownValuesAreFine()
    {
        Assert.Contains(Run(Config(("PerformanceLog:SlowWorldUpdateMeasure", "Sometimes"))).Issues, i => i.Key == "PerformanceLog:SlowWorldUpdateMeasure" && i.Severity == ConfigSeverity.Error);
        Assert.Empty(Run(Config(("PerformanceLog:SlowWorldUpdateMeasure", "tickduration"))).Issues);
        Assert.Empty(Run(Config(("PerformanceLog:SlowWorldUpdateMeasure", "FrameInterval"))).Issues);
    }

    [Fact]
    public void CharactersPerRealm_11_IsAnError()
        => Assert.Contains(Run(Config(("World:CharactersPerRealm", "11"))).Issues, i => i.Key == "World:CharactersPerRealm");

    [Fact]
    public void MissingDirectory_IsAnError_AndEmptyIsFine()
    {
        Assert.Contains(Run(Config(("World:Maps:DataDirectory", "Z:\\definitely\\not\\here"))).Issues, i => i.Key == "World:Maps:DataDirectory");
        Assert.Empty(Run(Config(("World:Maps:DataDirectory", ""))).Issues);
    }

    [Fact]
    public void AllProblemsAreReported_NotJustTheFirst()
    {
        ConfigReport report = Run(Config(("World:Port", "0"), ("World:BindAddress", "nope"), ("World:CharactersPerRealm", "0")));
        Assert.Equal(3, report.ErrorCount);
    }

    [Fact]
    public void ProviderMismatch_IsReported_PerComponent()
    {
        ConfigReport sqliteString = Run(Config(("Database:Auth:Provider", "Sqlite")));
        Assert.Contains(sqliteString.Issues, i => i.Key == "Database:Auth:ConnectionString" && i.Problem.Contains("Data Source"));

        ConfigReport mariaString = Run(Config(("Database:World:ConnectionString", "Data Source=world.db")));
        Assert.Contains(mariaString.Issues, i => i.Key == "Database:World:ConnectionString" && i.Problem.Contains("Server"));
    }

    [Fact]
    public void SingleDatabaseLayout_FallsBackToTheDatabaseSection()
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = "Data Source=arcane.db",
        }).Build();
        Assert.Empty(Run(config).Issues);
    }

    [Fact]
    public void NoConnectionString_IsAnError_AndUnknownProvider_IsAnError()
    {
        Assert.Contains(Run(Config(("Database:Auth:ConnectionString", ""))).Issues, i => i.Key == "Database:Auth:ConnectionString");
        Assert.Contains(Run(Config(("Database:Auth:Provider", "Oracle"))).Issues, i => i.Key == "Database:Auth:Provider");
    }

    [Fact]
    public void PasswordsNeverAppearInAnyMessage()
    {
        ConfigReport report = Run(Config(
            ("Database:Auth:ConnectionString", "Host=db.example.org;User=arcane;Password=arcane;SSL Mode=;"),
            ("Database:Characters:ConnectionString", "Server=10.0.0.5;User=bob;Password=Sup3rSecret!;;;=="),
            ("Database:World:Provider", "Sqlite")));
        Assert.NotEmpty(report.Issues);
        foreach (ConfigIssue issue in report.Issues)
        {
            Assert.DoesNotContain("Sup3rSecret", issue.ToString());
            Assert.DoesNotContain("Password=", issue.ToString());
        }
    }

    [Fact]
    public void DefaultCredentials_OnARemoteDatabase_Warn_ButNotOnLoopback()
    {
        ConfigReport remote = Run(Config(("Database:Auth:ConnectionString", "Server=db.example.org;Database=a;User=arcane;Password=arcane;")));
        Assert.Contains(remote.Issues, i => i.Severity == ConfigSeverity.Warning && i.Key == "Database:Auth:ConnectionString");

        ConfigReport local = Run(Config(("Database:Auth:ConnectionString", "Server=127.0.0.1;Database=a;User=arcane;Password=arcane;"), ("World:BindAddress", "127.0.0.1")));
        Assert.Empty(local.Issues);
    }

    [Fact]
    public void Strict_PromotesWarningsToInvalid()
    {
        (string, string?)[] warn = [("World:TickIntervalMs", "150")];
        Assert.False(Run(Config(warn)).IsInvalid);
        Assert.True(Run(Config([.. warn, ("Startup:Strict", "true")])).IsInvalid);
    }

    [Fact]
    public void CheckConfigVerb_Returns0ForValid_And78WithEveryErrorListed_ForInvalid()
    {
        var ok = new StringWriter();
        Assert.True(OpsCli.TryRun(["check-config"], Config(), ok, out int okCode));
        Assert.Equal(ExitCodes.Success, okCode);

        var bad = new StringWriter();
        Assert.True(OpsCli.TryRun(["check-config"], Config(("World:Port", "0"), ("World:CharactersPerRealm", "99")), bad, out int badCode));
        Assert.Equal(ExitCodes.InvalidConfiguration, badCode);
        Assert.Contains("World:Port", bad.ToString());
        Assert.Contains("World:CharactersPerRealm", bad.ToString());
    }

    [Fact]
    public void UnknownVerb_IsUsageError64_NeverTheRestartCode()
    {
        var output = new StringWriter();
        Assert.True(OpsCli.TryRun(["chek-config"], Config(), output, out int code));
        Assert.Equal(ExitCodes.Usage, code);
        Assert.NotEqual(ExitCodes.Restart, code);
        Assert.Contains("check-config", output.ToString());
    }

    [Theory]
    [InlineData("--World:Port=1")]
    [InlineData("World:Port=1")]
    [InlineData("/x")]
    public void HostOptions_AreNotVerbs(string arg)
        => Assert.False(OpsCli.TryRun([arg], Config(), new StringWriter(), out _));
}

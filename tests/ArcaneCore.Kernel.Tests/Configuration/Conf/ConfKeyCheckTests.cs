using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Configuration.Conf;

public sealed class ConfKeyCheckTests : IDisposable
{
    private readonly ConfCatalog _catalog = ConfFixtureCatalog.Create();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-confk-" + Guid.NewGuid().ToString("N")[..8]);

    public ConfKeyCheckTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A late watcher handle on Windows: the temp directory is not the subject of the test.
        }
    }

    private IConfigurationRoot Root(string text)
    {
        string path = Path.Combine(_dir, "world.conf");
        File.WriteAllText(path, text);
        return new ConfigurationBuilder().Add(new ConfConfigurationSource { Path = path }).Build();
    }

    private ConfigIssue[] Issues(string text, ConfigDaemon daemon = ConfigDaemon.World)
        => [.. new ConfKeyCheck(_catalog, daemon).Check(Root(text))];

    [Fact]
    public void UnknownKey_IsAWarningNamingFileLineAndClosestKey()
    {
        ConfigIssue issue = Assert.Single(Issues("# c\nFixture.Port = 1\nFixture.Prot = 2\n"));
        Assert.Equal(ConfigSeverity.Warning, issue.Severity);
        Assert.Contains("world.conf:3", issue.Problem, StringComparison.Ordinal);
        Assert.Contains("Did you mean Fixture.Port?", issue.Problem, StringComparison.Ordinal);
        Assert.Contains("Fixture.Port", issue.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownKey_FarFromEverything_SaysNoCloseMatch()
    {
        ConfigIssue issue = Assert.Single(Issues("Completely.Different.Thing = 2\n"));
        Assert.Contains("No close match", issue.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyOfTheOtherDaemon_IsAWarningNamingItsFile()
    {
        ConfigIssue issue = Assert.Single(Issues("Auth.Port = 3724\n"));
        Assert.Equal(ConfigSeverity.Warning, issue.Severity);
        Assert.Contains("realm.conf", issue.Fix, StringComparison.Ordinal);

        ConfigIssue reverse = Assert.Single(Issues("Fixture.Port = 1\n", ConfigDaemon.Realm));
        Assert.Contains("world.conf", reverse.Fix, StringComparison.Ordinal);
        Assert.Empty(Issues("Startup.Strict = true\nDatabase.ConnectionString = x\n"));
        Assert.Empty(Issues("Startup.Strict = true\nDatabase.ConnectionString = x\n", ConfigDaemon.Realm));
    }

    [Fact]
    public void KeysUnderAMapOrSequence_AndFrameworkPrefixes_AreKnown()
    {
        Assert.Empty(Issues("Fixture.Map.zeta = 5\nFixture.List.0 = a\nFixture.Items.0.Name = x\nLogging.LogLevel.Foo = Debug\nLogging:LogLevel:Microsoft.EntityFrameworkCore = Warning\nHostOptions.ShutdownTimeout = 00:00:30\n"));
        Assert.Single(Issues("Fixture.Map.zeta = notanumber\n"), i => i.Severity == ConfigSeverity.Error);
    }

    [Fact]
    public void SectionUsedAsKey_IsAWarning()
    {
        ConfigIssue issue = Assert.Single(Issues("Fixture.Sub = 5\n"));
        Assert.Equal(ConfigSeverity.Warning, issue.Severity);
        Assert.Contains("section", issue.Problem, StringComparison.Ordinal);
        Assert.Contains("Fixture.Sub.Depth", issue.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeMismatch_IsAnErrorWithFileAndLine()
    {
        ConfigIssue[] issues = Issues("Fixture.Enabled = 1\nFixture.Port = abc\nFixture.Mode = Slow\nFixture.Ratio = 1,5\nFixture.Wait = soon\n");
        Assert.Equal(5, issues.Length);
        Assert.All(issues, i => Assert.Equal(ConfigSeverity.Error, i.Severity));
        Assert.Contains("world.conf:1", issues[0].Problem, StringComparison.Ordinal);
        Assert.Contains("true", issues[0].Problem, StringComparison.Ordinal);
        Assert.Contains("world.conf:3", issues[2].Problem, StringComparison.Ordinal);
        Assert.Contains("Off | Fast", issues[2].Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void UnquotedValueWithInlineHash_IsAWarning()
    {
        ConfigIssue issue = Assert.Single(Issues("Fixture.Name = abc # note\n"));
        Assert.Equal(ConfigSeverity.Warning, issue.Severity);
        Assert.Contains("world.conf:1", issue.Problem, StringComparison.Ordinal);
        Assert.Empty(Issues("Fixture.Name = \"abc # note\"\nFixture.Path = a#b\n"));
    }

    [Fact]
    public void StartupStrict_PromotesAnUnknownKeyToInvalid()
    {
        IConfigurationRoot lax = Root("Fixture.Prot = 2\n");
        Assert.False(ConfigReport.Run(lax, [new ConfKeyCheck(_catalog, ConfigDaemon.World)]).IsInvalid);

        IConfigurationRoot strict = Root("Startup.Strict = true\nFixture.Prot = 2\n");
        ConfigReport report = ConfigReport.Run(strict, [new ConfKeyCheck(_catalog, ConfigDaemon.World)]);
        Assert.True(report.IsInvalid);
        Assert.Equal(1, report.WarningCount);
    }

    [Fact]
    public void NoConfProvider_MeansNoIssues()
    {
        IConfigurationRoot root = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Whatever:Typo"] = "x" }).Build();
        Assert.Empty(new ConfKeyCheck(_catalog, ConfigDaemon.World).Check(root));
        Assert.Empty(new ConfKeyCheck(_catalog, ConfigDaemon.World).Check(root.GetSection("Whatever")));
    }
}

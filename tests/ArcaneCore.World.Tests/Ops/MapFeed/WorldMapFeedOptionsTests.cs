using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.World.Ops.Control;
using ArcaneCore.World.Ops.MapFeed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// <c>World:Control:MapFeed</c> (docs/areas/map-feed.md, "Options"): the defaults, the ranges, the startup checks, and that the daemon's
/// production registration binds the section and adds the feed without turning it on.
/// </summary>
public sealed class WorldMapFeedOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static ConfigIssue[] Check(params (string Key, string Value)[] values)
        => [.. new MapFeedConfigChecks().Check(Config(values))];

    [Fact]
    public void TheDefaults_AreOff_AndTheDocumentedLimits()
    {
        var options = new WorldMapFeedOptions();

        Assert.False(options.Enabled);
        Assert.Equal(5, options.MaxHz);
        Assert.Equal(4096, options.MaxEntitiesPerFrame);
        Assert.Equal(2, options.MaxConnections);
        Assert.Empty(options.Problems());
        Assert.Equal("World:Control:MapFeed", WorldMapFeedOptions.SectionName);
        Assert.Equal("arcanecore-mapfeed-" + WorldControlPipeOptions.PidToken, WorldMapFeedOptions.PipeNameTemplate);
    }

    [Theory]
    [InlineData(1, 16, 1)]
    [InlineData(60, 65536, 16)]
    [InlineData(5, 4096, 2)]
    public void TheEdgesOfTheRanges_AreValid(int hz, int entities, int connections)
        => Assert.Empty(new WorldMapFeedOptions { MaxHz = hz, MaxEntitiesPerFrame = entities, MaxConnections = connections }.Problems());

    [Theory]
    [InlineData(0, 4096, 2, "MaxHz")]
    [InlineData(-1, 4096, 2, "MaxHz")]
    [InlineData(61, 4096, 2, "MaxHz")]
    [InlineData(5, 15, 2, "MaxEntitiesPerFrame")]
    [InlineData(5, 65537, 2, "MaxEntitiesPerFrame")]
    [InlineData(5, 0, 2, "MaxEntitiesPerFrame")]
    [InlineData(5, 4096, 0, "MaxConnections")]
    [InlineData(5, 4096, 17, "MaxConnections")]
    [InlineData(5, 4096, -2, "MaxConnections")]
    public void AValueOutsideItsRange_IsAProblemNamingTheKey(int hz, int entities, int connections, string key)
    {
        IReadOnlyList<string> problems = new WorldMapFeedOptions { MaxHz = hz, MaxEntitiesPerFrame = entities, MaxConnections = connections }.Problems();

        string problem = Assert.Single(problems);
        Assert.StartsWith(key, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryProblem_IsListedAtOnce()
        => Assert.Equal(3, new WorldMapFeedOptions { MaxHz = 0, MaxEntitiesPerFrame = 0, MaxConnections = 0 }.Problems().Count);

    [Fact]
    public void NoConfiguration_NoIssues()
        => Assert.Empty(Check());

    [Fact]
    public void AValidEnabledSection_HasNoIssues_OnWindows()
    {
        ConfigIssue[] issues = Check(
            ("World:Control:MapFeed:Enabled", "true"), ("World:Control:MapFeed:MaxHz", "10"),
            ("World:Control:MapFeed:MaxEntitiesPerFrame", "2000"), ("World:Control:MapFeed:MaxConnections", "4"));

        if (OperatingSystem.IsWindows())
        {
            Assert.Empty(issues);
        }
        else
        {
            // Elsewhere the feed cannot run (a Windows named pipe with an access list): the operator who asked for it hears so.
            ConfigIssue issue = Assert.Single(issues);
            Assert.Equal(ConfigSeverity.Warning, issue.Severity);
            Assert.Equal("World:Control:MapFeed:Enabled", issue.Key);
        }
    }

    [Theory]
    [InlineData("World:Control:MapFeed:Enabled", "maybe")]
    [InlineData("World:Control:MapFeed:MaxHz", "fast")]
    [InlineData("World:Control:MapFeed:MaxHz", "2.5")]
    [InlineData("World:Control:MapFeed:MaxEntitiesPerFrame", "many")]
    [InlineData("World:Control:MapFeed:MaxConnections", "two")]
    public void AValueThatCannotBeRead_IsAnError_EvenWhileTheFeedIsOff(string key, string value)
    {
        ConfigIssue issue = Assert.Single(Check((key, value)), i => i.Key == key);

        Assert.Equal(ConfigSeverity.Error, issue.Severity);
    }

    [Theory]
    [InlineData("World:Control:MapFeed:MaxHz", "0")]
    [InlineData("World:Control:MapFeed:MaxHz", "61")]
    [InlineData("World:Control:MapFeed:MaxEntitiesPerFrame", "15")]
    [InlineData("World:Control:MapFeed:MaxEntitiesPerFrame", "65537")]
    [InlineData("World:Control:MapFeed:MaxConnections", "0")]
    [InlineData("World:Control:MapFeed:MaxConnections", "17")]
    public void AnOutOfRangeValue_IsAnError_WhileTheFeedIsOn_AndOnlyAWarningWhileItIsOff(string key, string value)
    {
        ConfigIssue enabled = Assert.Single(Check(("World:Control:MapFeed:Enabled", "true"), (key, value)), i => i.Key == key);
        ConfigIssue disabled = Assert.Single(Check((key, value)), i => i.Key == key);

        Assert.Equal(ConfigSeverity.Error, enabled.Severity);
        Assert.Equal(ConfigSeverity.Warning, disabled.Severity);
        Assert.Contains(value, enabled.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryProblemOfTheSection_IsReportedTogether()
    {
        ConfigIssue[] issues = Check(
            ("World:Control:MapFeed:Enabled", "true"), ("World:Control:MapFeed:MaxHz", "0"),
            ("World:Control:MapFeed:MaxEntitiesPerFrame", "x"), ("World:Control:MapFeed:MaxConnections", "99"));

        Assert.Contains(issues, i => i.Key == "World:Control:MapFeed:MaxHz");
        Assert.Contains(issues, i => i.Key == "World:Control:MapFeed:MaxEntitiesPerFrame");
        Assert.Contains(issues, i => i.Key == "World:Control:MapFeed:MaxConnections");
        Assert.All(issues.Where(i => i.Key.EndsWith("MaxHz", StringComparison.Ordinal)), i => Assert.Equal(ConfigSeverity.Error, i.Severity));
    }

    [Fact]
    public void TheConfigCheck_IsOneTheDaemonRuns()
        => Assert.Contains(typeof(MapFeedConfigChecks), ConfigCheckDiscovery.ConcreteTypes(ArcaneCore.World.Ops.Cli.OpsCli.CheckAssemblies));

    [Fact]
    public void ProductionRegistration_BindsTheSection_AddsTheFeed_AndLeavesItOff()
    {
        var services = new ServiceCollection();
        services.AddWorldMapFeed(Config());
        using ServiceProvider provider = services.BuildServiceProvider();

        WorldMapFeedOptions options = provider.GetRequiredService<IOptions<WorldMapFeedOptions>>().Value;
        Assert.False(options.Enabled);
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(WorldMapFeed));
    }

    [Fact]
    public void ProductionRegistration_ReadsTheSectionsKeys()
    {
        var services = new ServiceCollection();
        services.AddWorldMapFeed(Config(
            ("World:Control:MapFeed:Enabled", "true"), ("World:Control:MapFeed:MaxHz", "12"),
            ("World:Control:MapFeed:MaxEntitiesPerFrame", "100"), ("World:Control:MapFeed:MaxConnections", "7")));
        using ServiceProvider provider = services.BuildServiceProvider();

        WorldMapFeedOptions options = provider.GetRequiredService<IOptions<WorldMapFeedOptions>>().Value;

        Assert.True(options.Enabled);
        Assert.Equal(12, options.MaxHz);
        Assert.Equal(100, options.MaxEntitiesPerFrame);
        Assert.Equal(7, options.MaxConnections);
    }
}

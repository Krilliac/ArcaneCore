using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.World.Ops.Cli;
using ArcaneCore.World.Ops.MapFeed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// <c>World:Control:MapFeed:Actions</c> and <c>World:Control:MapFeed:Web</c> (design 1.5, 0.10): the defaults the design states, the startup
/// checks, and that the daemon's production registration binds both without turning anything on.
/// </summary>
public sealed class MapFeedActionsOptionsTests
{
    private const string Root = "World:Control:MapFeed:Actions:";

    /// <summary>A created account's SID (RID 1001) of a made-up domain: acceptable as the web pipe's one client.</summary>
    private const string ClientSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static ConfigIssue[] Check(params (string Key, string Value)[] values) => [.. new MapFeedActionsConfigChecks().Check(Config(values))];

    [Fact]
    public void TheDefaults_AreOff_AndTheDocumentedValues()
    {
        var options = new MapFeedActionsOptions();

        Assert.Equal("World:Control:MapFeed:Actions", MapFeedActionsOptions.SectionName);
        Assert.False(options.Enabled);
        Assert.False(options.AllowOnLiveServer);
        Assert.False(options.WebLogin);
        Assert.Equal(AccountSecurity.Administrator, options.LocalSecurity);
        Assert.Equal(AccountSecurity.Moderator, options.WebLoginMinSecurity);
        Assert.Equal(AccountSecurity.GameMaster, options.PublicMaxSecurity);
        Assert.Equal(30, options.SettleSeconds);
        Assert.Equal(1, options.BulkConfirmAbove);
        Assert.Equal(10, options.LocalPerSecond);
        Assert.Equal(20, options.LocalBurst);
        Assert.Equal(2, options.WebPerSecond);
        Assert.Equal(5, options.WebBurst);
        Assert.Equal(90, options.WebAuditDays);
        Assert.True(options.AuditLocalDurable);
        Assert.Null(options.LocalAccountId);
    }

    [Fact]
    public void ThePublicDenyList_DefaultsToTheDestructiveRoots_AndAllowsNothingElse()
    {
        string[] deny = [.. new MapFeedActionsOptions().PublicDeny];

        // Design 1.5: .account, .server lifecycle and config writes, .reload, .hotcode, .hotmodule, .botctl raw, .ban/.unban.
        foreach (string root in new[] { "account", "reload", "hotcode", "hotmodule", "botctl raw", "ban", "unban" })
        {
            Assert.Contains(root, deny, StringComparer.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("tele", deny, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheWebPipeDefaults_AreOff_WithTwoConnections()
    {
        var options = new WebFeedPipeOptions();

        Assert.Equal("World:Control:MapFeed:Web", WebFeedPipeOptions.SectionName);
        Assert.False(options.Enabled);
        Assert.Null(options.ClientAccount);
        Assert.Equal(2, options.MaxConnections);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-1)]
    public void TheWebPipesConnectionCount_MustBeOneToEight(int count)
        => Assert.NotEmpty(new WebFeedPipeOptions { Enabled = true, ClientAccount = ClientSid, MaxConnections = count }.Problems());

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void TheEdgesOfTheWebPipesConnectionCount_AreValid(int count)
        => Assert.Empty(new WebFeedPipeOptions { Enabled = true, ClientAccount = ClientSid, MaxConnections = count }.Problems());

    [Theory]
    [InlineData("Everyone")]
    [InlineData("BUILTIN\\Users")]
    [InlineData("builtin\\administrators")]
    [InlineData("NT AUTHORITY\\SYSTEM")]
    [InlineData("NT AUTHORITY\\Authenticated Users")]
    [InlineData("NT SERVICE\\atlas-public")]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-11")]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-19")]
    [InlineData("S-1-5-20")]
    [InlineData("S-1-5-32-545")]
    public void TheWebPipesClientAccount_MayNotBeAGroupOrABuiltInPrincipal(string account)
        => Assert.Contains(new WebFeedPipeOptions { Enabled = true, ClientAccount = account }.Problems(), p => p.Contains("ClientAccount", StringComparison.Ordinal));

    [Theory]
    [InlineData("MACHINE\\atlas-public")]
    [InlineData(ClientSid)]
    public void TheWebPipesClientAccount_MayBeOneNamedAccount(string account)
        => Assert.Empty(new WebFeedPipeOptions { Enabled = true, ClientAccount = account }.Problems());

    [Fact]
    public void NoConfiguration_NoIssues() => Assert.Empty(Check());

    [Fact]
    public void ASaneEnabledSection_HasNoErrors()
        => Assert.DoesNotContain(Check((Root + "Enabled", "true"), (Root + "LocalSecurity", "GameMaster")), i => i.Severity == ConfigSeverity.Error);

    [Theory]
    [InlineData("LocalSecurity", "Console")] // no Console level exists (Account.cs:8-14): impossible by type
    [InlineData("LocalSecurity", "7")]
    [InlineData("LocalSecurity", "-1")]
    [InlineData("LocalSecurity", "root")]
    [InlineData("PublicMaxSecurity", "Console")]
    [InlineData("Enabled", "maybe")]
    [InlineData("SettleSeconds", "soon")]
    [InlineData("WebPerSecond", "fast")]
    public void AValueThatIsNotAnAllowedOne_IsAnError(string key, string value)
    {
        ConfigIssue issue = Assert.Single(Check((Root + key, value)), i => i.Key == Root + key);

        Assert.Equal(ConfigSeverity.Error, issue.Severity);
        Assert.False(string.IsNullOrWhiteSpace(issue.Fix));
    }

    [Theory]
    [InlineData("Player")]
    [InlineData("Moderator")]
    [InlineData("GameMaster")]
    [InlineData("Administrator")]
    [InlineData("administrator")]
    public void EveryRealSecurityLevelName_IsAllowedForTheLocalActor(string level)
        => Assert.DoesNotContain(Check((Root + "LocalSecurity", level)), i => i.Severity == ConfigSeverity.Error);

    [Fact]
    public void AllowOnLiveServer_IsAWarning_WhenActionsAreEnabled()
    {
        ConfigIssue issue = Assert.Single(Check((Root + "Enabled", "true"), (Root + "AllowOnLiveServer", "true")), i => i.Key == Root + "AllowOnLiveServer");

        Assert.Equal(ConfigSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void APublicMaxSecurityOfAdministrator_IsAWarning()
    {
        ConfigIssue issue = Assert.Single(Check((Root + "PublicMaxSecurity", "Administrator")), i => i.Key == Root + "PublicMaxSecurity");

        Assert.Equal(ConfigSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void TheConfigCheck_IsOneTheDaemonRuns()
        => Assert.Contains(typeof(MapFeedActionsConfigChecks), ConfigCheckDiscovery.ConcreteTypes(OpsCli.CheckAssemblies));

    [Fact]
    public void ProductionRegistration_BindsBothSections_AndLeavesEverythingOff()
    {
        var services = new ServiceCollection();
        services.AddWorldMapFeed(Config());
        using ServiceProvider provider = services.BuildServiceProvider();

        MapFeedActionsOptions actions = provider.GetRequiredService<IOptions<MapFeedActionsOptions>>().Value;
        WebFeedPipeOptions web = provider.GetRequiredService<IOptions<WebFeedPipeOptions>>().Value;

        Assert.False(actions.Enabled);
        Assert.False(actions.WebLogin);
        Assert.False(web.Enabled);
    }

    [Fact]
    public void ProductionRegistration_ReadsTheKeys()
    {
        var services = new ServiceCollection();
        services.AddWorldMapFeed(Config(
            (Root + "Enabled", "true"), (Root + "LocalSecurity", "GameMaster"), (Root + "WebLogin", "true"), (Root + "LocalAccountId", "42"),
            (Root + "PublicDeny:0", "zadmin"), (Root + "WebPerSecond", "3"),
            ("World:Control:MapFeed:Web:Enabled", "true"), ("World:Control:MapFeed:Web:ClientAccount", ClientSid), ("World:Control:MapFeed:Web:MaxConnections", "4")));
        using ServiceProvider provider = services.BuildServiceProvider();

        MapFeedActionsOptions actions = provider.GetRequiredService<IOptions<MapFeedActionsOptions>>().Value;
        WebFeedPipeOptions web = provider.GetRequiredService<IOptions<WebFeedPipeOptions>>().Value;

        Assert.True(actions.Enabled);
        Assert.Equal(AccountSecurity.GameMaster, actions.LocalSecurity);
        Assert.True(actions.WebLogin);
        Assert.Equal(42, actions.LocalAccountId);
        Assert.Contains("zadmin", actions.PublicDeny); // the binder appends to the default list
        Assert.Equal(3, actions.WebPerSecond);
        Assert.True(web.Enabled);
        Assert.Equal(ClientSid, web.ClientAccount);
        Assert.Equal(4, web.MaxConnections);
    }
}

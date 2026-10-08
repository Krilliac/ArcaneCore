using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotConfigurationBindingTests
{
    [Fact]
    public void ExplicitContinents_DoNotAppendToDefaults_ThroughActualDaemonRegistration()
    {
        IConfiguration configuration = Configuration("0", "1");
        using ServiceProvider services = new ServiceCollection().AddLogging().AddWorldDaemon(configuration).BuildServiceProvider();
        PlayerbotOptions options = services.GetRequiredService<IOptions<PlayerbotOptions>>().Value;
        Assert.Equal(new uint[] { 0, 1 }, options.AllowedMaps);
        options.Validate();
        Assert.Equal(options.AllowedMaps, PlayerbotOptions.Bind(configuration).AllowedMaps);
    }

    [Fact]
    public void ExplicitSingleMap_ReplacesDefaults_AndMissingMapKeepsDefaults()
    {
        Assert.Equal(new uint[] { 1 }, PlayerbotOptions.Bind(Configuration("1")).AllowedMaps);
        Assert.Equal(new uint[] { 0, 1 }, PlayerbotOptions.Bind(new ConfigurationBuilder().Build()).AllowedMaps);
    }

    [Fact]
    public void ExplicitDuplicateMaps_StillFailValidation()
    {
        PlayerbotOptions options = PlayerbotOptions.Bind(Configuration("1", "1"));
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void ThePartySection_BindsThroughTheActualDaemonRegistration()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PlayerbotOptions.SectionName + ":Party:InvitePolicy"] = "None",
            [PlayerbotOptions.SectionName + ":Party:Allowlist:0"] = "Guildleader",
            [PlayerbotOptions.SectionName + ":Party:LootRoll"] = "Greed",
            [PlayerbotOptions.SectionName + ":Party:TeleportToLeader"] = "false",
        }).Build();
        using ServiceProvider services = new ServiceCollection().AddLogging().AddWorldDaemon(configuration).BuildServiceProvider();
        PlayerbotOptions options = services.GetRequiredService<IOptions<PlayerbotOptions>>().Value;
        options.Validate();
        Assert.Equal(PlayerbotInvitePolicy.None, options.Party.InvitePolicy);
        Assert.Equal(["Guildleader"], options.Party.Allowlist);
        Assert.Equal(PlayerbotLootRoll.Greed, options.Party.LootRoll);
        Assert.False(options.Party.TeleportToLeader);
        Assert.True(options.Party.AutoRevive);
    }

    private static IConfiguration Configuration(params string[] maps)
        => new ConfigurationBuilder().AddInMemoryCollection(maps.Select((map, index) =>
            new KeyValuePair<string, string?>(PlayerbotOptions.SectionName + ":AllowedMaps:" + index, map))).Build();
}

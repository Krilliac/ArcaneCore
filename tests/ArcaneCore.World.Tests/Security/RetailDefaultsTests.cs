using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Security;

/// <summary>
/// Standing directive: deviations from retail sit behind config options defaulting to retail.
/// vmangos has no connection cap, no channel cap, no writer-drain timeout and no extra
/// movement-field checks, so every knob defaults to off and binds from configuration.
/// </summary>
public sealed class RetailDefaultsTests
{
    [Fact]
    public void WorldHardeningKnobs_DefaultToRetail()
    {
        var w = new WorldOptions();
        Assert.Equal(0, w.MaxConnections);
        Assert.Equal(0, w.MaxConnectionsPerIp);

        var s = new WorldSessionOptions();
        Assert.Equal(TimeSpan.Zero, s.WriterDrainGrace);
        Assert.False(s.StrictMovementFiniteness);

        Assert.Equal(0, new SocialOptions().MaxJoinedChannels);
    }

    [Fact]
    public void WorldSessionKnobs_BindFromTheWorldSection()
    {
        IConfiguration cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:WriterDrainGrace"] = "00:00:05",
            ["World:StrictMovementFiniteness"] = "true",
        }).Build();

        var s = new WorldSessionOptions();
        cfg.GetSection(WorldOptions.SectionName).Bind(s);

        Assert.Equal(TimeSpan.FromSeconds(5), s.WriterDrainGrace);
        Assert.True(s.StrictMovementFiniteness);
    }

    [Fact]
    public void SocialMaxJoinedChannels_BindsFromTheSocialSection()
    {
        IConfiguration cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Social:MaxJoinedChannels"] = "12",
        }).Build();
        var services = new ServiceCollection();
        services.Configure<SocialOptions>(cfg.GetSection(SocialOptions.SectionName));
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(12, provider.GetRequiredService<IOptions<SocialOptions>>().Value.MaxJoinedChannels);
    }

    [Fact]
    public void AddWorldDaemon_RegistersTheSocialSection()
    {
        IConfiguration cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Social:MaxJoinedChannels"] = "9",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorldDaemon(cfg);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(9, provider.GetRequiredService<IOptions<SocialOptions>>().Value.MaxJoinedChannels);
    }
}

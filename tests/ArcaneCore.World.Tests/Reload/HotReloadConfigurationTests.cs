using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

public sealed class HotReloadConfigurationTests
{
    [Fact]
    public void EnabledInAppsettings_IsBoundThroughWorldDaemonRegistration()
    {
        // The production path passes IConfiguration to AddWorldDaemon. The options infrastructure
        // is present because other options are registered, so an unbound IOptions<HotReloadOptions>
        // can silently supply Commands=false ahead of ReloadFeature's IConfiguration fallback.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream("""{"HotReload":{"Commands":true}}"""u8.ToArray()))
            .Build();
        using ServiceProvider services = new ServiceCollection()
            .AddLogging()
            .AddWorldDaemon(configuration)
            .BuildServiceProvider();

        Assert.True(configuration.GetValue<bool>("HotReload:Commands"));
        Assert.True(services.GetRequiredService<IOptions<HotReloadOptions>>().Value.Commands);
        Assert.True(services.GetRequiredService<ReloadFeature>().Enabled);
    }
}

using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// docs/guide/configuration.md: with HotReload:ApplyConfigFileChanges on, an edited world.conf applies its live keys while the daemon runs
/// (off by default: vmangos has no file watcher). The change notification of the
/// live configuration runs the <c>.reload config</c> pipeline (<see cref="ConfigFileReloadFeature"/>): a live key changes on the
/// world thread, a restart-only key keeps its value. The provider's own file watcher is replaced by an explicit
/// <see cref="IConfigurationRoot.Reload"/>, which fires the same change token without waiting on the file system.
/// </summary>
public sealed class ConfigFileReloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcane-conf-watch-" + Guid.NewGuid().ToString("N"));
    private readonly WorldRuntimeOptions _options = new() { TickIntervalMs = 5, AutosaveIntervalMs = 0, Motd = "original" };
    private readonly WorldRuntime _world;
    private readonly string _path;

    public ConfigFileReloadTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "world.conf");
        File.WriteAllText(_path, "World.Motd = original\nWorld.TickIntervalMs = 5\nWorld.AutosaveIntervalMs = 0\n");
        _world = new WorldRuntime(_options, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
    }

    public void Dispose()
    {
        _world.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private (ConfigurationManager Configuration, ConfigFileReloadFeature Feature) Start(HotReloadOptions? policy = null)
    {
        var configuration = new ConfigurationManager();
        ((IConfigurationBuilder)configuration).Add(new ConfConfigurationSource { Path = _path, Optional = false, ReloadOnChange = false });
        IServiceCollection services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        if (policy is not null)
        {
            services.AddSingleton(Options.Create(policy));
        }

        var feature = new ConfigFileReloadFeature(services.BuildServiceProvider(), NullLogger<ConfigFileReloadFeature>.Instance)
        {
            Debounce = TimeSpan.FromMilliseconds(20),
        };
        feature.Attach(_world);
        _world.Start();
        return (configuration, feature);
    }

    private async Task EditAndNotifyAsync(ConfigurationManager configuration, ConfigFileReloadFeature feature, string text, string expectedMotd)
    {
        File.WriteAllText(_path, text);
        ((IConfigurationRoot)configuration).Reload();
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && _options.Motd != expectedMotd)
        {
            await Task.Delay(20);
        }

        await feature.LastApply.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task EditedConfFile_AppliesALiveKey_AndKeepsARestartOnlyKey()
    {
        (ConfigurationManager configuration, ConfigFileReloadFeature feature) = Start(new HotReloadOptions { ApplyConfigFileChanges = true });
        Assert.True(feature.Enabled);

        await EditAndNotifyAsync(configuration, feature, "World.Motd = edited\nWorld.TickIntervalMs = 77\nWorld.AutosaveIntervalMs = 0\n", "edited");

        Assert.Equal("edited", _options.Motd);
        Assert.Equal(5, _options.TickIntervalMs);
        await feature.StopAsync();
    }

    [Fact]
    public async Task ByDefault_AnEditedFileLeavesTheLiveOptionsAlone_AsInVmangos()
    {
        (ConfigurationManager configuration, ConfigFileReloadFeature feature) = Start(new HotReloadOptions());
        Assert.False(feature.Enabled);

        File.WriteAllText(_path, "World.Motd = edited\n");
        ((IConfigurationRoot)configuration).Reload();
        await Task.Delay(300);

        Assert.Equal("edited", configuration["World:Motd"]);
        Assert.Equal("original", _options.Motd);
        await feature.StopAsync();
    }
}

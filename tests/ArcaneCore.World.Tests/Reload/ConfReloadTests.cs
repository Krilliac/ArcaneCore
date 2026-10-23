using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// <c>.reload config</c> with <c>world.conf</c> in the source list (docs/guide/configuration.md): the throwaway configuration the reload
/// builds reads the file again, a live key is applied, a restart-only key keeps its value and says so, a malformed file fails the reload
/// naming file and line and changes nothing, and the precedence appsettings.json &lt; world.conf holds through the reload.
/// </summary>
public sealed class ConfReloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcane-confreload-" + Guid.NewGuid().ToString("N"));
    private readonly WorldRuntimeOptions _options = new() { TickIntervalMs = 5, AutosaveIntervalMs = 0, Motd = "original", ListenRangeSay = 25 };
    private readonly WorldRuntime _world;

    public ConfReloadTests()
    {
        Directory.CreateDirectory(_directory);
        _world = new WorldRuntime(_options, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
    }

    public void Dispose()
    {
        _world.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private string ConfPath => Path.Combine(_directory, "world.conf");

    private void WriteConf(string text) => File.WriteAllText(ConfPath, text);

    private void WriteJson(string json) => File.WriteAllText(Path.Combine(_directory, "appsettings.json"), json);

    /// <summary>The source order of the real host: appsettings.json, then world.conf (ConfFiles.Attach), with no watcher.</summary>
    private ReloadCoordinator Reloader(bool withJson = false)
    {
        var configuration = new ConfigurationManager();
        if (withJson)
        {
            configuration.AddJsonFile(Path.Combine(_directory, "appsettings.json"), optional: false, reloadOnChange: false);
        }

        ((IConfigurationBuilder)configuration).Add(new ConfConfigurationSource { Path = ConfPath, Optional = false, ReloadOnChange = false });
        IServiceProvider services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).BuildServiceProvider();
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(_world);
        coordinator.Register(new ConfigContentReloadable(services));
        _world.Start();
        return coordinator;
    }

    [Fact]
    public async Task ALiveKeyChangedInWorldConf_IsAppliedByReloadConfig()
    {
        WriteConf("World.AutosaveIntervalMs = 0\nWorld.TickIntervalMs = 5\nWorld.Motd = \"original\"\nWorld.ListenRangeSay = 25\n");
        ReloadCoordinator coordinator = Reloader();
        WriteConf("World.AutosaveIntervalMs = 0\nWorld.TickIntervalMs = 5\nWorld.Motd = \"Reloaded # not a comment; kept\"\nWorld.ListenRangeSay = 40\n");

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal("Reloaded # not a comment; kept", _options.Motd);
        Assert.Equal(40f, _options.ListenRangeSay);
    }

    [Fact]
    public async Task AMalformedWorldConf_FailsTheReload_NamingFileAndLine_AndChangesNothing()
    {
        WriteConf("World.AutosaveIntervalMs = 0\nWorld.TickIntervalMs = 5\nWorld.Motd = \"original\"\n");
        ReloadCoordinator coordinator = Reloader();
        WriteConf("World.AutosaveIntervalMs = 0\nWorld.Motd = \"Changed\"\nWorld.ListenRangeSay 40\nWorld.Other = \"unterminated\n");

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        string text = result.Message + "\n" + string.Join('\n', result.Notes);
        Assert.Contains("world.conf:3", text, StringComparison.Ordinal);
        Assert.Contains("world.conf:4", text, StringComparison.Ordinal); // every problem of the file is reported at once
        Assert.Equal("original", _options.Motd);
        Assert.Equal(25f, _options.ListenRangeSay);
        Assert.Equal(5, _options.TickIntervalMs);
    }

    [Fact]
    public async Task ARestartOnlyKeyChangedInWorldConf_KeepsItsValue_AndSaysSo()
    {
        WriteConf("World.AutosaveIntervalMs = 0\nWorld.TickIntervalMs = 5\n");
        ReloadCoordinator coordinator = Reloader();
        WriteConf("World.AutosaveIntervalMs = 0\nWorld.TickIntervalMs = 77\nWorld.Motd = \"Reloaded\"\n");

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(5, _options.TickIntervalMs);
        Assert.Equal("Reloaded", _options.Motd);
        Assert.Contains("World:TickIntervalMs option can't be changed at reload, using current value (5).", result.Notes);
    }

    [Fact]
    public async Task WorldConfOverridesAppsettings_ThroughTheReload()
    {
        WriteJson("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Motd": "FromJson", "ListenRangeSay": 33 } }""");
        WriteConf("World.Motd = \"FromConf\"\n");
        ReloadCoordinator coordinator = Reloader(withJson: true);

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal("FromConf", _options.Motd); // world.conf is above appsettings.json
        Assert.Equal(33f, _options.ListenRangeSay); // a key only appsettings.json sets still comes through

        WriteConf("World.Motd = \"FromConfAgain\"\nWorld.ListenRangeSay = 44\n");
        result = await coordinator.ReloadAsync("config");
        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal("FromConfAgain", _options.Motd);
        Assert.Equal(44f, _options.ListenRangeSay);
    }
}

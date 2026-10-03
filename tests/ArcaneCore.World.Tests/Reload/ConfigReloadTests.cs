using System.Reflection;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// <c>.reload config</c> (vmangos ServerCommands.cpp:1016 → World::LoadConfigSettings(true),
/// World.cpp:445): the configuration is re-read into a candidate; live options are applied on
/// the world thread, restart-only ones keep their value with the vmangos message
/// (World.cpp:3044-3055), and an unreadable or invalid file changes nothing.
/// </summary>
public sealed class ConfigReloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcane-reload-" + Guid.NewGuid().ToString("N"));
    private readonly WorldRuntimeOptions _options = new() { TickIntervalMs = 5, AutosaveIntervalMs = 0, Motd = "original", ListenRangeSay = 25 };
    private readonly WorldRuntime _world;

    public ConfigReloadTests()
    {
        Directory.CreateDirectory(_directory);
        _world = new WorldRuntime(_options, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
    }

    public void Dispose()
    {
        _world.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private string Write(string json)
    {
        string path = Path.Combine(_directory, "appsettings.json");
        File.WriteAllText(path, json);
        return path;
    }

    private ReloadCoordinator Reloader(IConfiguration configuration, WorldOptions? live = null)
    {
        IServiceCollection services = new ServiceCollection().AddSingleton(configuration);
        if (live is not null)
        {
            services.AddSingleton(Options.Create(live));
        }

        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(_world);
        coordinator.Register(new ConfigContentReloadable(services.BuildServiceProvider()));
        _world.Start();
        return coordinator;
    }

    private static ConfigurationManager FromFile(string path)
    {
        var manager = new ConfigurationManager();
        manager.AddJsonFile(path, optional: false, reloadOnChange: false);
        return manager;
    }

    [Fact]
    public async Task LiveOptions_AreAppliedInPlace_OnTheWorldThread()
    {
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Motd": "Reloaded", "ListenRangeSay": 40, "AllowTwoSideChat": true, "InstantLogoutSecurity": "GameMaster", "Maps": { "GridCleanUpDelayMs": 120000, "GridUnload": false, "GridActivationDistance": 50 } } }""");
        MapOptions maps = _options.Maps;
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Same(_options, _world.Options);
        Assert.Same(maps, _options.Maps);
        Assert.Equal("Reloaded", _options.Motd);
        Assert.Equal(40f, _options.ListenRangeSay);
        Assert.True(_options.AllowTwoSideChat);
        Assert.Equal(AccountSecurity.GameMaster, _options.InstantLogoutSecurity);
        Assert.Equal(120000, _options.Maps.GridCleanUpDelayMs);
        Assert.False(_options.Maps.GridUnload);
        Assert.Equal(50f, _options.Maps.GridActivationDistance);
    }

    [Fact]
    public async Task ARestartOnlyOption_KeepsItsValue_AndSaysSo()
    {
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 77, "Motd": "Reloaded", "Maps": { "DataDirectory": "D:/other" } } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(5, _options.TickIntervalMs);
        Assert.Equal(string.Empty, _options.Maps.DataDirectory);
        Assert.Equal("Reloaded", _options.Motd);
        Assert.Contains("World:TickIntervalMs option can't be changed at reload, using current value (5).", result.Notes);
        Assert.Contains("World:Maps:DataDirectory option can't be changed at reload, using current value ().", result.Notes);
    }

    [Fact]
    public async Task ARestartOnlyOption_ThatDidNotChange_IsNotMentioned()
    {
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.DoesNotContain(result.Notes, n => n.Contains("can't be changed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorldOptionsListener_CannotChange_AtReload()
    {
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Port": 9999, "BindAddress": "127.0.0.1" } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path), new WorldOptions { Port = 8085, BindAddress = "0.0.0.0" });

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Contains("World:Port option can't be changed at reload, using current value (8085).", result.Notes);
        Assert.Contains("World:BindAddress option can't be changed at reload, using current value (0.0.0.0).", result.Notes);
    }

    [Fact]
    public async Task ATruncatedFile_ChangesNothing()
    {
        string path = Write("""{ "World": { "Motd": "Good" } }""");
        ConfigurationManager configuration = FromFile(path);
        ReloadCoordinator coordinator = Reloader(configuration);
        File.WriteAllText(path, """{ "World": { "Motd": "Tru""");

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Equal("original", _options.Motd);
        Assert.Equal(25f, _options.ListenRangeSay);
        Assert.Equal(0, _options.AutosaveIntervalMs);
    }

    [Fact]
    public async Task AMissingFile_ChangesNothing()
    {
        string path = Write("""{ "World": { "Motd": "Good" } }""");
        ConfigurationManager configuration = FromFile(path);
        ReloadCoordinator coordinator = Reloader(configuration);
        File.Delete(path);

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Equal("original", _options.Motd);
    }

    [Fact]
    public async Task AValueOfTheWrongType_ChangesNothing()
    {
        string path = Write("""{ "World": { "Motd": "Changed", "ListenRangeSay": "far" } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Equal("original", _options.Motd);
        Assert.Equal(25f, _options.ListenRangeSay);
    }

    [Fact]
    public async Task NegativeIntervalsAndRanges_AreRejected_ByName()
    {
        string path = Write("""{ "World": { "Motd": "Changed", "AutosaveIntervalMs": -1, "ListenRangeYell": -5, "Maps": { "GridCleanUpDelayMs": -1 } } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Rejected, result.Status);
        Assert.Contains(result.Notes, n => n.Contains("World:AutosaveIntervalMs", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("World:ListenRangeYell", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("World:Maps:GridCleanUpDelayMs", StringComparison.Ordinal));
        Assert.Equal("original", _options.Motd);
    }

    [Fact]
    public async Task AKeyRemovedFromTheFile_ReturnsToItsDefault()
    {
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        await coordinator.ReloadAsync("config");

        Assert.Equal(new WorldRuntimeOptions().Motd, _options.Motd);
        Assert.Equal(new WorldRuntimeOptions().ListenRangeSay, _options.ListenRangeSay);
    }

    [Fact]
    public async Task AConfigurationThatCannotBeReRead_Fails_WithoutChangingAnything()
    {
        IConfiguration frozen = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["World:Motd"] = "x" }).Build();
        ReloadCoordinator coordinator = Reloader(frozen);

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("re-read", result.Message);
        Assert.Equal("original", _options.Motd);
    }

    [Fact]
    public async Task ReloadingTwice_WithNoFileChange_ReportsNoChanges()
    {
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Motd": "original", "ListenRangeSay": 25 } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Contains("no changes", result.Message);
    }

    [Fact]
    public async Task ReloadAll_DoesNotReloadTheConfig()
    {
        string path = Write("""{ "World": { "Motd": "Changed" } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        IReadOnlyList<ReloadResult> results = await coordinator.ReloadAllAsync();

        Assert.Empty(results);
        Assert.Equal("original", _options.Motd);
    }

    [Fact]
    public void EveryWorldOption_IsClassified_LiveOrRestartOnly()
    {
        var expected = new SortedSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo property in typeof(WorldRuntimeOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true } && p.PropertyType != typeof(MapOptions)))
        {
            expected.Add($"World:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(MapOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"World:Maps:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(WorldOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"World:{property.Name}");
        }

        var classified = new SortedSet<string>(WorldConfigKeys.All.Select(k => k.Path), StringComparer.Ordinal);

        Assert.Equal(expected, classified);
        Assert.Equal(classified.Count, WorldConfigKeys.All.Count);
    }

    [Fact]
    public void TheRestartOnlyKeys_AreTheOnesTheProcessCapturesAtStart()
    {
        string[] restartOnly = [.. WorldConfigKeys.All.Where(k => !k.Live).Select(k => k.Path).Order(StringComparer.Ordinal)];

        // TickIntervalMs: WorldRuntime.Run reads it once for the thread's sleep and SpellFeature.Attach
        // for its timer; Maps:DataDirectory: TerrainManager / CollisionServices load at attach (vmangos
        // DataDir, World.cpp:932-935); Port / BindAddress: the listener is bound at start (World.cpp:598).
        Assert.Equal(["World:BindAddress", "World:Maps:DataDirectory", "World:Port", "World:TickIntervalMs"], restartOnly);
    }

    private sealed class NullSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }
}

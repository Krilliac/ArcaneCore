using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.AntiCheat;

/// <summary>The AntiCheat section: the startup check (exit 78 on a bad value) and <c>.reload config</c>, which applies it as a whole or not at all.</summary>
public sealed class AntiCheatConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcane-anticheat-" + Guid.NewGuid().ToString("N"));
    private readonly WorldRuntime _world = new(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);

    public AntiCheatConfigurationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        _world.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private static IReadOnlyList<ConfigIssue> Check(params (string Key, string Value)[] values)
        => [.. new AntiCheatConfigChecks().Check(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build())];

    [Fact]
    public void TheDefaults_PassTheStartupCheck()
        => Assert.Empty(Check());

    [Theory]
    [InlineData("AntiCheat:Action", "Ban")]
    [InlineData("AntiCheat:ScoreKick", "lots")]
    [InlineData("AntiCheat:ScoreKick", "-5")]
    [InlineData("AntiCheat:SpeedClock:MinSamples", "500")]
    [InlineData("AntiCheat:Autoban:FirstBanSeconds", "-1")]
    public void ABadValue_IsAStartupError(string key, string value)
        => Assert.Contains(Check((key, value)), i => i.Severity == ConfigSeverity.Error);

    [Fact]
    public void KickingWithoutTheAutoban_IsAWarning()
    {
        IReadOnlyList<ConfigIssue> issues = Check(("AntiCheat:Action", "Kick"));
        ConfigIssue warning = Assert.Single(issues);
        Assert.Equal(ConfigSeverity.Warning, warning.Severity);
        Assert.Empty(Check(("AntiCheat:Action", "Kick"), ("AntiCheat:Autoban:Enabled", "true")));
    }

    private (ReloadCoordinator Coordinator, AntiCheatFeature Feature) Reloader(string json)
    {
        string path = Path.Combine(_directory, "appsettings.json");
        File.WriteAllText(path, json);
        var configuration = new ConfigurationManager();
        configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
        ServiceProvider services = new ServiceCollection().AddSingleton<IConfiguration>(configuration)
            .AddSingleton(sp => new AntiCheatFeature(sp, NullLogger<AntiCheatFeature>.Instance)).BuildServiceProvider();
        AntiCheatFeature feature = services.GetRequiredService<AntiCheatFeature>();
        feature.Attach(_world);
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(_world);
        coordinator.Register(new ConfigContentReloadable(services));
        _world.Start();
        return (coordinator, feature);
    }

    [Fact]
    public async Task ReloadConfig_AppliesTheAntiCheatSection()
    {
        (ReloadCoordinator coordinator, AntiCheatFeature feature) = Reloader("""{ "AntiCheat": { "Action": "Kick", "ScoreKick": 200, "SpeedClock": { "TolerancePercent": 40 } } }""");
        Assert.Equal(AntiCheatAction.Log, feature.Options.Action);

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(AntiCheatAction.Kick, feature.Options.Action);
        Assert.Equal(200f, feature.Options.ScoreKick);
        Assert.Equal(40, feature.Options.SpeedClock.TolerancePercent);
    }

    [Fact]
    public async Task ReloadConfig_WithABadAntiCheatValue_IsRejected_AndChangesNothing()
    {
        (ReloadCoordinator coordinator, AntiCheatFeature feature) = Reloader("""{ "AntiCheat": { "Action": "Kick", "ScoreKick": 10 } }""");
        AntiCheatOptions before = feature.Options;

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Rejected, result.Status);
        Assert.Same(before, feature.Options);
        Assert.Equal(AntiCheatAction.Log, feature.Options.Action);
    }

    private sealed class NullSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(Kernel.Characters.CharacterState state)
        {
        }
    }
}

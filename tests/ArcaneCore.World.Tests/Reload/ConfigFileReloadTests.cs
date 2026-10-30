using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private readonly ListLogger _log = new();
    private ReloadCoordinator? _shared;
    private int _configBuilds;

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

    /// <summary>
    /// With <paramref name="shareCoordinator"/> the feature uses the coordinator of a <see cref="ReloadFeature"/> (HotReload:Commands on), as in the
    /// daemon, and <c>config</c> counts its builds in <c>_configBuilds</c>.
    /// </summary>
    private (ConfigurationManager Configuration, ConfigFileReloadFeature Feature) Start(
        HotReloadOptions? policy = null, TimeSpan? debounce = null, bool shareCoordinator = false)
    {
        var configuration = new ConfigurationManager();
        ((IConfigurationBuilder)configuration).Add(new ConfConfigurationSource { Path = _path, Optional = false, ReloadOnChange = false });
        IServiceCollection services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        if (policy is not null)
        {
            services.AddSingleton(Options.Create(policy));
        }

        if (shareCoordinator)
        {
            services.AddSingleton(sp => new ReloadFeature(sp, NullLogger<ReloadFeature>.Instance));
        }

        IServiceProvider provider = services.BuildServiceProvider();
        if (shareCoordinator)
        {
            _shared = provider.GetRequiredService<ReloadFeature>().Coordinator;
            _shared.Attach(_world);
            _shared.Register(new CountingConfigReloadable(new ConfigContentReloadable(provider), () => Interlocked.Increment(ref _configBuilds)));
        }

        var feature = new ConfigFileReloadFeature(provider, _log)
        {
            Debounce = debounce ?? TimeSpan.FromMilliseconds(20),
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

    /// <summary>Write the file and fire the change token, without waiting for anything.</summary>
    private void Edit(ConfigurationManager configuration, string text)
    {
        File.WriteAllText(_path, text);
        ((IConfigurationRoot)configuration).Reload();
    }

    /// <summary>Wait until the feature has started an apply (its last-apply task is no longer the initial completed one).</summary>
    private static async Task WaitForAnApplyToStartAsync(ConfigFileReloadFeature feature)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && ReferenceEquals(feature.LastApply, Task.CompletedTask))
        {
            await Task.Delay(10);
        }

        Assert.False(ReferenceEquals(feature.LastApply, Task.CompletedTask), "no apply started");
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

    [Fact]
    public async Task ASaveThatChangesNothing_IsSkipped_AndLeavesAnOperatorOverrideAlone()
    {
        (ConfigurationManager configuration, ConfigFileReloadFeature feature) = Start(new HotReloadOptions { Commands = true, ApplyConfigFileChanges = true }, shareCoordinator: true);
        await _world.InvokeAsync(() => _options.Motd = "operator override"); // set by a command since the file was read

        Edit(configuration, File.ReadAllText(_path)); // the same bytes saved again: the change token fires, no value differs
        await WaitForAnApplyToStartAsync(feature);
        await feature.LastApply.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, _configBuilds);
        Assert.Equal("operator override", _options.Motd);
        Assert.DoesNotContain(_log.Messages(), m => m.StartsWith("Configuration file change applied"));
        await feature.StopAsync();
    }

    [Fact]
    public async Task WhenTheSharedCoordinatorIsBusy_TheApplyRetriesAfterItIsFree()
    {
        (ConfigurationManager configuration, ConfigFileReloadFeature feature) = Start(
            new HotReloadOptions { Commands = true, ApplyConfigFileChanges = true }, shareCoordinator: true);
        var gate = new TaskCompletionSource();
        var blocker = new GateReloadable(gate.Task);
        _shared!.Register(blocker);
        Task<ReloadResult> holding = _shared.ReloadAsync("gate"); // another reload (a .reload command) holds the coordinator
        await blocker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(_shared.IsBusy);

        Edit(configuration, "World.Motd = edited\nWorld.TickIntervalMs = 5\nWorld.AutosaveIntervalMs = 0\n");
        await WaitForAnApplyToStartAsync(feature);
        await Task.Delay(300); // several quiet periods: every retry finds the coordinator busy and keeps the edit pending
        Assert.Equal("original", _options.Motd);
        Assert.Equal(0, _configBuilds);

        gate.SetResult();
        Assert.Equal(ReloadStatus.Applied, (await holding).Status);
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && _options.Motd != "edited")
        {
            await Task.Delay(20);
        }

        Assert.Equal("edited", _options.Motd);
        Assert.Equal(1, _configBuilds);
        await feature.StopAsync();
    }

    [Fact]
    public async Task AnInvalidFile_IsReportedOnce_AndAnUnchangedResaveStaysQuiet()
    {
        (ConfigurationManager configuration, ConfigFileReloadFeature feature) = Start(new HotReloadOptions { Commands = true, ApplyConfigFileChanges = true }, shareCoordinator: true);
        const string Invalid = "World.Motd = rejected\nWorld.MaxCommandsPerTick = 0\nWorld.AutosaveIntervalMs = 0\n";

        Edit(configuration, Invalid);
        await WaitForAnApplyToStartAsync(feature);
        await feature.LastApply.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, _log.Messages().Count(m => m.Contains("was not applied")));
        Assert.Equal(1, _configBuilds);
        Assert.Equal("original", _options.Motd);

        // Saved again without a change: the invalid content was already handled, so no second report and no second build.
        Edit(configuration, Invalid);
        await Task.Delay(300);
        await feature.LastApply.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, _log.Messages().Count(m => m.Contains("was not applied")));
        Assert.Equal(1, _configBuilds);

        // The next real edit is applied.
        await EditAndNotifyAsync(configuration, feature, "World.Motd = fixed\nWorld.AutosaveIntervalMs = 0\n", "fixed");
        Assert.Equal("fixed", _options.Motd);
        Assert.Equal(1, _log.Messages().Count(m => m.Contains("was not applied")));
        await feature.StopAsync();
    }

    [Fact]
    public async Task RepeatedChangeTokens_InsideTheQuietPeriod_CoalesceIntoOneReload()
    {
        (ConfigurationManager configuration, ConfigFileReloadFeature feature) = Start(
            new HotReloadOptions { Commands = true, ApplyConfigFileChanges = true }, debounce: TimeSpan.FromMilliseconds(400), shareCoordinator: true);

        foreach (string motd in new[] { "first", "second", "third" })
        {
            Edit(configuration, $"World.Motd = {motd}\nWorld.AutosaveIntervalMs = 0\n");
            await Task.Delay(20);
        }

        await WaitForAnApplyToStartAsync(feature);
        await feature.LastApply.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, _configBuilds);
        Assert.Equal("third", _options.Motd);
        await feature.StopAsync();
    }

    [Fact]
    public async Task StopAsync_WithAnApplyWaitingForAStoppedWorld_ReturnsWithoutTheSwap_EvenWithNoCommitTimeout()
    {
        (ConfigurationManager configuration, ConfigFileReloadFeature feature) = Start(
            new HotReloadOptions { ApplyConfigFileChanges = true, CommitTimeoutMs = 0 });
        _world.Stop(); // no tick will run the swap the apply is about to queue

        Edit(configuration, "World.Motd = edited\nWorld.TickIntervalMs = 5\nWorld.AutosaveIntervalMs = 0\n");
        await WaitForAnApplyToStartAsync(feature);
        await Task.Delay(300); // the build is long done: the apply is parked on the commit

        await feature.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("original", _options.Motd);
        Assert.Contains(_log.Messages(), m => m.Contains("abandoned"));
    }

    private sealed class ListLogger : ILogger<ConfigFileReloadFeature>
    {
        private readonly List<string> _messages = [];

        public List<string> Messages()
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }

    /// <summary><c>config</c> as the daemon registers it, counting how many candidates were built.</summary>
    private sealed class CountingConfigReloadable(IContentReloadable inner, Action built) : IContentReloadable
    {
        public string Name => inner.Name;

        public bool IncludedInAll => inner.IncludedInAll;

        public Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
        {
            built();
            return inner.BuildAsync(cancellationToken);
        }
    }

    /// <summary>A reload that holds the coordinator until its gate opens (a <c>.reload</c> command still building).</summary>
    private sealed class GateReloadable(Task gate) : IContentReloadable
    {
        public string Name => "gate";

        public bool IncludedInAll => false;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Candidate();
        }

        private sealed class Candidate : ContentCandidate
        {
            public override string Summary => "gate";

            public override void Commit(WorldRuntime world, ReloadTransaction transaction)
            {
            }
        }
    }
}

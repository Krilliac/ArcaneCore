using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>The world host begins the stop-drain lanes: the feature lane before the features stop, the save queue's lane before the save queue stops.</summary>
public sealed class WorldHostStopDrainTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task StopCoreAsync_BeginsBothLanes_InOrder()
    {
        var drain = new ShutdownDrain { Saves = new StopDrainBudget { StallBudget = TimeSpan.FromMilliseconds(300) } };
        var store = new InMemoryCharacterStore();
        var featureSawLanes = new FeatureProbe(drain);
        using IHost host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton<ICharacterStore>(store);
            services.AddSingleton(drain);
            services.AddSingleton<IWorldFeature>(featureSawLanes);
            services.AddSingleton(sp => new CharacterSaveQueue(
                sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance, drain: sp.GetRequiredService<ShutdownDrain>()));
            services.AddSingleton(sp => new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, sp.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance));
            services.AddSingleton<IHostedService>(sp => new WorldHost(
                sp.GetRequiredService<WorldRuntime>(), sp.GetRequiredService<CharacterSaveQueue>(), new CharacterDirectory(), sp.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<WorldHost>.Instance, drain: sp.GetRequiredService<ShutdownDrain>())
            {
                ListenerExpected = false,
            });
        }).Build();

        await host.StartAsync().WaitAsync(Deadline);
        Assert.False(drain.Features.Started);
        Assert.False(drain.Saves.Started);

        CharacterSaveQueue queue = host.Services.GetRequiredService<CharacterSaveQueue>();
        store.SaveOverride = async _ =>
        {
            await Task.Delay(200);
            throw new SqliteException("SQLite Error 5: 'database is locked'.", 5);
        };
        for (int id = 1; id <= 3; id++)
        {
            queue.Enqueue(new CharacterState(id, 0, 12, 0, 0, 83.5f, 0, 1, 0));
        }

        AggregateException failed = await Assert.ThrowsAsync<AggregateException>(() => host.StopAsync().WaitAsync(Deadline));

        InvalidOperationException saves = Assert.IsType<InvalidOperationException>(Assert.Single(failed.InnerExceptions));
        foreach (int id in new[] { 1, 2, 3 })
        {
            Assert.Contains(id.ToString(), saves.Message, StringComparison.Ordinal);
        }

        Assert.True(drain.Features.Started);
        Assert.True(drain.Saves.Started);
        Assert.True(featureSawLanes.FeaturesLaneStartedWhenStopped, "the feature lane must be begun before the features stop");
        Assert.False(featureSawLanes.SavesLaneStartedWhenStopped, "the save queue's lane is begun only after the features stopped");
        Assert.True(drain.Saves.Refused >= 1, drain.Saves.Describe());
        Assert.Equal(0, drain.Features.Refused);
        Assert.DoesNotContain(FaultLedger.Shared.Snapshot(), f => f.Site.StartsWith("world-host.stop-drain", StringComparison.Ordinal)); // the refusing queue records itself; the host only logs the lane
    }

    private sealed class FeatureProbe(ShutdownDrain drain) : IWorldFeature
    {
        public bool FeaturesLaneStartedWhenStopped { get; private set; }

        public bool SavesLaneStartedWhenStopped { get; private set; }

        public void Attach(WorldRuntime world)
        {
        }

        public Task StopAsync()
        {
            FeaturesLaneStartedWhenStopped = drain.Features.Started;
            SavesLaneStartedWhenStopped = drain.Saves.Started;
            return Task.CompletedTask;
        }
    }
}

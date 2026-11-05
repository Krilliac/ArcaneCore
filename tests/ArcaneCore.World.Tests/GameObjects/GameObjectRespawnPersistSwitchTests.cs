using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// <c>GameObjects:PersistRespawn</c> switches the durable game object respawn times off like <c>Creatures:Respawn:Persist</c>. Unproven until the integration gate.
/// </summary>
public sealed class GameObjectRespawnPersistSwitchTests
{
    private sealed class CountingStore : IGameObjectRespawnStore
    {
        public int Loads;

        public Task<IReadOnlyList<GameObjectRespawnRecord>> LoadAsync(long nowUnixSeconds, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Loads);
            return Task.FromResult<IReadOnlyList<GameObjectRespawnRecord>>([new GameObjectRespawnRecord(0, 0, 5, nowUnixSeconds + 500)]);
        }

        public Task SaveAsync(IReadOnlyCollection<GameObjectRespawnRecord> upserts, IReadOnlyCollection<GameObjectRespawnKey> deletes, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static ServiceProvider Build(CountingStore store, string? persist)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGameObjectRespawnStore>(store);
        if (persist is not null)
        {
            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [$"{GameObjectOptions.SectionName}:{nameof(GameObjectOptions.PersistRespawn)}"] = persist }).Build();
            services.AddSingleton(config);
        }

        return services.BuildServiceProvider();
    }

    private static WorldRuntime NewWorld(ServiceProvider sp)
    {
        var saves = new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        return new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
    }

    [Fact]
    public void PersistRespawnDefaultsToOn()
        => Assert.True(new GameObjectOptions().PersistRespawn);

    [Fact]
    public async Task PersistRespawnFalse_LeavesTheFeatureInactive_AndNeverReadsTheStore()
    {
        var store = new CountingStore();
        await using ServiceProvider sp = Build(store, "false");
        using WorldRuntime world = NewWorld(sp);
        var feature = new GameObjectRespawnFeature(sp, sp.GetRequiredService<ILogger<GameObjectRespawnFeature>>());

        feature.Attach(world);

        Assert.Null(feature.Persistence);
        Assert.Equal(0, store.Loads);
    }

    [Fact]
    public async Task PersistRespawnTrue_LoadsTheStoreAndActivatesTheFeature()
    {
        var store = new CountingStore();
        await using ServiceProvider sp = Build(store, "true");
        using WorldRuntime world = NewWorld(sp);
        var feature = new GameObjectRespawnFeature(sp, sp.GetRequiredService<ILogger<GameObjectRespawnFeature>>());

        feature.Attach(world);

        Assert.NotNull(feature.Persistence);
        Assert.Equal(1, store.Loads);
        await feature.StopAsync();
    }

    [Fact]
    public async Task NoConfigurationRegistered_KeepsTheDefaultOn_AndLoadsTheStore()
    {
        var store = new CountingStore();
        await using ServiceProvider sp = Build(store, null);
        using WorldRuntime world = NewWorld(sp);
        var feature = new GameObjectRespawnFeature(sp, sp.GetRequiredService<ILogger<GameObjectRespawnFeature>>());

        feature.Attach(world);

        Assert.NotNull(feature.Persistence);
        Assert.Equal(1, store.Loads);
        await feature.StopAsync();
    }
}

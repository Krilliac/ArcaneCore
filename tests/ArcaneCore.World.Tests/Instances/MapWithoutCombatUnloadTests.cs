using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Death;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Stats;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

/// <summary>
/// A map can exist without a <see cref="MapCombat"/> updater (a hot-reloaded default set, a test rig). The MapCreated and
/// MapUnloading handlers that follow a map's combat must skip such a map, not throw into the world's event fault path.
/// </summary>
public sealed class MapWithoutCombatUnloadTests
{
    [Theory]
    [InlineData(nameof(SelfResurrectionFeature))]
    [InlineData(nameof(DeathOutcomeWatcher))]
    [InlineData(nameof(ProgressionFeature))]
    [InlineData(nameof(ReputationFeature))]
    [InlineData(nameof(HonorFeature))]
    [InlineData(nameof(GameObjectLootFeature))]
    [InlineData(nameof(SpecialLootFeature))]
    [InlineData(nameof(StatsFeature))]
    [InlineData(nameof(PlayerbotSocialFeature))]
    [InlineData(nameof(PlayerbotPvpKillFeed))]
    public async Task AMapWithoutCombat_IsCreatedAndUnloaded_WithNoFault(string feature)
    {
        var collection = new ServiceCollection();
        if (feature == nameof(SpecialLootFeature))
        {
            // The special loot only installs where the game object and spell features exist.
            collection.AddSingleton(sp => new GameObjectLootFeature(sp, NullLogger<GameObjectLootFeature>.Instance));
            collection.AddSingleton(sp => new TeleportFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<TeleportFeature>.Instance));
            collection.AddSingleton(sp => new SpellFeature(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SpellFeature>.Instance));
        }

        await using ServiceProvider services = collection.BuildServiceProvider();
        IServiceScopeFactory scopes = services.GetRequiredService<IServiceScopeFactory>();
        var saves = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        var log = new ErrorLog();
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, log);

        // Runs before every feature's MapCreated handler: the new map loses its combat updater.
        world.MapCreated += static map =>
        {
            if (map.FindUpdater<MapCombat>() is { } combat)
            {
                map.RemoveUpdater(combat);
            }
        };

        long before = FaultsNaming(feature);
        Attached attached = Attach(feature, world, services, scopes);
        try
        {
            world.RunTick(0); // the features that install by a post
            Map map = world.GetMap(33, 101);
            Assert.Null(map.FindUpdater<MapCombat>());

            Assert.Equal(0, attached.TrackedMaps?.Invoke() ?? 0); // a map without combat is not followed (checked before the unload drops it anyway)

            world.UnloadMap(map);
            world.RunTick(0);

            Assert.True(map.IsUnloaded);
            Assert.Empty(log.Errors);
            Assert.Equal(0, FaultsNaming(feature) - before);
        }
        finally
        {
            await attached.Lifetime.DisposeAsync();
        }
    }

    private static Attached Attach(string feature, WorldRuntime world, IServiceProvider services, IServiceScopeFactory scopes)
    {
        switch (feature)
        {
            case nameof(SelfResurrectionFeature):
                var self = new SelfResurrectionFeature();
                self.Attach(world);
                return new Attached(new Sync(self));
            case nameof(DeathOutcomeWatcher):
                return new Attached(new Sync(new DeathOutcomeWatcher(world)));
            case nameof(ProgressionFeature):
                var progression = new ProgressionFeature(services, NullLogger<ProgressionFeature>.Instance);
                progression.Attach(world);
                return new Attached(new Sync(progression));
            case nameof(ReputationFeature):
                var reputation = new ReputationFeature(services, scopes, NullLoggerFactory.Instance);
                reputation.Attach(world);
                return new Attached(reputation);
            case nameof(HonorFeature):
                var honor = new HonorFeature(services, scopes, NullLoggerFactory.Instance);
                honor.Attach(world);
                return new Attached(honor);
            case nameof(GameObjectLootFeature):
                var objects = new GameObjectLootFeature(services, NullLogger<GameObjectLootFeature>.Instance);
                objects.Attach(world);
                return new Attached(new Async(objects.StopAsync));
            case nameof(SpecialLootFeature):
                var gameObjects = services.GetRequiredService<GameObjectLootFeature>();
                gameObjects.Attach(world);
                var spells = services.GetRequiredService<SpellFeature>();
                spells.Attach(world);
                var special = new SpecialLootFeature(services, NullLogger<SpecialLootFeature>.Instance);
                special.Attach(world);
                return new Attached(new Async(async () =>
                {
                    await gameObjects.StopAsync();
                    await spells.DisposeAsync();
                }));
            case nameof(StatsFeature):
                var stats = new StatsFeature(services, scopes, NullLogger<StatsFeature>.Instance);
                stats.Attach(world);
                return new Attached(new Sync(stats));
            case nameof(PlayerbotSocialFeature):
                var social = new PlayerbotSocialFeature(services, NullLogger<PlayerbotSocialFeature>.Instance);
                social.Attach(world);
                return new Attached(new Async(social.StopAsync), () => social.TrackedMapCount);
            case nameof(PlayerbotPvpKillFeed):
                var kills = new PlayerbotPvpKillFeed(world, static (_, _) => { });
                return new Attached(new Sync(kills), () => kills.MapCount);
            default:
                throw new ArgumentOutOfRangeException(nameof(feature), feature, null);
        }
    }

    private sealed record Attached(IAsyncDisposable Lifetime, Func<int>? TrackedMaps = null);

    private sealed class Async(Func<Task> stop) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await stop();
    }

    /// <summary>Ledger counts whose stack names <paramref name="feature"/> (the ledger is process-wide; the type name keeps other tests out).</summary>
    private static long FaultsNaming(string feature)
        => FaultLedger.Shared.Snapshot()
            .Where(f => f.Frames.Any(frame => frame.Contains(feature, StringComparison.Ordinal))
                || f.SampleStack.Contains(feature, StringComparison.Ordinal))
            .Sum(f => f.Count);

    private sealed class Sync(IDisposable inner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ErrorLog : ILogger<WorldRuntime>
    {
        private readonly List<string> _errors = [];

        public IReadOnlyList<string> Errors
        {
            get
            {
                lock (_errors) return [.. _errors];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                lock (_errors) _errors.Add($"{logLevel}: {formatter(state, exception)} {exception?.GetType().Name}");
            }
        }
    }
}

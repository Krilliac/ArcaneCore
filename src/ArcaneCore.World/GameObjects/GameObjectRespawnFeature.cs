using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.Features;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// Durable game object respawn times in the world daemon (an <see cref="IWorldFeature"/>, discovered), the counterpart of
/// <see cref="Creatures.CreatureRespawnFeature"/>: loads the stored times from <see cref="IGameObjectRespawnStore"/> when the world starts
/// (vmangos MapPersistentStateManager::LoadGameobjectRespawnTimes, Maps/MapPersistentStateMgr.cpp:1149-1156; rows that expired, or belong to a
/// dungeon instance that is gone, are dropped by the store), hands them to every map's <see cref="GameObjectMapSystem"/> through
/// <see cref="Persistence"/>, drains the write queue at shutdown after every system saved the objects that are still despawned (it throws if a
/// write is still not durable), and clears a deleted instance's times through <see cref="GameObjectRespawnQueue.ForgetInstance"/>.
/// Inactive without a store: the timers are then kept in memory only, as before. The clock is the creature feature's
/// <see cref="IRespawnClock"/> registration, else the system clock.
/// </summary>
public sealed class GameObjectRespawnFeature(IServiceProvider services, ILogger<GameObjectRespawnFeature> logger) : IWorldFeature
{
    private GameObjectRespawnQueue? _queue;

    /// <summary>The persistence the game object systems use; null when the feature is inactive.</summary>
    public IGameObjectRespawnPersistence? Persistence => _queue;

    /// <summary>The wall clock behind the stored times (a registered <see cref="IRespawnClock"/>, else the system clock).</summary>
    public IRespawnClock Clock => services.GetService<IRespawnClock>() ?? SystemRespawnClock.Instance;

    /// <summary>Writes queued or in progress (tests).</summary>
    public int PendingWrites => _queue?.Pending ?? 0;

    /// <summary>Wait until every queued write has been attempted (tests).</summary>
    public Task FlushAsync() => _queue?.FlushAsync() ?? Task.CompletedTask;

    public void Attach(WorldRuntime world)
    {
        IReadOnlyList<GameObjectRespawnRecord> stored = [];
        IServiceScopeFactory scopes = services.GetRequiredService<IServiceScopeFactory>();
        using (IServiceScope scope = scopes.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IGameObjectRespawnStore>() is not { } store)
            {
                return; // no characters database in this host: nothing to persist to
            }

            stored = store.LoadAsync(Clock.UnixSeconds).GetAwaiter().GetResult();
        }

        _queue = new GameObjectRespawnQueue(scopes, services.GetRequiredService<ILoggerFactory>().CreateLogger<GameObjectRespawnQueue>(), services.GetService<ShutdownDrain>()?.Features);
        _queue.LoadInitial(stored);
        _queue.Start();

        // A reset or deleted instance takes its respawn times with it. Features attach in type-name order, so the instance feature attaches after this
        // one; the subscription is a world command queued before the instance feature's own load, so the saves it drops at start reach it.
        GameObjectRespawnQueue queue = _queue;
        world.Post(() =>
        {
            if (services.GetService<InstanceFeature>() is { } instances)
            {
                instances.InstanceRemoved += queue.ForgetInstance;
            }
        });
        logger.LogInformation("game object respawn persistence: {Count} despawned spawn(s) still waiting to respawn", stored.Count);
    }

    public async Task StopAsync()
    {
        if (_queue is null)
        {
            return;
        }

        // The world thread has stopped: every system saves the objects it still holds despawned, then the queue drains.
        if (services.GetService<GameObjectLootFeature>() is { } objects)
        {
            foreach (GameObjectMapSystem system in objects.Systems)
            {
                system.SaveRespawnTimes();
            }
        }

        await _queue.StopAsync().ConfigureAwait(false);
    }
}

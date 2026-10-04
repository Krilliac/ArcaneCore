using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Instances;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Creatures;

/// <summary>
/// Durable creature respawn times in the world daemon (an <see cref="IWorldFeature"/>, discovered): loads the stored times from
/// <see cref="ICreatureRespawnStore"/> when the world starts (rows that expired, or belong to a dungeon instance that is gone, are dropped by the
/// store), hands them to every map's <see cref="CreatureMapSystem"/> through <see cref="Persistence"/>, and drains the write queue at shutdown after
/// every system saved the creatures that are still dead (it throws if a write is still not durable), and clears a deleted instance's times through <see cref="CreatureRespawnQueue.ForgetInstance"/>. Inactive without a store or with <c>Creatures:Respawn:Persist=false</c>: the timers are then
/// kept in memory only, as before.
/// </summary>
public sealed class CreatureRespawnFeature(IServiceProvider services, ILogger<CreatureRespawnFeature> logger) : IWorldFeature
{
    private CreatureRespawnQueue? _queue;

    /// <summary>The persistence the creature systems use; null when the feature is inactive.</summary>
    public ICreatureRespawnPersistence? Persistence => _queue;

    /// <summary>The wall clock behind the stored times (a registered <see cref="IRespawnClock"/>, else the system clock).</summary>
    public IRespawnClock Clock => services.GetService<IRespawnClock>() ?? SystemRespawnClock.Instance;

    /// <summary>Writes queued or in progress (tests).</summary>
    public int PendingWrites => _queue?.Pending ?? 0;

    /// <summary>Wait until every queued write has been attempted (tests).</summary>
    public Task FlushAsync() => _queue?.FlushAsync() ?? Task.CompletedTask;

    public void Attach(WorldRuntime world)
    {
        var options = new CreatureOptions();
        services.GetService<IConfiguration>()?.GetSection(CreatureOptions.SectionName).Bind(options);
        if (!options.Respawn.Persist)
        {
            logger.LogInformation("creature respawn persistence is off (Creatures:Respawn:Persist)");
            return;
        }

        IReadOnlyList<CreatureRespawnRecord> stored = [];
        IServiceScopeFactory scopes = services.GetRequiredService<IServiceScopeFactory>();
        using (IServiceScope scope = scopes.CreateScope())
        {
            if (scope.ServiceProvider.GetService<ICreatureRespawnStore>() is not { } store)
            {
                return; // no characters database in this host: nothing to persist to
            }

            stored = store.LoadAsync(Clock.UnixSeconds).GetAwaiter().GetResult();
        }

        _queue = new CreatureRespawnQueue(scopes, services.GetRequiredService<ILoggerFactory>().CreateLogger<CreatureRespawnQueue>());
        _queue.LoadInitial(stored);
        _queue.Start();

        // A reset or deleted instance takes its respawn times with it. Features attach in type-name order, so the instance feature attaches after this
        // one; the subscription is a world command queued before the instance feature's own load, so the saves it drops at start reach it.
        CreatureRespawnQueue queue = _queue;
        world.Post(() =>
        {
            if (services.GetService<InstanceFeature>() is { } instances)
            {
                instances.InstanceRemoved += queue.ForgetInstance;
            }
        });
        logger.LogInformation("creature respawn persistence: {Count} dead spawn(s) still waiting to respawn", stored.Count);
    }

    public async Task StopAsync()
    {
        if (_queue is null)
        {
            return;
        }

        // The world thread has stopped: every system saves the dead creatures it still holds (a no-op with the default SaveImmediately, which saved
        // each death when it happened), then the queue drains.
        if (services.GetService<CreatureWorldFeature>() is { } creatures)
        {
            foreach (CreatureMapSystem system in creatures.Systems)
            {
                system.SaveRespawnTimes();
            }
        }

        await _queue.StopAsync().ConfigureAwait(false);
    }
}

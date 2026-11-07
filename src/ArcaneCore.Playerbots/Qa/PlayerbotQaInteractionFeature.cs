using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// Feeds every map's creature deaths to the bot QA interaction monitor (<see cref="PlayerbotQaInteractionMonitor.OnUnitKilled"/>: a bot's kill of an
/// objective creature is one attempt of that objective). The game's decision sites reach the monitor through <see cref="Game.Diagnostics.IGameQaObserver"/>
/// and the bot sessions through <see cref="Net.IManagedPacketTap"/>, both from DI; this feature only adds the kill events. Inert when QA is off or not
/// registered. Detachable and stateless (docs/design/hot-reload-tiers.md, section 4.2): the map events go through the <see cref="FeatureSubscriptions"/> bag,
/// so a detach or the stop unsubscribes MapCreated, MapUnloading and every map's UnitKilled, and the host no longer pins the feature. World thread.
/// </summary>
public sealed class PlayerbotQaInteractionFeature(IServiceProvider services) : IDetachableWorldFeature
{
    private readonly HashSet<MapCombat> _combat = [];
    private PlayerbotQaInteractionMonitor? _monitor;
    private FeatureSubscriptions? _subscriptions;

    /// <summary>Stateless: the kill counters live in the monitor, a DI singleton, and nothing is carried over a detach.</summary>
    public DetachKind DetachKind => DetachKind.Stateless;

    /// <summary>Map kill handlers currently subscribed (a test reads it).</summary>
    internal int SubscribedMaps => _combat.Count;

    public void AttachFrom(WorldRuntime world, FeatureHandover? handover, FeatureSubscriptions subscriptions)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(subscriptions);
        _subscriptions = subscriptions;
        if (services.GetService<PlayerbotQaInteractionMonitor>() is not { Active: true } monitor) return;
        _monitor = monitor;
        subscriptions.OnMapCreated(world, Subscribe);
        subscriptions.OnMapUnloading(world, Unsubscribe);
        // The per-map kill handlers come and go with the maps; the bag undoes whichever are still on when it is disposed.
        subscriptions.Add(new Undo(UnsubscribeMaps));
        foreach (Map map in world.Maps) Subscribe(map);
    }

    /// <summary>Nothing runs in the background.</summary>
    public Task QuiesceAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Detach(FeatureHandover handover) => Release();

    public Task StopAsync()
    {
        try
        {
            _subscriptions?.Dispose(); // the feature is done: its handlers come off the world (the host never reuses the bag)
        }
        finally
        {
            Release();
        }

        return Task.CompletedTask;
    }

    private void Release()
    {
        UnsubscribeMaps();
        _monitor = null;
        _subscriptions = null;
    }

    private void Subscribe(Map map)
    {
        if (_combat.Add(map.Combat)) map.Combat.UnitKilled += OnUnitKilled;
    }

    private void Unsubscribe(Map map)
    {
        if (_combat.Remove(map.Combat)) map.Combat.UnitKilled -= OnUnitKilled;
    }

    private void UnsubscribeMaps()
    {
        foreach (MapCombat combat in _combat) combat.UnitKilled -= OnUnitKilled;
        _combat.Clear();
    }

    private void OnUnitKilled(Game.Entities.Unit? killer, Game.Entities.Unit victim) => _monitor?.OnUnitKilled(killer, victim);

    private sealed class Undo(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

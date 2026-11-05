using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// Feeds every map's creature deaths to the bot QA interaction monitor (<see cref="PlayerbotQaInteractionMonitor.OnUnitKilled"/>: a bot's kill of an
/// objective creature is one attempt of that objective). The game's decision sites reach the monitor through <see cref="Game.Diagnostics.IGameQaObserver"/>
/// and the bot sessions through <see cref="Net.IManagedPacketTap"/>, both from DI; this feature only adds the kill events. Inert when QA is off or not
/// registered. World thread.
/// </summary>
public sealed class PlayerbotQaInteractionFeature(IServiceProvider services) : IWorldFeature
{
    private readonly HashSet<MapCombat> _combat = [];
    private PlayerbotQaInteractionMonitor? _monitor;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (services.GetService<PlayerbotQaInteractionMonitor>() is not { Active: true } monitor) return;
        _monitor = monitor;
        world.MapCreated += Subscribe;
        world.MapUnloading += Unsubscribe;
        foreach (Map map in world.Maps) Subscribe(map);
    }

    public Task StopAsync()
    {
        foreach (MapCombat combat in _combat) combat.UnitKilled -= OnUnitKilled;
        _combat.Clear();
        return Task.CompletedTask;
    }

    private void Subscribe(Map map)
    {
        if (_combat.Add(map.Combat)) map.Combat.UnitKilled += OnUnitKilled;
    }

    private void Unsubscribe(Map map)
    {
        if (_combat.Remove(map.Combat)) map.Combat.UnitKilled -= OnUnitKilled;
    }

    private void OnUnitKilled(Game.Entities.Unit? killer, Game.Entities.Unit victim) => _monitor?.OnUnitKilled(killer, victim);
}

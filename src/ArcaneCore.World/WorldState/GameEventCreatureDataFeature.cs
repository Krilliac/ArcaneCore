using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// Wires game-event creature data into the world (docs/areas/game-events-weather.md): registers <see cref="GameEventCreatureData"/> as the creature-data
/// effect of every <see cref="GameEventService"/> and installs it as the <see cref="ICreatureEventData"/> of every creature system (from the world tick,
/// as soon as the system exists). Says once how many rows name equipment or spells, which are not applied.
/// </summary>
public sealed class GameEventCreatureDataFeature(IServiceProvider services, ILogger<GameEventCreatureDataFeature> logger) : IWorldFeature
{
    private WorldRuntime? _world;
    private GameEventCreatureData? _data;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        GameEventFeature events = services.GetRequiredService<GameEventFeature>();
        events.ServiceCreated += Wire;
        if (events.Service is { } existing)
        {
            Wire(existing);
        }

        world.WorldTick += _ => Install();
    }

    private void Wire(GameEventService service)
    {
        WorldRuntime world = _world ?? throw new InvalidOperationException("the game event creature data feature is not attached");
        _data = new GameEventCreatureData(service, service.Rows, () => world.Maps);
        service.AddEffects(_data);
        if (_data.EquipmentRows > 0 || _data.SpellRows > 0)
        {
            logger.LogWarning(
                "game_event_creature_data: {Equipment} row(s) name an equipment id and {Spells} row(s) a start or end spell; only entry_id and modelid are applied",
                _data.EquipmentRows, _data.SpellRows);
        }

        Install();
    }

    private void Install()
    {
        GameEventCreatureData? data = _data;
        if (data is null || _world is null)
        {
            return;
        }

        foreach (Map map in _world.Maps)
        {
            if (map.FindUpdater<CreatureMapSystem>() is { } creatures && !ReferenceEquals(creatures.EventData, data))
            {
                creatures.EventData = data;
            }
        }
    }
}

using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// Wires event quests into the world (docs/areas/game-events-weather.md): registers <see cref="GameEventQuests"/> as the quest effect of
/// every <see cref="GameEventService"/> the <see cref="GameEventFeature"/> builds, and keeps it in step with the quest feature's store
/// (which is built at its own pace) from the world tick. A replaced service gives its quests back before the new one takes them.
/// </summary>
public sealed class GameEventQuestFeature(IServiceProvider services, ILogger<GameEventQuestFeature> logger) : IWorldFeature
{
    private GameEventQuests? _quests;
    private readonly HashSet<uint> _reported = [];

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        GameEventFeature events = services.GetRequiredService<GameEventFeature>();
        events.ServiceCreated += Wire;
        if (events.Service is { } existing)
        {
            Wire(existing);
        }

        world.WorldTick += _ => Sync();
    }

    private void Wire(GameEventService service)
    {
        _quests?.Release();
        _reported.Clear();
        _quests = new GameEventQuests(service, service.Rows, () => services.GetService<QuestNpcFeature>()?.Services.Quests);
        service.AddEffects(_quests);
        Sync();
    }

    private void Sync()
    {
        if (_quests is null)
        {
            return;
        }

        foreach (uint missing in _quests.Resync())
        {
            if (_reported.Add(missing))
            {
                logger.LogError("game_event_quest lists quest {Quest}, which does not exist in quest_template; skipped", missing);
            }
        }
    }
}

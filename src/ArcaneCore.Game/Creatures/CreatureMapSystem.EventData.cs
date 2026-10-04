using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Game-event creature data of the creature system (docs/areas/game-events-weather.md): a spawn created while an event runs takes the event's
/// entry and model, and <see cref="RefreshEventData"/> brings live creatures in line when an event starts or stops (vmangos
/// GameEventMgr.cpp:985-1021, Creature::UpdateEntry).
/// </summary>
public sealed partial class CreatureMapSystem
{
    private ICreatureEventData? _eventData;

    /// <summary>
    /// The provider every creature creation asks (null: no event changes any creature, the default). Setting it re-evaluates the creatures it lists.
    /// </summary>
    public ICreatureEventData? EventData
    {
        get => _eventData;
        set
        {
            _eventData = value;
            if (value is not null)
            {
                RefreshEventData(value.Listed);
            }
        }
    }

    /// <summary>Apply the current event data (or none) to a creature that is not in the world yet or is live.</summary>
    private void ApplyEventData(Creature creature)
    {
        if (creature.Spawn is not { } spawn)
        {
            return;
        }

        GameEventCreatureOverride? data = _eventData?.For(spawn.Guid);
        CreatureTemplate? template = data is { EntryId: not 0 } ? _content.FindTemplate(data.EntryId) : null;
        uint display = data?.ModelId ?? 0;
        if (ReferenceEquals(creature.EventTemplate, template) && creature.EventDisplayId == display)
        {
            return;
        }

        creature.ApplyEventOverride(template, display);
    }

    /// <summary>
    /// Re-evaluate the event data of the live creatures with these database spawn guids: those whose event now runs take its entry and model,
    /// those whose event stopped return to their own (a respawn keeps whichever applies). Guids that are not live here are ignored.
    /// </summary>
    public void RefreshEventData(IEnumerable<uint> spawnGuids)
    {
        ArgumentNullException.ThrowIfNull(spawnGuids);
        _spawnByGuid ??= _spawnsByGrid.Values.SelectMany(l => l).ToDictionary(s => s.Guid);
        foreach (uint guid in spawnGuids)
        {
            if (_spawnByGuid.TryGetValue(guid, out CreatureSpawn? spawn)
                && FindLive(spawn, _options.Respawn.AlternateEntries ? _content.GetSpawnEntries(spawn.Guid) : []) is { } live)
            {
                ApplyEventData(live);
            }
        }
    }
}

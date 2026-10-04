using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.WorldState.Events;

/// <summary>
/// Game-event creature data (<c>game_event_creature_data</c>, vmangos GameEventMgr.cpp:444-526 and :961-1021): while an event runs, the creatures it
/// lists take its <c>entry_id</c> (another creature_template) and <c>modelid</c> (a forced model), live ones at once and ones created
/// later (a grid load, a respawn) as they are created, and return to their own when it stops. A spawn listed under several events takes the
/// data of the first running one in event order (<c>GetCreatureUpdateDataForActiveEvent</c>); one that is listed but whose event does not run
/// is untouched.
/// <para>
/// Not applied, and counted in <see cref="EquipmentRows"/> / <see cref="SpellRows"/> so the world can say so once: <c>equipment_id</c> (creatures
/// have no equipment model) and <c>spell_start</c> / <c>spell_end</c> (casting on the creature and keeping the aura across death and respawn
/// belongs to the aura engine). In classic-db 920 of the 977 rows are spell rows, 748 of them on Love is in the Air.
/// </para>
/// </summary>
public sealed class GameEventCreatureData : ICreatureEventData, IGameEventEffects
{
    private readonly IGameEventState _state;
    private readonly Func<IEnumerable<Map>> _maps;
    private readonly Dictionary<ushort, uint[]> _guidsByEvent;
    private readonly Dictionary<uint, (ushort Event, GameEventCreatureOverride Data)[]> _byGuid;

    public GameEventCreatureData(IGameEventState state, GameEventRows rows, Func<IEnumerable<Map>> maps)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        ArgumentNullException.ThrowIfNull(rows);
        _maps = maps ?? throw new ArgumentNullException(nameof(maps));
        _guidsByEvent = rows.CreatureData.ToDictionary(p => p.Key, p => p.Value.Select(r => r.Guid).Distinct().ToArray());
        _byGuid = rows.CreatureData
            .OrderBy(p => p.Key)
            .SelectMany(p => p.Value.Select(r => (Guid: r.Guid, Event: p.Key, Data: new GameEventCreatureOverride(r.EntryId, r.ModelId, r.EquipmentId, r.SpellStart, r.SpellEnd))))
            .GroupBy(t => t.Guid)
            .ToDictionary(g => g.Key, g => g.Select(t => (t.Event, t.Data)).ToArray());
        EquipmentRows = rows.CreatureData.Sum(p => p.Value.Count(r => r.EquipmentId != 0));
        SpellRows = rows.CreatureData.Sum(p => p.Value.Count(r => r.SpellStart != 0 || r.SpellEnd != 0));
    }

    /// <summary>Rows that name an equipment id (read, not applied).</summary>
    public int EquipmentRows { get; }

    /// <summary>Rows that name a start or end spell (read, not applied).</summary>
    public int SpellRows { get; }

    public IEnumerable<uint> Listed => _byGuid.Keys;

    public GameEventCreatureOverride? For(uint spawnGuid)
    {
        if (!_byGuid.TryGetValue(spawnGuid, out (ushort Event, GameEventCreatureOverride Data)[]? listed))
        {
            return null;
        }

        foreach ((ushort eventId, GameEventCreatureOverride data) in listed)
        {
            if (_state.IsActiveEvent(eventId))
            {
                return data;
            }
        }

        return null;
    }

    public void UpdateCreatureData(ushort eventId, bool activate)
    {
        if (!_guidsByEvent.TryGetValue(eventId, out uint[]? guids))
        {
            return;
        }

        foreach (Map map in _maps().ToArray())
        {
            map.FindUpdater<Creatures.CreatureMapSystem>()?.RefreshEventData(guids);
        }
    }
}

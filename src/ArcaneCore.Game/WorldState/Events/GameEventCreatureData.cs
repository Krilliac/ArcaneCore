using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.WorldState.Events;

/// <summary>
/// Game-event creature data (<c>game_event_creature_data</c>, vmangos GameEventMgr.cpp:444-526 and :961-1021): while an event runs, the creatures it
/// lists take its <c>entry_id</c> (another creature_template), <c>modelid</c> (a forced model), <c>equipment_id</c>, and <c>spell_start</c> / <c>spell_end</c>,
/// live ones at once and ones created later (a grid load, a respawn) as they are created, and return to their own when it stops. A spawn listed under
/// several events takes the data of the first running one in event order (<c>GetCreatureUpdateDataForActiveEvent</c>); one that is listed but whose
/// event does not run is untouched.
/// </summary>
public sealed class GameEventCreatureData : ICreatureEventData, IGameEventEffects
{
    private readonly IGameEventState _state;
    private readonly Func<IEnumerable<Map>> _maps;
    private readonly Dictionary<ushort, uint[]> _guidsByEvent;
    private readonly Dictionary<uint, (ushort Event, GameEventCreatureOverride Data)[]> _byGuid;
    private readonly Dictionary<uint, uint> _appliedEquipmentByGuid = [];
    private readonly Dictionary<uint, uint> _lastSpellCastByGuid = [];
    private readonly List<(uint Guid, uint SpellId)> _castSpells = [];

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

    /// <summary>Rows that name an equipment id.</summary>
    public int EquipmentRows { get; }

    /// <summary>Rows that name a start or end spell.</summary>
    public int SpellRows { get; }

    /// <summary>Optional callback invoked when an event starts (<c>spell_start</c>) or ends (<c>spell_end</c>) on a live creature.</summary>
    public Action<Creature, uint>? OnCastSpell { get; set; }

    /// <summary>Currently applied event equipment id by spawn guid.</summary>
    public IReadOnlyDictionary<uint, uint> AppliedEquipmentByGuid => _appliedEquipmentByGuid;

    /// <summary>The most recent event spell cast on each spawn guid.</summary>
    public IReadOnlyDictionary<uint, uint> LastSpellCastByGuid => _lastSpellCastByGuid;

    /// <summary>Ordered log of <c>(SpawnGuid, SpellId)</c> event spell casts.</summary>
    public IReadOnlyList<(uint Guid, uint SpellId)> CastSpells => _castSpells;

    public IEnumerable<uint> Listed => _byGuid.Keys;

    /// <summary>
    /// The data of the first running event that lists <paramref name="spawnGuid"/>, or null. Read-only: it is called from every grid spawn on whichever
    /// map thread updates that map. Today every caller holds the world serial gate, so two calls never overlap, but it writes no state of this
    /// world-wide singleton so that ungating spawn creation cannot race (the applied-equipment record is kept by <see cref="UpdateCreatureData"/>).
    /// </summary>
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
            if (map.FindUpdater<CreatureMapSystem>() is { } system)
            {
                system.RefreshEventData(guids);
                ApplyEquipmentAndSpells(system, eventId, guids, activate);
            }
        }
    }

    private void ApplyEquipmentAndSpells(CreatureMapSystem system, ushort eventId, uint[] guids, bool activate)
    {
        foreach (uint guid in guids)
        {
            GameEventCreatureOverride? active = For(guid);
            if (active is { EquipmentId: not 0 })
            {
                _appliedEquipmentByGuid[guid] = active.EquipmentId;
            }
            else
            {
                _appliedEquipmentByGuid.Remove(guid);
            }

            Creature? live = system.FindBySpawnGuid(guid);
            if (live is null)
            {
                continue;
            }

            // The event's equipment id (a creature_equip_template entry) is applied by the creature system with the rest of the event data
            // (CreatureMapSystem.ApplyEventData -> Creature.ApplyEquipment: all three slots and their info, the template's equipment when
            // the event stops), so a respawn keeps it while the event runs.
            if (live.DeathState != CreatureDeathState.Alive || !_byGuid.TryGetValue(guid, out (ushort Event, GameEventCreatureOverride Data)[]? rows))
            {
                continue;
            }

            GameEventCreatureOverride? eventRow = null;
            foreach ((ushort ev, GameEventCreatureOverride data) in rows)
            {
                if (ev == eventId)
                {
                    eventRow = data;
                    break;
                }
            }

            if (eventRow is null)
            {
                continue;
            }

            if (activate && ReferenceEquals(active, eventRow) && eventRow.SpellStart != 0)
            {
                _lastSpellCastByGuid[guid] = eventRow.SpellStart;
                _castSpells.Add((guid, eventRow.SpellStart));
                OnCastSpell?.Invoke(live, eventRow.SpellStart);
            }
            else if (!activate && eventRow.SpellEnd != 0)
            {
                _lastSpellCastByGuid[guid] = eventRow.SpellEnd;
                _castSpells.Add((guid, eventRow.SpellEnd));
                OnCastSpell?.Invoke(live, eventRow.SpellEnd);
            }
        }
    }

}

namespace ArcaneCore.Game.WorldState.Events;

/// <summary>What a running game event changes about one creature spawn (a <c>game_event_creature_data</c> row).</summary>
/// <param name="EntryId">The creature_template entry to take instead of the spawn's (0: keep).</param>
/// <param name="ModelId">The display id to force (0: keep).</param>
/// <param name="EquipmentId">Equipment id (read, not applied: creatures have no equipment model yet).</param>
/// <param name="SpellStart">Spell cast at the creature when the event starts (read, not applied: needs the aura engine).</param>
/// <param name="SpellEnd">Spell cast when the event ends (same).</param>
public sealed record GameEventCreatureOverride(uint EntryId, uint ModelId, uint EquipmentId, uint SpellStart, uint SpellEnd);

/// <summary>
/// The question the creature map systems ask when they create a spawn: does a running game event change it? (vmangos
/// <c>GameEventMgr::GetCreatureUpdateDataForActiveEvent</c>, GameEventMgr.cpp:961-983, asked by <c>Creature::LoadFromDB</c> and the
/// respawn path, Creature.cpp:837 and :1949.) World thread.
/// </summary>
public interface ICreatureEventData
{
    /// <summary>The data of the running event that lists this spawn guid (the first running one in event order), or null.</summary>
    GameEventCreatureOverride? For(uint spawnGuid);

    /// <summary>Every spawn guid listed in some event's creature data.</summary>
    IEnumerable<uint> Listed { get; }
}

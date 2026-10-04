namespace ArcaneCore.Game.WorldState.Events;

/// <summary>
/// Whether a database spawn may exist right now. vmangos never puts a creature or gameobject that is listed in
/// <c>game_event_creature</c> / <c>game_event_gameobject</c> (either sign) into the grid at load; the game-event system adds and
/// removes it as events start and stop (ObjectMgr.cpp:2330-2345 and :2498, GameEventMgr.cpp:807-960). The creature and
/// gameobject map systems consult the gate when a grid loads and expose <c>RefreshSpawns</c> for the event system to call when
/// an event changes. World thread.
/// </summary>
public interface ISpawnGate
{
    /// <summary>Whether the creature spawn with this database guid may be in the world.</summary>
    bool AllowsCreature(uint spawnGuid);

    /// <summary>Whether the gameobject spawn with this database guid may be in the world.</summary>
    bool AllowsGameObject(uint spawnGuid);

    /// <summary>Every creature spawn guid that is listed in an event (the ones the gate can ever refuse).</summary>
    IEnumerable<uint> GatedCreatures { get; }

    /// <summary>Every gameobject spawn guid that is listed in an event.</summary>
    IEnumerable<uint> GatedGameObjects { get; }
}

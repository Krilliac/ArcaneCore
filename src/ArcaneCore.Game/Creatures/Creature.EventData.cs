using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>Game-event overrides of a creature (docs/areas/game-events-weather.md).</summary>
public sealed partial class Creature
{
    /// <summary>
    /// The template a running game event swaps in for this creature (<c>game_event_creature_data.entry_id</c>; vmangos
    /// <c>Creature::UpdateEntry(entry, eventData)</c>, Creature.cpp:358-362), or null. While set it IS <see cref="Template"/>, so respawns
    /// re-initialise from it, and the entry, level, health, flags and loot follow it.
    /// </summary>
    public CreatureTemplate? EventTemplate { get; private set; }

    /// <summary>The model a running game event forces (<c>modelid</c> / vmangos <c>display_id</c>), or 0 for the template's own.</summary>
    public uint EventDisplayId { get; private set; }

    /// <summary>
    /// Set or clear the event overrides. A living creature is re-initialised at once (vmangos <c>UpdateEntry</c> re-runs the level and
    /// stat setup); a dead one only remembers them for its respawn.
    /// </summary>
    internal void ApplyEventOverride(CreatureTemplate? template, uint displayId)
    {
        EventTemplate = template;
        EventDisplayId = displayId;
        if (DeathState == CreatureDeathState.Alive)
        {
            InitializeFields();
        }
    }
}

using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureMapSystem
{
    /// <summary>Before corpse disposal drops the spell ownership of an object that can later respawn.</summary>
    public event Action<Creature>? CorpseRemoving;

    /// <summary>Before respawn initializes fields and invokes the AI's fresh-life hook (natural and forced paths).</summary>
    public event Action<Creature>? Respawning;
}

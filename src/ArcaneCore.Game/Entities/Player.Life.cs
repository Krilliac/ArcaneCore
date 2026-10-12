using ArcaneCore.Game.Death;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.Entities;

public sealed partial class Player
{
    /// <summary>The fatal environmental damage, retained until the bot reporter reads the death.</summary>
    public EnvironmentalDamageType? LastEnvironmentalDeath { get; set; }

    /// <summary>A fatal periodic spell tick whose aura caster was no longer resolvable.</summary>
    public bool LastMissingCasterPeriodicDeath { get; set; }

    /// <summary>The safe location selected for the last spirit release when its teleport request was accepted.</summary>
    public WorldSafeLoc? LastReleasedGraveyard { get; set; }

    /// <summary>The graveyard release hook has run for this death, even if no linked location was found.</summary>
    public bool RepopDestinationResolved { get; set; }

    /// <summary>
    /// The life that was stored when this player was loaded and what was applied from it, kept
    /// until the player has entered the world and its auras were restored (the features that
    /// finish the login read it on the world thread, then clear it). Null for a fresh character.
    /// </summary>
    public LoadedLife? LoadedLife { get; set; }
}

using ArcaneCore.Game.Death;

namespace ArcaneCore.Game.Entities;

public sealed partial class Player
{
    /// <summary>
    /// The life that was stored when this player was loaded and what was applied from it, kept
    /// until the player has entered the world and its auras were restored (the features that
    /// finish the login read it on the world thread, then clear it). Null for a fresh character.
    /// </summary>
    public LoadedLife? LoadedLife { get; set; }
}

namespace ArcaneCore.Game.WorldState.Events;

/// <summary>
/// A spawn gate that adds its own rule in front of another one (a battleground map's event gate in front of the game-event gate). The map
/// systems hold one <see cref="ISpawnGate"/>; a feature that installs its gate on every map sets <see cref="Inner"/> of a wrapping gate it
/// finds instead of replacing it, so both rules apply.
/// </summary>
public interface IWrappingSpawnGate : ISpawnGate
{
    /// <summary>The gate asked after this one's own rule (null: none).</summary>
    ISpawnGate? Inner { get; set; }
}

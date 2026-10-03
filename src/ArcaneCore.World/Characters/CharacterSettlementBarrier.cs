namespace ArcaneCore.World.Characters;

/// <summary>
/// A feature that settles a character's durable state in the background (economy commits
/// that move items and money between characters). Login waits on every barrier before it
/// flushes the save queue and re-reads the character, so a disconnected session's pending
/// settlement is never overwritten or observed half-way. Discovered on world features
/// (WorldFeatures seam interfaces).
/// </summary>
public interface ICharacterSettlementBarrier
{
    /// <summary>Completes once no settlement involving the character is in flight (storage and publication).</summary>
    Task WaitForSettlementAsync(int characterId, CancellationToken cancellationToken = default);
}

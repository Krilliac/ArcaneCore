using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Reputation;

/// <summary>
/// Character deletion for reputation (docs/integration/character-delete.md): queued reputation
/// writes drain before the rows are removed (by <c>CharacterReputationDataModule</c> in the
/// deletion transaction); afterwards <see cref="ReputationFeature.DeleteCharacter"/> queues a
/// delete behind any later write, so nothing can bring a row back, and that removal (conditional
/// on the id still having no character row) is awaited, bounded, so the deletion completes only
/// after it was attempted. Re-running it for the same character is harmless. Deletion is never
/// blocked by retained failed writes (<see cref="ReputationFeature.FlushAsync"/> neither retries nor
/// throws, and the deletion barrier seam is deliberately not used): <c>DeleteCharacter</c> discards
/// whatever the queue retained for the character.
/// </summary>
public sealed class ReputationCharacterDeleteHook(ReputationFeature reputation) : IWorldFeature, ICharacterDeleteHook
{
    /// <summary>How long the post-delete drain waits for the queued removals (tests shorten it).</summary>
    public TimeSpan DrainTimeout { get; init; } = CharacterDeletion.DrainTimeout;

    public void Attach(WorldRuntime world)
    {
    }

    public Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character) => reputation.FlushAsync();

    public async Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        reputation.DeleteCharacter(character.Id);
        await reputation.FlushAsync().WaitAsync(DrainTimeout).ConfigureAwait(false);
    }
}

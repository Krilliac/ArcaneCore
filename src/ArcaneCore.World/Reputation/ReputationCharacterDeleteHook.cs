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
/// after it was attempted. Re-running it for the same character is harmless.
/// </summary>
public sealed class ReputationCharacterDeleteHook(ReputationFeature reputation) : IWorldFeature, ICharacterDeleteHook
{
    public void Attach(WorldRuntime world)
    {
    }

    public Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character) => reputation.FlushAsync();

    public async Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        reputation.DeleteCharacter(character.Id);
        await reputation.FlushAsync().WaitAsync(CharacterDeletion.DrainTimeout).ConfigureAwait(false);
    }
}

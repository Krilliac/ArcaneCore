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
/// delete behind any later write, so nothing can bring a row back.
/// </summary>
public sealed class ReputationCharacterDeleteHook(ReputationFeature reputation) : IWorldFeature, ICharacterDeleteHook
{
    public void Attach(WorldRuntime world)
    {
    }

    public Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character) => reputation.FlushAsync();

    public Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        reputation.DeleteCharacter(character.Id);
        return Task.CompletedTask;
    }
}

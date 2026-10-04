using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Honor;

/// <summary>
/// Character deletion for honor (docs/integration/character-delete.md): queued honor writes drain before the rows are removed
/// (by <c>CharacterHonorDataModule</c> in the deletion transaction); afterwards <see cref="HonorFeature.DeleteCharacter"/>
/// queues a delete behind any later write and that removal (conditional on the id still having no character row) is awaited,
/// bounded, so the deletion completes only after it was attempted. Re-running it is harmless; retained failed writes never
/// block a deletion, the queue just discards them.
/// </summary>
public sealed class HonorCharacterDeleteHook(HonorFeature honor) : IWorldFeature, ICharacterDeleteHook
{
    /// <summary>How long the post-delete drain waits for the queued removals (tests shorten it).</summary>
    public TimeSpan DrainTimeout { get; init; } = CharacterDeletion.DrainTimeout;

    public void Attach(WorldRuntime world)
    {
    }

    public Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character) => honor.FlushAsync();

    public async Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        honor.DeleteCharacter(character.Id);
        await honor.FlushAsync().WaitAsync(DrainTimeout).ConfigureAwait(false);
    }
}

using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Talents;

/// <summary>
/// Character deletion for the talent state: queued writes of the character drain before the rows are removed, then the
/// desired state is dropped and the conditional store removal awaited (bounded), so a later character that reuses the id starts
/// empty (docs/integration/character-delete.md). Re-running it for the same character is harmless.
/// </summary>
public sealed class TalentCharacterDeleteHook(TalentFeature talents) : IWorldFeature, ICharacterDeleteHook
{
    /// <summary>How long the post-delete drain waits for the queued removals (tests shorten it).</summary>
    public TimeSpan DrainTimeout { get; init; } = CharacterDeletion.DrainTimeout;

    public void Attach(WorldRuntime world)
    {
    }

    public Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return talents.Persistence.FlushCharacterAsync(character.Id);
    }

    public async Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        talents.Persistence.DeleteCharacter(character.Id);
        await talents.Persistence.FlushCharacterAsync(character.Id).WaitAsync(DrainTimeout).ConfigureAwait(false);
    }
}

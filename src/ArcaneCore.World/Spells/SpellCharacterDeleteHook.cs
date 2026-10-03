using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Character deletion for the spellbook cache and the saved cooldowns/auras: queued learn/unlearn
/// writes and this character's queued state saves drain before the rows are removed, then the
/// cached book and any unsaved in-memory state snapshot are dropped, and their queued store
/// removals (conditional on the id still having no character row) are awaited, bounded, so the
/// deletion completes only after they were attempted; a later character that reuses the id starts
/// empty (docs/integration/character-delete.md, docs/integration/spells-persistence.md). Re-running
/// it for the same character is harmless.
/// </summary>
public sealed class SpellCharacterDeleteHook(SpellFeature spells) : IWorldFeature, ICharacterDeleteHook
{
    public void Attach(WorldRuntime world)
    {
    }

    public async Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        await spells.Spellbook.FlushAsync().ConfigureAwait(false);
        await spells.State.FlushCharacterAsync(character.Id).ConfigureAwait(false);
    }

    public async Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        spells.Spellbook.DeleteCharacter(character.Id);
        spells.State.DeleteCharacter(character.Id);

        // "Attempted", not "succeeded": both queues log and swallow a failed store call.
        await spells.Spellbook.FlushAsync().WaitAsync(CharacterDeletion.DrainTimeout).ConfigureAwait(false);
        await spells.State.FlushCharacterAsync(character.Id).WaitAsync(CharacterDeletion.DrainTimeout).ConfigureAwait(false);
    }
}

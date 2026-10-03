using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Character deletion for the spellbook cache and the saved cooldowns/auras: queued learn/unlearn
/// writes and this character's queued state saves drain before the rows are removed, then the
/// cached book and any unsaved in-memory state snapshot are dropped (their ordered store deletes
/// are no-ops by then), so a later character that reuses the id starts empty
/// (docs/integration/character-delete.md, docs/integration/spells-persistence.md).
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

    public Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        spells.Spellbook.DeleteCharacter(character.Id);
        spells.State.DeleteCharacter(character.Id);
        return Task.CompletedTask;
    }
}

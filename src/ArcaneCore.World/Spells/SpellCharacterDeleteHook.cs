using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Character deletion for the spellbook cache: queued learn/unlearn writes drain before the rows
/// are removed, then the cached book is dropped (its ordered store delete is a no-op by then), so a
/// later character that reuses the id starts empty (docs/integration/character-delete.md).
/// </summary>
public sealed class SpellCharacterDeleteHook(SpellFeature spells) : IWorldFeature, ICharacterDeleteHook
{
    public void Attach(WorldRuntime world)
    {
    }

    public Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character) => spells.Spellbook.FlushAsync();

    public Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        spells.Spellbook.DeleteCharacter(character.Id);
        return Task.CompletedTask;
    }
}

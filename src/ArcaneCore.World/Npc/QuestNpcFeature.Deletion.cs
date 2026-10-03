using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Npc;

/// <summary>
/// Character deletion for quests and flight paths: a settlement in flight and the character's
/// queued quest/taxi writes finish before the rows are removed; afterwards the retained snapshot
/// is forgotten so no shutdown retry or reused id writes it back (docs/integration/character-delete.md).
/// </summary>
public sealed partial class QuestNpcFeature : ICharacterDeleteHook
{
    public async Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        await WaitForSettlementAsync(character.Id).ConfigureAwait(false);
        await Persistence.FlushCharacterAsync(character.Id).ConfigureAwait(false);
    }

    public Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        Persistence.ForgetCharacter(character.Id);
        return Task.CompletedTask;
    }
}

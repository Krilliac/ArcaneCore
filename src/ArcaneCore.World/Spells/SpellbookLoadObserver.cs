using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Implemented on an <see cref="Features.IWorldFeature"/> that must act once a character's spellbook is loaded and
/// before the player is handed to the world thread (vmangos Player::LoadFromDB: _LoadSpells runs, and
/// everything that derives from the known spells follows; the skills area rebuilds a player's skills from them).
/// <see cref="SpellFeature.OnPlayerLoadingAsync"/> awaits each observer in feature order after the book
/// (with its defaults) is in <see cref="SpellFeature.Spellbook"/>. Session task: it may use the databases and
/// fill <paramref name="player"/>, never touch world state. An exception fails the login (fail closed).
/// </summary>
public interface ISpellbookLoadObserver
{
    Task OnSpellbookLoadedAsync(WorldSession session, CharacterRecord character, Player player);
}

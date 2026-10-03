using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// A hook into the single path every spell learn and removal takes (<see cref="SpellSystem.LearnSpell"/> and
/// <see cref="SpellSystem.RemoveSpell"/>). vmangos puts talent and primary-profession bookkeeping into
/// Player::AddSpell / RemoveSpell (Player.cpp:3606-3716, :3797-3885); here each feature registers an observer
/// instead of editing the shared learn path, so talents and professions coexist. Observers run on the world
/// thread, in registration order; an exception from one is not caught.
/// </summary>
public interface ISpellLearnObserver
{
    /// <summary>
    /// Called before the spell is added to the book (also when the book will report it as already known, so an
    /// observer must check for itself). Return false to veto the learn: nothing changes and nothing is sent.
    /// </summary>
    bool BeforeLearn(Player player, uint spellId) => true;

    /// <summary>Called after the spell is in the book, SMSG_LEARNED_SPELL is out and a learned passive has been cast.</summary>
    void AfterLearn(Player player, uint spellId)
    {
    }

    /// <summary>Called after the spell left the book and SMSG_REMOVED_SPELL was sent.</summary>
    void AfterRemove(Player player, uint spellId)
    {
    }
}

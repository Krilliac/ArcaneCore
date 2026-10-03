using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    private readonly List<ISpellLearnObserver> _learnObservers = [];

    /// <summary>The registered learn/remove observers, in call order.</summary>
    public IReadOnlyList<ISpellLearnObserver> LearnObservers => _learnObservers;

    /// <summary>Register an observer of <see cref="LearnSpell"/> and <see cref="RemoveSpell"/> (world thread).</summary>
    public void AddLearnObserver(ISpellLearnObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _learnObservers.Add(observer);
    }

    /// <summary>Unregister an observer; false when it was not registered.</summary>
    public bool RemoveLearnObserver(ISpellLearnObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return _learnObservers.Remove(observer);
    }

    /// <summary>
    /// Take a spell away from a player (vmangos Player::RemoveSpell, Player.cpp:3797-3885, without the dependent and
    /// higher-rank cascade, which a feature layers on through <see cref="ISpellLearnObserver"/>): the book forgets it,
    /// SMSG_REMOVED_SPELL tells the client, and the player's auras of that spell go. Returns false when the spell was
    /// not known, the book cannot forget spells, or a quest reward settlement holds the character.
    /// </summary>
    public bool RemoveSpell(Player player, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (IsQuestSettlementPending(player) || Spellbook is null || !Spellbook.ForgetSpell(player, spellId))
        {
            return false;
        }

        player.Session.Send(WorldOpcode.SmsgRemovedSpell, SpellPackets.BuildRemovedSpell(spellId));
        RemoveAuras(player, spellId);
        foreach (ISpellLearnObserver observer in _learnObservers.ToArray())
        {
            observer.AfterRemove(player, spellId);
        }

        return true;
    }

    private bool NotifyBeforeLearn(Player player, uint spellId)
    {
        foreach (ISpellLearnObserver observer in _learnObservers.ToArray())
        {
            if (!observer.BeforeLearn(player, spellId))
            {
                return false;
            }
        }

        return true;
    }

    private void NotifyAfterLearn(Player player, uint spellId)
    {
        foreach (ISpellLearnObserver observer in _learnObservers.ToArray())
        {
            observer.AfterLearn(player, spellId);
        }
    }
}

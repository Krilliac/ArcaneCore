using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Talents;

namespace ArcaneCore.Game.Talents;

public sealed partial class TalentService
{
    private const int MaxChainDepth = 64;

    /// <summary>
    /// Restore a player's stored talent state at login (the respec economy and the disabled set; nothing is sent and the
    /// sink is not told, because loading is not a change). Call <see cref="RemoveDisabledFromBook"/> afterwards.
    /// </summary>
    public void LoadState(Player player, RespecState respec, IEnumerable<uint> disabled)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(disabled);
        PlayerTalentState state = StateOf(player);
        state.Respec = respec;
        uint[] spells = [.. disabled];
        state.Disabled.Clear();
        state.Disabled.UnionWith(spells);
    }

    /// <summary>
    /// A spell that is both in the book and disabled is disabled: a crash between the two persisted writes can leave both,
    /// and the safe reading loses an ability rather than granting one. Silent (the player is loading). Returns what was taken
    /// out of the book.
    /// </summary>
    public IReadOnlyList<uint> RemoveDisabledFromBook(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (Spells.Spellbook is not { } book)
        {
            return [];
        }

        List<uint> removed = [];
        foreach (uint spell in StateOf(player).Disabled.Order())
        {
            if (book.HasSpell(player, spell) && book.ForgetSpell(player, spell))
            {
                removed.Add(spell);
            }
        }

        return removed;
    }

    private bool IsPassive(uint spellId) => Spells.Store.Get(spellId)?.IsPassive ?? false;

    /// <summary>
    /// vmangos Player::RemoveSpell(spell, disabled, false) as ResetTalents drives it (Player.cpp:3797-3885): LEARN_SPELL
    /// children first (:3803-3808), then the higher non-talent ranks the player holds (:3820-3827), then the spell itself.
    /// A talent is always removed outright (:3818); anything else is removed when passive and disabled otherwise, so it comes
    /// back with its talent.
    /// </summary>
    private void RemoveWithDependents(Player player, uint spellId, bool disableIfActive, HashSet<uint> visited)
    {
        if (!HasSpell(player, spellId) || !visited.Add(spellId))
        {
            return;
        }

        if (Spells.Store.Get(spellId) is { } info)
        {
            foreach (SpellEffectInfo effect in info.Effects)
            {
                if (effect.Effect == SpellEffectName.LearnSpell && effect.TriggerSpell != 0)
                {
                    uint child = effect.TriggerSpell;
                    RemoveWithDependents(player, child, disableIfActive: !IsPassive(child), visited);
                }
            }
        }

        bool disable = disableIfActive && !Catalog.TryGetRankPosition(spellId, out _);
        foreach (uint higher in HigherRanksKnown(player, spellId))
        {
            if (!Catalog.TryGetRankPosition(higher, out _))
            {
                RemoveWithDependents(player, higher, disableIfActive: !IsPassive(higher), visited);
            }
        }

        if (!HasSpell(player, spellId))
        {
            return;
        }

        if (disable)
        {
            // The sink hears of the disabled record before the book loses the spell, so a persisted crash window can only
            // lose an ability (spell gone) or leave both rows (which loads as disabled), never keep the spell enabled.
            StateOf(player).Disabled.Add(spellId);
            Sink?.DisabledChanged(player, spellId, true);
        }

        RemoveFromBook(player, spellId);
    }

    /// <summary>Take a known spell out of the book: announced when the player is in the world, silent while loading.</summary>
    private void RemoveFromBook(Player player, uint spellId)
    {
        if (player.IsInWorld)
        {
            Spells.RemoveSpell(player, spellId);
        }
        else
        {
            Spells.Spellbook?.ForgetSpell(player, spellId);
        }
    }

    /// <summary>The known spells whose rank chain leads up from <paramref name="spellId"/>, lowest rank first.</summary>
    private List<uint> HigherRanksKnown(Player player, uint spellId)
    {
        if (RankChain is null || KnownSpells is null)
        {
            return [];
        }

        List<(uint Spell, int Depth)> found = [];
        foreach (uint known in KnownSpells(player).ToArray())
        {
            if (known != spellId && ChainDepthAbove(known, spellId) is int depth and > 0)
            {
                found.Add((known, depth));
            }
        }

        return [.. found.OrderBy(f => f.Depth).ThenBy(f => f.Spell).Select(f => f.Spell)];
    }

    /// <summary>How many rank steps <paramref name="spell"/> lies above <paramref name="ancestor"/> (0 = not above).</summary>
    private int ChainDepthAbove(uint spell, uint ancestor)
    {
        uint cursor = spell;
        for (int depth = 1; depth <= MaxChainDepth; depth++)
        {
            cursor = RankChain!.PreviousRank(cursor);
            if (cursor == 0)
            {
                return 0;
            }

            if (cursor == ancestor)
            {
                return depth;
            }
        }

        return 0;
    }

    private void ClearDisabled(Player player, uint spellId)
    {
        if (StateOf(player).Disabled.Remove(spellId))
        {
            Sink?.DisabledChanged(player, spellId, false);
        }
    }

    /// <summary>
    /// After a talent rank spell is learned, the disabled ranks above it come back (vmangos Player::LearnSpell,
    /// Player.cpp:3784-3796: "learn all disabled higher ranks"), lowest first, each announced with SMSG_LEARNED_SPELL.
    /// </summary>
    private void ReEnableHigherRanks(Player player, uint talentSpell)
    {
        PlayerTalentState state = StateOf(player);
        if (state.Disabled.Count == 0 || RankChain is null)
        {
            return;
        }

        List<uint> back = [.. state.Disabled
            .Select(spell => (Spell: spell, Depth: ChainDepthAbove(spell, talentSpell)))
            .Where(x => x.Depth > 0)
            .OrderBy(x => x.Depth).ThenBy(x => x.Spell)
            .Select(x => x.Spell)];
        foreach (uint spell in back)
        {
            ClearDisabled(player, spell);
            Spells.LearnSpell(player, spell);
        }
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Talents;

namespace ArcaneCore.Game.Talents;

public sealed partial class TalentService
{
    /// <summary>
    /// The spell-removal half of vmangos Player::ResetTalents (Player.cpp:4075-4125): for every talent of the player's
    /// class tree (talents of another class are left alone, :4101-4104) the auras of each rank spell's trigger spells go,
    /// then the rank spell itself. Returns false when nothing is spent or a quest settlement holds the character.
    /// </summary>
    internal bool ResetTalentSpells(Player player)
    {
        if (!player.CanMutateQuestSettlementState)
        {
            return false;
        }

        if (UsedPoints(player) == 0)
        {
            UpdateFreeTalentPoints(player, resetIfNeed: false);   // vmangos: "for fix if need counter"
            return false;
        }

        uint classMask = ClassMask(player);
        foreach (TalentRecord talent in Catalog.Talents)
        {
            if ((Catalog.Tab(talent.TabId)!.ClassMask & classMask) == 0)
            {
                continue;
            }

            foreach (uint rankSpell in talent.RankSpells)
            {
                if (rankSpell == 0)
                {
                    continue;
                }

                if (Spells.Store.Get(rankSpell) is { } info)
                {
                    foreach (SpellEffectInfo effect in info.Effects)
                    {
                        if (effect.TriggerSpell != 0)
                        {
                            Spells.RemoveAuras(player, effect.TriggerSpell);
                        }
                    }
                }

                RemoveTalentSpell(player, rankSpell);
            }
        }

        UpdateFreeTalentPoints(player, resetIfNeed: false);
        return true;
    }

    /// <summary>Take a known talent rank spell out of the book: announced when the player is in the world, silent while loading.</summary>
    private void RemoveTalentSpell(Player player, uint spellId)
    {
        if (!HasSpell(player, spellId))
        {
            return;
        }

        if (player.IsInWorld)
        {
            Spells.RemoveSpell(player, spellId);
        }
        else
        {
            Spells.Spellbook?.ForgetSpell(player, spellId);
        }
    }
}

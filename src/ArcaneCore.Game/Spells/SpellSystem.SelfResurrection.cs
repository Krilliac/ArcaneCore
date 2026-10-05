using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// Build 5875's empty CMSG_SELF_RES uses the server's PLAYER_SELF_RES_SPELL;
    /// vmangos 0e3ff01 SpellHandler.cpp:461-473 casts normally and then clears it,
    /// including an unavailable spell or a rejected cast. Knowledge of the internal
    /// effect is not required (unlike CMSG_CAST_SPELL).
    /// </summary>
    public bool TrySelfResurrect(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Combat.DeathState is DeathState.Alive or DeathState.JustAlived
            || !player.IsInWorld || player.Map is null || IsInTransit(player)
            || IsQuestSettlementPending(player) || !ReferenceEquals(Units.Find(player, player.Guid), player))
        {
            return false;
        }

        uint spellId = player.GetUInt32(UpdateFields.PlayerSelfResSpell);
        if (spellId == 0)
        {
            return false;
        }

        try
        {
            return CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: false) == SpellCastResult.CastOk;
        }
        finally
        {
            player.SetUInt32(UpdateFields.PlayerSelfResSpell, 0);
        }
    }

    private void CaptureSelfResurrectionOffer(Player player)
    {
        if (player.GetUInt32(UpdateFields.PlayerSelfResSpell) != 0 || !player.IsInWorld
            || player.Map is null || IsInTransit(player) || IsQuestSettlementPending(player)
            || !ReferenceEquals(Units.Find(player, player.Guid), player))
        {
            return;
        }

        // Player.cpp:1538-1559 preserves the selection before death cleanup.
        // SelectResurrectionSpellId:19889-19939 iterates actual Dummy modifiers,
        // including multiple effects in a holder. Preserve its ordering rule:
        // a successful Nether roll can override Soulstone, and blocks a later
        // Soulstone selection despite the reference's "prio 3" comment.
        uint selected = 0;
        uint priority = 0;
        foreach (SpellAuraHolder holder in GetAuras(player))
        {
            if (holder.IsRemoved) continue;
            SpellInfo spell = holder.Spell;
            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura?.Type != AuraType.Dummy) continue;
                if (priority < 2 && spell.SpellVisual == 99 && spell.SpellIconId == 92)
                {
                    uint effectId = spell.Id switch
                    {
                        20707 => 3026,
                        20762 => 20758,
                        20763 => 20759,
                        20764 => 20760,
                        20765 => 20761,
                        _ => 0,
                    };
                    if (effectId == 0) continue;
                    selected = effectId;
                    priority = 3;
                }
                else if (spell.Id == 23701 && Random.Next(100) < 10)
                {
                    selected = 23700;
                    priority = 2;
                }
            }
        }

        // Player.cpp:19934-19939: the learned passive is distinct from its
        // internal effect. Cooldown readiness and a carried Ankh gate the offer.
        if (priority < 1 && Spellbook?.HasSpell(player, 20608) == true && Store.Get(21169) is { } reincarnation
            && IsSpellReady(player, reincarnation) && player.Inventory.GetItemCount(17030) > 0)
        {
            selected = 21169;
        }
        if (selected != 0)
        {
            player.SetUInt32(UpdateFields.PlayerSelfResSpell, selected);
        }
    }
}

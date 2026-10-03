using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// vmangos WorldSession::SendSpiritResurrect over the existing death flow:
/// <see cref="MapCombat.ResurrectAtSpiritHealer"/> (50% health/mana, corpse removed), then
/// resurrection sickness (Player::ResurrectPlayer applySickness: spell 15007 from level
/// Death.SicknessLevel 11, shortened to (level − 10) minutes below level 20), 25% durability loss
/// on equipment and bags (DurabilityLossAll(0.25, true)) and a save.
/// </summary>
/// <param name="spells">The spell system (sickness); without it, or without spell 15007 in the store, no sickness is applied.</param>
/// <param name="items">Durability loss; optional.</param>
/// <param name="save">Saves the character (vmangos SaveToDB).</param>
public sealed class SpiritHealerResurrection(Func<SpellSystem?> spells, IItemService? items, Action<Player> save) : IResurrection
{
    /// <summary>SPELL_ID_PASSIVE_RESURRECTION_SICKNESS (vmangos SharedDefines.h).</summary>
    public const uint ResurrectionSicknessSpell = 15007;

    /// <summary>Death.SicknessLevel default (vmangos mangosd.conf.dist).</summary>
    public const int SicknessStartLevel = 11;

    /// <summary>vmangos SendSpiritResurrect: DurabilityLossAll(0.25f, true).</summary>
    public const double DurabilityLossPercent = 0.25;

    public bool ResurrectAtSpiritHealer(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Map is not { } map || !map.Combat.ResurrectAtSpiritHealer(player))
        {
            return false;
        }

        ApplySickness(player);
        items?.DurabilityLossAll(player, DurabilityLossPercent);
        save(player);
        return true;
    }

    private void ApplySickness(Player player)
    {
        if (player.Level < SicknessStartLevel || spells() is not { } system || system.Store.Get(ResurrectionSicknessSpell) is null)
        {
            return;
        }

        system.CastSpell(player, ResurrectionSicknessSpell, SpellCastTargets.ForSelf(), triggered: true);
        if (player.Level >= SicknessStartLevel + 9)
        {
            return;
        }

        int durationMs = (player.Level - SicknessStartLevel + 1) * 60 * 1000;
        foreach (SpellAuraHolder holder in system.GetAuras(player).Where(h => h.Spell.Id == ResurrectionSicknessSpell && !h.IsPermanent))
        {
            holder.MaxDuration = durationMs;
            holder.Duration = durationMs;
            if (holder.Slot != SpellAuraHolder.NoSlot)
            {
                player.Session.Send(WorldOpcode.SmsgUpdateAuraDuration, SpellPackets.BuildUpdateAuraDuration(holder.Slot, (uint)durationMs));
            }
        }
    }
}

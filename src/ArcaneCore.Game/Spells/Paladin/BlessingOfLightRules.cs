using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Paladin;

/// <summary>
/// Blessing of Light (19977, 19978, 19979, Greater 25890; vmangos <c>Unit::SpellHealingBonusTaken</c>, Unit.cpp:5364-5378): the blessing's DUMMY
/// auras add to the healing the unit takes from Holy Light (effect 0's amount; CF_PALADIN_HOLY_LIGHT2, bit 31) and Flash of Light (effect 1's
/// amount; CF_PALADIN_FLASH_OF_LIGHT1, bit 13). The build 5875 data heal through 19968 (Holy Light, CF_PALADIN_HOLY_LIGHT1 bit 14) and 19993
/// (Flash of Light, bit 13) cast by the ranks' script effects (<see cref="HolyLightScript"/>), where the vmangos database makes the Holy Light ranks
/// direct heals with bit 31; both Holy Light bits count here. The amount joins the flat healing-taken bonus, so it goes through the heal's coefficient and
/// stack count like SPELL_AURA_MOD_HEALING. A blessing is told by the CF_PALADIN_BLESSINGS mask and spell visual 300.
/// </summary>
public static class BlessingOfLightRules
{
    public const int FlashOfLightBit = 13;
    public const int HolyLightBit = 31;
    public const int HolyLightHealBit = 14;
    public const int BlessingsBit = 28;
    public const uint BlessingOfLightVisual = 300;

    /// <summary>The flat healing-taken bonus of the Blessing of Light auras on <paramref name="target"/> for a heal of <paramref name="spell"/>.</summary>
    public static int TakenBonus(SpellSystem system, Unit target, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        bool holyLight = spell.IsFitToFamily(PaladinSpells.Family, HolyLightBit) || spell.IsFitToFamily(PaladinSpells.Family, HolyLightHealBit);
        bool flashOfLight = spell.IsFitToFamily(PaladinSpells.Family, FlashOfLightBit);
        if (!holyLight && !flashOfLight)
        {
            return 0;
        }

        int bonus = 0;
        foreach (SpellAuraHolder holder in system.GetAuras(target))
        {
            if (holder.IsRemoved || !holder.Spell.IsFitToFamily(PaladinSpells.Family, BlessingsBit) || holder.Spell.SpellVisual != BlessingOfLightVisual)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.AuraSpan)
            {
                if (aura is not { Type: AuraType.Dummy })
                {
                    continue;
                }

                if ((holyLight && aura.EffectIndex == 0) || (flashOfLight && !holyLight && aura.EffectIndex == 1))
                {
                    bonus += aura.Amount;
                }
            }
        }

        return bonus;
    }
}

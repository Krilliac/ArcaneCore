using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stats;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// vmangos <c>Player::GetWeaponBasedAuraModifier</c> and <c>Creature::GetWeaponBasedAuraModifier</c> (StatSystem.cpp:482-503, 959-976): the sum of a
/// unit's auras of one type, where an aura whose spell names an equipped item class (EquippedItemClass &gt;= 0: Precision, a bow or gun
/// hit bonus, the weapon finesse parry talents) only counts while the hand's weapon fits the spell's class and subclass mask
/// (<c>Item::IsFitToSpellRequirements</c>, Item.cpp:975-1003). A player's weapon is the item in the hand's slot whatever its state
/// (<c>Player::GetWeaponForAttack(attType)</c> asks for neither an unbroken nor a usable weapon); a creature's is its virtual item (display id,
/// class and subclass in UNIT_VIRTUAL_ITEM_SLOT_DISPLAY / UNIT_VIRTUAL_ITEM_INFO). World thread only; reads the live aura list, nothing is cached.
/// </summary>
public static class WeaponAuraModifiers
{
    /// <summary>The weapon-checked sum of <paramref name="unit"/>'s <paramref name="type"/> auras for <paramref name="attackType"/>.</summary>
    public static float Get(SpellSystem spells, Unit unit, WeaponAttackType attackType, AuraType type)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(unit);
        float amount = 0f;
        foreach (SpellAuraHolder holder in spells.GetAuras(unit))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            bool? fits = null;
            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is null || aura.Type != type)
                {
                    continue;
                }

                if (holder.Spell.EquippedItemClass >= 0 && !(fits ??= Fits(unit, attackType, holder.Spell)))
                {
                    continue;
                }

                amount += aura.Amount;
            }
        }

        return amount;
    }

    /// <summary>Whether the weapon <paramref name="unit"/> holds for <paramref name="attackType"/> meets <paramref name="spell"/>'s item requirement.</summary>
    public static bool Fits(Unit unit, WeaponAttackType attackType, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.EquippedItemClass < 0)
        {
            return true;
        }

        if (unit is Player player)
        {
            Item? weapon = PlayerStatSystem.GetWeaponForAttack(player, attackType, nonBroken: false, useable: false);
            return weapon is not null && EquippedItemCastCheck.IsFit(weapon, spell);
        }

        // Creature::GetWeaponBasedAuraModifier: no virtual item in the slot, no fit; otherwise its class and subclass.
        int slot = (int)attackType;
        if (slot is < 0 or > 2 || unit.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + slot) == 0)
        {
            return false;
        }

        int info = UpdateFields.UnitVirtualItemInfo + (slot * 2);
        byte itemClass = unit.GetByte(info, 0);
        byte subClass = unit.GetByte(info, 1);
        return spell.EquippedItemClass == itemClass
            && (spell.EquippedItemSubClassMask == 0 || (spell.EquippedItemSubClassMask & (1 << subClass)) != 0);
    }
}

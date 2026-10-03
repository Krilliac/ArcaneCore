namespace ArcaneCore.Game.Spells.Rules;

/// <summary>The pure parts of the spell crit rules (vmangos Unit::GetSpellCritChance, Unit.cpp:5212-5316; SpellCaster.cpp:958-1024).</summary>
public static class SpellCritRules
{
    /// <summary>vmangos SPELLFAMILY_POTION (SpellDefines.h:46).</summary>
    private const uint FamilyPotion = 13;

    /// <summary>vmangos SPELLFAMILY_WARLOCK (SpellDefines.h:38).</summary>
    private const uint FamilyWarlock = 5;

    /// <summary>vmangos CF_WARLOCK_HEALTHSTONE class mask (SpellClassMask.h:111).</summary>
    private const ulong WarlockHealthstoneFlag = 16;

    /// <summary>The fixed crit chance of potions and healthstones in percent (Unit.cpp:5229-5231).</summary>
    public const float FixedPotionCritPercent = 10.0f;

    /// <summary>
    /// vmangos SpellEntry::CanCrit (SpellEntry.h:1110-1121): not CANT_CRIT and at least one damage or
    /// heal effect (school damage, power burn, health leech, the weapon damage effects, heal, heal max health).
    /// </summary>
    public static bool CanCrit(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.HasAttribute(SpellAttributesEx2.CantCrit))
        {
            return false;
        }

        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.Effect is SpellEffectName.SchoolDamage or SpellEffectName.PowerBurn or SpellEffectName.HealthLeech
                or SpellEffectName.WeaponDamageNoschool or SpellEffectName.WeaponPercentDamage or SpellEffectName.WeaponDamage
                or SpellEffectName.NormalizedWeaponDmg or SpellEffectName.Heal or SpellEffectName.HealMaxHealth)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Potions and warlock healthstones crit at a constant chance (Unit.cpp:5229-5231).</summary>
    public static bool IsFixedChanceSpell(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return spell.SpellFamilyName == FamilyPotion
            || (spell.SpellFamilyName == FamilyWarlock && (spell.SpellFamilyFlags & WarlockHealthstoneFlag) != 0);
    }

    /// <summary>
    /// The extra damage of a crit before the talent bonus and the creature-type multiplier: the whole
    /// hit for melee and ranged class spells, half of it for everything else (SpellCaster.cpp:960-972).
    /// </summary>
    public static int CritBonus(uint damage, SpellDamageClass damageClass) =>
        damageClass is SpellDamageClass.Melee or SpellDamageClass.Ranged ? (int)damage : (int)(damage / 2);
}

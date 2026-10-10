using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Rules;

/// <summary>The pure parts of the spell crit rules (vmangos Unit::GetSpellCritChance, Unit.cpp:5212-5316; SpellCaster.cpp:958-1024).</summary>
public static class SpellCritRules
{
    /// <summary>vmangos SPELLFAMILY_POTION (SpellDefines.h:46).</summary>
    private const uint FamilyPotion = 13;

    /// <summary>vmangos SPELLFAMILY_WARLOCK (SpellDefines.h:38).</summary>
    private const uint FamilyWarlock = 5;

    /// <summary>vmangos CF_WARLOCK_HEALTHSTONE class mask: bit 16, CM0 0x00010000 (SpellClassMask.h:111).</summary>
    private const ulong WarlockHealthstoneFlag = 1UL << 16;

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
    /// Shatter: the OVERRIDE_CLASS_SCRIPTS misc values the magic crit roll reads and their bonus in percent against a frozen victim (vmangos
    /// Unit::GetSpellCritChance, Unit.cpp:5259-5290: 849, 910, 911, 912, 913 = ranks 1-5).
    /// </summary>
    public static IReadOnlyDictionary<int, float> ShatterBonuses { get; } = new Dictionary<int, float>
    {
        [849] = 10.0f,
        [910] = 20.0f,
        [911] = 30.0f,
        [912] = 40.0f,
        [913] = 50.0f,
    };

    /// <summary>
    /// The scripted magic crit bonus of <paramref name="caster"/> against <paramref name="victim"/> (vmangos Unit.cpp:5259-5290): every
    /// OVERRIDE_CLASS_SCRIPTS aura of the caster whose spell is of <paramref name="spell"/>'s family adds its Shatter bonus when the victim is
    /// frozen. Build 5875 is past 1.11.0 ("Shatter was changed to affect all spells, previously limited to Frost spells"), so the aura's class
    /// mask is not consulted.
    /// </summary>
    public static float ScriptedCritBonus(SpellSystem system, Unit caster, Unit victim, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(victim);
        ArgumentNullException.ThrowIfNull(spell);
        float bonus = 0f;
        bool? frozen = null;
        foreach (SpellAuraHolder holder in system.GetAuras(caster))
        {
            if (holder.IsRemoved || holder.Spell.SpellFamilyName != spell.SpellFamilyName)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.AuraSpan)
            {
                if (aura is { Type: AuraType.OverrideClassScripts } && ShatterBonuses.TryGetValue(aura.MiscValue, out float shatter)
                    && (frozen ??= IsFrozen(system, victim)))
                {
                    bonus += shatter;
                }
            }
        }

        return bonus;
    }

    /// <summary>
    /// vmangos Unit::IsFrozen (Unit.h:805, HasAuraState(AURA_STATE_FROZEN)): the state bit, which vmangos sets while a stun or root aura of a
    /// frost spell holds the unit (Aura::HandleAuraModStun / HandleAuraModRoot, SpellAuras.cpp:3565, 3807). The aura test is made here as well, so
    /// a frost stun or root counts even where the aura handlers do not set the bit.
    /// </summary>
    public static bool IsFrozen(SpellSystem system, Unit unit)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(unit);
        if ((unit.GetUInt32(UpdateFields.UnitFieldAurastate) & (1u << ((int)AuraState.Frozen - 1))) != 0)
        {
            return true;
        }

        const uint frostMask = 1u << (int)SpellSchool.Frost;
        foreach (SpellAuraHolder holder in system.GetAuras(unit))
        {
            if (!holder.IsRemoved && (holder.Spell.SchoolMask() & frostMask) != 0
                && holder.Auras.Any(a => a is { Type: AuraType.ModStun or AuraType.ModRoot }))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The extra damage of a crit before the talent bonus and the creature-type multiplier: the whole
    /// hit for melee and ranged class spells, half of it for everything else (SpellCaster.cpp:960-972).
    /// </summary>
    public static int CritBonus(uint damage, SpellDamageClass damageClass) =>
        damageClass is SpellDamageClass.Melee or SpellDamageClass.Ranged ? (int)damage : (int)(damage / 2);
}

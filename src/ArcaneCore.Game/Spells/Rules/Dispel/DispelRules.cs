namespace ArcaneCore.Game.Spells.Rules.Dispel;

/// <summary>The pure rules of SPELL_EFFECT_DISPEL (vmangos Spell::EffectDispel, SpellEffects.cpp:2456-2570).</summary>
public static class DispelRules
{
    private const uint FamilyWarrior = 4;
    private const uint FamilyWarlock = 5;

    /// <summary>vmangos CF_WARRIOR_SHIELD_SLAM: bit 32, CM1 0x00000001 (SpellClassMask.h:92).</summary>
    private const ulong ShieldSlamFlag = 1UL << 32;

    /// <summary>vmangos CF_WARLOCK_SPELLSTONE: bit 17, CM0 0x00020000 (SpellClassMask.h:112).</summary>
    private const ulong SpellstoneFlag = 1UL << 17;

    /// <summary>
    /// The dispel types a dispel effect with EffectMiscValue <paramref name="miscValue"/> removes: a negative
    /// value and ALL (7) are magic, curse, disease and poison; any other type is its own bit (SpellEffects.cpp:2480-2481).
    /// </summary>
    public static uint MaskFor(int miscValue) => miscValue < 0 ? DispelTypes.AllMask : DispelTypes.GetDispelMask((uint)miscValue);

    /// <summary>Shield Slam dispels with a 50% chance (SpellEffects.cpp:2461-2463).</summary>
    public static bool IsShieldSlam(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return spell.SpellFamilyName == FamilyWarrior && (spell.SpellFamilyFlags & ShieldSlamFlag) != 0;
    }

    /// <summary>The warlock spellstone dispels negative and positive effects alike (SpellEffects.cpp:2470-2473).</summary>
    public static bool IgnoresFaction(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return spell.SpellFamilyName == FamilyWarlock && (spell.SpellFamilyFlags & SpellstoneFlag) != 0;
    }

    /// <summary>Only magic and poison auras are filtered by the target's friendliness (SpellEffects.cpp:2486-2506).</summary>
    public static bool UsesPolarity(uint dispelType) => dispelType is (uint)DispelType.Magic or (uint)DispelType.Poison;
}

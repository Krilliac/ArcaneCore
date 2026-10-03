using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Ranged;

/// <summary>
/// Classification of a spell's ranged-combat behaviour from its raw Spell.dbc attribute bits.
/// Bit values: vmangos SpellDefines.h (SPELL_ATTR_USES_RANGED_SLOT 0x2 at line 831,
/// SPELL_ATTR_NEED_EXOTIC_AMMO 0x8 at 833, SPELL_ATTR_EX2_AUTO_REPEAT 0x20 at 911,
/// SPELL_ATTR_EX2_DO_NOT_RESET_COMBAT_TIMERS 0x20000 at 923). The reference repositories carry
/// no Spell.dbc, so the bits themselves must be confirmed against the developer's client data
/// (docs/areas/hunter.md).
/// </summary>
public static class RangedSpellFacts
{
    /// <summary>SPELL_ATTR_NEED_EXOTIC_AMMO (vanilla only).</summary>
    public const uint NeedExoticAmmoAttribute = 0x00000008;

    /// <summary>SPELL_ATTR_EX2_AUTO_REPEAT: Auto Shot, Shoot (wand), Throw.</summary>
    public const uint AutoRepeatAttributeEx2 = 0x00000020;

    /// <summary>SPELL_ATTR_EX3_NORMAL_RANGED_ATTACK (vmangos SpellDefines.h:962): the spell is a ranged weapon attack (Auto Shot, Throw, Shoot Bow/Gun/Crossbow, wand Shoot).</summary>
    public const uint NormalRangedAttackEx3 = 0x00008000;

    /// <summary>SPELL_ATTR_EX2_DO_NOT_RESET_COMBAT_TIMERS.</summary>
    public const uint DoNotResetCombatTimersEx2 = 0x00020000;

    /// <summary>vmangos SpellEntry::IsRangedSpell: the spell uses the ranged weapon slot.</summary>
    public static bool IsRanged(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return spell.HasAttribute(SpellAttributes.UsesRangedSlot);
    }

    /// <summary>
    /// vmangos SpellEntry::IsAutoRepeatRangedSpell, which is what Spell::IsAutoRepeat reports
    /// (Spell.cpp:80,118): ranged-slot spell with the auto-repeat attribute.
    /// </summary>
    public static bool IsAutoRepeatRanged(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return IsRanged(spell) && (((uint)spell.AttributesEx2) & AutoRepeatAttributeEx2) != 0;
    }

    /// <summary>The vanilla attribute: the spell needs ammunition flagged ITEM_FLAG_EXOTIC.</summary>
    public static bool NeedsExoticAmmo(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return (((uint)spell.Attributes) & NeedExoticAmmoAttribute) != 0;
    }

    /// <summary>The spell does not restart the swing timers (vmangos Player.cpp:22193-22197).</summary>
    public static bool DoesNotResetCombatTimers(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return (((uint)spell.AttributesEx2) & DoNotResetCombatTimersEx2) != 0;
    }

    /// <summary>
    /// vmangos SpellEntry::GetWeaponAttackType: the ranged damage class uses the ranged weapon;
    /// melee never does; any other class does when it carries the auto-repeat attribute (wand Shoot).
    /// </summary>
    public static bool UsesRangedWeapon(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return spell.DamageClass switch
        {
            SpellDamageClass.Ranged => true,
            SpellDamageClass.Melee => false,
            _ => (((uint)spell.AttributesEx2) & AutoRepeatAttributeEx2) != 0,
        };
    }

    /// <summary>
    /// A ranged weapon attack that rolls on the ranged hit and crit tables whatever its damage class says (vmangos
    /// SpellCaster::SpellHitResult, SpellCaster.cpp:215-232 and Unit::GetSpellCritChance, Unit.cpp:5231-5239): the weapon attack type
    /// is ranged and the spell carries EX3_NORMAL_RANGED_ATTACK. Wand Shoot is damage class magic and gets here through its Ex2
    /// auto-repeat attribute; the few spells that carry the Ex3 bit without being weapon attacks are excluded.
    /// </summary>
    public static bool IsNormalRangedAttack(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return UsesRangedWeapon(spell) && (spell.AttributesEx3 & NormalRangedAttackEx3) != 0;
    }

    /// <summary>The damage class the hit and crit tables use: ranged for <see cref="IsNormalRangedAttack"/>, else the spell's own.</summary>
    public static SpellDamageClass HitDamageClass(SpellInfo spell)
        => IsNormalRangedAttack(spell) ? SpellDamageClass.Ranged : spell.DamageClass;

    /// <summary>vmangos CLASSMASK_WAND_USERS (SharedDefines.h:112): priest, mage and warlock.</summary>
    public static bool IsWandUser(ArcaneCore.Game.Class playerClass) => playerClass is ArcaneCore.Game.Class.Priest or ArcaneCore.Game.Class.Mage or ArcaneCore.Game.Class.Warlock;

    /// <summary>The spell has a SPELL_EFFECT_WEAPON_DAMAGE(_NOSCHOOL) effect, the trigger of the ammunition checks in Spell::CheckCast.</summary>
    public static bool HasWeaponDamageEffect(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return spell.Effects.Any(e => e.Effect is SpellEffectName.WeaponDamage or SpellEffectName.WeaponDamageNoschool);
    }
}

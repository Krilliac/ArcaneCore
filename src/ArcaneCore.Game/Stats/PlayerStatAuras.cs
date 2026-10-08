using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Stats;

/// <summary>
/// One aura the player stat formulas read: its type, amount (as applied), misc value (a school mask for the damage types), polarity and the
/// item requirement of its spell (EquippedItemClass, EquippedItemSubClassMask, EquippedItemInventoryTypeMask) with the item that cast it.
/// </summary>
/// <param name="Type">MOD_CRIT_PERCENT, MOD_DAMAGE_DONE, MOD_DAMAGE_PERCENT_DONE, MOD_PARRY_PERCENT (weapon-restricted only) or MOD_SPELL_DAMAGE_OF_STAT_PERCENT.</param>
/// <param name="Amount">The modifier amount when the aura was applied (vmangos Modifier::m_amount).</param>
/// <param name="Misc">The modifier misc value.</param>
/// <param name="Positive">Aura::IsPositive of the effect (the client's positive / negative damage done fields).</param>
/// <param name="Spell">The spell, for its item requirement.</param>
/// <param name="CastItem">The item that cast the aura (an enchantment), empty for talents and buffs.</param>
public sealed record StatAuraTerm(AuraType Type, int Amount, int Misc, bool Positive, SpellInfo Spell, ObjectGuid CastItem)
{
    /// <summary>The spell names no item class: the term applies whatever the weapon (vmangos <c>EquippedItemClass == -1</c>).</summary>
    public bool IsGeneric => Spell.EquippedItemClass < 0;

    /// <summary>The spell names neither an item class nor an inventory type (vmangos: "skip item specific requirements", the client display fields).</summary>
    public bool IsItemIndependent => Spell.EquippedItemClass == -1 && Spell.EquippedItemInventoryTypeMask == 0;
}

/// <summary>
/// The auras of one player that the stat formulas fold in at every recompute, kept by the aura handlers of
/// <see cref="CombatStatAuras"/> (apply adds the term, removal takes the same term back) instead of being written as deltas, so a term that
/// depends on the worn weapon follows equip, unequip, break and disarm by itself:
/// <list type="bullet">
/// <item>MOD_CRIT_PERCENT: a generic aura is the FLAT_MOD of CRIT_PERCENTAGE and RANGED_CRIT_PERCENTAGE (Aura::HandleAuraModCritPercent,
/// SpellAuras.cpp:5021-5049); a weapon-restricted one counts for the hand whose usable, unbroken weapon fits, the off hand only for the aura its own item
/// cast (Player::_ApplyWeaponDependentAuraCritMod, Player.cpp:7029-7070).</item>
/// <item>MOD_DAMAGE_DONE (physical): a generic aura is UNIT_MOD_DAMAGE_PHYSICAL TOTAL_VALUE, a weapon-restricted one the hand's TOTAL_VALUE
/// (Aura::HandleModDamageDone, SpellAuras.cpp:5230-5316; Player::_ApplyWeaponDependentAuraDamageMod, Player.cpp:7072-7114).</item>
/// <item>MOD_DAMAGE_PERCENT_DONE: a generic physical aura is the hands' TOTAL_PCT (kept in <see cref="UnitModLedger"/> by the handler); a
/// weapon-restricted one the fitting hand's TOTAL_PCT, a magic one only for a wand user's ranged hand (Wand Specialization).</item>
/// <item>MOD_PARRY_PERCENT restricted to a weapon: counts while the main-hand weapon fits (Player::UpdateParryPercentage through
/// GetWeaponBasedAuraModifier, StatSystem.cpp:601).</item>
/// <item>MOD_DAMAGE_DONE by magic school and MOD_SPELL_DAMAGE_OF_STAT_PERCENT: the client's damage done fields
/// (Player::UpdateSpellDamageAndHealingBonus, StatSystem.cpp:80-88).</item>
/// </list>
/// World thread only. Small (a handful of auras per player); the reads allocate nothing.
/// </summary>
public sealed class PlayerStatAuras
{
    private const byte ItemClassWeapon = 2;
    private const uint SchoolMaskNormal = 1;
    private const uint SchoolMaskMagic = 0x7E;

    private readonly Dictionary<SpellAura, StatAuraTerm> _terms = [];

    /// <summary>The number of terms kept.</summary>
    public int Count => _terms.Count;

    /// <summary>Every term kept (no order).</summary>
    public IEnumerable<StatAuraTerm> Terms => _terms.Values;

    /// <summary>Keep <paramref name="term"/> for <paramref name="aura"/>; false when the aura already has one.</summary>
    public bool Add(SpellAura aura, StatAuraTerm term)
    {
        ArgumentNullException.ThrowIfNull(aura);
        ArgumentNullException.ThrowIfNull(term);
        return _terms.TryAdd(aura, term);
    }

    /// <summary>Forget the term of <paramref name="aura"/> and return it, or null when it has none.</summary>
    public StatAuraTerm? Remove(SpellAura aura)
    {
        ArgumentNullException.ThrowIfNull(aura);
        return _terms.Remove(aura, out StatAuraTerm? term) ? term : null;
    }

    /// <summary>Sum of the generic MOD_CRIT_PERCENT amounts (BaseModGroup CRIT_PERCENTAGE / RANGED_CRIT_PERCENTAGE FLAT_MOD).</summary>
    public float GenericCrit()
    {
        float total = 0f;
        foreach (StatAuraTerm term in _terms.Values)
        {
            if (term.Type == AuraType.ModCritPercent && term.IsGeneric)
            {
                total += term.Amount;
            }
        }

        return total;
    }

    /// <summary>
    /// The weapon-restricted MOD_CRIT_PERCENT a hand's weapon earns: <paramref name="weapon"/> is the usable, unbroken weapon of the hand (null: none).
    /// An aura cast by an item counts only for that item; one without a cast item (a talent) never for the off hand.
    /// </summary>
    public float WeaponCrit(Item? weapon, WeaponAttackType attackType)
    {
        if (weapon is null)
        {
            return 0f;
        }

        float total = 0f;
        foreach (StatAuraTerm term in _terms.Values)
        {
            if (term.Type != AuraType.ModCritPercent || term.IsGeneric
                || (!term.CastItem.IsEmpty && term.CastItem != weapon.Guid)
                || (term.CastItem.IsEmpty && attackType == WeaponAttackType.OffAttack)
                || !Spells.EquippedItemCastCheck.IsFit(weapon, term.Spell))
            {
                continue;
            }

            total += term.Amount;
        }

        return total;
    }

    /// <summary>UNIT_MOD_DAMAGE_PHYSICAL TOTAL_VALUE: the generic MOD_DAMAGE_DONE amounts with the physical school.</summary>
    public float PhysicalFlat()
    {
        float total = 0f;
        foreach (StatAuraTerm term in _terms.Values)
        {
            if (term.Type == AuraType.ModDamageDone && term.IsGeneric && (term.Misc & SchoolMaskNormal) != 0)
            {
                total += term.Amount;
            }
        }

        return total;
    }

    /// <summary>
    /// The TOTAL_VALUE (<paramref name="percent"/> false: MOD_DAMAGE_DONE) or TOTAL_PCT factor (true: MOD_DAMAGE_PERCENT_DONE) the
    /// weapon-restricted damage auras give a hand holding <paramref name="weapon"/> (usable and unbroken; null: none). A magic-only aura counts
    /// only for a wand user (Player::_ApplyWeaponDependentAuraDamageMod).
    /// </summary>
    public float WeaponDamage(Item? weapon, bool percent, bool wandUser)
    {
        float result = percent ? 1f : 0f;
        if (weapon is null || weapon.Template.Class != ItemClassWeapon)
        {
            return result;
        }

        AuraType type = percent ? AuraType.ModDamagePercentDone : AuraType.ModDamageDone;
        foreach (StatAuraTerm term in _terms.Values)
        {
            if (term.Type != type || term.IsGeneric
                || ((term.Misc & SchoolMaskNormal) == 0 && !wandUser)
                || !Spells.EquippedItemCastCheck.IsFit(weapon, term.Spell))
            {
                continue;
            }

            if (percent)
            {
                result *= PercentFactor.Multiplier(term.Amount);
            }
            else
            {
                result += term.Amount;
            }
        }

        return result;
    }

    /// <summary>The weapon-restricted MOD_PARRY_PERCENT the main-hand item earns (<paramref name="mainHand"/>: the weapon in the slot, any state).</summary>
    public float WeaponParry(Item? mainHand)
    {
        if (mainHand is null)
        {
            return 0f;
        }

        float total = 0f;
        foreach (StatAuraTerm term in _terms.Values)
        {
            if (term.Type == AuraType.ModParryPercent && !term.IsGeneric && Spells.EquippedItemCastCheck.IsFit(mainHand, term.Spell))
            {
                total += term.Amount;
            }
        }

        return total;
    }

    /// <summary>
    /// The client's PLAYER_FIELD_MOD_DAMAGE_DONE_POS / _NEG of <paramref name="school"/>: the positive and the negative MOD_DAMAGE_DONE amounts of
    /// the school (magic schools: item-independent auras only, HandleModDamageDone), plus for a magic school the spirit share of
    /// MOD_SPELL_DAMAGE_OF_STAT_PERCENT (truncated per aura, SpellBaseDamageBonusDone).
    /// </summary>
    public (int Positive, int Negative) DamageDone(int school, uint spirit)
    {
        uint mask = 1u << school;
        int positive = 0;
        int negative = 0;
        foreach (StatAuraTerm term in _terms.Values)
        {
            if (((uint)term.Misc & mask) == 0)
            {
                continue;
            }

            if (term.Type == AuraType.ModDamageDone && (school == 0 || term.IsItemIndependent))
            {
                if (term.Positive)
                {
                    positive += term.Amount;
                }
                else
                {
                    negative += term.Amount;
                }
            }
            else if (term.Type == AuraType.ModSpellDamageOfStatPercent && school != 0)
            {
                positive += (int)(spirit * term.Amount / 100.0f);
            }
        }

        return (positive, negative);
    }

    /// <summary>The client's PLAYER_FIELD_MOD_DAMAGE_DONE_PCT of <paramref name="school"/> (Player::UpdateDamageDonePercent, Player.cpp:7116-7136).</summary>
    public float DamageDonePercent(int school)
    {
        uint mask = 1u << school;
        float multiplier = 1.0f;
        foreach (StatAuraTerm term in _terms.Values)
        {
            if (term.Type == AuraType.ModDamagePercentDone && term.IsItemIndependent && ((uint)term.Misc & mask) != 0)
            {
                multiplier *= (100.0f + term.Amount) / 100.0f;
            }
        }

        return multiplier;
    }

    /// <summary>True for a school mask with the physical school (SPELL_SCHOOL_MASK_NORMAL).</summary>
    internal static bool HasPhysical(int misc) => ((uint)misc & SchoolMaskNormal) != 0;

    /// <summary>True for a school mask with a magic school.</summary>
    internal static bool HasMagic(int misc) => ((uint)misc & SchoolMaskMagic) != 0;
}

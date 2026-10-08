using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules;

namespace ArcaneCore.Game.Spells.Casters.Bonus;

/// <summary>
/// An explicit per-effect coefficient source (vmangos spell_template.effectBonusCoefficient1..3). Null means "no
/// explicit value": the default formula and the level penalty apply (SpellCaster.cpp:1737-1803). No implementation
/// ships yet (the vmangos values are GPL data and are not in the repository), so the engine runs formula-only.
/// </summary>
public interface ISpellBonusCoefficients
{
    float? Get(uint spellId, int effectIndex);
}

/// <summary>
/// Spell power for the world's <see cref="SpellSystem"/>: reads the caster and target auras and combines them with
/// <see cref="SpellBonusFormulas"/>. Caster side: ModDamageDone 13, ModHealingDone 135, ModSpellDamageOfStatPercent 174,
/// ModSpellHealingOfStatPercent 175 (spirit only in 1.12), ModDamagePercentDone 79, ModHealingDonePercent 136; target side:
/// ModDamageTaken 14, ModDamagePercentTaken 87, ModHealing 115, ModHealingPct 118. School matching uses the aura misc
/// value as a mask (the 1.12 build). Order and rounding follow vmangos: the caster side runs when a direct effect lands
/// or an over-time aura is created, the target side on every tick.
/// <para>
/// Damage done versus the victim's creature type (auras 168, 59, 180) is applied, and equipped-item restricted damage auras are left out of
/// spells as the reference does. Not modelled (documented limits): the paladin seal exception for weapon-restricted percent auras, class script
/// modifiers, Ignite, totems/pets using their owner, weapon-based periodic damage, and the melee class SCHOOL_DAMAGE spells' MeleeDamageBonusDone
/// (they take only the DAMAGE spell mod; weapon damage spells use <see cref="Combat.MeleeDamageBonus"/>).
/// </para>
/// </summary>
public sealed class SpellBonusModule(SpellSystem spells) : ISpellAmountModifier
{
    private const int SpiritStat = 4;

    // vmangos SpellDefines.h SPELLFAMILY_*, SpellClassMask.h CF_MAGE_FIRE_WARD (3), CF_MAGE_FROST_WARD (8), CF_PRIEST_POWER_WORD_SHIELD (0); Shadow Ward is
    // identified by icon 207 and category 56 (SpellAuras.cpp:5782, inside a SPELLFAMILY_WARLOCK case; the real rows carry family 0).
    private const uint MageFamily = 3;
    private const uint WarlockFamily = 5;
    private const uint PriestFamily = 6;
    private const int FireWardFlag = 3;
    private const int FrostWardFlag = 8;
    private const int PowerWordShieldFlag = 0;
    private const uint ShadowWardIcon = 207;
    private const uint ShadowWardCategory = 56;

    /// <summary>The explicit coefficient table, when one is loaded; null runs the formula only.</summary>
    public ISpellBonusCoefficients? Coefficients { get; set; }

    public float Modify(SpellAmountStage stage, Unit caster, Unit target, SpellInfo spell, int effectIndex, float amount, uint stack)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        if (stage == SpellAmountStage.AbsorbShield)
        {
            return AbsorbShield(caster, spell, amount);
        }

        bool overTime = stage is not (SpellAmountStage.DirectDamage or SpellAmountStage.DirectHeal);

        // Weapon based (melee / ranged class) damage belongs to the melee formulas: direct hits route by DmgClass
        // (SpellCaster.cpp:1243-1276, only NONE and MAGIC use SpellDamageBonusDone/Taken), periodic damage likewise
        // (Aura::CalculateDotDamage). Direct healing is not routed by class.
        if ((overTime || stage == SpellAmountStage.DirectDamage) && spell.DamageClass is SpellDamageClass.Melee or SpellDamageClass.Ranged)
        {
            // The weapon formulas skip spell power but still apply the DAMAGE / DOT spell mod to the done amount
            // (vmangos MeleeDamageBonusDone, SpellCaster.cpp:1443-1452); the target side (ticks) has no spell mod.
            return stage switch
            {
                SpellAmountStage.DirectDamage => spells.ModFloat(caster, spell, SpellModOp.Damage, amount),
                SpellAmountStage.DamageOverTimeSnapshot or SpellAmountStage.HealOverTimeSnapshot => spells.ModFloat(caster, spell, SpellModOp.Dot, amount),
                _ => amount,
            };
        }

        SpellBonusKind kind = overTime ? SpellBonusKind.OverTime : SpellBonusKind.SpellDirect;
        EffectiveCoefficient coefficient = SpellCoefficients.Resolve(spell, kind, Coefficients?.Get(spell.Id, effectIndex));
        int mask = 1 << (int)spell.School;
        return stage switch
        {
            SpellAmountStage.DirectDamage => Taken(false, Done(false, amount, caster, target, spell, mask, coefficient, 1, SpellModOp.Damage), target, spell, mask, coefficient, 1),
            SpellAmountStage.DirectHeal => Taken(true, Done(true, amount, caster, target, spell, mask, coefficient, 1, SpellModOp.Damage), target, spell, mask, coefficient, 1),
            SpellAmountStage.DamageOverTimeSnapshot => Done(false, amount, caster, target, spell, mask, coefficient, 1, SpellModOp.Dot),
            SpellAmountStage.HealOverTimeSnapshot => Done(true, amount, caster, target, spell, mask, coefficient, 1, SpellModOp.Dot),
            SpellAmountStage.DamageOverTimeTick => Taken(false, amount, target, spell, mask, coefficient, stack),
            _ => Taken(true, amount, target, spell, mask, coefficient, stack),
        };
    }

    /// <summary>
    /// vmangos Aura::HandleSchoolAbsorb (SpellAuras.cpp:5750-5810): Power Word: Shield adds 10 percent of the caster's +healing for the school
    /// (SpellBaseHealingBonusDone), Fire Ward and Frost Ward (mage family flags 3 and 8) and Shadow Ward (icon 207, category 56; family 5 in the vmangos database, but 0 in every real classic-db z2815 row 6229, 11739, 11740, 28610, so both are accepted)
    /// 10 percent of the +damage for the school (SpellBaseDamageBonusDone); the bonus is multiplied by CalculateLevelPenalty. Ice Barrier, Mana
    /// Shield, Spellstone and every other shield get nothing. The caller truncates to int like the int32 modifier of vmangos (rand_dither of an
    /// integer is the integer).
    /// </summary>
    private float AbsorbShield(Unit caster, SpellInfo spell, float amount)
    {
        int mask = 1 << (int)spell.School;
        float benefit = 0;
        if (spell.IsFitToFamily(PriestFamily, PowerWordShieldFlag))
        {
            benefit = BaseHealingBonusDone(caster, mask) * 0.1f;
        }
        else if (spell.IsFitToFamily(MageFamily, FireWardFlag) || spell.IsFitToFamily(MageFamily, FrostWardFlag)
            || (spell.SpellFamilyName is 0 or WarlockFamily && spell.SpellIconId == ShadowWardIcon && spell.Category == ShadowWardCategory))
        {
            benefit = BaseDamageBonusDone(caster, mask) * 0.1f;
        }

        return amount + (benefit * SpellCoefficients.LevelPenalty(spell));
    }

    /// <summary>
    /// vmangos SpellBaseDamageBonusDone (SpellCaster.cpp:1703-1735): ModDamageDone for the school from auras that name no item class or inventory
    /// type (a wand's damage aura is not spell power), plus the spirit based part (players).
    /// </summary>
    private float BaseDamageBonusDone(Unit caster, int mask)
    {
        float benefit = ItemIndependentSum(caster, AuraType.ModDamageDone, mask);
        if (caster is Player)
        {
            foreach (int percent in Amounts(caster, AuraType.ModSpellDamageOfStatPercent, a => (a.MiscValue & mask) != 0))
            {
                benefit += (int)(Spirit(caster) * percent / 100.0f); // vmangos truncates each aura's share
            }
        }

        return benefit;
    }

    /// <summary>vmangos SpellBaseHealingBonusDone: ModHealingDone for the school plus the spirit based part (players).</summary>
    private float BaseHealingBonusDone(Unit caster, int mask)
    {
        float benefit = Sum(caster, AuraType.ModHealingDone, a => (a.MiscValue & mask) != 0);
        if (caster is Player)
        {
            benefit += Sum(caster, AuraType.ModSpellHealingOfStatPercent, null) * Spirit(caster) / 100.0f;
        }

        return benefit;
    }

    /// <summary>
    /// vmangos SpellDamageBonusDone / SpellHealingBonusDone. <paramref name="modOp"/> is the spell mod applied to the finished
    /// done amount (DAMAGE for direct damage and healing, DOT for over-time snapshots; SpellCaster.cpp:1446, :1522, :1697), and
    /// SPELL_BONUS_DAMAGE scales the coefficient when there is a benefit to scale (SpellBonusWithCoeffs, :1760-1766).
    /// </summary>
    /// <para>
    /// The damage side also takes the creature type of <paramref name="target"/> (SpellCaster.cpp:1613-1620, 1677-1679): MOD_DAMAGE_DONE_VERSUS
    /// multiplies, MOD_DAMAGE_DONE_CREATURE adds outside the coefficient, MOD_FLAT_SPELL_DAMAGE_VERSUS adds to the benefit; and only
    /// MOD_DAMAGE_PERCENT_DONE / MOD_DAMAGE_DONE auras that name no item class or inventory type count (:1592-1600, :1709-1715), so a wand
    /// specialization does not boost spells.
    /// </para>
    private float Done(bool heal, float amount, Unit caster, Unit target, SpellInfo spell, int mask, EffectiveCoefficient coefficient, uint stack, SpellModOp modOp)
    {
        if (SpellBonusFormulas.IgnoresCasterModifiers(spell)
            || (heal && spell.DamageClass == SpellDamageClass.None && spell.IsPassive))
        {
            return Math.Max(amount, 0);
        }

        float benefit;
        float percent;
        float flat = 0f;
        if (heal)
        {
            benefit = Sum(caster, AuraType.ModHealingDone, a => (a.MiscValue & mask) != 0);
            if (caster is Player)
            {
                benefit += Sum(caster, AuraType.ModSpellHealingOfStatPercent, null) * Spirit(caster) / 100.0f;
            }

            percent = SpellBonusFormulas.MultiplicativePercent(Amounts(caster, AuraType.ModHealingDonePercent, null));
        }
        else
        {
            benefit = ItemIndependentSum(caster, AuraType.ModDamageDone, mask);
            if (caster is Player)
            {
                benefit += Sum(caster, AuraType.ModSpellDamageOfStatPercent, a => (a.MiscValue & mask) != 0) * Spirit(caster) / 100.0f;
            }

            uint typeMask = target.CreatureTypeMask();
            benefit += Sum(caster, AuraType.ModFlatSpellDamageVersus, a => ((uint)a.MiscValue & typeMask) != 0);
            flat = Sum(caster, AuraType.ModDamageDoneCreature, a => ((uint)a.MiscValue & typeMask) != 0);
            percent = spell.EquippedItemClass == -1
                ? SpellBonusFormulas.MultiplicativePercent(ItemIndependentAmounts(caster, AuraType.ModDamagePercentDone, mask))
                : 1.0f;
            percent *= SpellBonusFormulas.MultiplicativePercent(Amounts(caster, AuraType.ModDamageDoneVersus, a => ((uint)a.MiscValue & typeMask) != 0));
        }

        if (benefit != 0)
        {
            coefficient = coefficient with { Coefficient = spells.ModFloat(caster, spell, SpellModOp.SpellBonusDamage, coefficient.Coefficient * 100.0f) / 100.0f };
        }

        float done = SpellBonusFormulas.AmountDone(amount, flat, benefit, coefficient, stack, percent);
        float modified = spells.ModFloat(caster, spell, modOp, done);
        return modified > 0 ? modified : 0;
    }

    /// <summary>vmangos Unit::SpellDamageBonusTaken / SpellHealingBonusTaken.</summary>
    private float Taken(bool heal, float amount, Unit target, SpellInfo spell, int mask, EffectiveCoefficient coefficient, uint stack)
    {
        if (heal)
        {
            List<int> modifiers = [.. Amounts(target, AuraType.ModHealingPct, null)];
            float percent = SpellBonusFormulas.HealingTakenPercentMultiplier(
                modifiers.Where(m => m < 0).DefaultIfEmpty(0).Min(), modifiers.Where(m => m > 0).DefaultIfEmpty(0).Max());
            if (spell.DamageClass == SpellDamageClass.None)
            {
                return Math.Max(amount * percent, 0);
            }

            return SpellBonusFormulas.AmountTaken(amount, Sum(target, AuraType.ModHealing, a => (a.MiscValue & mask) != 0), coefficient, stack, percent);
        }

        if (SpellBonusFormulas.IgnoresDamageTakenModifiers(spell))
        {
            return amount;
        }

        float damagePercent = SpellBonusFormulas.MultiplicativePercent(Amounts(target, AuraType.ModDamagePercentTaken, a => (a.MiscValue & mask) != 0));
        return SpellBonusFormulas.AmountTaken(amount, Sum(target, AuraType.ModDamageTaken, a => (a.MiscValue & mask) != 0), coefficient, stack, damagePercent);
    }

    private static float Spirit(Unit unit) => unit.GetUInt32(UpdateFields.UnitFieldStat0 + SpiritStat);

    /// <summary>The sum of the auras of <paramref name="type"/> for the school mask whose spell names no item class and no inventory type.</summary>
    private int ItemIndependentSum(Unit unit, AuraType type, int mask)
    {
        int total = 0;
        foreach (int amount in ItemIndependentAmounts(unit, type, mask))
        {
            total += amount;
        }

        return total;
    }

    private IEnumerable<int> ItemIndependentAmounts(Unit unit, AuraType type, int mask)
    {
        foreach (SpellAuraHolder holder in spells.GetAuras(unit))
        {
            if (holder.IsRemoved || holder.Spell.EquippedItemClass != -1 || holder.Spell.EquippedItemInventoryTypeMask != 0)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is not null && aura.Type == type && (aura.MiscValue & mask) != 0)
                {
                    yield return aura.Amount;
                }
            }
        }
    }

    private int Sum(Unit unit, AuraType type, Func<SpellAura, bool>? filter) => spells.GetTotalAuraModifier(unit, type, filter);

    private IEnumerable<int> Amounts(Unit unit, AuraType type, Func<SpellAura, bool>? filter)
    {
        foreach (SpellAuraHolder holder in spells.GetAuras(unit))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is not null && aura.Type == type && (filter is null || filter(aura)))
                {
                    yield return aura.Amount;
                }
            }
        }
    }
}

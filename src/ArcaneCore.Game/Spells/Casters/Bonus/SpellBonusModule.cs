using ArcaneCore.Game.Entities;

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
/// Not modelled (documented limits): damage done versus creature types (aura 168/59/180), equipped-item restricted
/// auras, class script modifiers, talent spell mods, Ignite, totems/pets using their owner, and weapon-based periodic damage.
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
            return amount;
        }

        SpellBonusKind kind = overTime ? SpellBonusKind.OverTime : SpellBonusKind.SpellDirect;
        EffectiveCoefficient coefficient = SpellCoefficients.Resolve(spell, kind, Coefficients?.Get(spell.Id, effectIndex));
        int mask = 1 << (int)spell.School;
        return stage switch
        {
            SpellAmountStage.DirectDamage => Taken(false, Done(false, amount, caster, spell, mask, coefficient, 1), target, spell, mask, coefficient, 1),
            SpellAmountStage.DirectHeal => Taken(true, Done(true, amount, caster, spell, mask, coefficient, 1), target, spell, mask, coefficient, 1),
            SpellAmountStage.DamageOverTimeSnapshot => Done(false, amount, caster, spell, mask, coefficient, 1),
            SpellAmountStage.HealOverTimeSnapshot => Done(true, amount, caster, spell, mask, coefficient, 1),
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

    /// <summary>vmangos SpellBaseDamageBonusDone: ModDamageDone for the school plus the spirit based part (players).</summary>
    private float BaseDamageBonusDone(Unit caster, int mask)
    {
        float benefit = Sum(caster, AuraType.ModDamageDone, a => (a.MiscValue & mask) != 0);
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

    /// <summary>vmangos SpellDamageBonusDone / SpellHealingBonusDone.</summary>
    private float Done(bool heal, float amount, Unit caster, SpellInfo spell, int mask, EffectiveCoefficient coefficient, uint stack)
    {
        if (SpellBonusFormulas.IgnoresCasterModifiers(spell)
            || (heal && spell.DamageClass == SpellDamageClass.None && spell.IsPassive))
        {
            return Math.Max(amount, 0);
        }

        float benefit;
        float percent;
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
            benefit = Sum(caster, AuraType.ModDamageDone, a => (a.MiscValue & mask) != 0);
            if (caster is Player)
            {
                benefit += Sum(caster, AuraType.ModSpellDamageOfStatPercent, a => (a.MiscValue & mask) != 0) * Spirit(caster) / 100.0f;
            }

            percent = SpellBonusFormulas.MultiplicativePercent(Amounts(caster, AuraType.ModDamagePercentDone, a => (a.MiscValue & mask) != 0));
        }

        return SpellBonusFormulas.AmountDone(amount, 0, benefit, coefficient, stack, percent);
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

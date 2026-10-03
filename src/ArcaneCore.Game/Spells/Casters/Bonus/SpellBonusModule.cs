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

    /// <summary>The explicit coefficient table, when one is loaded; null runs the formula only.</summary>
    public ISpellBonusCoefficients? Coefficients { get; set; }

    public float Modify(SpellAmountStage stage, Unit caster, Unit target, SpellInfo spell, int effectIndex, float amount, uint stack)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        bool overTime = stage is not (SpellAmountStage.DirectDamage or SpellAmountStage.DirectHeal);

        // Periodic damage of weapon based spells belongs to the melee formulas (Aura::CalculateDotDamage).
        if (overTime && spell.DamageClass is SpellDamageClass.Melee or SpellDamageClass.Ranged)
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

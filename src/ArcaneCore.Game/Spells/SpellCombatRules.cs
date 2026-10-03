using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The combat modifiers of spell landing (vmangos Unit::SpellHitResult, SpellCaster::IsSpellCrit /
/// SpellCriticalDamageBonus, Unit::CalculateAbsorbResistBlock, SpellCaster::CalcArmorReducedDamage).
/// The spell system calls these in its own pipeline, before damage reaches <see cref="IDamageSink"/>.
/// </summary>
public interface ISpellCombatRules
{
    /// <summary>Whether <paramref name="spell"/> lands on <paramref name="target"/> (<see cref="SpellMissInfo.None"/> = hit).</summary>
    SpellMissInfo RollHit(SpellSystem system, Unit caster, Unit target, SpellInfo spell);

    /// <summary>Whether a direct damage or healing effect is a critical strike.</summary>
    bool RollCrit(SpellSystem system, Unit caster, Unit target, SpellInfo spell);

    /// <summary>The critical bonus multiplier (vmangos SpellCriticalDamageBonus: +50% magic, +100% melee/ranged).</summary>
    float CritMultiplier(SpellInfo spell);

    /// <summary>The part of <paramref name="damage"/> resisted by the target's school resistance (partial resists).</summary>
    uint RollPartialResist(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint damage);

    /// <summary>Physical damage after the target's armor (unchanged for other schools).</summary>
    uint ApplyArmor(Unit caster, Unit target, SpellInfo spell, uint damage);
}

/// <summary>Stock <see cref="ISpellCombatRules"/> implementations.</summary>
public static class SpellCombatRules
{
    /// <summary>Every spell hits, never crits, nothing is resisted or mitigated (the engine's behaviour before combat modifiers).</summary>
    public static ISpellCombatRules Neutral { get; } = new NeutralSpellCombatRules();

    private sealed class NeutralSpellCombatRules : ISpellCombatRules
    {
        public SpellMissInfo RollHit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => SpellMissInfo.None;

        public bool RollCrit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => false;

        public float CritMultiplier(SpellInfo spell) => 1.0f;

        public uint RollPartialResist(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint damage) => 0;

        public uint ApplyArmor(Unit caster, Unit target, SpellInfo spell, uint damage) => damage;
    }
}

/// <summary>
/// Patch 1.12 spell combat rules, re-implemented from the vmangos/cmangos-classic behaviour (no
/// code copied; formulas cited per member). Damage class NONE spells always hit and never crit
/// (vmangos Unit::SpellHitResult / IsSpellCrit "case SPELL_DAMAGE_CLASS_NONE").
/// </summary>
public class VanillaSpellCombatRules : ISpellCombatRules
{
    /// <summary>
    /// Base spell critical chance (percent) of a player before stat-driven bonuses. The stats area
    /// owns vmangos Player::UpdateSpellCritChance (intellect and class base); until then a flat value
    /// is used, recorded as a limitation.
    /// </summary>
    public float PlayerBaseSpellCrit { get; init; } = 5.0f;

    /// <summary>Melee/ranged spell crit of a creature (vmangos Creature: 5% base).</summary>
    public float CreatureBaseMeleeCrit { get; init; } = 5.0f;

    /// <summary>
    /// vmangos Unit::SpellHitResult: self casts and positive spells always land; an evading creature
    /// evades; magic spells use <see cref="MagicHitChance"/>; melee/ranged spells roll miss, dodge
    /// and parry (no glancing/crushing, vmangos MeleeSpellHitResult).
    /// </summary>
    public virtual SpellMissInfo RollHit(SpellSystem system, Unit caster, Unit target, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        if (ReferenceEquals(caster, target) || spell.IsPositive)
        {
            return SpellMissInfo.None;
        }

        if (target is ICombatCreature { IsInEvadeMode: true })
        {
            return SpellMissInfo.Evade;
        }

        switch (spell.DamageClass)
        {
            case SpellDamageClass.Magic:
            {
                float hit = MagicHitChance(caster, target) + system.GetTotalAuraModifier(caster, AuraType.ModSpellHitChance);
                hit = Math.Clamp(hit, 1.0f, 99.0f);
                int roll = system.Random.Next(0, 10_000);
                return roll < (int)((100.0f - hit) * 100.0f) ? SpellMissInfo.Resist : SpellMissInfo.None;
            }

            case SpellDamageClass.Melee:
            case SpellDamageClass.Ranged:
                return MeleeSpellHitResult(system, caster, target, spell);
            default:
                return SpellMissInfo.None;
        }
    }

    /// <summary>
    /// vmangos SpellCaster::MagicSpellHitChance base: 96% minus the level difference while the target
    /// is less than three levels above the caster, then 94% minus 7 (player target) or 11 (creature)
    /// per level beyond two; clamped to 1–99% by the caller.
    /// </summary>
    public static float MagicHitChance(Unit caster, Unit target)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        int levelDiff = target.Level - caster.Level;
        return levelDiff < 3
            ? 96 - levelDiff
            : 94 - ((levelDiff - 2) * (target is Player ? 7 : 11));
    }

    /// <summary>Melee spell table: miss, then dodge, then parry from the front (percent rolls out of 10000).</summary>
    protected virtual SpellMissInfo MeleeSpellHitResult(SpellSystem system, Unit caster, Unit target, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(spell);
        WeaponAttackType attack = spell.DamageClass == SpellDamageClass.Ranged ? WeaponAttackType.RangedAttack : WeaponAttackType.BaseAttack;
        MapCombat? combat = caster.Map?.FindUpdater<MapCombat>();
        float miss;
        float dodge = 0;
        float parry = 0;
        if (combat is not null)
        {
            MeleeRollInput input = combat.BuildRollInput(caster, target, attack) with { DualWield = false };
            miss = MeleeHitTable.MissChance(input, input.VictimDefenseSkill - input.AttackerWeaponSkill);
            if (attack != WeaponAttackType.RangedAttack)
            {
                dodge = input.DodgeChance;
                parry = input.FromBehind ? 0 : input.ParryChance;
            }
        }
        else
        {
            miss = 5.0f + ((target.Level - caster.Level) * 5 * 0.1f);
        }

        miss = Math.Clamp(miss, 0f, 60f);
        int roll = system.Random.Next(0, 10_000);
        int bound = (int)(miss * 100);
        if (roll < bound)
        {
            return SpellMissInfo.Miss;
        }

        bound += (int)(Math.Max(0f, dodge) * 100);
        if (roll < bound)
        {
            return SpellMissInfo.Dodge;
        }

        bound += (int)(Math.Max(0f, parry) * 100);
        return roll < bound ? SpellMissInfo.Parry : SpellMissInfo.None;
    }

    /// <summary>
    /// vmangos SpellCaster::IsSpellCrit: CANT_CRIT and damage class NONE never crit; magic spells use
    /// the caster's base spell crit plus SPELL_AURA_MOD_SPELL_CRIT_CHANCE and the school's
    /// SPELL_AURA_MOD_SPELL_CRIT_CHANCE_SCHOOL; melee/ranged spells use PLAYER_(RANGED_)CRIT_PERCENTAGE
    /// (creatures 5%) plus SPELL_AURA_MOD_CRIT_PERCENT.
    /// </summary>
    public virtual bool RollCrit(SpellSystem system, Unit caster, Unit target, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.HasAttribute(SpellAttributesEx2.CantCrit))
        {
            return false;
        }

        float chance = CritChance(system, caster, spell);
        return chance > 0 && system.Random.Next(0, 10_000) < (int)(chance * 100);
    }

    /// <summary>The crit chance in percent for <see cref="RollCrit"/>.</summary>
    public virtual float CritChance(SpellSystem system, Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        int schoolMask = 1 << (int)spell.School;
        return spell.DamageClass switch
        {
            SpellDamageClass.Magic => (caster is Player ? PlayerBaseSpellCrit : 0f)
                + system.GetTotalAuraModifier(caster, AuraType.ModSpellCritChance)
                + system.GetTotalAuraModifier(caster, AuraType.ModSpellCritChanceSchool, a => (a.MiscValue & schoolMask) != 0),
            SpellDamageClass.Melee => (caster is Player ? caster.GetFloat(UpdateFields.PlayerCritPercentage) : CreatureBaseMeleeCrit)
                + system.GetTotalAuraModifier(caster, AuraType.ModCritPercent),
            SpellDamageClass.Ranged => (caster is Player ? caster.GetFloat(UpdateFields.PlayerRangedCritPercentage) : CreatureBaseMeleeCrit)
                + system.GetTotalAuraModifier(caster, AuraType.ModCritPercent),
            _ => 0f,
        };
    }

    public virtual float CritMultiplier(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return spell.DamageClass is SpellDamageClass.Melee or SpellDamageClass.Ranged ? 2.0f : 1.5f;
    }

    /// <summary>
    /// Partial resists of magic damage (vmangos Unit::CalculateAbsorbResistBlock /
    /// SpellCaster::GetResistancesAtLevel, re-implemented): only damage-class MAGIC, non-holy,
    /// non-binary spells. The average resisted fraction is resistance × 0.15 / caster level, capped
    /// at 75%, with creatures above the caster gaining 8 resistance per level. The outcome is one of
    /// the 1.12 quarter steps (0/25/50/75%), drawn from the two steps around the average so the
    /// expected value equals the average — a documented simplification of the wider vanilla spread.
    /// </summary>
    public virtual uint RollPartialResist(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint damage)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        if (damage == 0 || spell.DamageClass != SpellDamageClass.Magic || spell.School is SpellSchool.Normal or SpellSchool.Holy
            || IsBinary(spell))
        {
            return 0;
        }

        float fraction = AverageResistFraction(caster, target, spell.School);
        if (fraction <= 0)
        {
            return 0;
        }

        int lowStep = (int)(fraction / 0.25f);
        float upperChance = (fraction - (lowStep * 0.25f)) / 0.25f;
        int step = system.Random.NextDouble() < upperChance ? lowStep + 1 : lowStep;
        step = Math.Clamp(step, 0, 3);
        return (uint)(damage * step / 4);
    }

    /// <summary>The expected resisted fraction (0–0.75) of <paramref name="school"/> damage from <paramref name="caster"/>.</summary>
    public static float AverageResistFraction(Unit caster, Unit target, SpellSchool school)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        int resistance = target.GetInt32(UpdateFields.UnitFieldResistances + (int)school);
        if (target is not Player && target.Level > caster.Level)
        {
            resistance += 8 * (target.Level - caster.Level);
        }

        if (resistance <= 0)
        {
            return 0f;
        }

        float level = Math.Max((int)caster.Level, 1);
        return Math.Clamp(resistance * 0.15f / level, 0f, 0.75f);
    }

    /// <summary>
    /// A binary spell (any effect besides damage) is fully resisted or not at all (vanilla
    /// "binary spells"; vmangos SpellEntry::IsBinary-style check, simplified to the effect list).
    /// </summary>
    public static bool IsBinary(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.IsEmpty || effect.Effect is SpellEffectName.SchoolDamage or SpellEffectName.HealthLeech)
            {
                continue;
            }

            if (effect.Effect == SpellEffectName.ApplyAura && effect.AuraType is AuraType.PeriodicDamage or AuraType.Dummy)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>vmangos SpellCaster::CalcArmorReducedDamage for physical (school NORMAL) spell damage.</summary>
    public virtual uint ApplyArmor(Unit caster, Unit target, SpellInfo spell, uint damage)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        if (damage == 0 || spell.School != SpellSchool.Normal)
        {
            return damage;
        }

        return MeleeHitTable.ApplyArmor(damage, target.GetInt32(UpdateFields.UnitFieldResistances), caster.Level);
    }
}

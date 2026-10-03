using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules;

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
public class VanillaSpellCombatRules : ISpellCombatRules, ISpellCritAmounts
{
    /// <summary>
    /// Base spell critical chance (percent) of a player before stat-driven bonuses. The stats area
    /// owns vmangos Player::UpdateSpellCritChance (intellect and class base); until then a flat value
    /// is used, recorded as a limitation.
    /// </summary>
    public float PlayerBaseSpellCrit { get; init; } = 5.0f;

    /// <summary>Melee/ranged spell crit of a creature (vmangos Creature: 5% base).</summary>
    public float CreatureBaseMeleeCrit { get; init; } = 5.0f;

    /// <summary>Rule tunables (docs/areas/spell-rules.md); every default is the retail behaviour.</summary>
    public SpellRuleOptions Options { get; init; } = new();

    /// <summary>Talent spell modifiers (resist-miss chance, crit chance, crit damage); the identity until the talents area installs one.</summary>
    public ISpellModifiers Modifiers { get; init; } = ISpellModifiers.None;

    /// <summary>Stat-driven base spell crit (the stats area); null uses <see cref="PlayerBaseSpellCrit"/> flat for every unit.</summary>
    public ISpellCritSource? CritSource { get; init; }

    private static readonly SpellRuleOptions s_defaultOptions = new();

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
                // vmangos MagicSpellHitResult (SpellCaster.cpp:772-793): a dead victim and IGNORE_RESISTANCES never miss.
                if (!target.IsAlive || spell.IgnoresResistances())
                {
                    return SpellMissInfo.None;
                }

                float hit = MagicHitPercent(system, caster, target, spell);
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
    /// vmangos SpellCaster::MagicSpellHitChance base (SpellCaster.cpp:795-834, see <see cref="Rules.MagicHitChance.Base"/>):
    /// 96% minus the level difference while the target is less than three levels above the caster, then
    /// 94% minus 7 (player target) or 11 (creature) per level beyond two, never below the 22% floor.
    /// World bosses count as the other side's level + 3 (SpellCaster.cpp:70-114). Unclamped above 99;
    /// <see cref="MagicHitPercent"/> adds the modifiers and clamps.
    /// </summary>
    public static float MagicHitChance(Unit caster, Unit target) => MagicHitChance(caster, target, s_defaultOptions);

    /// <inheritdoc cref="MagicHitChance(Unit, Unit)"/>
    public static float MagicHitChance(Unit caster, Unit target, SpellRuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        int levelDiff = target.EffectiveLevelAgainst(caster, options.WorldBossLevelDiff) - caster.EffectiveLevelAgainst(target, options.WorldBossLevelDiff);
        return Rules.MagicHitChance.Base(levelDiff, target is Player, options.MagicHitFloorPercent);
    }

    /// <summary>
    /// The percent chance <paramref name="spell"/> hits <paramref name="target"/> (vmangos SpellCaster::MagicSpellHitChance,
    /// SpellCaster.cpp:795-880): ALWAYS_HIT is 100; otherwise the level curve with the floor, the talent
    /// resist-miss spell mod, the victim's attacker-hit auras by school, AoE avoidance for area spells,
    /// the victim's mechanic and debuff (dispel type) resistance, the caster's spell hit, and for binary
    /// spells the victim's resist chance (no innate resistance); clamped to 1-99.
    /// </summary>
    public virtual float MagicHitPercent(SpellSystem system, Unit caster, Unit target, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.IsAlwaysHit())
        {
            return 100.0f;
        }

        float chance = Modifiers.Apply(caster, spell, SpellModOp.ResistMissChance, MagicHitChance(caster, target, Options));
        uint schoolMask = spell.SchoolMask();
        chance += system.GetTotalAuraModifier(target, AuraType.ModAttackerSpellHitChance, a => ((uint)a.MiscValue & schoolMask) != 0);
        if (spell.IsAreaEffect())
        {
            chance -= system.GetTotalAuraModifier(target, AuraType.ModAoeAvoidance);
        }

        if (spell.Mechanic != 0)
        {
            chance -= system.GetTotalAuraModifier(target, AuraType.ModMechanicResistance, a => a.MiscValue == (int)spell.Mechanic);
        }

        chance -= system.GetTotalAuraModifier(target, AuraType.ModDebuffResistance, a => a.MiscValue == (int)spell.Dispel);
        chance += system.GetTotalAuraModifier(caster, AuraType.ModSpellHitChance);
        bool binary = IsBinary(spell);
        float resistChance = binary ? ResistChance(system, caster, target, spell.School, innateResists: false) : 0.0f;
        return Rules.MagicHitChance.Finish(chance, binary, resistChance);
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
    /// vmangos Unit::IsSpellCrit (Unit.cpp:5318-5322): one roll against <see cref="CritChance(SpellSystem, Unit, Unit?, SpellInfo)"/>.
    /// </summary>
    public virtual bool RollCrit(SpellSystem system, Unit caster, Unit target, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        float chance = CritChance(system, caster, target, spell);
        return chance > 0 && system.Random.Next(0, 10_000) < (int)(chance * 100);
    }

    /// <summary>The crit chance in percent against no particular victim (see the overload with a target).</summary>
    public float CritChance(SpellSystem system, Unit caster, SpellInfo spell) => CritChance(system, caster, null, spell);

    /// <summary>
    /// vmangos Unit::GetSpellCritChance (Unit.cpp:5212-5316): creatures that are not player-owned never crit
    /// (unless <see cref="SpellRuleOptions.CreatureSpellCrit"/>); spells with no damage or heal effect or with
    /// CANT_CRIT never crit; potions and healthstones crit 10%; magic spells use the caster's school crit
    /// (<see cref="ISpellCritSource"/> base + MOD_SPELL_CRIT_CHANCE + the school's MOD_SPELL_CRIT_CHANCE_SCHOOL)
    /// plus, for hostile spells, the victim's MOD_ATTACKER_SPELL_CRIT_CHANCE by school; melee and ranged
    /// class spells use the white crit chance plus the school aura, and always crit a player who is not
    /// standing; the talent crit-chance spell mod applies last and the result is never negative.
    /// </summary>
    public virtual float CritChance(SpellSystem system, Unit caster, Unit? target, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        if ((caster is Creatures.Creature && !Options.CreatureSpellCrit) || !SpellCritRules.CanCrit(spell))
        {
            return 0f;
        }

        uint schoolMask = spell.SchoolMask();
        float chance;
        if (SpellCritRules.IsFixedChanceSpell(spell))
        {
            chance = SpellCritRules.FixedPotionCritPercent;
        }
        else
        {
            switch (spell.DamageClass)
            {
                case SpellDamageClass.Magic:
                    chance = spell.School == SpellSchool.Normal
                        ? 0f
                        : (CritSource ?? ISpellCritSource.Flat(PlayerBaseSpellCrit)).SpellCritPercent(caster, spell.School)
                            + system.GetTotalAuraModifier(caster, AuraType.ModSpellCritChance)
                            + system.GetTotalAuraModifier(caster, AuraType.ModSpellCritChanceSchool, a => ((uint)a.MiscValue & schoolMask) != 0);
                    if (target is not null && !spell.IsPositive)
                    {
                        chance += system.GetTotalAuraModifier(target, AuraType.ModAttackerSpellCritChance, a => ((uint)a.MiscValue & schoolMask) != 0);
                    }

                    break;
                case SpellDamageClass.Melee:
                case SpellDamageClass.Ranged:
                    if (target is Player { StandState: not StandState.Stand })
                    {
                        return 100f;
                    }

                    float white = spell.DamageClass == SpellDamageClass.Ranged
                        ? (caster is Player ? caster.GetFloat(UpdateFields.PlayerRangedCritPercentage) : CreatureBaseMeleeCrit)
                        : (caster is Player ? caster.GetFloat(UpdateFields.PlayerCritPercentage) : CreatureBaseMeleeCrit);
                    chance = white
                        + system.GetTotalAuraModifier(caster, AuraType.ModCritPercent)
                        + system.GetTotalAuraModifier(caster, AuraType.ModSpellCritChanceSchool, a => ((uint)a.MiscValue & schoolMask) != 0);
                    break;
                default:
                    return 0f;
            }
        }

        chance = Modifiers.Apply(caster, spell, SpellModOp.CriticalChance, chance);
        return chance > 0f ? chance : 0f;
    }

    public virtual float CritMultiplier(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return spell.DamageClass is SpellDamageClass.Melee or SpellDamageClass.Ranged ? 2.0f : 1.5f;
    }

    /// <summary>
    /// vmangos SpellCaster::SpellCriticalDamageBonus (SpellCaster.cpp:958-993): the damage plus the crit
    /// bonus (the whole hit for melee/ranged class, half otherwise, plus the talent crit-damage spell mod),
    /// times the caster's MOD_CRIT_PERCENT_VERSUS multiplier for the victim's creature type.
    /// </summary>
    public virtual uint CriticalDamage(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint damage)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        int bonus = (int)Modifiers.Apply(caster, spell, SpellModOp.CritDamageBonus, SpellCritRules.CritBonus(damage, spell.DamageClass));
        return (uint)((damage + bonus) * CritVersusMultiplier(system, caster, target));
    }

    /// <summary>
    /// vmangos SpellCaster::SpellCriticalHealingBonus (SpellCaster.cpp:995-1024): the heal plus its crit
    /// bonus (whole for melee/ranged class, half otherwise) times the creature-type multiplier, when positive.
    /// </summary>
    public virtual uint CriticalHeal(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint amount)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        float bonus = SpellCritRules.CritBonus(amount, spell.DamageClass) * CritVersusMultiplier(system, caster, target);
        return bonus > 0f ? (uint)(amount + bonus) : amount;
    }

    /// <summary>The product of the caster's MOD_CRIT_PERCENT_VERSUS auras matching the victim's creature type (vmangos GetTotalAuraMultiplierByMiscMask).</summary>
    private static float CritVersusMultiplier(SpellSystem system, Unit caster, Unit target)
    {
        uint typeMask = target.CreatureTypeMask();
        float multiplier = 1.0f;
        foreach (SpellAuraHolder holder in system.GetAuras(caster))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is not null && aura.Type == AuraType.ModCritPercentVersus && ((uint)aura.MiscValue & typeMask) != 0)
                {
                    multiplier *= (100.0f + aura.Amount) / 100.0f;
                }
            }
        }

        return multiplier;
    }

    /// <summary>
    /// Partial resists of damage (vmangos Unit::CalculateDamageAbsorbAndResist, Unit.cpp:1936-1946): every
    /// non-physical school except binary spells and IGNORE_RESISTANCES spells (the damage class is not
    /// tested, holy is resisted unless <see cref="SpellRuleOptions.IgnoreHolyResistance"/>). The resisted
    /// fraction is <see cref="ResistChance"/>; the outcome is one of the quarter steps (0/25/50/75%), drawn
    /// from the two steps around the average so the expected value equals the average. This is an approximation of
    /// vmangos' 31-row distribution table (Unit.cpp:1885-1918, external data not shipped) and ignores the
    /// DoT one-tenth rule and vulnerability bonus damage; see docs/areas/spell-rules.md.
    /// </summary>
    public virtual uint RollPartialResist(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint damage)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        if (damage == 0 || spell.School == SpellSchool.Normal || IsBinary(spell) || spell.IgnoresResistances()
            || (spell.School == SpellSchool.Holy && Options.IgnoreHolyResistance))
        {
            return 0;
        }

        float fraction = ResistChance(system, caster, target, spell.School, innateResists: true);
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

    /// <summary>
    /// vmangos SpellCaster::GetSpellResistChance (SpellCaster.cpp:882-925): the resist chance of
    /// <paramref name="school"/> damage from <paramref name="caster"/> (negative for a vulnerability), see
    /// <see cref="SpellResistance.Chance"/>. Penetration is the caster's MOD_TARGET_RESISTANCE auras; the
    /// innate creature resistance (8 per level of difference) only applies to damage, not to the hit roll.
    /// </summary>
    public float ResistChance(SpellSystem system, Unit caster, Unit target, SpellSchool school, bool innateResists)
    {
        ArgumentNullException.ThrowIfNull(system);
        uint mask = SpellSchoolMasks.Of(school);
        int penetration = system.GetTotalAuraModifier(caster, AuraType.ModTargetResistance, a => ((uint)a.MiscValue & mask) != 0);
        return ResistChanceCore(caster, target, school, penetration, innateResists, Options);
    }

    /// <summary>The resist chance (-0.75..0.75) of <paramref name="school"/> damage from <paramref name="caster"/>, without penetration auras.</summary>
    public static float AverageResistFraction(Unit caster, Unit target, SpellSchool school) =>
        ResistChanceCore(caster, target, school, 0, true, s_defaultOptions);

    private static float ResistChanceCore(Unit caster, Unit target, SpellSchool school, int penetration, bool innateResists, SpellRuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        int resistance = target.GetInt32(UpdateFields.UnitFieldResistances + (int)school);
        int levelDiff = target.EffectiveLevelAgainst(caster, options.WorldBossLevelDiff) - caster.EffectiveLevelAgainst(target, options.WorldBossLevelDiff);
        return SpellResistance.Chance(resistance, penetration, MeleeHitTable.SkillMaxForLevel(caster, target), caster.Level, levelDiff, innateResists && target is not Player);
    }

    /// <summary>Binary spells are all-or-nothing, see <see cref="SpellBinary.IsBinary"/>.</summary>
    public static bool IsBinary(SpellInfo spell) => SpellBinary.IsBinary(spell);

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

        return MeleeHitTable.ApplyArmor(damage, target.GetUInt32(UpdateFields.UnitFieldResistances), caster.Level);
    }
}

using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// Everything the vanilla melee hit table needs, already resolved from the units (so the roll
/// itself is pure and testable). Chances are percentages (5 = 5%) as the vmangos getters
/// return them; skills are skill points.
/// </summary>
public readonly record struct MeleeRollInput
{
    public WeaponAttackType AttackType { get; init; }

    /// <summary>The victim is a player (PvP skill constants apply).</summary>
    public bool VictimIsPlayer { get; init; }

    /// <summary>The attacker is a creature (not player-controlled): crit vs sitting, block school check.</summary>
    public bool AttackerIsCreature { get; init; }

    /// <summary>vmangos IsCharmerOrOwnerPlayerOrPlayerItself for attacker and victim (glancing blows).</summary>
    public bool AttackerIsPlayerControlled { get; init; }

    public bool VictimIsPlayerControlled { get; init; }

    public bool VictimEvading { get; init; }

    public bool VictimStanding { get; init; }

    /// <summary>The victim does not have the attacker in its front π arc.</summary>
    public bool FromBehind { get; init; }

    /// <summary>Creature attacker that may score crushing blows (not a pet, no NO_CRUSHING_BLOWS flag).</summary>
    public bool AttackerCanCrush { get; init; }

    /// <summary>Creature victim without CREATURE_FLAG_EXTRA_NO_PARRY / NO_BLOCK.</summary>
    public bool VictimCreatureCanParry { get; init; }

    public bool VictimCreatureCanBlock { get; init; }

    public byte VictimLevel { get; init; }

    public int AttackerMaxSkill { get; init; }

    public int VictimMaxSkill { get; init; }

    public int AttackerWeaponSkill { get; init; }

    public int VictimDefenseSkill { get; init; }

    /// <summary>Attacker dual wields (19% white-swing miss penalty).</summary>
    public bool DualWield { get; init; }

    /// <summary>+hit from auras/gear, percent (SPELL_AURA_MOD_HIT_CHANCE).</summary>
    public float HitBonus { get; init; }

    /// <summary>Attacker base crit before skill adjustments (PLAYER_CRIT_PERCENTAGE or 5 for creatures).</summary>
    public float BaseCritChance { get; init; }

    public float DodgeChance { get; init; }

    public float ParryChance { get; init; }

    public float BlockChance { get; init; }
}

/// <summary>Result of <see cref="MeleeHitTable.Roll"/>.</summary>
public readonly record struct MeleeRollResult(MeleeHitOutcome Outcome, HitInfo HitInfo);

/// <summary>
/// The vanilla melee attack table, ported line by line from vmangos
/// SpellCaster::RollMeleeOutcomeAgainst / GetMeleeMissChance and Unit::GetUnitCriticalChance
/// (src/game/Objects/SpellCaster.cpp, Unit.cpp) for white swings (no spell). It is a single
/// roll over 0–9999 against stacked ranges: miss, dodge, parry, glancing, block, crit,
/// crushing, then a normal hit. cmangos-classic Unit::RollMeleeOutcomeAgainst has the same
/// ordering.
/// </summary>
public static class MeleeHitTable
{
    /// <summary>vmangos SpellCaster::GetSkillMaxForLevel: GetLevelForTarget × 5 (world bosses: target level + 3).</summary>
    public static int SkillMaxForLevel(Unit unit, Unit? target)
    {
        int level = unit.Level;
        if (unit is ICombatCreature { IsWorldBoss: true } && target is not null)
        {
            level = Math.Clamp(target.Level + CombatConstants.WorldBossLevelDiff, 1, 255);
        }

        return level * 5;
    }

    /// <summary>vmangos SpellCaster::GetMeleeMissChance for a white swing, percent (0–60).</summary>
    public static float MissChance(in MeleeRollInput input, int fullSkillDiff)
    {
        if (!input.VictimStanding)
        {
            return 0f;
        }

        float miss = 5.0f;
        if (input.DualWield && input.AttackType != WeaponAttackType.RangedAttack)
        {
            miss += 19.0f;
        }

        float skillDiffBonus = input.VictimIsPlayer ? fullSkillDiff * 0.04f
            : fullSkillDiff < -10 ? fullSkillDiff * 0.2f
            : fullSkillDiff * 0.1f;
        miss -= skillDiffBonus;

        if (!input.VictimIsPlayer && input.VictimLevel < 10)
        {
            miss *= input.VictimLevel / 10.0f;
        }

        float hit = input.HitBonus;
        if (fullSkillDiff < -10 && hit > 0)
        {
            hit -= 1.0f; // first 1% of +hit ignored vs >10 defense over weapon skill (1.12)
        }

        miss -= hit;
        return Math.Clamp(miss, 0f, 60f);
    }

    /// <summary>vmangos Unit::GetUnitCriticalChance (white swing), percent, floored at 0.</summary>
    public static float CritChance(in MeleeRollInput input)
    {
        float crit = input.BaseCritChance;
        int skillDiff = input.AttackerWeaponSkill - input.VictimDefenseSkill;
        int minSkill = Math.Min(input.AttackerMaxSkill, input.AttackerWeaponSkill);
        int cappedSkillDiff = minSkill - input.VictimDefenseSkill;
        crit += input.VictimIsPlayer || skillDiff > 0 ? skillDiff * 0.04f : cappedSkillDiff * 0.2f;
        return crit < 0f ? 0f : crit;
    }

    /// <summary>Roll the table with a fresh roll from <paramref name="random"/> (urand(0, 9999)).</summary>
    public static MeleeRollResult Roll(in MeleeRollInput input, ICombatRandom random)
        => Roll(input, random.Next(0, CombatConstants.RollRange - 1));

    /// <summary>Roll the table for a given roll in [0, 9999].</summary>
    public static MeleeRollResult Roll(in MeleeRollInput input, int roll)
    {
        if (input.VictimEvading)
        {
            return new(MeleeHitOutcome.Evade, HitInfo.Miss | HitInfo.SwingNoHitSound);
        }

        HitInfo info = HitInfo.None;
        int attackerWeaponSkill = input.AttackerWeaponSkill;
        int skillDiff = attackerWeaponSkill - input.VictimMaxSkill;
        int fullSkillDiff = attackerWeaponSkill - input.VictimDefenseSkill;
        int cappedSkillDiff = Math.Min(input.AttackerMaxSkill, attackerWeaponSkill) - input.VictimMaxSkill;
        int blockSkillBonus = input.VictimIsPlayer ? 4 * skillDiff : 10 * skillDiff;
        int dodgeSkillBonus = input.VictimIsPlayer ? 4 * skillDiff : 10 * skillDiff;
        int parrySkillBonus = input.VictimIsPlayer ? 4 * skillDiff : cappedSkillDiff < -10 ? 60 * cappedSkillDiff : 20 * cappedSkillDiff;
        int sum = 0;

        int missChance = (int)(MissChance(input, fullSkillDiff) * 100);
        int dodgeChance = (int)(input.DodgeChance * 100);
        int blockChance = (int)(input.BlockChance * 100);
        int parryChance = (int)(input.ParryChance * 100);

        if (missChance > 0 && roll < (sum += missChance))
        {
            return new(MeleeHitOutcome.Miss, info | HitInfo.Miss);
        }

        int critChance = (int)(CritChance(input) * 100);

        // Always crit a sitting player (unless crit chance is 0 and the attacker is not a creature).
        if (input.VictimIsPlayer && !input.VictimStanding && (critChance > 0 || input.AttackerIsCreature))
        {
            return new(MeleeHitOutcome.Crit, info | HitInfo.CriticalHit);
        }

        bool canDodge, canParry, canBlock, canGlancing, canCrushing;
        if (input.AttackType == WeaponAttackType.RangedAttack)
        {
            canDodge = canParry = canBlock = canGlancing = canCrushing = false;
        }
        else
        {
            canDodge = true;
            canParry = true;
            canBlock = true; // physical white swings; a creature with a non-physical melee school cannot be blocked
            canGlancing = input.AttackerIsPlayerControlled && !input.VictimIsPlayerControlled;
            canCrushing = input.AttackerIsCreature && input.AttackerCanCrush;

            if (input.FromBehind)
            {
                if (input.VictimIsPlayer)
                {
                    canDodge = false; // no dodging from behind in PvP
                }

                canParry = false;
                canBlock = false;
            }

            if (!input.VictimIsPlayer)
            {
                canParry &= input.VictimCreatureCanParry;
                canBlock &= input.VictimCreatureCanBlock;
            }
        }

        if (canDodge)
        {
            dodgeChance -= dodgeSkillBonus;
            if (!input.VictimIsPlayer && input.VictimLevel < 10)
            {
                dodgeChance = (int)(dodgeChance * (input.VictimLevel / 10.0f));
            }

            if (dodgeChance > 0)
            {
                info |= HitInfo.RolledDodge;
                if (roll < (sum += dodgeChance))
                {
                    return new(MeleeHitOutcome.Dodge, info);
                }
            }
        }

        if (canParry && parryChance > 0)
        {
            parryChance -= parrySkillBonus;
            if (!input.VictimIsPlayer && input.VictimLevel < 10)
            {
                parryChance = (int)(parryChance * (input.VictimLevel / 10.0f));
            }

            if (parryChance > 0)
            {
                info |= HitInfo.RolledParry;
                if (roll < (sum += parryChance))
                {
                    return new(MeleeHitOutcome.Parry, info);
                }
            }
        }

        if (canGlancing)
        {
            // +skill above level × 5 does not reduce glancing frequency before BC.
            if (attackerWeaponSkill > input.AttackerMaxSkill)
            {
                attackerWeaponSkill = input.AttackerMaxSkill;
            }

            int glance = (10 + ((input.VictimDefenseSkill - attackerWeaponSkill) * 2)) * 100;
            glance = Math.Clamp(glance, 0, 4000);
            if (roll < (sum += glance))
            {
                return new(MeleeHitOutcome.Glancing, info | HitInfo.Glancing);
            }
        }

        if (canBlock && blockChance > 0)
        {
            blockChance -= blockSkillBonus;
            if (!input.VictimIsPlayer && blockChance > 500)
            {
                blockChance = 500; // mobs never block more than 5%
            }

            if (!input.VictimIsPlayer && input.VictimLevel < 10)
            {
                blockChance = (int)(blockChance * (input.VictimLevel / 10.0f));
            }

            if (blockChance > 0)
            {
                info |= HitInfo.RolledBlock;
                if (roll < (sum += blockChance))
                {
                    return new(MeleeHitOutcome.Block, info | HitInfo.Block);
                }
            }
        }

        if (critChance > 0 && roll < (sum += critChance))
        {
            return new(MeleeHitOutcome.Crit, info | HitInfo.CriticalHit);
        }

        if (canCrushing)
        {
            // Crushing blows need the mob's level × 5 to exceed the victim's (capped) defense by 15.
            int defense = Math.Min(input.VictimDefenseSkill, input.VictimMaxSkill);
            int tmp = input.AttackerMaxSkill - defense;
            if (tmp >= 15)
            {
                tmp = (tmp * 200) - 1500; // 2% per point, 15% minimum
                if (roll < (sum += tmp))
                {
                    return new(MeleeHitOutcome.Crushing, info | HitInfo.Crushing);
                }
            }
        }

        return new(MeleeHitOutcome.Normal, info);
    }

    /// <summary>
    /// Armor mitigation (vmangos SpellCaster::CalcArmorReducedDamage):
    /// reduction = 0.1·armor / (8.5·level + 40), then r / (1 + r), clamped to 0–75%; at least 1
    /// damage remains.
    /// </summary>
    public static uint ApplyArmor(uint damage, float armor, int attackerLevel)
    {
        if (armor < 0f)
        {
            armor = 0f;
        }

        float tmp = 0.1f * armor / ((8.5f * attackerLevel) + 40f);
        tmp /= 1.0f + tmp;
        tmp = Math.Clamp(tmp, 0f, 0.75f);
        float reduced = damage - (damage * tmp);
        return reduced < 1f ? 1u : (uint)reduced;
    }

    /// <summary>
    /// Glancing blow damage range (vmangos Unit::CalculateMeleeDamage, MELEE_HIT_GLANCING):
    /// diff = defense − weapon skill; low = 1.3 − 0.05·diff, high = 1.2 − 0.03·diff; casters
    /// (priest, mage, warlock) lose 0.7 / 0.3; low in [0.01, 0.91] (0.6 for casters), high in
    /// [0.2, 0.99]; the multiplier is frand(low, high) (vmangos GetGlancingBlowDamageMultiplier,
    /// "Baeza formula").
    /// </summary>
    public static (float Low, float High) GlancingRange(int victimDefenseSkill, int attackerWeaponSkill, Class attackerClass)
    {
        int diff = victimDefenseSkill - attackerWeaponSkill;
        float low = 1.3f - (0.05f * diff);
        float high = 1.2f - (0.03f * diff);
        bool caster = attackerClass is Class.Priest or Class.Mage or Class.Warlock;
        if (caster)
        {
            low -= 0.7f;
            high -= 0.3f;
        }

        low = Math.Clamp(low, 0.01f, caster ? 0.6f : 0.91f);
        high = Math.Clamp(high, 0.2f, 0.99f);
        return (low, high);
    }
}

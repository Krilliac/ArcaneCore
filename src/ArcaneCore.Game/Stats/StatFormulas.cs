using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Progression;

namespace ArcaneCore.Game.Stats;

/// <summary>How the weapon damage of one attack is sourced (vmangos StatSystem.cpp:383-444).</summary>
public enum WeaponDamageMode
{
    /// <summary>The equipped weapon's range (plus the ammo DPS for the first ranged entry).</summary>
    Weapon = 0,

    /// <summary>A shapeshift that does not use weapons: the level based range (StatSystem.cpp:383-432).</summary>
    ShapeshiftForm = 1,

    /// <summary>A broken or unusable weapon: the fist range (StatSystem.cpp:434-439).</summary>
    CannotUseWeapon = 2,
}

/// <summary>
/// Inputs of <see cref="StatFormulas.CalculateMinMaxDamage"/>, named after the locals of
/// vmangos Player::CalculateMinMaxDamage (StatSystem.cpp:354-455).
/// </summary>
/// <param name="AttackType">Which hand; only the ranged first entry is special.</param>
/// <param name="Index">The weapon damage entry (0 is the physical main entry).</param>
/// <param name="AttSpeed">GetAPMultiplier(...) * m_modRecalcDamagePct (StatSystem.cpp:372).</param>
/// <param name="TotalAttackPower">GetTotalAttackPowerValue for the attack type (<see cref="StatFormulas.TotalAttackPower"/>).</param>
/// <param name="BaseValue">UnitMods damage group BASE_VALUE.</param>
/// <param name="BasePct">UnitMods damage group BASE_PCT.</param>
/// <param name="TotalValue">UnitMods damage group TOTAL_VALUE.</param>
/// <param name="TotalPct">UnitMods damage group TOTAL_PCT (0.5 off-hand by default).</param>
/// <param name="TotalPhysical">GetTotalAuraModValue(UNIT_MOD_DAMAGE_PHYSICAL).</param>
/// <param name="WeaponMin">GetWeaponDamageRange(attType, MINDAMAGE, index).</param>
/// <param name="WeaponMax">GetWeaponDamageRange(attType, MAXDAMAGE, index).</param>
/// <param name="Mode">Weapon, weaponless shapeshift form, or unusable weapon.</param>
/// <param name="Level">The unit's level (capped at 60 for the shapeshift range).</param>
/// <param name="AmmoDps">GetAmmoDPS (ranged first entry only).</param>
public readonly record struct DamageInputs(
    WeaponAttackType AttackType,
    int Index,
    float AttSpeed,
    float TotalAttackPower,
    float BaseValue,
    float BasePct,
    float TotalValue,
    float TotalPct,
    float TotalPhysical,
    float WeaponMin,
    float WeaponMax,
    WeaponDamageMode Mode,
    uint Level,
    float AmmoDps);

/// <summary>A minimum and maximum damage value.</summary>
public readonly record struct DamageRange(float Min, float Max);

/// <summary>
/// Pure vanilla player stat and combat-stat formulas, reimplemented from the behaviour of
/// vmangos/core 4b3d241 (D:\refs\vmangos\src\game). Nothing here reads or writes an entity; the
/// player stat system feeds in the modifier groups and applies the results. No reference code is
/// copied. Float arithmetic mirrors the reference's float and int conversions so results agree
/// bit for bit where the reference is deterministic.
/// </summary>
public static class StatFormulas
{
    /// <summary>Percent per skill point above or below the level maximum (StatSystem.cpp:523, 574, 599, 635).</summary>
    public const float SkillPercentPerPoint = 0.04f;

    /// <summary>Block and parry percent before skill and auras (StatSystem.cpp:521, 597).</summary>
    public const float BaseBlockParryPercent = 5.0f;

    /// <summary>Armor per point of agility (StatSystem.cpp:130).</summary>
    public const float ArmorPerAgility = 2.0f;

    /// <summary>Stamina and intellect points that give 1 health or mana each; later points give more (StatSystem.cpp:151, 159).</summary>
    public const float FirstStatPoints = 20.0f;

    /// <summary>Health per stamina point past the first 20 (StatSystem.cpp:154).</summary>
    public const float HealthPerStamina = 10.0f;

    /// <summary>Mana per intellect point past the first 20 (StatSystem.cpp:162).</summary>
    public const float ManaPerIntellect = 15.0f;

    /// <summary>Highest level whose shapeshift weapon range grows (StatSystem.cpp:397-399).</summary>
    public const uint ShapeshiftDamageMaxLevel = 60;

    // Item sub class of daggers (vmangos ItemPrototype.h:207 ITEM_SUBCLASS_WEAPON_DAGGER).
    private const uint WeaponSubClassDagger = 15;

    /// <summary>Player::GetHealthBonusFromStamina (StatSystem.cpp:149-155).</summary>
    public static float HealthBonusFromStamina(float stamina)
    {
        float baseStam = stamina < FirstStatPoints ? stamina : FirstStatPoints;
        float moreStam = stamina - baseStam;
        return baseStam + (moreStam * HealthPerStamina);
    }

    /// <summary>Player::GetManaBonusFromIntellect (StatSystem.cpp:157-163).</summary>
    public static float ManaBonusFromIntellect(float intellect)
    {
        float baseInt = intellect < FirstStatPoints ? intellect : FirstStatPoints;
        float moreInt = intellect - baseInt;
        return baseInt + (moreInt * ManaPerIntellect);
    }

    /// <summary>The agility part of the dynamic armor (StatSystem.cpp:130).</summary>
    public static float ArmorFromAgility(float agility) => agility * ArmorPerAgility;

    /// <summary>
    /// One SPELL_AURA_MOD_RESISTANCE_OF_STAT_PERCENT aura on the physical school adds
    /// intellect * amount / 100 armor (StatSystem.cpp:133-140).
    /// </summary>
    public static float ArmorFromIntellectPercent(float intellect, float auraAmount) => intellect * (auraAmount * 0.01f);

    /// <summary>Player::UpdateMaxHealth (StatSystem.cpp:165-175): never below 1.</summary>
    public static uint MaxHealth(float baseValue, float createHealth, float basePct, float totalValue, float totalPct, float stamina)
    {
        float value = baseValue + createHealth;
        value *= basePct;
        value += totalValue + HealthBonusFromStamina(stamina);
        value *= totalPct;
        return (uint)Math.Max(1, (int)value);
    }

    /// <summary>
    /// Player::UpdateMaxPower (StatSystem.cpp:177-192). Only mana with a non-zero create pool gets the
    /// intellect bonus; the result is truncated to an unsigned integer.
    /// </summary>
    public static uint MaxPower(PowerType power, float createPower, float intellect, float baseValue, float basePct, float totalValue, float totalPct)
    {
        float bonusPower = power == PowerType.Mana && createPower > 0 ? ManaBonusFromIntellect(intellect) : 0;
        float value = baseValue + createPower;
        value *= basePct;
        value += totalValue + bonusPower;
        value *= totalPct;
        return value <= 0 ? 0 : (uint)value;
    }

    /// <summary>
    /// Unit::GetAttackPowerFromStrengthAndAgility (StatSystem.cpp:194-307) for the 5875 client
    /// (the cat form agility term is compiled in above client 1.6.1, StatSystem.cpp:279-284).
    /// <paramref name="predatoryStrikesPct"/> is the amount of the Predatory Strikes dummy aura
    /// (spell icon 1563) of a druid in cat or bear form, 0 when absent (StatSystem.cpp:253-271).
    /// </summary>
    public static float AttackPowerFromStrengthAndAgility(bool ranged, Class playerClass, uint level, float strength, float agility,
        ShapeshiftForm form = ShapeshiftForm.None, float predatoryStrikesPct = 0f)
    {
        float lvl = level;
        float val2 = 0.0f;
        if (ranged)
        {
            switch (playerClass)
            {
                case Class.Hunter:
                    val2 = (lvl * 2.0f) + (agility * 2.0f) - 10.0f;
                    break;
                case Class.Rogue:
                case Class.Warrior:
                    val2 = lvl + agility - 10.0f;
                    break;
                case Class.Druid:
                    val2 = form is ShapeshiftForm.Cat or ShapeshiftForm.Bear or ShapeshiftForm.DireBear ? 0.0f : agility - 10.0f;
                    break;
                default:
                    val2 = agility - 10.0f;
                    break;
            }

            return val2;
        }

        switch (playerClass)
        {
            case Class.Warrior:
            case Class.Paladin:
                val2 = (lvl * 3.0f) + (strength * 2.0f) - 20.0f;
                break;
            case Class.Rogue:
            case Class.Hunter:
                val2 = (lvl * 2.0f) + strength + agility - 20.0f;
                break;
            case Class.Shaman:
                val2 = (lvl * 2.0f) + (strength * 2.0f) - 20.0f;
                break;
            case Class.Druid:
            {
                float levelMult = form is ShapeshiftForm.Cat or ShapeshiftForm.Bear or ShapeshiftForm.DireBear
                    ? predatoryStrikesPct / 100.0f
                    : 0.0f;
                val2 = form switch
                {
                    ShapeshiftForm.Cat => (level * levelMult) + (strength * 2.0f) + agility - 20.0f,
                    ShapeshiftForm.Bear or ShapeshiftForm.DireBear => (level * levelMult) + (strength * 2.0f) - 20.0f,
                    _ => (strength * 2.0f) - 20.0f,
                };
                break;
            }

            case Class.Mage:
            case Class.Priest:
            case Class.Warlock:
                val2 = strength - 10.0f;
                break;
        }

        return val2;
    }

    /// <summary>
    /// Unit::GetTotalAttackPowerValue (Unit.cpp:8037-8061): the base field plus the positive and
    /// negative mod halves, never negative, times 1 + the multiplier field (compiled in above
    /// client 1.8.4, Unit.cpp:8044).
    /// </summary>
    public static float TotalAttackPower(int attackPower, short modPositive, short modNegative, float multiplier)
    {
        int ap = attackPower + modPositive + modNegative;
        return ap < 0 ? 0.0f : ap * (1.0f + multiplier);
    }

    /// <summary>
    /// SpellCaster::GetAPMultiplier (SpellCaster.cpp:1826-1853): the attack time in seconds, or for a
    /// normalized player attack the fixed weapon constant (2.4 fist, 3.3 two-hand, 2.8 ranged,
    /// 1.7 dagger, otherwise 2.4). <paramref name="weapon"/> is null when nothing is equipped.
    /// </summary>
    public static float ApMultiplier(uint attackTimeMs, bool normalized, bool isPlayer, InventoryType? weapon, uint weaponSubClass)
    {
        if (!normalized || !isPlayer)
        {
            return attackTimeMs / 1000.0f;
        }

        if (weapon is null)
        {
            return 2.4f;
        }

        return weapon.Value switch
        {
            InventoryType.TwoHandWeapon => 3.3f,
            InventoryType.Ranged or InventoryType.RangedRight or InventoryType.Thrown => 2.8f,
            _ => weaponSubClass == WeaponSubClassDagger ? 1.7f : 2.4f,
        };
    }

    /// <summary>
    /// The class base added to crit (StatSystem.cpp:552-572) and to dodge (StatSystem.cpp:611-631):
    /// druid 0.9, mage 3.2, paladin 0.7, priest 3.0, shaman 1.7, warlock 2.0, others 0.
    /// </summary>
    public static float ClassBaseCritDodge(Class playerClass) => playerClass switch
    {
        Class.Druid => 0.9f,
        Class.Mage => 3.2f,
        Class.Paladin => 0.7f,
        Class.Priest => 3.0f,
        Class.Shaman => 1.7f,
        Class.Warlock => 2.0f,
        _ => 0.0f,
    };

    /// <summary>
    /// Player::UpdateCritPercentage (StatSystem.cpp:531-577). <paramref name="flatMod"/> is the aura
    /// FLAT slot (SPELL_AURA_MOD_CRIT_PERCENT) and <paramref name="critFromAgility"/> the value
    /// UpdateAllCritPercentages stores in the PCT slot (StatSystem.cpp:581-584); GetTotalPercentageModValue
    /// is their sum (Player.h:1525).
    /// </summary>
    public static float CritPercentage(Class playerClass, float flatMod, float critFromAgility, int weaponSkill, int maxSkillForLevel)
    {
        float value = flatMod + critFromAgility;
        value += ClassBaseCritDodge(playerClass);
        value += (weaponSkill - maxSkillForLevel) * SkillPercentPerPoint;
        return value < 0.0f ? 0.0f : value;
    }

    /// <summary>Player::UpdateBlockPercentage (StatSystem.cpp:514-529).</summary>
    public static float BlockPercentage(bool canBlock, int defenseSkill, int maxSkillForLevel, float blockAura)
    {
        if (!canBlock)
        {
            return 0.0f;
        }

        float value = BaseBlockParryPercent;
        value += (defenseSkill - maxSkillForLevel) * SkillPercentPerPoint;
        value += blockAura;
        return value < 0.0f ? 0.0f : value;
    }

    /// <summary>Player::UpdateParryPercentage (StatSystem.cpp:590-605).</summary>
    public static float ParryPercentage(bool canParry, int defenseSkill, int maxSkillForLevel, float weaponParryAura)
    {
        if (!canParry)
        {
            return 0.0f;
        }

        float value = BaseBlockParryPercent;
        value += (defenseSkill - maxSkillForLevel) * SkillPercentPerPoint;
        value += weaponParryAura;
        return value < 0.0f ? 0.0f : value;
    }

    /// <summary>Player::UpdateDodgePercentage (StatSystem.cpp:607-640).</summary>
    public static float DodgePercentage(Class playerClass, float dodgeFromAgility, int defenseSkill, int maxSkillForLevel, float dodgeAura)
    {
        float value = ClassBaseCritDodge(playerClass);
        value += dodgeFromAgility;
        value += (defenseSkill - maxSkillForLevel) * SkillPercentPerPoint;
        value += dodgeAura;
        return value < 0.0f ? 0.0f : value;
    }

    /// <summary>Player::GetShieldBlockValue (Player.cpp:5137-5144).</summary>
    public static uint ShieldBlockValue(float flat, float strength, float pct)
    {
        float value = (flat + (strength / 20) - 1) * pct;
        value = value < 0 ? 0 : value;
        return (uint)value;
    }

    /// <summary>
    /// Player::CalculateMinMaxDamage (StatSystem.cpp:354-455):
    /// <c>((base + AP / 14 * speed + weapon) * basePct + total + physical) * totalPct</c>.
    /// </summary>
    public static DamageRange CalculateMinMaxDamage(in DamageInputs inputs)
    {
        float attSpeed = inputs.AttSpeed;
        float baseValue = inputs.BaseValue + (inputs.TotalAttackPower / 14.0f * attSpeed);
        float basePct = inputs.BasePct;
        float totalValue = inputs.TotalValue;
        float totalPct = inputs.TotalPct;
        float totalPhys = inputs.TotalPhysical;
        float weaponMin = inputs.WeaponMin;
        float weaponMax = inputs.WeaponMax;

        if (inputs.Mode == WeaponDamageMode.ShapeshiftForm)
        {
            if (inputs.Index > 0)
            {
                // Druids do not use weapons, so extra damage entries add nothing (StatSystem.cpp:385-393).
                weaponMin = 0.0f;
                weaponMax = 0.0f;
            }
            else
            {
                uint lvl = Math.Min(inputs.Level, ShapeshiftDamageMaxLevel);
                weaponMin = lvl * 0.85f * attSpeed;
                weaponMax = lvl * 1.25f * attSpeed;
                totalValue = 0.0f;
            }
        }
        else if (inputs.Mode == WeaponDamageMode.CannotUseWeapon)
        {
            weaponMin = UnitModConstants.BaseMinDamage;
            weaponMax = UnitModConstants.BaseMaxDamage;
            totalValue = 0.0f;
        }
        else if (inputs.AttackType == WeaponAttackType.RangedAttack && inputs.Index == 0)
        {
            weaponMin += inputs.AmmoDps * attSpeed;
            weaponMax += inputs.AmmoDps * attSpeed;
        }

        if (inputs.Index != 0)
        {
            baseValue = 0.0f;
            totalValue = 0.0f;
            totalPhys = 0.0f;
        }

        return new DamageRange(
            (((baseValue + weaponMin) * basePct) + totalValue + totalPhys) * totalPct,
            (((baseValue + weaponMax) * basePct) + totalValue + totalPhys) * totalPct);
    }

    /// <summary>
    /// The percent chance (0..100) that a combat roll raises a skill: Player::UpdateCombatSkills
    /// (Player.cpp:5341-5400). The maximum is 5 * player level, not the skill's own maximum
    /// (Player.cpp:5356). Returns 0 once the value reached that maximum. The caller has already
    /// excluded player victims and shapeshifted weapon gains (Player.cpp:5346-5352);
    /// <paramref name="mobLevel"/> is Unit::GetLevelForTarget and <paramref name="intellect"/>
    /// only matters for weapon skills.
    /// </summary>
    public static float CombatSkillGainChance(bool defence, uint playerLevel, uint currentSkillValue, uint mobLevel, float intellect)
    {
        uint currentSkillMax = 5 * playerLevel;
        if (currentSkillMax <= currentSkillValue)
        {
            return 0.0f;
        }

        uint skillDiff = currentSkillMax - currentSkillValue;
        float chance;
        if (defence)
        {
            uint greyLevel = ExperienceFormulas.GrayLevel(playerLevel);
            if (mobLevel > playerLevel + 5)
            {
                mobLevel = playerLevel + 5;
            }

            int levelDiff = unchecked((int)(mobLevel - greyLevel));
            if (levelDiff < 3)
            {
                levelDiff = 3;
            }

            chance = (float)(3 * levelDiff * skillDiff) / playerLevel;
        }
        else
        {
            if (currentSkillMax * 0.9f > currentSkillValue)
            {
                // 1% - 90% of the maximum: the chance falls from 100% to 50%.
                chance = Math.Min(100.0f, currentSkillMax * 0.9f * 50 / currentSkillValue);
            }
            else
            {
                // 90% - 100%: from 50% down to a level dependent minimum (Player.cpp:5391-5393).
                chance = (0.5f - (0.0168966f * currentSkillValue * (300.0f / currentSkillMax)) + (0.0152069f * currentSkillMax * (300.0f / currentSkillMax))) * 100.0f;
                if (skillDiff <= 3)
                {
                    chance *= 0.5f / (4 - skillDiff);
                }
            }

            chance += Math.Min(10.0f, 0.02f * intellect);
        }

        return Math.Min(100.0f, chance);
    }
}

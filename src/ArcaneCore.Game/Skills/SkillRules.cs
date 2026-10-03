using ArcaneCore.Game.Progression;
using ArcaneCore.Kernel.Skills;

namespace ArcaneCore.Game.Skills;

/// <summary>
/// The pure skill formulas, reimplemented from vmangos src/game (Objects/Player.cpp, World.h, SpellCaster.h);
/// no reference code is copied. Chances are per mille (<c>irand(1, 1000) &lt;= chance</c>) unless noted.
/// </summary>
public static class SkillRules
{
    /// <summary>vmangos SpellCaster::GetSkillMaxForLevel (SpellCaster.h:284): level times five.</summary>
    public static ushort MaxForLevel(uint level) => (ushort)(level * 5);

    /// <summary>
    /// vmangos World::GetConfigMaxSkillValue (World.h:744-748): a world-wide constant (300 at the default
    /// maximum level 60), <c>lvl = max(60, MaxPlayerLevel)</c>, <c>lvl &gt; 60 ? 300 + ((lvl - 60) * 75) / 10 : lvl * 5</c>.
    /// </summary>
    public static ushort ConfigMaxSkillValue(uint maxPlayerLevel)
    {
        uint level = Math.Max(60u, maxPlayerLevel);
        return (ushort)(level > 60 ? 300 + (((level - 60) * 75) / 10) : level * 5);
    }

    /// <summary>
    /// vmangos <c>SkillGainChance</c> (Player.cpp:5203-5212): the configured percentage for the colour the skill
    /// is in, times ten. Grey at or above <paramref name="grayLevel"/>, then green, yellow, otherwise orange.
    /// </summary>
    public static int GainChance(uint skillValue, uint grayLevel, uint greenLevel, uint yellowLevel, SkillOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (skillValue >= grayLevel)
        {
            return (int)(options.ChanceGrey * 10);
        }

        if (skillValue >= greenLevel)
        {
            return (int)(options.ChanceGreen * 10);
        }

        return skillValue >= yellowLevel ? (int)(options.ChanceYellow * 10) : (int)(options.ChanceOrange * 10);
    }

    /// <summary>
    /// The chance of UpdateCraftSkill for one SkillLineAbility row (Player.cpp:5214-5243): the recipe's
    /// "trivial high" (max_value) is grey, the mean of high and low is green and the low (min_value) is yellow.
    /// </summary>
    public static int CraftChance(uint skillValue, uint maxValue, uint minValue, SkillOptions options)
        => GainChance(skillValue, maxValue, (maxValue + minValue) / 2, minValue, options);

    /// <summary>
    /// The chance of UpdateGatherSkill (Player.cpp:5247-5281), or null for a skill the function does not
    /// handle (it returns false for those). Herbalism and Lockpicking use the plain chance; Skinning and Mining
    /// halve it every <c>steps</c> skill points (<c>&gt;&gt; (skillValue / steps)</c>) unless the step option is 0.
    /// </summary>
    public static int? GatherChance(uint skillId, uint skillValue, uint redLevel, uint multiplicator, SkillOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        uint steps;
        switch (skillId)
        {
            case SkillIds.Herbalism:
            case SkillIds.Lockpicking:
                steps = 0;
                break;
            case SkillIds.Skinning:
                steps = options.SkinningSteps;
                break;
            case SkillIds.Mining:
                steps = options.MiningSteps;
                break;
            default:
                return null;
        }

        unchecked
        {
            uint chance = (uint)GainChance(skillValue, redLevel + 100, redLevel + 50, redLevel + 25, options) * multiplicator;
            if (steps != 0)
            {
                uint shift = skillValue / steps;
                chance = shift >= 32 ? 0 : chance >> (int)shift;
            }

            return (int)chance;
        }
    }

    /// <summary>vmangos UpdateFishingSkill (Player.cpp:5283-5296): <c>skill &lt; 75 ? 100 : 2500 / (skill - 50)</c> percent, times ten.</summary>
    public static int FishingChance(uint skillValue) => (skillValue < 75 ? 100 : (int)(2500 / (skillValue - 50))) * 10;

    /// <summary>
    /// The percentage chance of a combat skill-up (vmangos Player::UpdateCombatSkills, Player.cpp:5341-5410), or
    /// null when the skill already reached the maximum for the level. <paramref name="currentSkill"/> is the pure
    /// skill value (GetBaseDefenseSkillValue / GetBaseWeaponSkillValue). Defence uses the old formula
    /// <c>3 * max(3, victim - gray) * (max - current) / level</c> with the victim level capped at five above the
    /// player; weapons use the 100%-to-50% curve below 90% of the maximum, then the level-dependent tail, plus
    /// at most 10% from intellect.
    /// </summary>
    public static float? CombatGainChance(bool defence, uint playerLevel, uint victimLevel, uint currentSkill, float intellect)
    {
        uint currentMax = 5 * playerLevel;
        if (currentMax <= currentSkill)
        {
            return null;
        }

        uint skillDiff = currentMax - currentSkill;
        float chance;
        if (defence)
        {
            uint grayLevel = ExperienceFormulas.GrayLevel(playerLevel);
            uint mobLevel = victimLevel;
            if (mobLevel > playerLevel + 5)
            {
                mobLevel = playerLevel + 5;
            }

            int levelDifference = (int)mobLevel - (int)grayLevel;
            if (levelDifference < 3)
            {
                levelDifference = 3;
            }

            chance = (float)(3 * levelDifference * skillDiff) / playerLevel;
        }
        else
        {
            if (currentMax * 0.9f > currentSkill)
            {
                chance = Math.Min(100.0f, currentMax * 0.9f * 50 / currentSkill);
            }
            else
            {
                chance = (0.5f - (0.0168966f * currentSkill * (300.0f / currentMax)) + (0.0152069f * currentMax * (300.0f / currentMax))) * 100.0f;
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

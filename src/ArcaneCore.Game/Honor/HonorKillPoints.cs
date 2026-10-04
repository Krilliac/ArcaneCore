using ArcaneCore.Game.Progression;

namespace ArcaneCore.Game.Honor;

/// <summary>
/// Honor awarded for a kill. Behavioural port of vmangos <c>MaNGOS::Honor::GetHonorGain</c>
/// (Formulas.h:179-224, 1.12 branch) and <c>HonorMgr::DishonorableKillPoints</c> (HonorMgr.cpp:1035-1050).
/// The float/double promotion of the original is reproduced so results agree to the last digit. No
/// reference code is copied.
/// </summary>
public static class HonorKillPoints
{
    /// <summary>RACIAL_LEADER_HONOR for patch 1.12 (HonorMgr.h:145-152).</summary>
    public const float RacialLeaderHonor = 488f;

    /// <summary>
    /// Honor for killing a player. <paramref name="totalKills"/> is how often the killer already killed this
    /// exact victim today (10 % less each time, nothing from the tenth, 1.12 value); the level factor is the
    /// XP one. A negative victim visual rank is clamped to 0: vmangos passes it as uint32 so the exponent
    /// overflows to infinity (undefined result), which is pinned here instead.
    /// </summary>
    public static float Honorable(uint killerLevel, uint victimLevel, sbyte victimVisualRank, uint totalKills, uint groupSize)
    {
        if (groupSize == 0)
        {
            return 0f;
        }

        const float penalty = 10.0f;
        if (totalKills >= (uint)penalty)
        {
            return 0f;
        }

        float diffLevelPenalty = ExperienceFormulas.LevelFactor(killerLevel, victimLevel);
        double sameVictimPenalty = 1 - (totalKills / penalty);
        double levelCoeff = killerLevel switch
        {
            >= 60 => 1,
            >= 50 => 0.9545,
            >= 40 => 0.5707,
            >= 30 => 0.3434,
            >= 20 => 0.2070,
            _ => 0.1212,
        };

        const float expFactor = 188.3f;
        uint rank = victimVisualRank < 0 ? 0u : (uint)victimVisualRank;
        return (float)(levelCoeff * sameVictimPenalty * (expFactor * Math.Exp(0.05331 * rank)) * diffLevelPenalty / groupSize);
    }

    /// <summary>HonorMgr::DishonorableKillPoints: the penalty (positive number) for killing a civilian.</summary>
    public static float Dishonorable(byte level)
    {
        float result = 10.0f;
        if (level is >= 30 and <= 35)
        {
            result += 1.5f * (level - 29);
        }

        if (level is >= 36 and <= 41)
        {
            result = result + 9 + (2 * (level - 35));
        }

        if (level is >= 42 and <= 50)
        {
            result = result + 21 + (3.2f * (level - 41));
        }

        if (level >= 51)
        {
            result = result + 50 + (4 * (level - 50));
        }

        return result > 100 ? 100.0f : result;
    }
}

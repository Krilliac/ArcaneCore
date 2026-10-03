namespace ArcaneCore.Game.Reputation;

/// <summary>vmangos SharedDefines.h ReputationRank (the byte quest/vendor/item code compares).</summary>
public enum ReputationRank : byte
{
    Hated = 0,
    Hostile = 1,
    Unfriendly = 2,
    Neutral = 3,
    Friendly = 4,
    Honored = 5,
    Revered = 6,
    Exalted = 7,
}

/// <summary>vmangos ReputationMgr.h FactionFlags (the u8 flag byte of SMSG_INITIALIZE_FACTIONS; gtker FactionFlag).</summary>
[Flags]
public enum FactionStateFlags : uint
{
    None = 0x00,

    /// <summary>Shown in the client's reputation pane.</summary>
    Visible = 0x01,

    /// <summary>Player-controlled at-war state; makes reputation factions hostile to the player.</summary>
    AtWar = 0x02,

    /// <summary>Hidden from the pane; reputation still accrues.</summary>
    Hidden = 0x04,

    /// <summary>Never visible (opposite team factions).</summary>
    InvisibleForced = 0x08,

    /// <summary>Cannot be set at war (own team core factions) unless already Hated.</summary>
    PeaceForced = 0x10,

    /// <summary>Player-controlled "inactive" grouping in the pane.</summary>
    Inactive = 0x20,

    /// <summary>Present in vmangos for vanilla, unused by 1.12 content.</summary>
    Rival = 0x40,
}

/// <summary>Where a reputation change comes from (vmangos ReputationSource).</summary>
public enum ReputationSource
{
    Kill,
    Quest,
    Spell,
}

/// <summary>
/// Rank thresholds and gain arithmetic. Behavior re-implemented from vmangos/core
/// 4b3d241cffe245a1f68da11380bce96c23db48c0 ReputationMgr.cpp (PointsInRank, ReputationToRank,
/// GetRepPointsToRank), Formulas.h (GetGrayLevel) and Player.cpp CalculateReputationGain;
/// no GPL source is copied.
/// </summary>
public static class ReputationMath
{
    /// <summary>ReputationMgr::Reputation_Cap: the last point of Exalted.</summary>
    public const int Cap = 42999;

    /// <summary>ReputationMgr::Reputation_Bottom: the first point of Hated.</summary>
    public const int Bottom = -42000;

    private static readonly int[] s_pointsInRank = [36000, 3000, 3000, 3000, 6000, 12000, 21000, 1000];

    /// <summary>Width of each rank in points, Hated through Exalted.</summary>
    public static IReadOnlyList<int> PointsInRank => s_pointsInRank;

    /// <summary>ReputationMgr::ReputationToRank: Hated &lt; -6000 ≤ Hostile &lt; -3000 ≤ Unfriendly &lt; 0 ≤ Neutral &lt; 3000 ≤ Friendly &lt; 9000 ≤ Honored &lt; 21000 ≤ Revered &lt; 42000 ≤ Exalted.</summary>
    public static ReputationRank ToRank(int reputation)
    {
        int limit = Cap + 1;
        for (int rank = s_pointsInRank.Length - 1; rank > 0; rank--)
        {
            limit -= s_pointsInRank[rank];
            if (reputation >= limit)
            {
                return (ReputationRank)rank;
            }
        }

        return ReputationRank.Hated;
    }

    /// <summary>ReputationMgr::GetRepPointsToRank: the first reputation value of <paramref name="rank"/>.</summary>
    public static int FirstPointOf(ReputationRank rank)
    {
        if ((int)rank >= s_pointsInRank.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(rank));
        }

        int sum = 0;
        for (int i = 0; i < (int)rank; i++)
        {
            sum += s_pointsInRank[i];
        }

        return sum + Bottom;
    }

    public static int Clamp(long reputation) => (int)Math.Clamp(reputation, Bottom, Cap);

    /// <summary>vanilla MaNGOS::XP::GetGrayLevel (vmangos Formulas.h).</summary>
    public static uint GrayLevel(uint playerLevel) => playerLevel switch
    {
        <= 5 => 0,
        <= 39 => playerLevel - 5 - (playerLevel / 10),
        _ => playerLevel - 1 - (playerLevel / 5),
    };

    /// <summary>
    /// Player::CalculateReputationGain before dithering: losses are unscaled; gains add
    /// <paramref name="gainModifierPercent"/> (SPELL_AURA_MOD_REPUTATION_GAIN and the faction
    /// aura for kills) and are scaled for gray kills or low quests (patch 1.9 table).
    /// </summary>
    public static float GainBeforeDither(ReputationSource source, int rep, uint playerLevel, uint creatureOrQuestLevel,
        ReputationRates rates, float gainModifierPercent = 0)
    {
        ArgumentNullException.ThrowIfNull(rates);
        float percent = 100f;
        if (rep > 0)
        {
            percent += gainModifierPercent;
            float rate = source switch
            {
                ReputationSource.Kill => creatureOrQuestLevel <= GrayLevel(playerLevel) ? rates.LowLevelKill : 1f,
                ReputationSource.Quest => QuestLevelRate(playerLevel, creatureOrQuestLevel),
                _ => 1f,
            };
            percent *= rate;
        }

        return percent <= 0 ? 0 : rates.Gain * rep * percent / 100f;
    }

    /// <summary>Patch 1.9: 20% less per level once the quest is five or more levels below, down to 20%.</summary>
    public static float QuestLevelRate(uint playerLevel, uint questLevel)
    {
        uint diff = playerLevel >= questLevel + 5 ? playerLevel - questLevel - 5 : 0;
        return diff switch
        {
            0 => 1f,
            1 => 0.8f,
            2 => 0.6f,
            3 => 0.4f,
            _ => 0.2f,
        };
    }

    /// <summary>vmangos rand_dither: floor plus one with probability of the fractional part.</summary>
    public static int Dither(float value, double roll)
    {
        if (!float.IsFinite(value))
        {
            return 0;
        }

        double floor = Math.Floor(value);
        double result = floor + (roll < value - floor ? 1 : 0);
        return (int)Math.Clamp(result, int.MinValue, int.MaxValue);
    }
}

/// <summary>World rates (vmangos Rate.Reputation.Gain, Rate.Reputation.LowLevel.Kill).</summary>
public sealed class ReputationRates
{
    public float Gain { get; set; } = 1f;

    public float LowLevelKill { get; set; } = 0.2f;
}

namespace ArcaneCore.Game.Progression;

/// <summary>
/// Vanilla experience formulas, reimplemented from the behaviour of vmangos/core
/// 4b3d241cffe245a1f68da11380bce96c23db48c0 <c>Formulas.h</c> (MaNGOS::XP) and
/// <c>Group.cpp</c> (RewardGroupAtKill). No reference code is copied.
/// </summary>
public static class ExperienceFormulas
{
    /// <summary>MaNGOS::XP nBaseExp for the 1-60 content.</summary>
    public const float BaseExperience = 45f;

    /// <summary>XP::GetGrayLevel: creatures at or below this level give no experience.</summary>
    public static uint GrayLevel(uint playerLevel) => playerLevel switch
    {
        <= 5 => 0,
        <= 39 => playerLevel - 5 - (playerLevel / 10),
        _ => playerLevel - 1 - (playerLevel / 5),
    };

    /// <summary>XP::GetZeroDifference: the level gap below which a lower creature's experience is reduced linearly.</summary>
    public static uint ZeroDifference(uint playerLevel) => playerLevel switch
    {
        < 8 => 5,
        < 10 => 6,
        < 12 => 7,
        < 16 => 8,
        < 20 => 9,
        < 30 => 11,
        < 40 => 12,
        < 45 => 13,
        < 50 => 14,
        < 55 => 15,
        < 60 => 16,
        _ => 17,
    };

    /// <summary>
    /// XP::BaseGainLevelFactor: +5% per level above the player (capped at +4 levels), a linear
    /// reduction over the zero difference below it, zero at or below the gray level.
    /// </summary>
    public static float LevelFactor(uint playerLevel, uint creatureLevel)
    {
        if (creatureLevel >= playerLevel)
        {
            uint diff = Math.Min(creatureLevel - playerLevel, 4u);
            return 1.0f + (0.05f * diff);
        }

        if (creatureLevel <= GrayLevel(playerLevel))
        {
            return 0f;
        }

        uint zd = ZeroDifference(playerLevel);
        return (zd + creatureLevel - playerLevel) / (float)zd;
    }

    /// <summary>XP::BaseGain: (level × 5 + 45) × level factor.</summary>
    public static float BaseGain(uint playerLevel, uint creatureLevel)
        => ((playerLevel * 5) + BaseExperience) * LevelFactor(playerLevel, creatureLevel);

    /// <summary>
    /// XP::Gain for a creature kill: base gain; elites ×2 (×2.5 in a non-raid dungeon) and the
    /// elite rate; then the kill rate; rounded to the nearest integer (std::nearbyint, ties to even).
    /// </summary>
    public static uint KillGain(uint playerLevel, uint creatureLevel, bool elite, bool nonRaidDungeon,
        float killRate = 1.0f, float eliteRate = 1.0f)
    {
        float gain = BaseGain(playerLevel, creatureLevel);
        if (gain <= 0)
        {
            return 0;
        }

        if (elite)
        {
            gain *= nonRaidDungeon ? 2.5f : 2.0f;
            gain *= eliteRate;
        }

        gain *= killRate;
        if (!float.IsFinite(gain) || gain <= 0)
        {
            return 0;
        }

        double rounded = Math.Round(gain, MidpointRounding.ToEven);
        return rounded >= uint.MaxValue ? uint.MaxValue : (uint)rounded;
    }

    /// <summary>XP::xp_in_group_rate (vmangos marks the values as an assumption; reproduced as-is).</summary>
    public static float GroupRate(int count) => count switch
    {
        <= 2 => 1.0f,
        3 => 1.166f,
        4 => 1.3f,
        5 => 1.4f,
        _ => Math.Max(1.0f - (count * 0.05f), 0.01f),
    };

    /// <summary>Player::GetHealthBonusFromStamina: the first 20 stamina give 1 health, the rest 10.</summary>
    public static uint HealthBonusFromStamina(uint stamina)
    {
        uint baseStamina = Math.Min(stamina, 20u);
        return baseStamina + ((stamina - baseStamina) * 10);
    }

    /// <summary>Player::GetManaBonusFromIntellect: the first 20 intellect give 1 mana, the rest 15.</summary>
    public static uint ManaBonusFromIntellect(uint intellect)
    {
        uint baseIntellect = Math.Min(intellect, 20u);
        return baseIntellect + ((intellect - baseIntellect) * 15);
    }
}

/// <summary>
/// One group member's share of a kill (vmangos Group::RewardGroupAtKill + RewardGroupAtKill_helper).
/// <paramref name="Experience"/> is zero for members that receive quest credit only.
/// </summary>
public readonly record struct KillShare(int MemberIndex, uint Experience, bool QuestCredit);

/// <summary>A candidate for kill rewards (already filtered to the reward distance and the victim's map).</summary>
public readonly record struct KillCandidate(uint Level, bool Alive, bool Ghost);

/// <summary>Distributes kill experience over the nearby members of a group, or a solo killer.</summary>
public static class KillExperience
{
    /// <summary>
    /// The shares of every candidate. XP is computed for the highest-level member to whom the
    /// victim is not gray, divided by level over the members' level sum and multiplied by the
    /// group rate; if a higher member sees the victim gray, everyone gets half plus one. Members
    /// above that not-gray member get nothing; dead members get no XP; ghosts get no credit.
    /// </summary>
    public static IReadOnlyList<KillShare> Distribute(IReadOnlyList<KillCandidate> candidates, uint creatureLevel,
        bool elite, bool nonRaidDungeon, float killRate = 1.0f, float eliteRate = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        int count = 0;
        uint sumLevel = 0;
        int maxIndex = -1;
        int notGrayIndex = -1;
        for (int i = 0; i < candidates.Count; i++)
        {
            KillCandidate c = candidates[i];
            if (!c.Alive)
            {
                continue;
            }

            count++;
            sumLevel += c.Level;
            if (maxIndex < 0 || candidates[maxIndex].Level < c.Level)
            {
                maxIndex = i;
            }

            if (creatureLevel > ExperienceFormulas.GrayLevel(c.Level)
                && (notGrayIndex < 0 || candidates[notGrayIndex].Level < c.Level))
            {
                notGrayIndex = i;
            }
        }

        uint xp = notGrayIndex < 0 ? 0 : ExperienceFormulas.KillGain(candidates[notGrayIndex].Level, creatureLevel,
            elite, nonRaidDungeon, killRate, eliteRate);
        float groupRate = ExperienceFormulas.GroupRate(count);
        var shares = new List<KillShare>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            KillCandidate c = candidates[i];
            uint memberXp = 0;
            if (c.Alive && notGrayIndex >= 0 && sumLevel > 0 && c.Level <= candidates[notGrayIndex].Level)
            {
                float rate = groupRate * c.Level / sumLevel;
                float value = maxIndex == notGrayIndex || candidates[maxIndex].Level == candidates[notGrayIndex].Level
                    ? xp * rate
                    : (xp * rate / 2) + 1;
                memberXp = value >= uint.MaxValue ? uint.MaxValue : (uint)value;
            }

            shares.Add(new KillShare(i, memberXp, c.Alive || !c.Ghost));
        }

        return shares;
    }
}

namespace ArcaneCore.Game.Honor;

/// <summary>
/// The pair of rank numbers the client works with: the internal rank (0..18, the value stored in
/// PLAYER_BYTES_3 and compared by channels and conditions) and the visual rank (-4..14, the number the
/// honor tab shows and rank-gated items use), plus the band the rank bar is measured in.
/// </summary>
public readonly record struct HonorRankInfo(byte Rank, sbyte VisualRank, float MaxRp, float MinRp, bool Positive);

/// <summary>
/// Rank thresholds and the rank bar byte. Behavioural port of vmangos <c>HonorMgr::CalculateRank</c>,
/// <c>CalculateRankInfo</c> and the bar computation in <c>HonorMgr::Update</c> (HonorMgr.cpp:909-913,
/// 980-1033). No reference code is copied.
/// </summary>
public static class HonorRanks
{
    /// <summary>NEGATIVE_HONOR_RANK_COUNT (HonorMgr.h:141).</summary>
    public const int NegativeRankCount = 4;

    /// <summary>POSITIVE_HONOR_RANK_COUNT (HonorMgr.h:142).</summary>
    public const int PositiveRankCount = 15;

    /// <summary>HONOR_RANK_COUNT (HonorMgr.h:143).</summary>
    public const int RankCount = 19;

    /// <summary>The unranked starting state (HonorMgr::InitRankInfo, HonorMgr.cpp:942-949).</summary>
    public static HonorRankInfo None => new(0, 0, 2000f, 0f, true);

    /// <summary>HonorMgr::CalculateRank: the rank for a rank point total.</summary>
    public static HonorRankInfo Calculate(float rankPoints)
    {
        if (rankPoints == 0)
        {
            return None;
        }

        bool positive = rankPoints > 0;
        if (!positive)
        {
            rankPoints *= -1;
        }

        int rankCount = positive ? PositiveRankCount - 2 : NegativeRankCount;
        int firstRank = positive ? NegativeRankCount + 1 : 1;
        int rank;
        if (rankPoints < 2000f)
        {
            rank = positive ? firstRank : NegativeRankCount;
        }
        else if (rankPoints > (rankCount - 1) * 5000f)
        {
            rank = positive ? RankCount - 1 : firstRank;
        }
        else
        {
            rank = (int)(rankPoints / 5000f) + firstRank;
            rank = positive ? rank + 1 : NegativeRankCount - rank;
        }

        return Describe((byte)rank, positive);
    }

    /// <summary>HonorMgr::CalculateRankInfo: the band and visual rank for an internal rank.</summary>
    public static HonorRankInfo Describe(byte rank, bool positive)
    {
        if (rank == 0)
        {
            return None;
        }

        int step = positive ? rank - NegativeRankCount - 1 : rank - NegativeRankCount;
        float max = step * 5000f;
        if (max < 0)
        {
            max *= -1;
        }

        float min = max > 5000f ? max - 5000f : 2000f;
        if (rank == 5)
        {
            max = 2000f;
            min = 0f;
        }

        int visual = rank > NegativeRankCount ? rank - NegativeRankCount : rank * -1;
        return new HonorRankInfo(rank, (sbyte)visual, max, min, positive);
    }

    /// <summary>
    /// PLAYER_FIELD_BYTES2 byte 0, the honor tab progress bar. vmangos truncates the absolute rank points
    /// to an integer, takes the fraction of the band in float arithmetic and casts it to uint8
    /// (HonorMgr.cpp:909-913). The cast of a negative float is undefined in C++; here it is the
    /// two's-complement low byte of the truncated integer (what x86 produces), pinned for the GM-only
    /// negative case.
    /// </summary>
    public static byte RankBar(float rankPoints, HonorRankInfo rank)
    {
        uint abs = (uint)(rankPoints >= 0f ? rankPoints : -1 * rankPoints);
        float fraction = (abs - rank.MinRp) / (rank.MaxRp - rank.MinRp);
        float scaled = fraction * (rank.Positive ? 255 : -255);
        if (!float.IsFinite(scaled))
        {
            return 0;
        }

        return unchecked((byte)(int)scaled);
    }
}

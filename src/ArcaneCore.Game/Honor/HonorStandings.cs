namespace ArcaneCore.Game.Honor;

/// <summary>
/// The rank point curve of one faction for one week: <see cref="Brk"/> are the standing positions of the
/// break points, <see cref="Fx"/> the contribution points at each, <see cref="Fy"/> the rank points they pay.
/// </summary>
public sealed record HonorScoreCurve(float[] Brk, float[] Fx, float[] Fy);

/// <summary>
/// Weekly standing maths. Behavioural port of vmangos <c>HonorMaintenancer::GenerateScores</c>,
/// <c>CalculateRpEarning</c>, <c>CalculateRpDecay</c>, <c>MaximumRpAtLevel</c> and
/// <c>GetStandingCPByPosition</c> (HonorMgr.cpp:35-46, 468-615, 1.12 branch; the pre-1.12 break point table
/// is not used). No reference code is copied.
/// </summary>
public static class HonorStandings
{
    // 1.12+ break point fractions of the pool (HonorMgr.cpp:494-508), index 0 first.
    private static readonly float[] BreakFractions =
        [1.000f, 0.845f, 0.697f, 0.566f, 0.436f, 0.327f, 0.228f, 0.159f, 0.100f, 0.060f, 0.035f, 0.020f, 0.008f, 0.003f];

    /// <summary>GetStandingCPByPosition: the contribution at a 1-based position, 0 outside the list.</summary>
    public static float CpByPosition(IReadOnlyList<float> sortedDescending, uint position)
        => position >= 1 && position <= sortedDescending.Count ? sortedDescending[(int)position - 1] : 0f;

    /// <summary>
    /// GenerateScores for one faction. <paramref name="sortedDescending"/> are the active players'
    /// contribution points, best first; <paramref name="poolSizeOverride"/> is World:Honor:PoolSizePerFaction
    /// (0 uses the list size).
    /// </summary>
    public static HonorScoreCurve Generate(IReadOnlyList<float> sortedDescending, uint poolSizeOverride)
    {
        ArgumentNullException.ThrowIfNull(sortedDescending);
        if (sortedDescending.Count == 0)
        {
            throw new ArgumentException("A standing list needs at least one player.", nameof(sortedDescending));
        }

        uint poolSize = poolSizeOverride == 0 ? (uint)sortedDescending.Count : poolSizeOverride;
        var brk = new float[14];
        for (int i = 0; i < 14; i++)
        {
            brk[i] = MathF.Floor((BreakFractions[i] * poolSize) + 0.5f);
        }

        var fy = new float[15];
        fy[1] = 400;
        for (int i = 2; i <= 13; i++)
        {
            fy[i] = (i - 1) * 1000;
        }

        fy[14] = 13000;

        var fx = new float[15];
        float first = sortedDescending[0];
        bool top = false;
        for (int i = 1; i <= 13; i++)
        {
            float honor = 0f;
            float temp = CpByPosition(sortedDescending, (uint)brk[i]);
            if (temp != 0f)
            {
                honor += temp;
                temp = CpByPosition(sortedDescending, (uint)brk[i] + 1);
                if (temp != 0f)
                {
                    honor += temp;
                }
            }

            fx[i] = honor != 0f ? honor / 2 : 0f;
            if (!top && honor == 0f)
            {
                fx[i] = fx[i - 1] != 0f ? first : 0f;
                top = true;
            }
        }

        fx[14] = !top ? first : 0f;
        return new HonorScoreCurve(brk, fx, fy);
    }

    /// <summary>CalculateRpEarning: rank points paid for a week's contribution points.</summary>
    public static float Earning(float cp, HonorScoreCurve curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        int i = 0;
        while (i < 14 && curve.Brk[i] > 0 && curve.Fx[i] <= cp)
        {
            i++;
        }

        if (i > 0 && curve.Fx[i] > cp && cp >= curve.Fx[i - 1])
        {
            return ((curve.Fy[i] - curve.Fy[i - 1]) * (cp - curve.Fx[i - 1]) / (curve.Fx[i] - curve.Fx[i - 1])) + curve.Fy[i - 1];
        }

        return curve.Fy[i];
    }

    /// <summary>
    /// CalculateRpDecay: the new rank points from last week's points and this week's earning. A fraction
    /// (World:Honor:RpDecay, retail 0.2) of the old points is lost; a net loss is halved and limited to 2500.
    /// </summary>
    public static float Decay(float earning, float rankPoints, float decayMultiplier)
    {
        float decay = MathF.Floor((decayMultiplier * rankPoints) + 0.5f);
        float delta = earning - decay;
        if (delta < 0)
        {
            delta /= 2;
        }

        if (delta < -2500)
        {
            delta = -2500;
        }

        return rankPoints + delta;
    }

    /// <summary>MaximumRpAtLevel: the most rank points a character of the level can hold after a week.</summary>
    public static float MaximumRankPointsAtLevel(byte level) => level switch
    {
        <= 29 => 6500,
        <= 35 => 7150 + (975 * (level - 30)),
        <= 39 => 12025 + (1300 * (level - 35)),
        <= 43 => 17225 + (1625 * (level - 39)),
        <= 52 => 23725 + (2275 * (level - 43)),
        <= 60 => 44200 + (2600 * (level - 52)),
        _ => 65000,
    };
}

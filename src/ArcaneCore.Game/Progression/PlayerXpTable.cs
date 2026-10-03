namespace ArcaneCore.Game.Progression;

/// <summary>
/// Experience required to leave each level (vmangos <c>player_xp_for_level</c>, read through
/// ObjectMgr::GetXPForLevel). The 1.12 values equal MaNGOS::XP::xp_to_level rounded to the
/// nearest hundred; <c>PlayerXpTableTests</c> checks every row against that formula.
/// </summary>
public static class PlayerXpTable
{
    /// <summary>The highest level the table describes (leaving level 59).</summary>
    public const int MaxTableLevel = 59;

    private static readonly uint[] Required =
    [
        0, // level 0 is unused
        400, 900, 1400, 2100, 2800, 3600, 4500, 5400, 6500, 7600,
        8800, 10100, 11400, 12900, 14400, 16000, 17700, 19400, 21300, 23200,
        25200, 27300, 29400, 31700, 34000, 36400, 38900, 41400, 44300, 47400,
        50800, 54500, 58600, 62800, 67100, 71600, 76100, 80800, 85700, 90700,
        95800, 101000, 106300, 111800, 117500, 123200, 129100, 135100, 141200, 147500,
        153900, 160400, 167100, 173900, 180800, 187900, 195000, 202300, 209800,
    ];

    /// <summary>ObjectMgr::GetXPForLevel: XP to reach the next level, or 0 at/after the configured maximum.</summary>
    public static uint XpForLevel(uint level, uint maxPlayerLevel = 60)
        => level >= 1 && level < maxPlayerLevel && level <= MaxTableLevel ? Required[level] : 0;
}

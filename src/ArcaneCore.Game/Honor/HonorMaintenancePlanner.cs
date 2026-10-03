using System.Globalization;
using System.Text;
using ArcaneCore.Kernel.Honor;

namespace ArcaneCore.Game.Honor;

/// <summary>
/// The knobs of the weekly calculation. <see cref="Retail"/> is vmangos' 1.12 behaviour (World.cpp:641-654):
/// 20 % weekly decay, 15 honorable kills to take part, standings pooled by the real list size.
/// </summary>
/// <param name="RpDecay">World:Honor:RpDecay, the fraction of rank points lost per week (0..1).</param>
/// <param name="MinHonorKills">World:Honor:MinHonorKills; 0 selects the retail value (15).</param>
/// <param name="PoolSizePerFaction">World:Honor:PoolSizePerFaction; 0 uses the number of ranked players.</param>
public sealed record HonorMaintenanceOptions(float RpDecay, uint MinHonorKills, uint PoolSizePerFaction)
{
    /// <summary>MIN_HONOR_KILLS_POST_1_10 (HonorMgr.h:140).</summary>
    public const uint RetailMinHonorKills = 15;

    public static HonorMaintenanceOptions Retail { get; } = new(0.2f, 0, 0);

    /// <summary>The kills needed to be ranked: the configured value, or the retail one when it is 0.</summary>
    public uint EffectiveMinHonorKills => MinHonorKills == 0 ? RetailMinHonorKills : MinHonorKills;
}

/// <summary>The outcome of one weekly calculation: per-character results and the calculation report.</summary>
/// <param name="Updates">One entry for every character in the week's input.</param>
/// <param name="AllianceCount">Ranked Alliance players.</param>
/// <param name="HordeCount">Ranked Horde players.</param>
/// <param name="InactiveCount">Players below the kill minimum (they only decay).</param>
/// <param name="Report">The vmangos HCR text (HonorMgr.cpp:328-466).</param>
public sealed record HonorMaintenancePlan(
    IReadOnlyList<HonorRankUpdate> Updates, int AllianceCount, int HordeCount, int InactiveCount, string Report);

/// <summary>
/// One maintenance period: the week to settle (<see cref="WeekBegin"/>..<see cref="WeekEnd"/> inclusive) and the
/// maintenance days afterwards. Contribution rows dated before <see cref="DeleteCpBefore"/> are dropped, so the
/// week-end day survives for the honor tab's "Yesterday" group (HonorMgr.cpp:266-267).
/// </summary>
public readonly record struct HonorPeriod(uint WeekBegin, uint WeekEnd, uint NewLast, uint NewNext)
{
    public uint DeleteCpBefore => WeekEnd;
}

/// <summary>
/// Pure weekly maintenance planning. Behavioural port of vmangos <c>HonorMaintenancer</c>
/// (HonorMgr.cpp:104-183 standing lists and distribution, 238-326 flush and period loop, 617-669 days;
/// World.h:735-741 last maintenance day) with the 1.12 branches. No I/O: the world feature loads the weekly
/// scores from the store, calls <see cref="Plan"/> and applies the result. No reference code is copied.
/// </summary>
public static class HonorMaintenancePlanner
{
    private const uint SecondsPerDay = 86_400;

    /// <summary>World::m_gameDay: the Unix day of the offset time.</summary>
    public static uint GameDay(long unixSeconds, int timeZoneOffsetSeconds)
        => (uint)Math.Max(0, (unixSeconds + timeZoneOffsetSeconds) / SecondsPerDay);

    /// <summary>The weekday (Sunday 0) of a game day. Day 0 is Thursday 1970-01-01.</summary>
    public static int Weekday(uint gameDay) => (int)((gameDay + 4) % 7);

    /// <summary>World::GetLastMaintenanceDay: the most recent game day (today included) on the maintenance weekday.</summary>
    public static uint LastMaintenanceDay(uint gameDay, uint maintenanceDay)
    {
        uint back = (uint)((Weekday(gameDay) - (int)maintenanceDay + 7) % 7);
        return back > gameDay ? 0 : gameDay - back;
    }

    /// <summary>
    /// The periods to settle (HonorMaintenancer::DoMaintenance, HonorMgr.cpp:277-322): while the game day has
    /// reached the next maintenance day, settle the week that starts on the last day and advance both days by a
    /// week. Oldest first.
    /// </summary>
    public static IReadOnlyList<HonorPeriod> Periods(uint lastDay, uint nextDay, uint gameDay)
    {
        List<HonorPeriod> periods = [];
        while (gameDay >= nextDay)
        {
            uint newLast = nextDay;
            periods.Add(new HonorPeriod(lastDay, lastDay + 6, newLast, newLast + 7));
            lastDay = newLast;
            nextDay = newLast + 7;
        }

        return periods;
    }

    /// <summary>
    /// Calculate one week: split into ranked players per faction (at least the minimum kills and an account) and
    /// inactive players, rank each faction by contribution points, pay rank points from the faction's curve,
    /// decay, cap by level, and work out the highest rank.
    /// </summary>
    /// <param name="scores">The week's inputs from <see cref="IHonorStore.ListWeeklyScoresAsync"/>.</param>
    /// <param name="isAlliance">Whether a race belongs to the Alliance (anything else counts as Horde).</param>
    /// <param name="options">The calculation options.</param>
    public static HonorMaintenancePlan Plan(IReadOnlyList<HonorWeeklyScore> scores, Func<byte, bool> isAlliance, HonorMaintenanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(isAlliance);
        ArgumentNullException.ThrowIfNull(options);

        uint minKills = options.EffectiveMinHonorKills;
        List<HonorWeeklyScore> alliance = [];
        List<HonorWeeklyScore> horde = [];
        List<HonorWeeklyScore> inactive = [];
        foreach (HonorWeeklyScore score in scores)
        {
            if (score.Hk < minKills || score.AccountId == 0)
            {
                inactive.Add(score);
            }
            else if (isAlliance(score.Race))
            {
                alliance.Add(score);
            }
            else
            {
                horde.Add(score);
            }
        }

        // std::sort on contribution points descending; ties are broken by character id here so the order is stable.
        static List<HonorWeeklyScore> Ordered(List<HonorWeeklyScore> list)
            => [.. list.OrderByDescending(s => s.Cp).ThenBy(s => s.CharacterId)];
        alliance = Ordered(alliance);
        horde = Ordered(horde);

        Dictionary<int, Result> results = [];
        var report = new StringBuilder();
        WriteFaction(report, "Alliance", alliance, options, results);
        report.Append("--------------------------------------------------\n\n");
        WriteFaction(report, "Horde", horde, options, results);
        report.Append("--------------------------------------------------\n\n");

        foreach (HonorWeeklyScore score in inactive)
        {
            float newRp = Finite(HonorStandings.Decay(0f, score.OldRankPoints, options.RpDecay));
            results[score.CharacterId] = new Result(0f, newRp, 0);
        }

        if (inactive.Count > 0)
        {
            report.Append("Inactive players decay\n\nCount: ").Append(inactive.Count).Append("\n\n");
            foreach (HonorWeeklyScore score in inactive)
            {
                Result r = results[score.CharacterId];
                report.Append("Guid: ").Append(score.CharacterId)
                    .Append(", HK: ").Append(score.Hk)
                    .Append(", DK: ").Append(score.Dk)
                    .Append(", CP: ").Append(Num(score.Cp))
                    .Append(", oldRp: ").Append(Num(score.OldRankPoints))
                    .Append(", newRp: ").Append(Num(r.NewRp))
                    .Append(", capRp: ").Append(Num(HonorStandings.MaximumRankPointsAtLevel(score.Level)))
                    .Append(", standing: ").Append(r.Standing).Append('\n');
            }
        }

        List<HonorRankUpdate> updates = [];
        foreach (HonorWeeklyScore score in scores)
        {
            Result r = results[score.CharacterId];
            HonorRankInfo current = HonorRanks.Calculate(r.NewRp);
            HonorRankInfo highest = HonorRanks.Describe(score.HighestRank, true);
            if (current.VisualRank > 0 && current.VisualRank > highest.VisualRank)
            {
                highest = current;
            }

            updates.Add(new HonorRankUpdate(score.CharacterId, Finite(r.NewRp), r.Standing, highest.Rank, score.Hk, score.Dk, Finite(score.Cp)));
        }

        return new HonorMaintenancePlan(updates, alliance.Count, horde.Count, inactive.Count, report.ToString());
    }

    private static void WriteFaction(
        StringBuilder report, string name, List<HonorWeeklyScore> list, HonorMaintenanceOptions options, Dictionary<int, Result> results)
    {
        if (list.Count == 0)
        {
            return;
        }

        HonorScoreCurve curve = HonorStandings.Generate([.. list.Select(s => s.Cp)], options.PoolSizePerFaction);
        report.Append(name).Append(" Honor Scores\n\nStanding size: ").Append(list.Count).Append("\n\n");
        for (int i = 0; i < curve.Brk.Length; i++)
        {
            report.Append("BRK[").Append(i).Append("] = ").Append(Num(curve.Brk[i])).Append('\n');
        }

        report.Append('\n');
        for (int i = 0; i < curve.Fx.Length; i++)
        {
            report.Append("FX[").Append(i).Append("] = ").Append(Num(curve.Fx[i])).Append('\n');
        }

        report.Append('\n');
        for (int i = 0; i < curve.Fy.Length; i++)
        {
            report.Append("FY[").Append(i).Append("] = ").Append(Num(curve.Fy[i])).Append('\n');
        }

        report.Append('\n');
        uint position = 1;
        foreach (HonorWeeklyScore score in list)
        {
            float earning = HonorStandings.Earning(score.Cp, curve);
            float newRp = HonorStandings.Decay(earning, score.OldRankPoints, options.RpDecay);
            newRp = Math.Min(HonorStandings.MaximumRankPointsAtLevel(score.Level), newRp);
            results[score.CharacterId] = new Result(earning, newRp, position);
            report.Append("Guid: ").Append(score.CharacterId)
                .Append(", HK: ").Append(score.Hk)
                .Append(", DK: ").Append(score.Dk)
                .Append(", CP: ").Append(Num(score.Cp))
                .Append(", oldRp: ").Append(Num(score.OldRankPoints))
                .Append(", earning: ").Append(Num(earning))
                .Append(", newRp: ").Append(Num(newRp))
                .Append(", capRp: ").Append(Num(HonorStandings.MaximumRankPointsAtLevel(score.Level)))
                .Append(", standing: ").Append(position).Append('\n');
            position++;
        }
    }

    // finiteAlways (vmangos): a non-finite float is stored as 0.
    private static float Finite(float value) => float.IsFinite(value) ? value : 0f;

    // C++ ostream default float formatting: six significant digits, lowercase exponent.
    private static string Num(float value)
        => value.ToString("G6", CultureInfo.InvariantCulture).Replace("E+", "e+", StringComparison.Ordinal).Replace("E-", "e-", StringComparison.Ordinal);

    private readonly record struct Result(float Earning, float NewRp, uint Standing);
}

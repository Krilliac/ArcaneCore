using ArcaneCore.Game.Honor;
using ArcaneCore.Kernel.Honor;
using Xunit;

namespace ArcaneCore.Game.Tests.Honor;

/// <summary>The weekly maintenance planner (vmangos HonorMgr.cpp:104-183, 238-326, 617-669, World.h:735-741).</summary>
public sealed class HonorMaintenancePlannerTests
{
    private const byte Human = 1;
    private const byte Orc = 2;

    private static readonly Func<byte, bool> IsAlliance = race => race is 1 or 3 or 4 or 7;

    private static HonorMaintenanceOptions Retail => HonorMaintenanceOptions.Retail;

    // Character ids 1..n of one faction with contribution points (n+1-i)*100.
    private static List<HonorWeeklyScore> Faction(int firstId, int n, byte race, byte level = 60, float oldRp = 20000f, uint hk = 20)
        => [.. Enumerable.Range(1, n).Select(i => new HonorWeeklyScore(firstId + i - 1, level, 1, race, oldRp, 0, hk, 0, (n + 1 - i) * 100f))];

    [Fact]
    public void Retail_defaults_are_twenty_percent_decay_fifteen_minimum_kills_and_pool_by_list_size()
    {
        Assert.Equal(0.2f, Retail.RpDecay);
        Assert.Equal(15u, Retail.EffectiveMinHonorKills);
        Assert.Equal(0u, Retail.PoolSizePerFaction);
        Assert.Equal(15u, new HonorMaintenanceOptions(0.2f, 0, 0).EffectiveMinHonorKills);
        Assert.Equal(40u, new HonorMaintenanceOptions(0.2f, 40, 0).EffectiveMinHonorKills);
    }

    [Fact]
    public void Each_faction_is_ranked_on_its_own_curve_and_decayed_from_its_old_points()
    {
        List<HonorWeeklyScore> all = [.. Faction(1, 100, Human), .. Faction(1001, 100, Orc)];
        HonorMaintenancePlan plan = HonorMaintenancePlanner.Plan(all, IsAlliance, Retail);

        Assert.Equal((100, 100, 0), (plan.AllianceCount, plan.HordeCount, plan.InactiveCount));
        HonorRankUpdate first = plan.Updates.Single(u => u.CharacterId == 1);
        HonorRankUpdate second = plan.Updates.Single(u => u.CharacterId == 2);
        HonorRankUpdate hordeFirst = plan.Updates.Single(u => u.CharacterId == 1001);
        Assert.Equal((28000f, 1u), (first.RankPoints, first.Standing));       // earning 12000, decay 4000
        Assert.Equal((26500f, 2u), (second.RankPoints, second.Standing));     // earning 10500
        Assert.Equal((28000f, 1u), (hordeFirst.RankPoints, hordeFirst.Standing));
        Assert.Equal(200, plan.Updates.Count);
        Assert.Equal(20u, first.WeekHk);
        Assert.Equal(10000f, first.WeekCp);
    }

    [Fact]
    public void Fourteen_kills_is_inactive_and_fifteen_is_active()
    {
        List<HonorWeeklyScore> scores =
        [
            new(1, 60, 1, Human, 13000f, 0, 14, 0, 5000f),
            new(2, 60, 1, Human, 13000f, 0, 15, 0, 5000f),
        ];
        HonorMaintenancePlan plan = HonorMaintenancePlanner.Plan(scores, IsAlliance, Retail);
        Assert.Equal((1, 1), (plan.AllianceCount, plan.InactiveCount));
        HonorRankUpdate inactive = plan.Updates.Single(u => u.CharacterId == 1);
        HonorRankUpdate active = plan.Updates.Single(u => u.CharacterId == 2);
        Assert.Equal((11700f, 0u), (inactive.RankPoints, inactive.Standing));
        Assert.Equal(1u, active.Standing);
        Assert.NotEqual(11700f, active.RankPoints);
    }

    [Fact]
    public void A_character_without_an_account_is_inactive()
    {
        HonorMaintenancePlan plan = HonorMaintenancePlanner.Plan([new HonorWeeklyScore(1, 60, 0, Human, 1000f, 0, 99, 0, 9999f)], IsAlliance, Retail);
        Assert.Equal(1, plan.InactiveCount);
        Assert.Equal(0u, plan.Updates.Single().Standing);
        Assert.Equal(900f, plan.Updates.Single().RankPoints); // decay 200, delta -200 halved to -100
    }

    [Fact]
    public void Inactive_players_decay_without_a_level_cap_and_never_gain()
    {
        // Decay is applied to the old points alone; the level cap is only applied to active players (HonorMgr.cpp:171-183).
        HonorMaintenancePlan plan = HonorMaintenancePlanner.Plan([new HonorWeeklyScore(1, 30, 1, Human, 20000f, 0, 0, 0, 0f)], IsAlliance, Retail);
        Assert.Equal(18000f, plan.Updates.Single().RankPoints); // decay 4000, delta -4000 halved to -2000
    }

    [Fact]
    public void Active_players_are_capped_by_level()
    {
        // The top scorer of 100 earns 12000 from zero rank points; a level 30 character holds at most 7150.
        List<HonorWeeklyScore> all = Faction(1, 100, Human, level: 30, oldRp: 0f);
        HonorMaintenancePlan plan = HonorMaintenancePlanner.Plan(all, IsAlliance, Retail);
        Assert.Equal(7150f, plan.Updates.Single(u => u.CharacterId == 1).RankPoints);
        Assert.Equal(1u, plan.Updates.Single(u => u.CharacterId == 1).Standing);
    }

    [Fact]
    public void Highest_rank_only_rises_when_the_new_rank_beats_it_and_is_positive()
    {
        // The top scorer ends on 28000 rank points: internal rank 11 (visual 7).
        HonorMaintenancePlan Run(byte highest)
        {
            List<HonorWeeklyScore> all = Faction(1, 100, Human);
            all[0] = all[0] with { HighestRank = highest };
            return HonorMaintenancePlanner.Plan(all, IsAlliance, Retail);
        }

        Assert.Equal((byte)11, Run(6).Updates.Single(u => u.CharacterId == 1).HighestRank);
        Assert.Equal((byte)12, Run(12).Updates.Single(u => u.CharacterId == 1).HighestRank);
        // Negative rank points never set the highest rank.
        HonorMaintenancePlan none = HonorMaintenancePlanner.Plan([new HonorWeeklyScore(1, 60, 0, Human, -3000f, 0, 0, 0, 0f)], IsAlliance, Retail);
        Assert.Equal((byte)0, none.Updates.Single().HighestRank);
        Assert.Equal(-2400f, none.Updates.Single().RankPoints); // decay -600 against earning 0: delta +600
    }

    [Fact]
    public void Standing_ties_are_broken_by_character_id_for_a_deterministic_order()
    {
        List<HonorWeeklyScore> scores =
        [
            new(9, 60, 1, Human, 0f, 0, 20, 0, 500f),
            new(3, 60, 1, Human, 0f, 0, 20, 0, 500f),
        ];
        HonorMaintenancePlan plan = HonorMaintenancePlanner.Plan(scores, IsAlliance, Retail);
        Assert.Equal(1u, plan.Updates.Single(u => u.CharacterId == 3).Standing);
        Assert.Equal(2u, plan.Updates.Single(u => u.CharacterId == 9).Standing);
    }

    [Fact]
    public void An_empty_week_plans_nothing()
    {
        HonorMaintenancePlan plan = HonorMaintenancePlanner.Plan([], IsAlliance, Retail);
        Assert.Empty(plan.Updates);
        Assert.Equal((0, 0, 0), (plan.AllianceCount, plan.HordeCount, plan.InactiveCount));
    }

    [Fact]
    public void Pool_size_override_changes_the_curve()
    {
        List<HonorWeeklyScore> all = Faction(1, 100, Human);
        float natural = HonorMaintenancePlanner.Plan(all, IsAlliance, Retail).Updates.Single(u => u.CharacterId == 50).RankPoints;
        float pooled = HonorMaintenancePlanner.Plan(all, IsAlliance, Retail with { PoolSizePerFaction = 50 }).Updates.Single(u => u.CharacterId == 50).RankPoints;
        Assert.NotEqual(natural, pooled);
    }

    [Fact]
    public void Outstanding_periods_run_oldest_first_and_end_on_the_current_week()
    {
        IReadOnlyList<HonorPeriod> periods = HonorMaintenancePlanner.Periods(100, 107, 121);
        Assert.Equal(
            [
                new HonorPeriod(100, 106, 107, 114),
                new HonorPeriod(107, 113, 114, 121),
                new HonorPeriod(114, 120, 121, 128),
            ],
            periods);
        Assert.Equal(3, (121 - 107) / 7 + 1); // vmangos "total periods" figure (HonorMgr.cpp:277)
    }

    [Theory]
    [InlineData(106u, 0)]
    [InlineData(100u, 0)]
    [InlineData(107u, 1)]
    [InlineData(113u, 1)]
    [InlineData(114u, 2)]
    public void A_period_runs_only_once_the_game_day_reaches_the_next_day(uint gameDay, int expectedPeriods)
        => Assert.Equal(expectedPeriods, HonorMaintenancePlanner.Periods(100, 107, gameDay).Count);

    [Fact]
    public void The_week_end_day_is_kept_by_the_row_cut()
    {
        HonorPeriod p = HonorMaintenancePlanner.Periods(100, 107, 107).Single();
        Assert.Equal(106u, p.WeekEnd);
        Assert.Equal(106u, p.DeleteCpBefore); // rows dated before the week end go; the week end day survives for "Yesterday"
    }

    [Theory]
    // Game day 0 is Thursday 1970-01-01 (weekday 4 with Sunday = 0).
    [InlineData(0u, 4u, 0u)]
    [InlineData(3u, 4u, 0u)]  // day 3 is Sunday; the last Thursday was day 0
    [InlineData(7u, 4u, 7u)]  // Thursday
    [InlineData(10u, 3u, 6u)] // day 10 is Sunday; the last Wednesday was day 6
    [InlineData(6u, 3u, 6u)]  // day 6 is Wednesday
    public void The_last_maintenance_day_is_the_most_recent_configured_weekday(uint gameDay, uint maintenanceDay, uint expected)
        => Assert.Equal(expected, HonorMaintenancePlanner.LastMaintenanceDay(gameDay, maintenanceDay));

    [Fact]
    public void Game_day_is_the_offset_unix_day()
    {
        Assert.Equal(1u, HonorMaintenancePlanner.GameDay(86_400, 0));
        Assert.Equal(0u, HonorMaintenancePlanner.GameDay(86_399, 0));
        Assert.Equal(1u, HonorMaintenancePlanner.GameDay(86_399, 3600));
        Assert.Equal(0u, HonorMaintenancePlanner.GameDay(0, -3600));
        Assert.Equal(4, HonorMaintenancePlanner.Weekday(0));
        Assert.Equal(0, HonorMaintenancePlanner.Weekday(3));
    }

    [Fact]
    public void The_report_lists_break_points_curves_and_every_character_in_the_vmangos_shape()
    {
        List<HonorWeeklyScore> all = [.. Faction(1, 100, Human), new HonorWeeklyScore(5000, 60, 1, Orc, 13000f, 0, 3, 1, 10f)];
        string report = HonorMaintenancePlanner.Plan(all, IsAlliance, Retail).Report;
        Assert.Contains("Alliance Honor Scores", report);
        Assert.Contains("Standing size: 100", report);
        Assert.Contains("BRK[0] = 100", report);
        Assert.Contains("FX[1] = 1550", report);
        Assert.Contains("FY[13] = 12000", report);
        Assert.Contains("Guid: 1, HK: 20, DK: 0, CP: 10000, oldRp: 20000, earning: 12000, newRp: 28000, capRp: 65000, standing: 1", report);
        Assert.Contains("Inactive players decay", report);
        Assert.Contains("Guid: 5000, HK: 3, DK: 1, CP: 10, oldRp: 13000, newRp: 11700, capRp: 65000, standing: 0", report);
        Assert.DoesNotContain("Horde Honor Scores", report);
    }
}

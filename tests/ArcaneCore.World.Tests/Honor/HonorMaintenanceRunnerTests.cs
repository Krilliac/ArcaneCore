using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.World.Honor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Honor;

/// <summary>
/// The weekly honor calculation against a store (vmangos HonorMaintenancer::DoMaintenance, HonorMgr.cpp:270-326). The day is passed
/// in, so no test depends on the clock; the store is in memory, with the same set-based rules as the EF store (whose own tests run on
/// every provider).
/// </summary>
public sealed class HonorMaintenanceRunnerTests
{
    private const byte Human = 1;
    private const byte Orc = 2;

    private sealed class Rig(MemoryHonorStore store, ServiceProvider provider, HonorFeature honor, HonorMaintenanceRunner runner) : IAsyncDisposable
    {
        public MemoryHonorStore Store { get; } = store;

        public HonorFeature Honor { get; } = honor;

        public HonorMaintenanceRunner Runner { get; } = runner;

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    private static Rig Create(HonorMaintenanceState? maintenance = null, params (string Key, string Value)[] settings)
    {
        var store = new MemoryHonorStore();
        if (maintenance is not null)
        {
            store.SaveMaintenanceAsync(maintenance).GetAwaiter().GetResult();
        }

        ServiceProvider provider = new ServiceCollection()
            .AddSingleton<IHonorStore>(store)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build())
            .BuildServiceProvider();
        var honor = new HonorFeature(provider, provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
        var runner = new HonorMaintenanceRunner(honor, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        return new Rig(store, provider, honor, runner);
    }

    private static void Rows(MemoryHonorStore store, int id, int count, float cp, uint date, HonorKind kind = HonorKind.Honorable)
        => store.Seed(id, store.State(id), [.. store.Rows(id), .. Enumerable.Range(0, count).Select(i => new HonorCpRecord(4, (uint)(1000 + i), cp, date, (byte)kind))]);

    private static Rig Week(params (string Key, string Value)[] settings)
    {
        Rig rig = Create(new HonorMaintenanceState(100, 107, false), settings);
        MemoryHonorStore s = rig.Store;
        foreach (int id in new[] { 1, 2, 3 })
        {
            s.AddCharacter(id, Human);
        }

        s.AddCharacter(4, Orc);
        s.AddCharacter(5, Orc);
        Rows(s, 1, 20, 10f, 103);
        Rows(s, 2, 20, 20f, 103);
        Rows(s, 3, 20, 30f, 103);
        Rows(s, 4, 20, 15f, 104);
        Rows(s, 5, 3, 15f, 104);                   // below 15 honorable kills: inactive
        s.Seed(5, s.State(5) with { RankPoints = 13000f }, [.. s.Rows(5)]);
        Rows(s, 1, 1, 1f, 106);                     // the week-end day itself: counted and kept
        Rows(s, 1, 1, 99f, 90);                     // before the week: not counted, and cut
        return rig;
    }

    [Fact]
    public async Task Nothing_is_done_until_the_next_maintenance_day_is_reached()
    {
        await using Rig rig = Create(new HonorMaintenanceState(100, 107, false));
        Assert.Equal(0, await rig.Runner.RunDueAsync(106, null));
        Assert.Equal(new HonorMaintenanceState(100, 107, false), rig.Store.Maintenance);
        Assert.Equal(100u, rig.Honor.WeekBeginDay);
    }

    [Fact]
    public async Task One_week_is_ranked_per_faction_applied_in_one_step_and_the_days_move_on()
    {
        await using Rig rig = Week();

        Assert.Equal(1, await rig.Runner.RunDueAsync(107, null));

        MemoryHonorStore s = rig.Store;
        Assert.Equal(11700f, s.State(5).RankPoints);   // inactive: 20 % decay, nothing earned
        Assert.Equal(20, s.State(3).StoredHk);
        Assert.Equal(20u, s.State(3).LastWeekHk);
        Assert.Equal(600f, s.State(3).LastWeekCp);
        // Rows before the week-end day are gone, the week-end day survives for "Yesterday".
        Assert.Equal([106u], s.Rows(1).Select(r => r.Date));
        Assert.Empty(s.Rows(3));
        Assert.Equal(new HonorMaintenanceState(107, 114, false), s.Maintenance);
        Assert.Equal(107u, rig.Honor.WeekBeginDay);
    }

    [Fact]
    public async Task Standings_follow_contribution_within_each_faction_and_inactive_players_have_none()
    {
        await using Rig rig = Week();
        await rig.Runner.RunDueAsync(107, null);
        MemoryHonorStore s = rig.Store;
        Assert.Equal((1u, 2u, 3u), (s.State(3).Standing, s.State(2).Standing, s.State(1).Standing)); // Alliance by contribution
        Assert.Equal((1u, 0u), (s.State(4).Standing, s.State(5).Standing));                          // Horde, and the inactive one
        Assert.True(s.State(3).RankPoints > 0);
    }

    [Fact]
    public async Task Every_outstanding_week_runs_oldest_first_in_one_pass()
    {
        await using Rig rig = Create(new HonorMaintenanceState(100, 107, false));
        Assert.Equal(3, await rig.Runner.RunDueAsync(121, null));
        Assert.Equal(new HonorMaintenanceState(121, 128, false), rig.Store.Maintenance);
        Assert.Equal(121u, rig.Honor.WeekBeginDay);
        Assert.Equal(3, rig.Runner.PeriodsApplied);
        Assert.Equal(0, await rig.Runner.RunDueAsync(121, null)); // nothing left
    }

    [Fact]
    public async Task A_failing_apply_changes_nothing_and_the_next_run_applies_the_week_once()
    {
        await using Rig rig = Week();
        MemoryHonorStore s = rig.Store;
        s.FailApply = true;
        await Assert.ThrowsAsync<IOException>(() => rig.Runner.RunDueAsync(107, null));
        Assert.Equal(new HonorMaintenanceState(100, 107, false), s.Maintenance);
        Assert.Equal(0, s.State(3).StoredHk);
        Assert.Equal(22, s.Rows(1).Count);
        Assert.Equal(100u, rig.Honor.WeekBeginDay);

        s.FailApply = false;
        Assert.Equal(1, await rig.Runner.RunDueAsync(107, null));
        Assert.Equal(20, s.State(3).StoredHk);       // applied exactly once
        Assert.Equal(new HonorMaintenanceState(107, 114, false), s.Maintenance);
    }

    [Fact]
    public async Task A_host_without_a_store_has_no_weekly_calculation()
    {
        await using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        var honor = new HonorFeature(provider, provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
        var runner = new HonorMaintenanceRunner(honor, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        Assert.Equal(0, await runner.RunDueAsync(10_000, null));
    }

    [Fact]
    public async Task City_protectors_are_the_best_ranked_character_of_each_race_when_the_option_is_on()
    {
        await using Rig rig = Week(("World:Honor:CityProtector", "true"));
        await rig.Runner.RunDueAsync(107, null);
        MemoryHonorStore s = rig.Store;
        Assert.True(s.State(3).CityProtector);   // human, standing 1
        Assert.True(s.State(4).CityProtector);   // orc, standing 1
        Assert.False(s.State(1).CityProtector);
        Assert.False(s.State(2).CityProtector);
        Assert.False(s.State(5).CityProtector);  // inactive: no standing
    }

    [Fact]
    public async Task Without_the_option_the_title_is_never_touched()
    {
        await using Rig rig = Week();
        MemoryHonorStore s = rig.Store;
        s.Seed(2, s.State(2) with { CityProtector = true }, [.. s.Rows(2)]);
        await rig.Runner.RunDueAsync(107, null);
        Assert.True(s.State(2).CityProtector);
        Assert.False(s.State(3).CityProtector);
    }

    [Fact]
    public void The_city_protector_choice_breaks_standing_ties_by_character_id_and_skips_unranked_races()
    {
        HonorWeeklyScore Score(int id, byte race) => new(id, 60, 1, race, 0f, 0, 20, 0, 100f);
        HonorRankUpdate Update(int id, uint standing) => new(id, 0f, standing, 0, 20, 0, 100f);
        IReadOnlyCollection<int> chosen = HonorMaintenanceRunner.CityProtectors(
            [Score(9, Human), Score(4, Human), Score(7, Orc), Score(8, Orc), Score(6, 3)],
            [Update(9, 1), Update(4, 1), Update(7, 2), Update(8, 0), Update(6, 0)]);
        Assert.Equal([4, 7], chosen);
    }

    [Fact]
    public async Task A_calculation_report_is_written_when_a_directory_is_configured()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcane-hcr-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using Rig rig = Week(("World:Honor:ReportDirectory", directory));
            await rig.Runner.RunDueAsync(107, null, TimeProvider.System);
            string file = Assert.Single(Directory.GetFiles(directory, "HCR_*.txt"));
            string text = await File.ReadAllTextAsync(file);
            Assert.Contains("Alliance Honor Scores", text);
            Assert.Contains("Horde Honor Scores", text);
            Assert.Contains("Inactive players decay", text);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task No_report_is_written_without_a_directory()
    {
        await using Rig rig = Week();
        await rig.Runner.RunDueAsync(107, null);
        Assert.Equal(1, rig.Runner.PeriodsApplied); // and no exception from a missing directory
    }

    [Fact]
    public async Task Online_players_are_updated_in_memory_exactly_as_the_rows_were()
    {
        var store = new MemoryHonorStore();
        uint today = HonorMaintenancePlanner.GameDay(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 0);
        uint thisWeek = HonorMaintenancePlanner.LastMaintenanceDay(today, new HonorOptions().MaintenanceDay);
        await store.SaveMaintenanceAsync(new HonorMaintenanceState(thisWeek, thisWeek + 7, false)); // not due: attach leaves it
        await using WorldTestHost host = HonorTestServices.Start(store);
        byte[] key = await host.AddAccountAsync("HONWEEKLY");
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("HONWEEKLY", key);
        await client.CreateCharacterAsync("Honweekly");
        store.AddCharacter(1, Human);
        Rows(store, 1, 20, 50f, thisWeek + 1);
        await client.LoginAsync(1);
        Assert.Equal(20u, await host.PlayerStateAsync("Honweekly", p => p.GetUInt32(UpdateFields.PlayerFieldThisWeekKills)));

        // The week ends: the rows are not due for the store until we say so.
        await store.SaveMaintenanceAsync(new HonorMaintenanceState(thisWeek, thisWeek, false));
        HonorMaintenanceFeature maintenance = await host.PlayerStateAsync("Honweekly",
            p => ((ArcaneCore.World.Net.WorldSession)p.Session).Services.GetRequiredService<HonorMaintenanceFeature>());
        Assert.Equal(1, await maintenance.RunAsync(live: true));

        Assert.Equal(thisWeek + 7, store.Maintenance!.NextDay);
        (uint thisWeekKills, uint lastWeekKills, uint standing, int rank, uint lifetime) = await host.PlayerStateAsync("Honweekly", p => (
            p.GetUInt32(UpdateFields.PlayerFieldThisWeekKills),
            p.GetUInt32(UpdateFields.PlayerFieldLastWeekKills),
            p.GetUInt32(UpdateFields.PlayerFieldLastWeekRank),
            (int)p.GetByte(UpdateFields.PlayerBytes3, 3),
            p.GetUInt32(UpdateFields.PlayerFieldLifetimeHonorbaleKills)));
        CharacterHonorState stored = store.State(1);
        Assert.Equal((0u, 20u, 1u), (thisWeekKills, lastWeekKills, standing)); // rows cut: this week starts again from nothing
        Assert.Equal(stored.Standing, standing);
        Assert.Equal(HonorRanks.Calculate(stored.RankPoints).Rank, (byte)rank);
        Assert.True(rank >= 5);
        Assert.Equal(20u, lifetime);                                           // stored 20 + this week's 0
        await client.DisposeAsync();
    }
}

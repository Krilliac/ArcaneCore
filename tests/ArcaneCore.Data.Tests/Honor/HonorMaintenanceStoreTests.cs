using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Honor;
using ArcaneCore.Kernel.Honor;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Honor;

/// <summary>
/// The weekly inputs and the single-transaction maintenance write (vmangos HonorMgr.cpp:62-102, 238-268).
/// The transaction is DML only, so MariaDB (InnoDB) and PostgreSQL roll it back like SQLite; only SQLite runs
/// locally, the others run on hosted CI.
/// </summary>
public sealed class HonorMaintenanceStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WeeklyScores_CountKillsPerType_ExcludeDishonorFromCp_AndHonorBothBounds(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await HonorTestSupport.CreateWithCharactersAsync(_databases, provider,
            ("Hunter", 1, 60, 10), ("Quiet", 2, 40, 11), ("Ranked", 3, 55, 12), ("Idle", 4, 20, 13));
        int hunter = await HonorTestSupport.IdOfAsync(cs, "Hunter");
        int quiet = await HonorTestSupport.IdOfAsync(cs, "Quiet");
        int ranked = await HonorTestSupport.IdOfAsync(cs, "Ranked");

        await WriteAsync(cs, s => s.AppendCpAsync(hunter,
        [
            new HonorCpRecord(4, 1, 100.5f, 100, (byte)HonorKind.Honorable),   // week begin: counted (BETWEEN is inclusive)
            new HonorCpRecord(4, 2, 50f, 106, (byte)HonorKind.Honorable),      // week end: counted
            new HonorCpRecord(4, 3, 70f, 107, (byte)HonorKind.Honorable),      // after the week
            new HonorCpRecord(4, 4, 40f, 99, (byte)HonorKind.Honorable),       // before the week
            new HonorCpRecord(3, 5, 25f, 101, (byte)HonorKind.Dishonorable),   // DK: counted as dk, no cp
            new HonorCpRecord(0, 0, 30f, 102, (byte)HonorKind.Bonus),          // bonus: cp only
            new HonorCpRecord(0, 0, 10f, 103, (byte)HonorKind.Quest),
        ]));
        await WriteAsync(cs, s => s.AppendCpAsync(quiet, [new HonorCpRecord(0, 0, 5f, 103, (byte)HonorKind.Other)]));
        await WriteAsync(cs, s => s.SaveStateAsync(ranked, CharacterHonorState.Empty with { RankPoints = 8000f, HighestRank = 7 }));

        IReadOnlyList<HonorWeeklyScore> scores = await ListAsync(cs, 100, 106);
        Assert.Equal(
            [
                new HonorWeeklyScore(hunter, 60, 10, 1, 0f, 0, 2, 1, 190.5f),
                new HonorWeeklyScore(quiet, 40, 11, 2, 0f, 0, 0, 0, 5f),
                new HonorWeeklyScore(ranked, 55, 12, 3, 8000f, 7, 0, 0, 0f),
            ],
            scores);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Apply_WritesEverythingInOneStep_AndDropsOnlyOlderRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await HonorTestSupport.CreateWithCharactersAsync(_databases, provider,
            ("Winner", 1, 60, 1), ("Loser", 2, 60, 2), ("Fresh", 3, 60, 3));
        int winner = await HonorTestSupport.IdOfAsync(cs, "Winner");
        int loser = await HonorTestSupport.IdOfAsync(cs, "Loser");
        int fresh = await HonorTestSupport.IdOfAsync(cs, "Fresh");
        await WriteAsync(cs, s => s.SaveStateAsync(winner, new CharacterHonorState(1000f, 6, 9, 3, 10f, 50, 4, 0x01, false)));
        await WriteAsync(cs, s => s.SaveStateAsync(loser, new CharacterHonorState(20000f, 12, 1, 3, 10f, 60, 5, 0, false)));
        await WriteAsync(cs, s => s.AppendCpAsync(winner,
        [
            new HonorCpRecord(4, 1, 10f, 105, 1),
            new HonorCpRecord(4, 2, 10f, 106, 1),   // the week-end day survives (Yesterday must stay visible)
            new HonorCpRecord(4, 3, 10f, 107, 1),
        ]));

        var batch = new HonorMaintenanceBatch(
            [
                new HonorRankUpdate(winner, 5555.55f, 1, 7, 12, 2, 345.67f),
                new HonorRankUpdate(fresh, 100f, 2, 0, 0, 0, 0f),
            ],
            DeleteCpBefore: 106,
            new HonorMaintenanceState(107, 114, false));
        await WriteAsync(cs, s => s.ApplyMaintenanceAsync(batch));

        CharacterHonorData w = await LoadAsync(cs, winner);
        Assert.Equal(new CharacterHonorState(5555.5f, 7, 1, 12, 345.7f, 62, 6, 0x01, false), w.State);
        Assert.Equal([106u, 107u], w.Cp.Select(r => r.Date));
        // The loser had a standing but was not in the weekly result: standings clear for everyone first.
        Assert.Equal(new CharacterHonorState(20000f, 12, 0, 3, 10f, 60, 5, 0, false), (await LoadAsync(cs, loser)).State);
        Assert.Equal(2u, (await LoadAsync(cs, fresh)).State.Standing);
        Assert.Equal(new HonorMaintenanceState(107, 114, false), await GetMaintenanceAsync(cs));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Apply_FailingAtTheEnd_ChangesNothing_AndARetrySucceeds(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await HonorTestSupport.CreateWithCharactersAsync(_databases, provider, ("Atomic", 1, 60, 1));
        int id = await HonorTestSupport.IdOfAsync(cs, "Atomic");
        await WriteAsync(cs, s => s.SaveStateAsync(id, new CharacterHonorState(1000f, 6, 9, 0, 0f, 10, 1, 0, false)));
        await WriteAsync(cs, s => s.AppendCpAsync(id, [new HonorCpRecord(4, 1, 10f, 1, 1)]));
        await WriteAsync(cs, s => s.SaveMaintenanceAsync(new HonorMaintenanceState(100, 107, true)));
        var batch = new HonorMaintenanceBatch([new HonorRankUpdate(id, 2000f, 1, 6, 5, 1, 50f)], 50, new HonorMaintenanceState(107, 114, false));

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            var failing = new EfHonorStore(db, _ => throw new InvalidOperationException("injected failure before commit"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ApplyMaintenanceAsync(batch));
        }

        Assert.Equal(new CharacterHonorState(1000f, 6, 9, 0, 0f, 10, 1, 0, false), (await LoadAsync(cs, id)).State);
        Assert.Single((await LoadAsync(cs, id)).Cp);
        Assert.Equal(new HonorMaintenanceState(100, 107, true), await GetMaintenanceAsync(cs));

        await WriteAsync(cs, s => s.ApplyMaintenanceAsync(batch));
        Assert.Equal(new CharacterHonorState(2000f, 6, 1, 5, 50f, 15, 2, 0, false), (await LoadAsync(cs, id)).State);
        Assert.Empty((await LoadAsync(cs, id)).Cp);
        Assert.Equal(new HonorMaintenanceState(107, 114, false), await GetMaintenanceAsync(cs));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CityProtectors_AreExactlyTheNamedCharacters_WhenGiven_AndUntouchedWhenNull(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await HonorTestSupport.CreateWithCharactersAsync(_databases, provider, ("OldTitle", 1, 60, 1), ("NewTitle", 2, 60, 2));
        int old = await HonorTestSupport.IdOfAsync(cs, "OldTitle");
        int fresh = await HonorTestSupport.IdOfAsync(cs, "NewTitle");
        await WriteAsync(cs, s => s.SaveStateAsync(old, CharacterHonorState.Empty with { CityProtector = true }));
        var state = new HonorMaintenanceState(1, 8, false);

        await WriteAsync(cs, s => s.ApplyMaintenanceAsync(new HonorMaintenanceBatch([], 0, state)));
        Assert.True((await LoadAsync(cs, old)).State.CityProtector);

        await WriteAsync(cs, s => s.ApplyMaintenanceAsync(new HonorMaintenanceBatch([], 0, state, [fresh])));
        Assert.False((await LoadAsync(cs, old)).State.CityProtector);
        Assert.True((await LoadAsync(cs, fresh)).State.CityProtector);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task MaintenanceState_RoundTripsLargeDays_AndStartsEmpty(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await HonorTestSupport.CreateWithCharactersAsync(_databases, provider);
        Assert.Null(await GetMaintenanceAsync(cs));
        var state = new HonorMaintenanceState(uint.MaxValue - 7, uint.MaxValue, true);
        await WriteAsync(cs, s => s.SaveMaintenanceAsync(state));
        await WriteAsync(cs, s => s.SaveMaintenanceAsync(state with { Marker = false }));
        Assert.Equal(state with { Marker = false }, await GetMaintenanceAsync(cs));
        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(1, await verify.Set<HonorMaintenanceRow>().CountAsync());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static async Task<CharacterHonorData> LoadAsync(DatabaseConnectionOptions cs, int id)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        return await new EfHonorStore(db).LoadAsync(id);
    }

    private static async Task<IReadOnlyList<HonorWeeklyScore>> ListAsync(DatabaseConnectionOptions cs, uint begin, uint end)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        return await new EfHonorStore(db).ListWeeklyScoresAsync(begin, end);
    }

    private static async Task<HonorMaintenanceState?> GetMaintenanceAsync(DatabaseConnectionOptions cs)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        return await new EfHonorStore(db).GetMaintenanceAsync();
    }

    private static async Task WriteAsync(DatabaseConnectionOptions cs, Func<EfHonorStore, Task> action)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await action(new EfHonorStore(db));
    }
}

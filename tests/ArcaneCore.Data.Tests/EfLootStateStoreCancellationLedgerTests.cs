using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Loot;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Loot;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// A loot commit that is cancelled (caller gave up, host shutting down) or hits an out-of-memory condition rolls back and
/// propagates without being reported as a database fault or anomaly, as in the sibling economy and quest-reward stores; a genuine
/// database error is still recorded in both ledgers.
/// </summary>
public sealed class EfLootStateStoreCancellationLedgerTests : IAsyncLifetime
{
    private const uint Instance = 101;
    private const string FaultSite = "data.loot-store";
    private const string CommitAnomaly = "database/loot-commit-failed";

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Failures() =>
    [
        [new OperationCanceledException("cancelled mid-commit")],
        [new OutOfMemoryException("simulated")],
    ];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Commit_CancelledOrOutOfMemory_PropagatesWithoutFaultOrAnomaly(Exception failure)
    {
        (long faults, long anomalies) = Counts();
        await using CharacterDbContext db = await ContextAsync(failure);

        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => new EfLootStateStore(db).CommitAsync(Request()));

        Assert.Same(failure, thrown);
        Assert.Equal((faults, anomalies), Counts());
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task Commit_GenuineDatabaseError_IsStillRecordedInBothLedgers()
    {
        (long faults, long anomalies) = Counts();
        var failure = new InvalidOperationException("simulated database failure");
        await using CharacterDbContext db = await ContextAsync(failure);

        Exception thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => new EfLootStateStore(db).CommitAsync(Request()));

        Assert.Same(failure, thrown);
        Assert.Equal((faults + 1, anomalies + 1), Counts());
        await AssertNothingWrittenAsync();
    }

    private static (long Faults, long Anomalies) Counts()
        => (FaultLedger.Shared.Snapshot().Where(s => s.Site == FaultSite).Sum(s => s.Count), AnomalyLedger.Shared.CountOf(CommitAnomaly));

    private static LootCommitRequest Request()
    {
        var key = new LootStateKey(Instance, 77002);
        var chest = new LootStateRecord(key, SourceEntry: 3000, LootOwnerCharacterId: 0, Generation: 1, Consumed: false, RespawnAtUnix: 0,
            Recipients: [], Items: [new LootStateItem(0, 117, 2, IsQuest: false, IsPerPlayer: false, IsLooted: false, [], [])]);
        return new LootCommitRequest(Guid.NewGuid(), key, null, chest, [], []);
    }

    private DatabaseConnectionOptions? _connection;

    /// <summary>A dedicated context whose save throws <paramref name="failure"/> after the commit's reads have run inside the transaction.</summary>
    private async Task<CharacterDbContext> ContextAsync(Exception failure)
    {
        _connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (CharacterDbContext seed = TestContexts.Create<CharacterDbContext>(_connection))
        {
            await SchemaBootstrapper.EnsureAsync(seed, CharacterDbContext.Schema);
            await new EfInstanceStore(seed).SaveInstanceAsync(new InstanceRecord(Instance, 36, 1_700_000_000));
        }

        return new CharacterDbContext(new DbContextOptionsBuilder<CharacterDbContext>(TestContexts.Options<CharacterDbContext>(_connection))
            .AddInterceptors(new FailOnSave(failure)).Options);
    }

    private async Task AssertNothingWrittenAsync()
    {
        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(_connection!);
        Assert.Empty(await verify.Set<LootStateRow>().ToListAsync());
        Assert.Empty(await verify.Set<LootOperationRow>().ToListAsync());
    }

    private sealed class FailOnSave(Exception failure) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => throw failure;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();
}

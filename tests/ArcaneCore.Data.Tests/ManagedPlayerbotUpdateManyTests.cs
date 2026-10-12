using System.Data.Common;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Playerbots;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>The batched checkpoint write of managed playerbots: one transaction per chunk, per-row optimistic checks.</summary>
public sealed class ManagedPlayerbotUpdateManyTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static ManagedPlayerbot Bot(int n)
        => new(Guid.NewGuid(), n, n, "bot" + n, true, ManagedPlayerbotState.Running, PlayerbotGoalKind.Explore, 0, 0, 0, 100, 100);

    private async Task<(DatabaseConnectionOptions Cs, ManagedPlayerbot[] Bots)> SeedAsync(int count)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        ManagedPlayerbot[] bots = Enumerable.Range(1, count).Select(Bot).ToArray();
        foreach (ManagedPlayerbot bot in bots)
            db.Set<ManagedPlayerbotRow>().Add(new ManagedPlayerbotRow
            {
                BotId = bot.BotId, AccountId = bot.AccountId, CharacterId = bot.CharacterId, AccountName = bot.AccountName,
                DesiredEnabled = bot.DesiredEnabled, State = bot.State, Goal = bot.Goal, Revision = bot.Revision,
                CreatedUnix = bot.CreatedUnix, UpdatedUnix = bot.UpdatedUnix,
            });
        await db.SaveChangesAsync();
        return (cs, bots);
    }

    private static ManagedPlayerbotUpdate Next(ManagedPlayerbot bot)
        => new(bot with { Goal = PlayerbotGoalKind.Grind, TargetEntry = 9, QuestId = 3, Revision = bot.Revision + 1, UpdatedUnix = 200 }, bot.Revision);

    [Fact]
    public async Task ThreeHundredRows_OpenThreeTransactions_AndAStaleRowFailsAlone()
    {
        (DatabaseConnectionOptions cs, ManagedPlayerbot[] bots) = await SeedAsync(300);
        var counter = new TransactionCounter();
        DbContextOptionsBuilder<CharacterDbContext> builder = new();
        DataServiceCollectionExtensions.ConfigureProvider(builder, cs);
        builder.AddInterceptors(counter);
        ManagedPlayerbotUpdate[] updates = bots.Select(Next).ToArray();
        updates[150] = updates[150] with { ExpectedRevision = 5 }; // stale: the stored row is at revision 0

        IReadOnlyList<bool> results;
        await using (var db = new CharacterDbContext(builder.Options))
            results = await new EfManagedPlayerbotStore(db).UpdateManyAsync(updates);

        Assert.Equal(3, counter.Started); // ceil(300 / 128)
        Assert.Equal(3, counter.Committed);
        Assert.Equal(300, results.Count);
        Assert.False(results[150]);
        Assert.Equal(299, results.Count(r => r));

        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs);
        var store = new EfManagedPlayerbotStore(read);
        ManagedPlayerbot stale = (await store.FindAsync(bots[150].BotId))!;
        Assert.Equal((0L, PlayerbotGoalKind.Explore), (stale.Revision, stale.Goal));
        ManagedPlayerbot done = (await store.FindAsync(bots[299].BotId))!;
        Assert.Equal((1L, PlayerbotGoalKind.Grind, 9u, 3u), (done.Revision, done.Goal, done.TargetEntry, done.QuestId));
    }

    [Fact]
    public async Task AnEmptyBatch_OpensNoTransaction()
    {
        (DatabaseConnectionOptions cs, _) = await SeedAsync(1);
        var counter = new TransactionCounter();
        DbContextOptionsBuilder<CharacterDbContext> builder = new();
        DataServiceCollectionExtensions.ConfigureProvider(builder, cs);
        builder.AddInterceptors(counter);
        await using var db = new CharacterDbContext(builder.Options);
        Assert.Empty(await new EfManagedPlayerbotStore(db).UpdateManyAsync([]));
        Assert.Equal(0, counter.Started);
    }

    [Fact]
    public async Task AMismatchedIdentity_IsRefusedLikeUpdateAsync()
    {
        (DatabaseConnectionOptions cs, ManagedPlayerbot[] bots) = await SeedAsync(2);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        var store = new EfManagedPlayerbotStore(db);
        ManagedPlayerbotUpdate wrongOwner = Next(bots[0]);
        wrongOwner = wrongOwner with { Bot = wrongOwner.Bot with { CharacterId = 999 } };
        IReadOnlyList<bool> results = await store.UpdateManyAsync([wrongOwner, Next(bots[1])]);
        Assert.Equal([false, true], results);
        Assert.Equal(0, (await store.FindAsync(bots[0].BotId))!.Revision);
    }

    private sealed class TransactionCounter : DbTransactionInterceptor
    {
        private int _started;
        private int _committed;

        public int Started => Volatile.Read(ref _started);

        public int Committed => Volatile.Read(ref _committed);

        public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
        {
            Interlocked.Increment(ref _started);
            return base.TransactionStarted(connection, eventData, result);
        }

        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
            DbTransaction result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _started);
            return base.TransactionStartedAsync(connection, eventData, result, cancellationToken);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _committed);
            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }
}

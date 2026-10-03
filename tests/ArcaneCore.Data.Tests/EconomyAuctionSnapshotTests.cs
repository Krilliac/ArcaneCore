using System.Data;
using System.Data.Common;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The auction row + escrow item snapshot read used by startup, recovery and character deletion.
/// Every test runs on every available provider: SQLite always, MariaDB and PostgreSQL when their server
/// connection strings are set (the hosted CI matrix). A provider that is not available is absent from the
/// theory, not passed, so a local run proves SQLite only.
/// </summary>
public sealed class EconomyAuctionSnapshotTests : IAsyncLifetime
{
    private const long Expires = 1_900_007_200;
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Snapshot_filters_rows_and_returns_only_matching_owner_zero_escrow(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateAsync(provider);
        await using (CharacterDbContext seed = TestContexts.Create<CharacterDbContext>(connection))
        {
            await AddAuctionAsync(seed, 1, itemGuid: 100, seller: 10);
            await AddAuctionAsync(seed, 2, itemGuid: 200, seller: 20);
            await AddAuctionAsync(seed, 3, itemGuid: 300, seller: 10);
            // Auction 3's item is owned by a character, not escrowed: it is no escrow at all.
            (await seed.Set<ItemInstanceRow>().SingleAsync(i => i.Guid == 300)).OwnerGuid = 77;
            // Auction 2's item row is gone.
            seed.Remove(await seed.Set<ItemInstanceRow>().SingleAsync(i => i.Guid == 200));
            await seed.SaveChangesAsync();
        }

        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        var store = new EfEconomyStore(db);
        AuctionSnapshot all = await store.GetAuctionSnapshotAsync(new AuctionSnapshotFilter());
        Assert.Equal([1u, 2u, 3u], all.Auctions.Select(a => a.Id).ToArray());
        Assert.Equal([100u], all.Escrow.Keys.ToArray());

        AuctionSnapshot one = await store.GetAuctionSnapshotAsync(new AuctionSnapshotFilter(AuctionId: 1));
        Assert.Equal(1u, Assert.Single(one.Auctions).Id);
        Assert.Equal((100u, 4000u), (one.Escrow[100].Guid, one.Escrow[100].Entry));

        AuctionSnapshot sellers = await store.GetAuctionSnapshotAsync(new AuctionSnapshotFilter(SellerId: 10));
        Assert.Equal([1u, 3u], sellers.Auctions.Select(a => a.Id).ToArray());
        Assert.Equal([100u], sellers.Escrow.Keys.ToArray());

        AuctionSnapshot both = await store.GetAuctionSnapshotAsync(new AuctionSnapshotFilter(AuctionId: 1, SellerId: 20));
        Assert.Empty(both.Auctions);
        Assert.Empty(both.Escrow);
        Assert.Empty((await store.GetAuctionSnapshotAsync(new AuctionSnapshotFilter(AuctionId: 99))).Auctions);

        // The dedicated context is left usable and clean: no transaction, nothing tracked, and a commit may follow.
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(EconomyCommitResult.Committed, await store.CommitAsync(new EconomyCommitRequest(Guid.NewGuid(), [],
            [new DeleteAuction(one.Auctions[0]), new DeleteEscrowItem(100)])));
        Assert.Empty((await store.GetAuctionSnapshotAsync(new AuctionSnapshotFilter(AuctionId: 1))).Auctions);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Snapshot_refuses_a_context_with_a_caller_transaction(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await using var caller = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EfEconomyStore(db).GetAuctionSnapshotAsync(new AuctionSnapshotFilter()));
        Assert.Same(caller, db.Database.CurrentTransaction);
    }

    /// <summary>
    /// SQLite promotes a repeatable-read transaction to BEGIN IMMEDIATE, which would make this pure
    /// read fail against any writer that has a transaction open. The deferred transaction reads beside it.
    /// </summary>
    [Fact]
    public async Task Sqlite_snapshot_reads_beside_an_open_writer_without_waiting_for_its_lock()
    {
        DatabaseConnectionOptions connection = await CreateAsync(DatabaseProvider.Sqlite);
        await using (CharacterDbContext seed = TestContexts.Create<CharacterDbContext>(connection))
        {
            await AddAuctionAsync(seed, 1, itemGuid: 100, seller: 10);
        }

        var impatient = new DatabaseConnectionOptions
        {
            Provider = DatabaseProvider.Sqlite,
            ConnectionString = connection.ConnectionString + ";Default Timeout=1;Pooling=False",
        };
        await using CharacterDbContext writer = TestContexts.Create<CharacterDbContext>(impatient);
        await using var writing = await writer.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await writer.Database.ExecuteSqlRawAsync("UPDATE auction SET deposit = deposit + 1 WHERE id = 1");

        await using CharacterDbContext reader = TestContexts.Create<CharacterDbContext>(impatient);
        AuctionSnapshot snapshot = await new EfEconomyStore(reader).GetAuctionSnapshotAsync(new AuctionSnapshotFilter(AuctionId: 1));
        Assert.Equal(1u, Assert.Single(snapshot.Auctions).Deposit);
        Assert.Contains(100u, snapshot.Escrow.Keys);
        await writing.RollbackAsync();
    }

    /// <summary>
    /// Another connection deletes the auction and hands the item to a character between the snapshot's
    /// two statements. The snapshot still holds the row with its owner-0 item. On SQLite this works
    /// because EF Core creates the database in WAL mode, where a read transaction is a true snapshot;
    /// on MariaDB and PostgreSQL it is the repeatable-read transaction.
    /// </summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AuctionSnapshot_is_consistent_against_concurrent_external_release(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateAsync(provider);
        await using (CharacterDbContext seed = TestContexts.Create<CharacterDbContext>(connection))
        {
            await AddAuctionAsync(seed, 1, itemGuid: 100, seller: 10);
        }

        var gate = new AuctionReadGate();
        await using CharacterDbContext readerDb = Context(connection, gate);
        Task<AuctionSnapshot> snapshot = new EfEconomyStore(readerDb).GetAuctionSnapshotAsync(new AuctionSnapshotFilter(AuctionId: 1));
        try
        {
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(20));
            await ExternalReleaseAsync(connection).WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            gate.Release();
        }

        AuctionSnapshot result = await snapshot.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1u, Assert.Single(result.Auctions).Id);
        Assert.Contains(100u, result.Escrow.Keys);
        await AssertReleasedAsync(connection);
    }

    /// <summary>The positive control: the legacy two-call read under the same interleaving returns the row without its item.</summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Legacy_two_query_read_is_torn_by_the_same_external_release(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateAsync(provider);
        await using (CharacterDbContext seed = TestContexts.Create<CharacterDbContext>(connection))
        {
            await AddAuctionAsync(seed, 1, itemGuid: 100, seller: 10);
        }

        var gate = new AuctionReadGate();
        await using CharacterDbContext readerDb = Context(connection, gate);
        var store = new EfEconomyStore(readerDb);
        Task<IReadOnlyList<AuctionRecord>> rows = store.GetAuctionsAsync();
        try
        {
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(20));
            await ExternalReleaseAsync(connection).WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            gate.Release();
        }

        AuctionRecord row = Assert.Single(await rows.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Empty(await store.GetEscrowItemsAsync([row.ItemGuid]));
    }

    private async Task<DatabaseConnectionOptions> CreateAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        return connection;
    }

    private static async Task AssertReleasedAsync(DatabaseConnectionOptions connection)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        Assert.False(await db.Set<AuctionRow>().AnyAsync(a => a.Id == 1));
        Assert.Equal(5, (await db.Set<ItemInstanceRow>().SingleAsync(i => i.Guid == 100)).OwnerGuid);
    }

    private static async Task AddAuctionAsync(CharacterDbContext db, uint id, uint itemGuid, int seller)
    {
        var item = new ItemInstanceRow { Guid = itemGuid };
        item.CopyFrom(0, new ItemInstanceData { Guid = itemGuid, Entry = 4000, Count = 2 });
        db.Add(item);
        var auction = new AuctionRow();
        auction.CopyFrom(new AuctionRecord
        {
            Id = id, HouseId = 1, ItemGuid = itemGuid, ItemEntry = 4000, ItemCount = 2, SellerId = seller,
            StartBid = 50, Buyout = 500, ExpireTime = Expires, Deposit = 1,
        });
        db.Add(auction);
        await db.SaveChangesAsync();
    }

    /// <summary>What a release does on the world side: the auction row goes and the item moves to a character, in one transaction.</summary>
    private static async Task ExternalReleaseAsync(DatabaseConnectionOptions connection)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        db.Remove(await db.Set<AuctionRow>().SingleAsync(a => a.Id == 1));
        (await db.Set<ItemInstanceRow>().SingleAsync(i => i.Guid == 100)).OwnerGuid = 5;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static CharacterDbContext Context(DatabaseConnectionOptions connection, IInterceptor interceptor)
        => new(new DbContextOptionsBuilder<CharacterDbContext>(TestContexts.Options<CharacterDbContext>(connection))
            .AddInterceptors(interceptor).Options);

    /// <summary>Pauses after the first query that reads the auction table has executed.</summary>
    private sealed class AuctionReadGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult(true);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("auction", StringComparison.OrdinalIgnoreCase)
                && Interlocked.CompareExchange(ref _held, 1, 0) == 0)
            {
                _entered.TrySetResult(true);
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }
}

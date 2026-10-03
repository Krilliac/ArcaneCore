using System.Data;
using System.Data.Common;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// A paid bid must not disappear when its offline seller is deleted. The interleaving uses
/// independent real contexts on the read-committed server providers. SQLite's writer locking
/// does not support that ordering; its real-store controls cover preservation and rollback.
/// </summary>
public sealed class EconomyCharacterDeletionRaceTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public static IEnumerable<object[]> ReadCommittedProvidersAndOwnership()
    {
        foreach (object[] provider in Providers())
        {
            if (provider[0] is DatabaseProvider.MariaDb or DatabaseProvider.PostgreSql)
            {
                yield return [provider[0], false];
                yield return [provider[0], true];
            }
        }
    }

    [ReadCommittedProviderTheory]
    [MemberData(nameof(ReadCommittedProvidersAndOwnership))]
    public async Task BidCommittedAfterCleanupRead_RefusesDeletionAndPreservesPaidAuction(
        DatabaseProvider provider, bool callerOwned)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken ct = deadline.Token;
        Seed seed = await CreateAsync(provider, ct);
        var gate = new AuctionReadGate();
        await using CharacterDbContext deletionDb = Context(seed.Connection, gate);
        await using IDbContextTransaction? caller = callerOwned
            ? await deletionDb.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
            : null;
        Task<bool> deletion = new EfCharacterStore(deletionDb).DeleteAsync(
            seed.Seller.Id, 1, CharacterDataCleanups.All, ct);
        try
        {
            // The deletion SELECT has executed, but cleanup has not yet removed the auction.
            // The bid uses another connection; no work is submitted to the intercepted context.
            await gate.Entered.WaitAsync(ct);
            AuctionRecord paid = await BidAsync(seed, ct);
            gate.Release();

            if (callerOwned)
            {
                await Assert.ThrowsAsync<CharacterDeletionRefusedException>(() => deletion);
                Assert.Same(caller, deletionDb.Database.CurrentTransaction);
                await caller!.RollbackAsync(ct);
            }
            else
            {
                Assert.False(await deletion);
                Assert.Null(deletionDb.Database.CurrentTransaction);
            }

            Assert.Empty(deletionDb.ChangeTracker.Entries());
            await AssertPaidAuctionAsync(seed, paid, sellerExists: true, ct);
            await AssertSellerRowsAsync(seed, ct);
        }
        finally
        {
            // Always settle the owned operation before disposing its connection/transaction.
            gate.Release();
            try
            {
                await deletion;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The refusal is asserted above; preserve the original failure during cleanup.
            }
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SellerDeletion_PreservesAnAlreadyPaidAuctionAndItsEscrow(DatabaseProvider provider)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken ct = deadline.Token;
        Seed seed = await CreateAsync(provider, ct);
        AuctionRecord paid = await BidAsync(seed, ct);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(seed.Seller.Id, 1, ct));
        }

        await AssertPaidAuctionAsync(seed, paid, sellerExists: false, ct);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LaterCleanupRefusal_RollsBackConditionalAuctionAndEscrowRemoval(DatabaseProvider provider)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken ct = deadline.Token;
        Seed seed = await CreateAsync(provider, ct);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            ICharacterDataCleanup[] cleanups = [.. CharacterDataCleanups.All, new RefusingCleanup()];
            Assert.False(await new EfCharacterStore(db).DeleteAsync(seed.Seller.Id, 1, cleanups, ct));
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
        }

        await AssertUnbidAuctionAsync(seed, ct);
        await AssertSellerRowsAsync(seed, ct);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CallerRollback_RestoresConditionalAuctionAndEscrowRemoval(DatabaseProvider provider)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken ct = deadline.Token;
        Seed seed = await CreateAsync(provider, ct);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            await using IDbContextTransaction caller = await db.Database.BeginTransactionAsync(ct);
            Assert.True(await new EfCharacterStore(db).DeleteAsync(seed.Seller.Id, 1, ct));
            Assert.Same(caller, db.Database.CurrentTransaction);
            Assert.False(await db.Set<AuctionRow>().AnyAsync(r => r.Id == seed.Auction.Id, ct));
            Assert.False(await db.Set<ItemInstanceRow>().AnyAsync(r => r.Guid == seed.Auction.ItemGuid, ct));
            await caller.RollbackAsync(ct);
        }

        await AssertUnbidAuctionAsync(seed, ct);
        await AssertSellerRowsAsync(seed, ct);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<Seed> CreateAsync(DatabaseProvider provider, CancellationToken ct)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema, cancellationToken: ct);
        var characters = new EfCharacterStore(db);
        CharacterRecord seller = await characters.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Seller" }, ct);
        CharacterRecord bidder = await characters.CreateAsync(new CharacterRecord { AccountId = 2, Name = "Bidder" }, ct);
        var item = new ItemInstanceData { Guid = 900001, Entry = 117, Count = 4 };
        CharacterState sellerBefore = State(seller.Id, 1000, [new(0, 23, item)]);
        CharacterState bidderBefore = State(bidder.Id, 1000, []);
        await characters.SaveStateAsync(sellerBefore, ct);
        await characters.SaveStateAsync(bidderBefore, ct);
        await new EfCharacterSpellStore(db).AddAsync(seller.Id, [6603u], ct);
        var auction = new AuctionRecord
        {
            Id = 1, HouseId = 2, ItemGuid = item.Guid, ItemEntry = item.Entry, ItemCount = item.Count,
            SellerId = seller.Id, StartBid = 50, Buyout = 100, ExpireTime = 1_900_007_200, Deposit = 12,
        };
        CharacterState sellerAfter = sellerBefore with { Money = 988, Inventory = new InventorySnapshot([]) };
        Assert.Equal(EconomyCommitResult.Committed, await new EfEconomyStore(db).CommitAsync(
            new EconomyCommitRequest(Guid.NewGuid(), [new EconomyParticipant(sellerBefore, sellerAfter)],
                [new EscrowFromInventory(seller.Id, item), new InsertAuction(auction)]), ct));
        return new Seed(connection, sellerAfter, bidderBefore, auction);
    }

    private static CharacterState State(int id, uint money, InventoryItemData[] items)
        => new(id, 0, 12, 1, 2, 3, 0, 10, 50, Money: money,
            ActionButtons: [new ActionButton(0, 6603, 0)], Home: new(0, 12, 1, 2, 3),
            Inventory: new InventorySnapshot(items));

    private static async Task<AuctionRecord> BidAsync(Seed seed, CancellationToken ct)
    {
        AuctionRecord paid = seed.Auction with { BidderId = seed.Bidder.Id, Bid = 75 };
        var request = new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(seed.Bidder, seed.Bidder with { Money = seed.Bidder.Money - paid.Bid })],
            [new UpdateAuction(seed.Auction, paid)]);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        var store = new EfEconomyStore(db);
        Assert.Equal(EconomyCommitResult.Committed, await store.CommitAsync(request, ct));
        Assert.True(await store.IsCommittedAsync(request.OperationId, ct));
        return paid;
    }

    private static async Task AssertPaidAuctionAsync(Seed seed, AuctionRecord paid, bool sellerExists, CancellationToken ct)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.Equal(sellerExists, await db.Characters.AnyAsync(c => c.Id == seed.Seller.Id, ct));
        Assert.Equal(paid, Assert.Single(await new EfEconomyStore(db).GetAuctionsAsync(ct)));
        Assert.Equal(seed.Bidder.Money - paid.Bid,
            (await new EfCharacterStore(db).GetByIdAsync(seed.Bidder.Id, ct))!.Money);
        await AssertEscrowAsync(db, seed.Auction.ItemGuid, ct);
    }

    private static async Task AssertUnbidAuctionAsync(Seed seed, CancellationToken ct)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.Equal(seed.Auction, Assert.Single(await new EfEconomyStore(db).GetAuctionsAsync(ct)));
        Assert.Equal(seed.Bidder.Money, (await new EfCharacterStore(db).GetByIdAsync(seed.Bidder.Id, ct))!.Money);
        await AssertEscrowAsync(db, seed.Auction.ItemGuid, ct);
    }

    private static async Task AssertSellerRowsAsync(Seed seed, CancellationToken ct)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.Equal(seed.Seller.Money, (await new EfCharacterStore(db).GetByIdAsync(seed.Seller.Id, ct))!.Money);
        Assert.Equal([new ActionButton(0, 6603, 0)],
            await new EfCharacterStore(db).GetActionButtonsAsync(seed.Seller.Id, ct));
        Assert.Equal([6603u], await new EfCharacterSpellStore(db).GetAsync(seed.Seller.Id, ct));
    }

    private static async Task AssertEscrowAsync(CharacterDbContext db, uint guid, CancellationToken ct)
    {
        ItemInstanceRow item = Assert.Single(await db.Set<ItemInstanceRow>().Where(r => r.Guid == guid).ToListAsync(ct));
        Assert.Equal(0, item.OwnerGuid);
        Assert.Equal(4u, item.Count);
        Assert.Equal(1, await db.Set<AuctionRow>().CountAsync(r => r.ItemGuid == guid, ct));
        Assert.False(await db.Set<CharacterInventoryRow>().AnyAsync(r => r.ItemGuid == guid, ct));
        Assert.False(await db.Set<MailRow>().AnyAsync(r => r.ItemGuid == guid, ct));
    }

    private static CharacterDbContext Context(DatabaseConnectionOptions connection, IInterceptor interceptor)
        => new(new DbContextOptionsBuilder<CharacterDbContext>(TestContexts.Options<CharacterDbContext>(connection))
            .AddInterceptors(interceptor).Options);

    private sealed record Seed(DatabaseConnectionOptions Connection, CharacterState Seller, CharacterState Bidder, AuctionRecord Auction);

    private sealed class RefusingCleanup : ICharacterDataCleanup
    {
        public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
            => throw new CharacterDeletionRefusedException("injected later cleanup refusal");
    }

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

    // Avoid empty-theory failures on a SQLite-only developer machine. The skipped theory is
    // explicitly a server-provider race; SQLite's three independent controls still execute.
    private sealed class ReadCommittedProviderTheoryAttribute : TheoryAttribute
    {
        public ReadCommittedProviderTheoryAttribute()
        {
            if (!Providers().Any(p => p[0] is DatabaseProvider.MariaDb or DatabaseProvider.PostgreSql))
            {
                Skip = "The read-committed bid/deletion interleaving needs MariaDB or PostgreSQL.";
            }
        }
    }
}

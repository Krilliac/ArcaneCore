using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// <see cref="MintEscrowItem"/>, the auction house bot's only way to create an item: it exists once, owned by nobody, referenced by
/// exactly its own auction, and a replay of the same operation creates nothing.
/// </summary>
public sealed class EconomyMintEscrowTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static ItemInstanceData Item(uint guid) => new()
    {
        Guid = guid, Entry = 4000, Count = 5, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21], Durability = 30,
    };

    private static AuctionRecord Auction(uint id, uint itemGuid) => new()
    {
        Id = id, HouseId = 7, ItemGuid = itemGuid, ItemEntry = 4000, ItemCount = 5, SellerId = 0, StartBid = 75, Buyout = 100,
        ExpireTime = 1_900_000_000,
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Mint_with_its_auction_creates_one_ownerless_item_and_a_replay_creates_nothing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateAsync(provider);
        var request = new EconomyCommitRequest(Guid.NewGuid(), [], [new MintEscrowItem(Item(900)), new InsertAuction(Auction(1, 900))]);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            Assert.Equal(EconomyCommitResult.Committed, await new EfEconomyStore(db).CommitAsync(request));
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            Assert.Equal(EconomyCommitResult.AlreadyCommitted, await new EfEconomyStore(db).CommitAsync(request));
        }

        await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(connection);
        ItemInstanceRow row = await check.Set<ItemInstanceRow>().AsNoTracking().SingleAsync();
        Assert.Equal((900u, 0, 4000u, 5u, 30u), (row.Guid, row.OwnerGuid, row.ItemId, row.Count, row.Durability));
        AuctionSnapshot snapshot = await new EfEconomyStore(check).GetAuctionSnapshotAsync(new AuctionSnapshotFilter(SellerId: 0));
        Assert.Equal(1u, Assert.Single(snapshot.Auctions).Id);
        Assert.True(snapshot.Escrow.ContainsKey(900));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Mint_is_refused_for_an_existing_guid_or_without_exactly_one_reference(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            Assert.Equal(EconomyCommitResult.Committed, await new EfEconomyStore(db).CommitAsync(
                new EconomyCommitRequest(Guid.NewGuid(), [], [new MintEscrowItem(Item(900)), new InsertAuction(Auction(1, 900))])));
        }

        async Task<EconomyCommitResult> Commit(params EconomyChange[] changes)
        {
            await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
            return await new EfEconomyStore(db).CommitAsync(new EconomyCommitRequest(Guid.NewGuid(), [], changes));
        }

        // The GUID exists: a second copy is never created, even under a new auction.
        Assert.Equal(EconomyCommitResult.Conflict, await Commit(new MintEscrowItem(Item(900)), new InsertAuction(Auction(2, 900))));
        // No reference: an ownerless item nothing points at would be lost.
        Assert.Equal(EconomyCommitResult.Conflict, await Commit(new MintEscrowItem(Item(901))));
        // Two references: refused by the integrity pass or, first, by the unique auction item index; either way rolled back.
        try
        {
            Assert.Equal(EconomyCommitResult.Conflict, await Commit(new MintEscrowItem(Item(902)), new InsertAuction(Auction(3, 902)), new InsertAuction(Auction(4, 902))));
        }
        catch (DbUpdateException)
        {
        }
        Assert.Throws<ArgumentException>(() => EconomyRequestValidation.Validate(
            new EconomyCommitRequest(Guid.NewGuid(), [], [new MintEscrowItem(Item(903)), new MintEscrowItem(Item(903)), new InsertAuction(Auction(5, 903))])));
        // Server mail's CreateEscrowItem shares the GUID space: twice, or once beside a mint, is refused before the store.
        Assert.Throws<ArgumentException>(() => EconomyRequestValidation.Validate(
            new EconomyCommitRequest(Guid.NewGuid(), [], [new CreateEscrowItem(Item(904)), new CreateEscrowItem(Item(904))])));
        Assert.Throws<ArgumentException>(() => EconomyRequestValidation.Validate(
            new EconomyCommitRequest(Guid.NewGuid(), [], [new CreateEscrowItem(Item(905)), new MintEscrowItem(Item(905)), new InsertAuction(Auction(6, 905))])));

        await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(connection);
        uint[] items = await check.Set<ItemInstanceRow>().AsNoTracking().Select(r => r.Guid).ToArrayAsync();
        uint[] auctions = await check.Set<AuctionRow>().AsNoTracking().Select(r => r.Id).ToArrayAsync();
        Assert.Equal(900u, Assert.Single(items));
        Assert.Equal(1u, Assert.Single(auctions));
    }

    private async Task<DatabaseConnectionOptions> CreateAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        return connection;
    }
}

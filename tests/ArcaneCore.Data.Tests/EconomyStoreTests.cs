using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Economy transactions (mail, auction, trade) on the provider matrix: every item exists exactly
/// once after a commit, a refused commit, a retried commit or a commit that fails half-way.
/// </summary>
public sealed class EconomyStoreTests : IAsyncLifetime
{
    private const long Now = 1_900_000_000;
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public static IEnumerable<object[]> ProvidersAndOrders()
    {
        foreach (object[] provider in Providers())
        {
            yield return [provider[0], false];
            yield return [provider[0], true];
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SendMail_EscrowsItem_ThenTakeItemReleasesIt_ExactlyOnce(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        MailRecord mail = Letter(1, seed.A.Id, seed.B.Id, itemGuid: 100, itemEntry: 117, money: 40, textId: 1);
        CharacterState aAfter = seed.A with { Money = seed.A.Money - 70, Inventory = Without(seed.A.Inventory!, 100) };
        EconomyCommitResult sent = await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(seed.A, aAfter)],
            [new EscrowFromInventory(seed.A.Id, ItemOf(seed.A, 100)), new InsertMail(mail, "hello")]));
        Assert.Equal(EconomyCommitResult.Committed, sent);

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            var store = new EfEconomyStore(db);
            Assert.Equal(mail, Assert.Single(await store.GetMailsAsync(seed.B.Id)));
            Assert.Equal("hello", await store.GetItemTextAsync(1));
            Assert.Equal(ItemOf(seed.A, 100).Count, (await store.GetEscrowItemsAsync([100]))[100].Count);
            Assert.Equal(aAfter.Money, (await new EfCharacterStore(db).GetByIdAsync(seed.A.Id))!.Money);
            Assert.DoesNotContain(await new EfItemStore(db).GetInventoryAsync(seed.A.Id), i => i.Item.Guid == 100);
            Assert.Equal(new EconomyIdSeed(1, 0, 1), await store.GetIdSeedAsync());
        }

        await AssertItemOnceAsync(seed, 100);
        ItemInstanceData escrowed = ItemOf(seed.A, 100);
        CharacterState bAfter = seed.B with
        {
            Money = seed.B.Money + 40,
            Inventory = new InventorySnapshot([.. seed.B.Inventory!.Items, new InventoryItemData(0, 30, escrowed)]),
        };
        EconomyCommitResult taken = await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(seed.B, bAfter)],
            [new UpdateMail(mail, mail with { ItemGuid = 0, ItemEntry = 0, Money = 0 }), new ReleaseFromEscrow(seed.B.Id, 100)]));
        Assert.Equal(EconomyCommitResult.Committed, taken);
        await AssertStateAsync(seed.Connection, bAfter);
        await AssertItemOnceAsync(seed, 100);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            Assert.Empty(await new EfEconomyStore(db).GetEscrowItemsAsync([100]));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RetriedOperation_IsAlreadyCommitted_AndAppliesNothingTwice(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        MailRecord mail = Letter(1, seed.A.Id, seed.B.Id, money: 10);
        var request = new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(seed.A, seed.A with { Money = seed.A.Money - 40 })], [new InsertMail(mail, null)]);
        Assert.Equal(EconomyCommitResult.Committed, await CommitAsync(seed, request));
        Assert.Equal(EconomyCommitResult.AlreadyCommitted, await CommitAsync(seed, request));
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.Single(await new EfEconomyStore(db).GetMailsAsync(seed.B.Id));
        Assert.Equal(seed.A.Money - 40, (await new EfCharacterStore(db).GetByIdAsync(seed.A.Id))!.Money);
        Assert.True(await new EfEconomyStore(db).IsCommittedAsync(request.OperationId));
        Assert.False(await new EfEconomyStore(db).IsCommittedAsync(Guid.NewGuid()));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task StaleMoneyInventoryOrRow_IsAConflict_AndWritesNothing(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        MailRecord mail = Letter(1, seed.A.Id, seed.B.Id, itemGuid: 100, itemEntry: 117);
        CharacterState aAfter = seed.A with { Inventory = Without(seed.A.Inventory!, 100) };

        // Durable money differs from the planned Before.
        Assert.Equal(EconomyCommitResult.Conflict, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(seed.A with { Money = seed.A.Money + 1 }, aAfter with { Money = seed.A.Money + 1 })],
            [new EscrowFromInventory(seed.A.Id, ItemOf(seed.A, 100)), new InsertMail(mail, null)])));

        // Durable inventory differs (a stack count changed since the snapshot).
        CharacterState staleInventory = seed.A with
        {
            Inventory = new InventorySnapshot([.. seed.A.Inventory!.Items.Select(i => i.Item.Guid == 101 ? i with { Item = i.Item with { Count = 9 } } : i)]),
        };
        Assert.Equal(EconomyCommitResult.Conflict, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(staleInventory, staleInventory with { Inventory = Without(staleInventory.Inventory!, 100) })],
            [new EscrowFromInventory(seed.A.Id, ItemOf(seed.A, 100)), new InsertMail(mail, null)])));

        // A letter precondition that no longer holds.
        Assert.Equal(EconomyCommitResult.Conflict, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(), [],
            [new UpdateMail(mail, mail with { Checked = MailCheckMask.Read })])));

        // A receiver that does not exist.
        Assert.Equal(EconomyCommitResult.Conflict, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(seed.A, aAfter)],
            [new EscrowFromInventory(seed.A.Id, ItemOf(seed.A, 100)), new InsertMail(mail with { ReceiverId = 9999 }, null)])));

        // A missing participant.
        Assert.Equal(EconomyCommitResult.CharacterMissing, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(seed.A with { Id = 9999 }, seed.A with { Id = 9999, Money = 1 })], [])));

        await AssertStateAsync(seed.Connection, seed.A);
        await AssertStateAsync(seed.Connection, seed.B);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.Empty(await new EfEconomyStore(db).GetMailsAsync(seed.B.Id));
        Assert.Empty(await db.Set<EconomyOperationRow>().ToListAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CommitFailingAtSave_RollsBackEverything_AndIsNotCommitted(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        MailRecord mail = Letter(1, seed.A.Id, seed.B.Id, itemGuid: 100, itemEntry: 117, textId: 5);
        var request = new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(seed.A, seed.A with { Money = seed.A.Money - 30, Inventory = Without(seed.A.Inventory!, 100) })],
            [new EscrowFromInventory(seed.A.Id, ItemOf(seed.A, 100)), new InsertMail(mail, "body")]);
        await using (CharacterDbContext db = Context(seed.Connection, new FailingSave()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new EfEconomyStore(db).CommitAsync(request));
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
        }

        await AssertStateAsync(seed.Connection, seed.A);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            var store = new EfEconomyStore(db);
            Assert.False(await store.IsCommittedAsync(request.OperationId));
            Assert.Empty(await store.GetMailsAsync(seed.B.Id));
            Assert.Null(await store.GetItemTextAsync(5));
        }

        // The retry of the same operation then commits once.
        Assert.Equal(EconomyCommitResult.Committed, await CommitAsync(seed, request));
        await AssertItemOnceAsync(seed, 100);
    }

    [Theory]
    [MemberData(nameof(ProvidersAndOrders))]
    public async Task Trade_SwapsItemsAndGold_Atomically(DatabaseProvider provider, bool reversed)
    {
        Seed seed = await CreateAsync(provider);
        ItemInstanceData x = ItemOf(seed.A, 100);
        ItemInstanceData y = ItemOf(seed.B, 200);
        CharacterState aAfter = seed.A with
        {
            Money = seed.A.Money - 25 + 5,
            Inventory = new InventorySnapshot([.. Without(seed.A.Inventory!, 100).Items, new InventoryItemData(0, 23, y)]),
        };
        CharacterState bAfter = seed.B with
        {
            Money = seed.B.Money - 5 + 25,
            Inventory = new InventorySnapshot([.. Without(seed.B.Inventory!, 200).Items, new InventoryItemData(0, 24, x)]),
        };
        EconomyParticipant[] participants = [new(seed.A, aAfter), new(seed.B, bAfter)];
        if (reversed)
        {
            Array.Reverse(participants);
        }

        Assert.Equal(EconomyCommitResult.Committed, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(), participants, [])));
        await AssertStateAsync(seed.Connection, aAfter);
        await AssertStateAsync(seed.Connection, bAfter);
        await AssertItemOnceAsync(seed, 100);
        await AssertItemOnceAsync(seed, 200);

        // A stale replay of the opposite direction cannot run: both Before snapshots are gone.
        Assert.Equal(EconomyCommitResult.Conflict, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(), participants, [])));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DuplicationAttempts_AreRefused(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        MailRecord mail = Letter(1, seed.A.Id, seed.B.Id, itemGuid: 100, itemEntry: 117);
        CharacterState aAfter = seed.A with { Inventory = Without(seed.A.Inventory!, 100) };
        Assert.Equal(EconomyCommitResult.Committed, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(seed.A, aAfter)],
            [new EscrowFromInventory(seed.A.Id, ItemOf(seed.A, 100)), new InsertMail(mail, null)])));
        ItemInstanceData item = ItemOf(seed.A, 100);
        CharacterState bAfter = seed.B with { Inventory = new InventorySnapshot([.. seed.B.Inventory!.Items, new InventoryItemData(0, 30, item)]) };

        // Release without detaching it from the letter: the integrity pass sees two holders.
        Assert.Equal(EconomyCommitResult.Conflict, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(seed.B, bAfter)], [new ReleaseFromEscrow(seed.B.Id, 100)])));

        // A second letter (or an auction) referencing the same escrowed item.
        Assert.Equal(EconomyCommitResult.Conflict, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(), [],
            [new InsertMail(Letter(2, seed.A.Id, seed.B.Id, itemGuid: 100, itemEntry: 117), null)])));
        Assert.Equal(EconomyCommitResult.Conflict, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(), [],
            [new InsertAuction(Auction(1, seed.A.Id, 100))])));

        // An existing item cannot appear in an inventory as if it were new.
        Assert.Equal(EconomyCommitResult.Conflict, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(seed.B, bAfter)], [])));
        CharacterState stolen = seed.B with
        {
            Inventory = new InventorySnapshot([.. seed.B.Inventory!.Items, new InventoryItemData(0, 31, ItemOf(seed.A, 101))]),
        };
        Assert.Equal(EconomyCommitResult.Conflict, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(seed.B, stolen)], [])));

        // Requests that lose an item, or put one into two inventories, are rejected outright.
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => new EfEconomyStore(db).CommitAsync(new EconomyCommitRequest(Guid.NewGuid(),
                [new EconomyParticipant(seed.B, seed.B with { Inventory = Without(seed.B.Inventory!, 200) })], [])));
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            ItemInstanceData y = ItemOf(seed.B, 200);
            await Assert.ThrowsAsync<ArgumentException>(() => new EfEconomyStore(db).CommitAsync(new EconomyCommitRequest(Guid.NewGuid(),
                [
                    new EconomyParticipant(seed.B, seed.B),
                    new EconomyParticipant(aAfter, aAfter with { Inventory = new InventorySnapshot([.. aAfter.Inventory!.Items, new InventoryItemData(0, 33, y)]) }),
                ], [])));
        }

        await AssertItemOnceAsync(seed, 100);
        await AssertStateAsync(seed.Connection, seed.B);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Auction_ListBidAndSettle_KeepsOneEscrowReference(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        AuctionRecord auction = Auction(1, seed.A.Id, 100);
        Assert.Equal(EconomyCommitResult.Committed, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(seed.A, seed.A with { Money = seed.A.Money - 12, Inventory = Without(seed.A.Inventory!, 100) })],
            [new EscrowFromInventory(seed.A.Id, ItemOf(seed.A, 100)), new InsertAuction(auction)])));
        await AssertItemOnceAsync(seed, 100);

        AuctionRecord bid = auction with { BidderId = seed.B.Id, Bid = 60 };
        Assert.Equal(EconomyCommitResult.Committed, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(seed.B, seed.B with { Money = seed.B.Money - 60 })], [new UpdateAuction(auction, bid)])));

        // Settlement: the auction row goes, the won letter takes over the escrow reference.
        MailRecord won = Letter(7, 2, seed.B.Id, itemGuid: 100, itemEntry: 117, textId: 9) with { MessageType = MailMessageType.Auction };
        MailRecord proceeds = Letter(8, 2, seed.A.Id, money: 69) with { MessageType = MailMessageType.Auction };
        Assert.Equal(EconomyCommitResult.Committed, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(), [],
            [new DeleteAuction(bid), new InsertMail(won, "0000000000000001:60:0"), new InsertMail(proceeds, null)])));
        await AssertItemOnceAsync(seed, 100);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        var store = new EfEconomyStore(db);
        Assert.Empty(await store.GetAuctionsAsync());
        Assert.Equal(won, Assert.Single(await store.GetMailsAsync(seed.B.Id)));
        Assert.Equal(proceeds, Assert.Single(await store.GetMailsAsync(seed.A.Id)));
        // Ids are seeded from the rows that exist, as vmangos does at startup.
        Assert.Equal(new EconomyIdSeed(8, 0, 9), await store.GetIdSeedAsync());

        // A stale bid on the settled auction is refused.
        Assert.Equal(EconomyCommitResult.Conflict, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(), [],
            [new UpdateAuction(bid, bid with { Bid = 70 })])));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LetterText_FollowsReturns_AndLetterItems(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        MailRecord first = Letter(1, seed.A.Id, seed.B.Id, textId: 1);
        MailRecord second = Letter(2, seed.A.Id, seed.B.Id, textId: 2);
        Assert.Equal(EconomyCommitResult.Committed, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(), [],
            [new InsertMail(first, "one"), new InsertMail(second, "two")])));

        // Returning moves the text to the returned copy.
        MailRecord returned = first with { Id = 3, SenderId = (uint)seed.B.Id, ReceiverId = seed.A.Id, Checked = MailCheckMask.Returned };
        Assert.Equal(EconomyCommitResult.Committed, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(), [],
            [new DeleteMail(first), new InsertMail(returned, null)])));

        // A letter item keeps the text after its letter is deleted.
        var letter = new ItemInstanceData { Guid = 500, Entry = 8383, Count = 1, TextId = 2, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21] };
        CharacterState bAfter = seed.B with { Inventory = new InventorySnapshot([.. seed.B.Inventory!.Items, new InventoryItemData(0, 35, letter)]) };
        MailRecord copied = second with { Checked = MailCheckMask.Copied };
        Assert.Equal(EconomyCommitResult.Committed, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(seed.B, bAfter)], [new UpdateMail(second, copied)])));
        Assert.Equal(EconomyCommitResult.Committed, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(), [], [new DeleteMail(copied)])));

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            var store = new EfEconomyStore(db);
            Assert.Equal("one", await store.GetItemTextAsync(1));
            Assert.Equal("two", await store.GetItemTextAsync(2));
            Assert.Contains(await new EfItemStore(db).GetInventoryAsync(seed.B.Id), i => i.Item.Guid == 500 && i.Item.TextId == 2);
        }

        // Deleting the last holder deletes the text.
        Assert.Equal(EconomyCommitResult.Committed, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(), [], [new DeleteMail(returned)])));
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            Assert.Null(await new EfEconomyStore(db).GetItemTextAsync(1));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Queries_FindExpiredAndInvolvedLetters(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        MailRecord old = Letter(1, seed.A.Id, seed.B.Id) with { ExpireTime = Now - 10 };
        MailRecord fresh = Letter(2, seed.B.Id, seed.A.Id);
        MailRecord system = Letter(3, (uint)seed.B.Id, seed.A.Id) with { MessageType = MailMessageType.Auction, ExpireTime = Now - 5 };
        Assert.Equal(EconomyCommitResult.Committed, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(), [],
            [new InsertMail(old, null), new InsertMail(fresh, null), new InsertMail(system, null)])));
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        var store = new EfEconomyStore(db);
        Assert.Equal([old, system], await store.GetExpiredMailsAsync(Now, 10));
        Assert.Equal([old], await store.GetExpiredMailsAsync(Now, 1));
        // B received "old" and sent "fresh"; the auction letter's sender id is not a character.
        Assert.Equal([old, fresh], await store.GetMailsInvolvingAsync(seed.B.Id));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeletingACharacter_ReturnsOrDeletesItsLetters_AndNeutralizesItsAuctions(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        CharacterState b = seed.B with
        {
            Inventory = new InventorySnapshot([.. seed.B.Inventory!.Items, new(0, 24, Item(201, 118, 1)), new(0, 25, Item(202, 118, 1))]),
        };
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            await new EfCharacterStore(db).SaveStateAsync(b);
        }

        MailRecord gift = Letter(1, seed.A.Id, b.Id, itemGuid: 100, itemEntry: 117, money: 40, textId: 1);
        MailRecord note = Letter(2, seed.A.Id, b.Id, textId: 2);
        AuctionRecord bidOn = Auction(10, seed.A.Id, 101) with { BidderId = b.Id, Bid = 60 };
        CharacterState aAfter = seed.A with { Money = seed.A.Money - 40, Inventory = Without(Without(seed.A.Inventory!, 100), 101) };
        Assert.Equal(EconomyCommitResult.Committed, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(seed.A, aAfter)],
            [
                new EscrowFromInventory(seed.A.Id, ItemOf(seed.A, 100)), new InsertMail(gift, "gift"), new InsertMail(note, "note"),
                new EscrowFromInventory(seed.A.Id, ItemOf(seed.A, 101)), new InsertAuction(bidOn),
            ])));

        AuctionRecord unbid = Auction(11, b.Id, 200);
        AuctionRecord withBid = Auction(12, b.Id, 201) with { BidderId = seed.A.Id, Bid = 70 };
        MailRecord cod = Letter(3, b.Id, seed.A.Id, itemGuid: 202, itemEntry: 118) with { Cod = 500 };
        CharacterState bAfter = b with { Inventory = new InventorySnapshot([]) };
        Assert.Equal(EconomyCommitResult.Committed, await CommitAsync(seed, new EconomyCommitRequest(Guid.NewGuid(),
            [new EconomyParticipant(b, bAfter)],
            [
                new EscrowFromInventory(b.Id, ItemOf(b, 200)), new InsertAuction(unbid),
                new EscrowFromInventory(b.Id, ItemOf(b, 201)), new InsertAuction(withBid),
                new EscrowFromInventory(b.Id, ItemOf(b, 202)), new InsertMail(cod, null),
            ])));

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(b.Id, accountId: 2));
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            var store = new EfEconomyStore(db);
            Assert.Empty(await store.GetMailsAsync(b.Id));
            List<MailRecord> aliceMail = [.. (await store.GetMailsAsync(seed.A.Id)).OrderBy(m => m.Id)];
            Assert.Equal([1u, 3u], aliceMail.Select(m => m.Id));
            MailRecord back = aliceMail[0];
            Assert.Equal(((uint)b.Id, 100u, 40u, 0u, 1u), (back.SenderId, back.ItemGuid, back.Money, back.Cod, back.ItemTextId));
            Assert.Equal(MailCheckMask.HasBody | MailCheckMask.Returned, back.Checked);
            Assert.True(back.ExpireTime > back.DeliverTime + (29 * 86400));
            Assert.Equal("gift", await store.GetItemTextAsync(1));
            Assert.Null(await store.GetItemTextAsync(2));
            Assert.Equal((0u, 202u), (aliceMail[1].Cod, aliceMail[1].ItemGuid));

            List<AuctionRecord> auctions = [.. (await store.GetAuctionsAsync()).OrderBy(a => a.Id)];
            Assert.Equal([bidOn with { BidderId = 0, Bid = 0 }, withBid], auctions);
            Assert.False(await db.Set<ItemInstanceRow>().AnyAsync(r => r.Guid == 200));
        }

        foreach (uint guid in new uint[] { 100, 101, 201, 202 })
        {
            await AssertItemOnceAsync(seed, guid);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<Seed> CreateAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var characters = new EfCharacterStore(db);
        CharacterRecord a = await characters.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Sender", Race = 1, Class = 1, Level = 10 });
        CharacterRecord b = await characters.CreateAsync(new CharacterRecord { AccountId = 2, Name = "Receiver", Race = 1, Class = 1, Level = 10 });
        CharacterState aState = State(a.Id, 1000, [new(0, 23, Item(100, 117, 4)), new(0, 24, Item(101, 25, 1))]);
        CharacterState bState = State(b.Id, 500, [new(0, 23, Item(200, 118, 2))]);
        await characters.SaveStateAsync(aState);
        await characters.SaveStateAsync(bState);
        return new Seed(connection, aState, bState);
    }

    private static CharacterState State(int id, uint money, InventoryItemData[] items)
        => new(id, 0, 12, 1, 2, 3, 0, 10, 50, Money: money, ActionButtons: [], Home: new(0, 12, 1, 2, 3), Inventory: new InventorySnapshot(items));

    private static ItemInstanceData Item(uint guid, uint entry, uint count) => new()
    {
        Guid = guid, Entry = entry, Count = count, Durability = 17, Creator = 7, Flags = 0,
        Charges = [-1, 0, 0, 0, 2], Enchantments = Enumerable.Range(1, 21).Select(i => (uint)i).ToArray(),
    };

    private static ItemInstanceData ItemOf(CharacterState state, uint guid) => state.Inventory!.Items.Single(i => i.Item.Guid == guid).Item;

    private static InventorySnapshot Without(InventorySnapshot inventory, uint guid) => new([.. inventory.Items.Where(i => i.Item.Guid != guid)]);

    private static MailRecord Letter(uint id, int sender, int receiver, uint itemGuid = 0, uint itemEntry = 0, uint money = 0, uint textId = 0)
        => Letter(id, (uint)sender, receiver, itemGuid, itemEntry, money, textId);

    private static MailRecord Letter(uint id, uint sender, int receiver, uint itemGuid = 0, uint itemEntry = 0, uint money = 0, uint textId = 0) => new()
    {
        Id = id, MessageType = MailMessageType.Normal, SenderId = sender, ReceiverId = receiver, Subject = $"letter {id}",
        ItemTextId = textId, ItemGuid = itemGuid, ItemEntry = itemEntry, Money = money,
        Checked = textId != 0 ? MailCheckMask.HasBody : MailCheckMask.None, DeliverTime = Now, ExpireTime = Now + (30 * 86400),
    };

    private static AuctionRecord Auction(uint id, int seller, uint itemGuid) => new()
    {
        Id = id, HouseId = 2, ItemGuid = itemGuid, ItemEntry = 117, ItemCount = 4, SellerId = seller,
        StartBid = 50, Buyout = 100, ExpireTime = Now + 7200, Deposit = 12,
    };

    private static async Task<EconomyCommitResult> CommitAsync(Seed seed, EconomyCommitRequest request)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        EconomyCommitResult result = await new EfEconomyStore(db).CommitAsync(request);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Null(db.Database.CurrentTransaction);
        return result;
    }

    private static async Task AssertStateAsync(DatabaseConnectionOptions connection, CharacterState expected)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        Assert.Equal(expected.Money, (await new EfCharacterStore(db).GetByIdAsync(expected.Id))!.Money);
        Assert.True(EconomyRequestValidation.SameInventory(expected.Inventory!.Items, await new EfItemStore(db).GetInventoryAsync(expected.Id)));
    }

    /// <summary>The item exists exactly once: one row, held either by one slot or by one letter/auction.</summary>
    private static async Task AssertItemOnceAsync(Seed seed, uint guid)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        ItemInstanceRow row = Assert.Single(await db.Set<ItemInstanceRow>().Where(r => r.Guid == guid).ToListAsync());
        int slots = await db.Set<CharacterInventoryRow>().CountAsync(r => r.ItemGuid == guid);
        int references = await db.Set<MailRow>().CountAsync(r => r.ItemGuid == guid) + await db.Set<AuctionRow>().CountAsync(r => r.ItemGuid == guid);
        Assert.Equal(1, slots + references);
        Assert.Equal(row.OwnerGuid == 0, references == 1);
    }

    private static CharacterDbContext Context(DatabaseConnectionOptions connection, IInterceptor interceptor)
        => new(new DbContextOptionsBuilder<CharacterDbContext>(TestContexts.Options<CharacterDbContext>(connection))
            .AddInterceptors(interceptor).Options);

    private sealed record Seed(DatabaseConnectionOptions Connection, CharacterState A, CharacterState B);

    /// <summary>Fails the economy SaveChanges after every row change has been staged.</summary>
    private sealed class FailingSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => eventData.Context?.ChangeTracker.Entries<EconomyOperationRow>().Any() == true
                ? throw new InvalidOperationException("injected storage failure")
                : ValueTask.FromResult(result);
    }
}

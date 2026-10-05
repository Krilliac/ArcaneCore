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

public sealed class EscrowLootCleanupTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> ProvidersAndHolders()
    {
        foreach (object[] provider in TestDatabases.AvailableProviders())
        {
            yield return [provider[0], false];
            yield return [provider[0], true];
        }
    }

    [Theory]
    [MemberData(nameof(ProvidersAndHolders))]
    public async Task DeleteEscrowItem_RemovesLootAndHolder_AndRetryDoesNotTouchOtherItems(DatabaseProvider provider, bool auction)
    {
        Seed seed = await CreateAsync(provider, auction);
        var request = new EconomyCommitRequest(Guid.NewGuid(), [], [seed.DeleteHolder, new DeleteEscrowItem(100)]);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            var store = new EfEconomyStore(db);
            Assert.Equal(EconomyCommitResult.Committed, await store.CommitAsync(request));
            Assert.Equal(EconomyCommitResult.AlreadyCommitted, await store.CommitAsync(request));
        }

        await AssertItemsAsync(seed, deleted: true);
    }

    [Theory]
    [MemberData(nameof(ProvidersAndHolders))]
    public async Task DeleteEscrowItem_SaveFailureRollsBackLootAndHolder(DatabaseProvider provider, bool auction)
    {
        Seed seed = await CreateAsync(provider, auction);
        var request = new EconomyCommitRequest(Guid.NewGuid(), [], [seed.DeleteHolder, new DeleteEscrowItem(100)]);
        var options = new DbContextOptionsBuilder<CharacterDbContext>(TestContexts.Options<CharacterDbContext>(seed.Connection))
            .AddInterceptors(new FailingSave()).Options;
        await using (var db = new CharacterDbContext(options))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new EfEconomyStore(db).CommitAsync(request));
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
        }

        await AssertItemsAsync(seed, deleted: false);
        await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.False(await new EfEconomyStore(check).IsCommittedAsync(request.OperationId));
    }

    [Theory]
    [MemberData(nameof(ProvidersAndHolders))]
    public async Task DeleteEscrowItem_ReferencedItemConflictRollsBackLoot(DatabaseProvider provider, bool auction)
    {
        Seed seed = await CreateAsync(provider, auction);
        // Keeping the holder makes the integrity pass refuse after SaveChanges, so the
        // transaction must restore the item and its generated loot together.
        var request = new EconomyCommitRequest(Guid.NewGuid(), [], [new DeleteEscrowItem(100)]);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            Assert.Equal(EconomyCommitResult.Conflict, await new EfEconomyStore(db).CommitAsync(request));
        }

        await AssertItemsAsync(seed, deleted: false);
        await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.False(await new EfEconomyStore(check).IsCommittedAsync(request.OperationId));
    }

    [Theory]
    [MemberData(nameof(ProvidersAndHolders))]
    public async Task CharacterDeletion_RemovesLootOfDiscardedMailOrUnbidAuction(DatabaseProvider provider, bool auction)
    {
        Seed seed = await CreateAsync(provider, auction);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(seed.CharacterId, 1));
        }

        await AssertItemsAsync(seed, deleted: true);
    }

    [Theory]
    [MemberData(nameof(ProvidersAndHolders))]
    public async Task CharacterDeletion_KeepsLootForReturnedMailOrBidAuction(DatabaseProvider provider, bool auction)
    {
        Seed seed = await CreateAsync(provider, auction);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            if (auction)
            {
                AuctionRow row = await db.Set<AuctionRow>().SingleAsync(r => r.Id == 1);
                row.BidderId = (await db.Set<AuctionRow>().SingleAsync(r => r.Id == 2)).SellerId;
                row.Bid = 50;
            }
            else
            {
                MailRow row = await db.Set<MailRow>().SingleAsync(r => r.Id == 1);
                row.MessageType = (byte)MailMessageType.Normal;
                row.SenderId = (uint)(await db.Set<MailRow>().SingleAsync(r => r.Id == 2)).ReceiverId;
            }

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            Assert.True(await new EfCharacterStore(db).DeleteAsync(seed.CharacterId, 1));
        }

        await AssertItemsAsync(seed, deleted: false);
    }

    [Theory]
    [MemberData(nameof(ProvidersAndHolders))]
    public async Task DeleteEscrowItem_RefusesAnOwnedItemWithoutRemovingItsLoot(DatabaseProvider provider, bool auction)
    {
        Seed seed = await CreateAsync(provider, auction);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            (await db.Set<ItemInstanceRow>().SingleAsync(r => r.Guid == 100)).OwnerGuid = seed.CharacterId;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            Assert.Equal(EconomyCommitResult.Conflict, await new EfEconomyStore(db).CommitAsync(
                new EconomyCommitRequest(Guid.NewGuid(), [], [new DeleteEscrowItem(100)])));
        }

        await AssertItemsAsync(seed, deleted: false);
    }

    [Theory]
    [MemberData(nameof(ProvidersAndHolders))]
    public async Task CharacterDeletion_SaveFailureRollsBackEscrowLootAndHolder(DatabaseProvider provider, bool auction)
    {
        Seed seed = await CreateAsync(provider, auction);
        var options = new DbContextOptionsBuilder<CharacterDbContext>(TestContexts.Options<CharacterDbContext>(seed.Connection))
            .AddInterceptors(new FailingSave()).Options;
        await using (var db = new CharacterDbContext(options))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new EfCharacterStore(db).DeleteAsync(seed.CharacterId, 1));
        }

        await AssertItemsAsync(seed, deleted: false);
        await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.NotNull(await new EfCharacterStore(check).GetByIdAsync(seed.CharacterId));
    }

    [Theory]
    [MemberData(nameof(ProvidersAndHolders))]
    public async Task CharacterDeletion_StaleEscrowReferenceDoesNotRemoveAnotherOwnersLoot(DatabaseProvider provider, bool auction)
    {
        Seed seed = await CreateAsync(provider, auction);
        int otherOwner;
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            otherOwner = (await db.Characters.SingleAsync(c => c.AccountId == 2)).Id;
            (await db.Set<ItemInstanceRow>().SingleAsync(r => r.Guid == 100)).OwnerGuid = otherOwner;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            Assert.True(await new EfCharacterStore(db).DeleteAsync(seed.CharacterId, 1));
        }

        await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.Equal(otherOwner, (await check.Set<ItemInstanceRow>().SingleAsync(r => r.Guid == 100)).OwnerGuid);
        Dictionary<uint, ItemLootData> loot = await ItemLootPersistence.LoadAsync(check, [100, 200], CancellationToken.None);
        Assert.Equal(seed.ItemLoot, loot[100]);
        Assert.Equal(seed.SurvivorLoot, loot[200]);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<Seed> CreateAsync(DatabaseProvider provider, bool auction)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var characters = new EfCharacterStore(db);
        CharacterRecord first = await characters.CreateAsync(new CharacterRecord { AccountId = 1, Name = "First", Race = 1, Class = 1, Level = 1 });
        CharacterRecord second = await characters.CreateAsync(new CharacterRecord { AccountId = 2, Name = "Second", Race = 1, Class = 1, Level = 1 });

        // Seed durable ownerless items with generated contents, as held by mail/auction escrow.
        // Include multiple loot slots and a money-only container to cover both sidecar tables.
        ItemInstanceData item = new() { Guid = 100, Entry = 4632, Count = 1, Loot = new ItemLootData(25, [new(0, 117, 2, false), new(1, 118, 1, true)]) };
        ItemInstanceData survivor = new() { Guid = 200, Entry = 4632, Count = 1, Loot = new ItemLootData(50, []) };
        foreach (ItemInstanceData data in new[] { item, survivor })
        {
            var row = new ItemInstanceRow { Guid = data.Guid };
            row.CopyFrom(0, data);
            db.Add(row);
        }

        await ItemLootPersistence.StageReplaceAsync(db, [item, survivor], [100, 200], CancellationToken.None);
        EconomyChange deleteHolder;
        if (auction)
        {
            var firstAuction = new AuctionRecord { Id = 1, HouseId = 2, ItemGuid = 100, ItemEntry = 4632, ItemCount = 1, SellerId = first.Id, StartBid = 50, ExpireTime = 1_900_000_000 };
            foreach (AuctionRecord record in new[] { firstAuction, firstAuction with { Id = 2, ItemGuid = 200, SellerId = second.Id } })
            {
                var row = new AuctionRow();
                row.CopyFrom(record);
                db.Add(row);
            }

            deleteHolder = new DeleteAuction(firstAuction);
        }
        else
        {
            // System mail cannot return to a living character on receiver deletion.
            var firstMail = new MailRecord { Id = 1, MessageType = MailMessageType.Auction, SenderId = 2, ReceiverId = first.Id, Subject = "contents", ItemGuid = 100, ItemEntry = 4632, ExpireTime = 1_900_000_000 };
            foreach (MailRecord record in new[] { firstMail, firstMail with { Id = 2, ItemGuid = 200, ReceiverId = second.Id } })
            {
                var row = new MailRow();
                row.CopyFrom(record);
                db.Add(row);
            }

            deleteHolder = new DeleteMail(firstMail);
        }

        await db.SaveChangesAsync();
        return new Seed(connection, first.Id, auction, deleteHolder, item.Loot!, survivor.Loot!);
    }

    private static async Task AssertItemsAsync(Seed seed, bool deleted)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.Equal(!deleted, await db.Set<ItemInstanceRow>().AnyAsync(r => r.Guid == 100));
        Assert.Equal(!deleted, await db.Set<ItemLootStateRow>().AnyAsync(r => r.ItemGuid == 100));
        Assert.Equal(deleted ? 0 : 2, await db.Set<ItemLootRow>().CountAsync(r => r.ItemGuid == 100));
        Dictionary<uint, ItemLootData> loot = await ItemLootPersistence.LoadAsync(db, [100, 200], CancellationToken.None);
        Assert.Equal(seed.SurvivorLoot, loot[200]);
        if (!deleted)
        {
            Assert.Equal(seed.ItemLoot, loot[100]);
        }

        Assert.True(await db.Set<ItemInstanceRow>().AnyAsync(r => r.Guid == 200 && r.OwnerGuid == 0));
        uint[] holders = seed.Auction
            ? await db.Set<AuctionRow>().OrderBy(r => r.Id).Select(r => r.ItemGuid).ToArrayAsync()
            : await db.Set<MailRow>().OrderBy(r => r.Id).Select(r => r.ItemGuid).ToArrayAsync();
        Assert.Equal(deleted ? [200u] : new[] { 100u, 200u }, holders);
    }

    private sealed record Seed(DatabaseConnectionOptions Connection, int CharacterId, bool Auction, EconomyChange DeleteHolder, ItemLootData ItemLoot, ItemLootData SurvivorLoot);

    private sealed class FailingSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("injected storage failure");
    }
}

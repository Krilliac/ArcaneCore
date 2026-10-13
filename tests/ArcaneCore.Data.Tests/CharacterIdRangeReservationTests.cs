using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ids;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class CharacterIdRangeReservationTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void IdRangeModule_IsACharactersModuleWithCleanupAndSeededScopes()
    {
        var module = new CharacterIdRangeDataModule();
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.Equal(CharacterIdRangeDataModule.Version, module.SchemaVersion);
        Assert.Contains(CharacterDataCleanups.All, c => c.GetType() == module.GetType());
        Assert.Contains(module.SchemaChanges, c => c is CreateTableChange { Table: CharacterIdRangeDataModule.Table });

        foreach (string scope in IdRangeScopes.All)
        {
            Assert.Contains(
                module.SchemaChanges,
                c => c is EnsureRowChange { Table: CharacterIdRangeDataModule.Table, KeyColumn: "scope" } row
                     && string.Equals(row.KeyValue as string, scope, StringComparison.Ordinal));
        }

        var services = new ServiceCollection();
        module.AddServices(services);
        Assert.Contains(services, d => d.ServiceType == typeof(IIdRangeReservationStore) && d.ImplementationType == typeof(EfCharacterIdRangeStore));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FirstReservation_StartsAtDefaultMinimumsWhenTablesAreEmpty(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
        }

        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        var store = new EfCharacterIdRangeStore(db);

        IdRangeBlock itemBlock = await store.ReserveBlockAsync(IdRangeScopes.Item, 100);
        Assert.Equal(new IdRangeBlock(IdRangeScopes.Item, 1, 100), itemBlock);

        IdRangeBlock groupBlock = await store.ReserveBlockAsync(IdRangeScopes.Group, 50);
        Assert.Equal(new IdRangeBlock(IdRangeScopes.Group, 1, 50), groupBlock);

        IdRangeBlock guildBlock = await store.ReserveBlockAsync(IdRangeScopes.Guild, 25);
        Assert.Equal(new IdRangeBlock(IdRangeScopes.Guild, 1, 25), guildBlock);

        IdRangeBlock instanceBlock = await store.ReserveBlockAsync(IdRangeScopes.Instance, 40);
        Assert.Equal(new IdRangeBlock(IdRangeScopes.Instance, IdRangeScopes.FirstDynamicInstanceId, IdRangeScopes.FirstDynamicInstanceId + 39), instanceBlock);

        IdRangeBlock mailBlock = await store.ReserveBlockAsync(IdRangeScopes.Mail, 10);
        Assert.Equal(new IdRangeBlock(IdRangeScopes.Mail, 1, 10), mailBlock);

        IdRangeBlock auctionBlock = await store.ReserveBlockAsync(IdRangeScopes.Auction, 10);
        Assert.Equal(new IdRangeBlock(IdRangeScopes.Auction, 1, 10), auctionBlock);

        IdRangeBlock textBlock = await store.ReserveBlockAsync(IdRangeScopes.ItemText, 10);
        Assert.Equal(new IdRangeBlock(IdRangeScopes.ItemText, 1, 10), textBlock);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FirstReservation_SeedsAboveExistingMaxRowsAcrossAllScopes(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);

            setup.Set<ItemInstanceRow>().Add(new ItemInstanceRow
            {
                Guid = 500,
                OwnerGuid = 1,
                ItemId = 25,
                Count = 1,
                Charges = string.Empty,
                Enchantments = string.Empty,
                Text = 77,
            });
            setup.Set<CharacterInventoryRow>().Add(new CharacterInventoryRow
            {
                Guid = 1,
                Bag = 0,
                Slot = 23,
                ItemGuid = 850,
                ItemId = 25,
            });
            setup.Set<GroupRow>().Add(new GroupRow
            {
                GroupId = 42,
                LeaderId = 1,
                LootMethod = 0,
                LooterId = 1,
                LootThreshold = 2,
            });
            setup.Set<GuildRow>().Add(new GuildRow
            {
                Id = 19,
                Name = "Order",
                LeaderId = 1,
                Motd = string.Empty,
                Info = string.Empty,
                CreatedAt = 1000,
            });
            setup.Set<InstanceRow>().Add(new InstanceRow
            {
                Id = 320,
                MapId = 36,
                ResetTime = 2000,
            });
            setup.Set<CharacterInstanceRow>().Add(new CharacterInstanceRow
            {
                CharacterId = 1,
                InstanceId = 410,
                Permanent = true,
            });
            setup.Set<MailRow>().Add(new MailRow
            {
                Id = 910,
                SenderId = 1,
                ReceiverId = 2,
                Subject = "Hello",
                ItemTextId = 305,
                ItemGuid = 0,
            });
            setup.Set<AuctionRow>().Add(new AuctionRow
            {
                Id = 1200,
                HouseId = 7,
                ItemGuid = 850,
                ItemEntry = 25,
                ItemCount = 1,
                SellerId = 1,
                StartBid = 100,
            });
            setup.Set<ItemTextRow>().Add(new ItemTextRow
            {
                Id = 640,
                Text = "Saved letter",
            });

            await setup.SaveChangesAsync();
        }

        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        var store = new EfCharacterIdRangeStore(db);

        IdRangeBlock itemBlock = await store.ReserveBlockAsync(IdRangeScopes.Item, 64);
        Assert.Equal(851u, itemBlock.Start);
        Assert.Equal(914u, itemBlock.EndInclusive);

        IdRangeBlock groupBlock = await store.ReserveBlockAsync(IdRangeScopes.Group, 16);
        Assert.Equal(43u, groupBlock.Start);
        Assert.Equal(58u, groupBlock.EndInclusive);

        IdRangeBlock guildBlock = await store.ReserveBlockAsync(IdRangeScopes.Guild, 8);
        Assert.Equal(20u, guildBlock.Start);
        Assert.Equal(27u, guildBlock.EndInclusive);

        IdRangeBlock instanceBlock = await store.ReserveBlockAsync(IdRangeScopes.Instance, 32);
        Assert.Equal(411u, instanceBlock.Start);
        Assert.Equal(442u, instanceBlock.EndInclusive);

        IdRangeBlock mailBlock = await store.ReserveBlockAsync(IdRangeScopes.Mail, 20);
        Assert.Equal(911u, mailBlock.Start);
        Assert.Equal(930u, mailBlock.EndInclusive);

        IdRangeBlock auctionBlock = await store.ReserveBlockAsync(IdRangeScopes.Auction, 25);
        Assert.Equal(1201u, auctionBlock.Start);
        Assert.Equal(1225u, auctionBlock.EndInclusive);

        IdRangeBlock textBlock = await store.ReserveBlockAsync(IdRangeScopes.ItemText, 15);
        Assert.Equal(641u, textBlock.Start);
        Assert.Equal(655u, textBlock.EndInclusive);

        // Next reservation continues monotonically from the previously reserved EndInclusive + 1.
        IdRangeBlock secondItemBlock = await store.ReserveBlockAsync(IdRangeScopes.Item, 10);
        Assert.Equal(915u, secondItemBlock.Start);
        Assert.Equal(924u, secondItemBlock.EndInclusive);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ConcurrentReservations_AcrossCallersAndScopes_YieldStrictlyDisjointContiguousBlocks(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
        }

        string[] scopes = [IdRangeScopes.Item, IdRangeScopes.Group, IdRangeScopes.Guild, IdRangeScopes.Instance];
        const int workersPerScope = 6;
        const int blocksPerWorker = 5;
        const uint blockSize = 32;

        using var startGate = new ManualResetEventSlim(false);
        var tasks = new List<Task<IdRangeBlock[]>>();

        foreach (string scope in scopes)
        {
            for (int w = 0; w < workersPerScope; w++)
            {
                string workerScope = scope;
                tasks.Add(Task.Run(async () =>
                {
                    startGate.Wait();
                    var reserved = new IdRangeBlock[blocksPerWorker];
                    for (int i = 0; i < blocksPerWorker; i++)
                    {
                        await using CharacterDbContext workerDb = TestContexts.Create<CharacterDbContext>(connection);
                        var store = new EfCharacterIdRangeStore(workerDb);
                        reserved[i] = await store.ReserveBlockAsync(workerScope, blockSize);
                    }

                    return reserved;
                }));
            }
        }

        startGate.Set();
        IdRangeBlock[][] allResults = await Task.WhenAll(tasks);

        foreach (string scope in scopes)
        {
            IdRangeBlock[] scopeBlocks = allResults
                .SelectMany(x => x)
                .Where(b => b.Scope == scope)
                .OrderBy(b => b.Start)
                .ToArray();

            Assert.Equal(workersPerScope * blocksPerWorker, scopeBlocks.Length);

            uint expectedStart = IdRangeScopes.GetMinFirstId(scope);
            var seenIds = new HashSet<uint>();

            foreach (IdRangeBlock block in scopeBlocks)
            {
                Assert.Equal(blockSize, block.Count);
                Assert.Equal(expectedStart, block.Start);
                Assert.Equal(expectedStart + blockSize - 1u, block.EndInclusive);

                for (uint id = block.Start; id <= block.EndInclusive; id++)
                {
                    Assert.True(seenIds.Add(id), $"Duplicate ID {id} reserved in scope '{scope}'.");
                }

                expectedStart = block.EndInclusive + 1u;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ReserveBlockAsync_FailsClosedOnOverflow(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
            await setup.Set<CharacterIdRangeRow>()
                .Where(r => r.Scope == IdRangeScopes.Item)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.NextId, (long)uint.MaxValue - 2));
            await setup.Set<CharacterIdRangeRow>()
                .Where(r => r.Scope == IdRangeScopes.Guild)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.NextId, (long)int.MaxValue));
        }

        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        var store = new EfCharacterIdRangeStore(db);

        // Requesting 5 IDs when only 3 remain in uint32 space ([uint.MaxValue - 2 .. uint.MaxValue]) fails closed.
        await Assert.ThrowsAsync<IdRangeExhaustedException>(
            () => store.ReserveBlockAsync(IdRangeScopes.Item, 5));

        // Requesting the exact 3 remaining IDs succeeds and reaches uint.MaxValue.
        IdRangeBlock finalItemBlock = await store.ReserveBlockAsync(IdRangeScopes.Item, 3);
        Assert.Equal(uint.MaxValue - 2u, finalItemBlock.Start);
        Assert.Equal(uint.MaxValue, finalItemBlock.EndInclusive);

        // Any subsequent reservation after uint.MaxValue is exhausted fails closed.
        await Assert.ThrowsAsync<IdRangeExhaustedException>(
            () => store.ReserveBlockAsync(IdRangeScopes.Item, 1));

        // Signed 32-bit scope ("guild") allows reserving int.MaxValue as a 1-ID block, then fails closed.
        IdRangeBlock finalGuildBlock = await store.ReserveBlockAsync(IdRangeScopes.Guild, 1);
        Assert.Equal((uint)int.MaxValue, finalGuildBlock.Start);
        Assert.Equal((uint)int.MaxValue, finalGuildBlock.EndInclusive);

        await Assert.ThrowsAsync<IdRangeExhaustedException>(
            () => store.ReserveBlockAsync(IdRangeScopes.Guild, 1));
    }
}

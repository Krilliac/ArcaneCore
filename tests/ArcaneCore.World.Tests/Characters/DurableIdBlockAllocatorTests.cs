using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Items;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Characters;

public sealed class DurableIdBlockAllocatorTests
{
    [Fact]
    public async Task ItemGuidAllocator_AllocatesFromReservedBlocks_PrefetchesAtLowWatermark_AndFailsClosedWhenExhausted()
    {
        var prefetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePrefetch = new TaskCompletionSource<IdRangeBlock>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reserveCalls = 0;

        var itemAllocator = new ItemGuidAllocator();
        DurableIdBlockAllocator blockAllocator = itemAllocator.ConfigureBlockReservation(
            reserveBlockAsync: async (scope, count, ct) =>
            {
                int call = Interlocked.Increment(ref reserveCalls);
                if (call == 1)
                {
                    return new IdRangeBlock(scope, 100, 100 + count - 1);
                }

                if (call == 2)
                {
                    prefetchStarted.TrySetResult();
                    return await releasePrefetch.Task.WaitAsync(ct);
                }

                throw new IdRangeExhaustedException(scope, "No further blocks in test store.");
            },
            blockSize: 4,
            lowWatermark: 2);

        // Prime initial block [100..103] at startup before world ticks run.
        IdRangeBlock first = await blockAllocator.ReserveAndEnqueueAsync();
        Assert.Equal(new IdRangeBlock(IdRangeScopes.Item, 100, 103), first);
        Assert.Equal(4ul, itemAllocator.RemainingReservedIds);

        // Next() #1 -> 100 (remaining: 3 > lowWatermark 2 -> no prefetch yet)
        Assert.Equal(100u, itemAllocator.Next());
        Assert.Equal(3ul, itemAllocator.RemainingReservedIds);
        Assert.False( prefetchStarted.Task.IsCompleted);

        // Next() #2 -> 101 (remaining: 2 <= lowWatermark 2 -> background prefetch triggered without blocking Next()!)
        Assert.Equal(101u, itemAllocator.Next());
        await prefetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // While background prefetch I/O is still in-flight, Next() #3 -> 102 returns immediately from active block!
        Assert.Equal(102u, itemAllocator.Next());
        Assert.Equal(1ul, itemAllocator.RemainingReservedIds);

        // Complete the background prefetch with [104..107] and await completion.
        releasePrefetch.SetResult(new IdRangeBlock(IdRangeScopes.Item, 104, 107));
        await blockAllocator.WaitForPendingPrefetchAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // Consume remaining ID from first block (103) and all 4 IDs from second block (104..107).
        Assert.Equal(103u, itemAllocator.Next());
        Assert.Equal(104u, itemAllocator.Next());
        Assert.Equal(105u, itemAllocator.Next());
        Assert.Equal(106u, itemAllocator.Next());
        Assert.Equal(107u, itemAllocator.Next());
        Assert.Equal(0ul, itemAllocator.RemainingReservedIds);

        // Wait for the third prefetch (which throws IdRangeExhaustedException in the background) to finish.
        await blockAllocator.WaitForPendingPrefetchAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // Once all reserved blocks are exhausted, Next() fails closed with IdRangeExhaustedException.
        Assert.Throws<IdRangeExhaustedException>(() => itemAllocator.Next());
    }

    [Fact]
    public void ItemGuidAllocator_FailsClosedWhenUnprimedOrOverflowed()
    {
        // Unprimed durable block allocator fails closed immediately rather than falling back to unsafe IDs.
        var unprimed = new ItemGuidAllocator(new DurableIdBlockAllocator(IdRangeScopes.Item));
        Assert.Throws<IdRangeExhaustedException>(() => unprimed.Next());

        // Local-mode overflow at uint.MaxValue fails closed with IdRangeExhaustedException (which is an InvalidOperationException).
        var local = new ItemGuidAllocator(uint.MaxValue);
        Assert.Throws<IdRangeExhaustedException>(() => local.Next());
    }

    [Fact]
    public void GroupGuildAndInstanceManagers_ConsumeReservedBlocks_AndFailClosedWhenExhausted()
    {
        using var world = new WorldRuntime(new WorldRuntimeOptions(), new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        var lookup = new SocialCustomerLookup();
        var socialContext = new SocialContext(world, lookup, new NoopSocialPersistence(), new SocialOptions());
        lookup.Upsert(new CharacterInfo(1, 1, "Leader", Race.Human, Class.Warrior, 60, 0));
        lookup.Upsert(new CharacterInfo(2, 2, "Member", Race.Human, Class.Warrior, 60, 0));

        // GroupManager with reserved block [500..500]
        var groupAlloc = new DurableIdBlockAllocator(IdRangeScopes.Group);
        groupAlloc.EnqueueBlock(new IdRangeBlock(IdRangeScopes.Group, 500, 500));
        socialContext.Groups.IdBlockAllocator = groupAlloc;

        // GuildManager with reserved block [700..700]
        var guildAlloc = new DurableIdBlockAllocator(IdRangeScopes.Guild);
        guildAlloc.EnqueueBlock(new IdRangeBlock(IdRangeScopes.Guild, 700, 700));
        socialContext.Guilds.IdBlockAllocator = guildAlloc;
        socialContext.Guilds.Load([]);

        GuildAdminResult guildResult = socialContext.Guilds.Create(1, "TestGuild", out Guild? createdGuild);
        Assert.Equal(GuildAdminResult.Ok, guildResult);
        Assert.NotNull(createdGuild);
        Assert.Equal(700, createdGuild!.Id);

        // Second guild creation fails closed because the reserved block [700..700] is exhausted.
        Assert.Throws<IdRangeExhaustedException>(() => socialContext.Guilds.Create(2, "SecondGuild", out _));

        // InstanceManager with reserved block [900..901]
        var instanceManager = new InstanceManager(world);
        var instanceAlloc = new DurableIdBlockAllocator(IdRangeScopes.Instance);
        instanceAlloc.EnqueueBlock(new IdRangeBlock(IdRangeScopes.Instance, 900, 901));
        instanceManager.IdBlockAllocator = instanceAlloc;

        Assert.Equal(900u, instanceManager.AllocateInstanceId());
        Assert.Equal(901u, instanceManager.AllocateInstanceId());
        Assert.Throws<IdRangeExhaustedException>(() => instanceManager.AllocateInstanceId());
    }

    [Fact]
    public async Task Allocator_RecoversAfterLastPrefetchFails_WhenStoreComesBack()
    {
        int calls = 0;
        bool storeUp = false;
        var allocator = new DurableIdBlockAllocator(
            IdRangeScopes.Item,
            blockSize: 4,
            lowWatermark: 2,
            reserveBlockAsync: (scope, count, ct) =>
            {
                Interlocked.Increment(ref calls);
                if (!storeUp && calls > 1)
                    throw new InvalidOperationException("db down");
                return Task.FromResult(new IdRangeBlock(scope, 500, 500 + count - 1));
            });

        await allocator.ReserveAndEnqueueAsync();
        for (int i = 0; i < 4; i++)
        {
            allocator.Next();
            await allocator.WaitForPendingPrefetchAsync();
        }

        // Block spent, last prefetch failed: fail closed, but the store is back now.
        storeUp = true;
        Assert.Throws<IdRangeExhaustedException>(() => allocator.Next());
        await allocator.WaitForPendingPrefetchAsync();

        Assert.Equal(500u, allocator.Next());
    }

    [Fact]
    public async Task WorldHost_PrimeDurableIdAllocatorsAsync_ReservesDisjointBlocksAcrossRestarts()
    {
        var sharedStore = new InMemoryIdRangeReservationStore();

        for (uint run = 0; run < 2; run++)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IIdRangeReservationStore>(sharedStore);
            services.AddSingleton<ItemsFeature>();
            services.AddSingleton<EconomyFeature>();

            await using ServiceProvider provider = services.BuildServiceProvider();
            var scopes = provider.GetRequiredService<IServiceScopeFactory>();

            await WorldHost.PrimeDurableIdAllocatorsAsync(provider, scopes, blockSize: 10, lowWatermark: 2);

            ItemsFeature items = provider.GetRequiredService<ItemsFeature>();
            uint firstItemGuid = items.GuidAllocator.Next();
            Assert.Equal(run * 10u + 1u, firstItemGuid);
        }
    }

    private sealed class SocialCustomerLookup : ICharacterLookup
    {
        private readonly Dictionary<uint, CharacterInfo> _byId = [];

        public void Upsert(CharacterInfo info) => _byId[info.Id] = info;

        public CharacterInfo? Find(uint characterId) => _byId.GetValueOrDefault(characterId);

        public CharacterInfo? FindByName(string name)
            => _byId.Values.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class NoopSocialPersistence : ISocialPersistence
    {
        public void SetSocial(int characterId, int otherId, ArcaneCore.Kernel.Social.SocialFlags flags) { }
        public void SaveGuild(ArcaneCore.Kernel.Social.GuildData guild) { }
        public void DeleteGuild(int guildId) { }
    }

    private sealed class InMemoryIdRangeReservationStore : IIdRangeReservationStore
    {
        private readonly ConcurrentDictionary<string, uint> _nextByScope = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public Task<IdRangeBlock> ReserveBlockAsync(string scope, uint count, CancellationToken ct = default)
        {
            lock (_gate)
            {
                uint start = _nextByScope.GetOrAdd(scope, IdRangeScopes.GetMinFirstId);
                uint end = start + count - 1u;
                _nextByScope[scope] = end + 1u;
                return Task.FromResult(new IdRangeBlock(scope, start, end));
            }
        }
    }
}

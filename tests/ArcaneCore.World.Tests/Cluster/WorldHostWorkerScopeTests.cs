using System.Collections.Concurrent;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Clustering;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using EconomyFeature = ArcaneCore.World.Economy.EconomyFeature;
using ItemsFeature = ArcaneCore.World.Items.ItemsFeature;
using PetsFeature = ArcaneCore.World.Pets.PetsFeature;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>
/// Step C3 of the per-worker character writer lock (_wf/writer-lock-DESIGN.md): the world host in <c>Cluster:Fencing:Scope=Worker</c>. Several in-process hosts share one
/// SQLite characters file (allowed only through <c>AllowSqliteWorkerScope</c>, which exists for exactly these tests), each with its own container and writer guard, so the
/// database sees independent clients as it would from separate processes. The shard sidecar locks and the durable <c>worker:&lt;shard&gt;</c> rows are the real ones.
/// This suite is SQLite-only; the same worker-scope host paths on MariaDB and PostgreSQL (shard advisory lock, claim-then-probe through the realm lock, reclaim) are the
/// <c>WorkerScope*</c> cases in <c>TwoWorldFencingProviderTests</c>, which run where <c>ARCANECORE_TEST_MARIADB</c> / <c>ARCANECORE_TEST_POSTGRES</c> name a server.
/// </summary>
public sealed class WorldHostWorkerScopeTests
{
    private const string ShardA = "shard-a";
    private const string ShardB = "shard-b";

    [Fact]
    public async Task TwoHostsWithDistinctShards_BothStart_AndHoldTheirOwnScopes()
    {
        using var directory = new TempDirectory("arcanecore-workerscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit a = db.NewWorker(ShardA, "0,1");
        await using HostKit b = db.NewWorker(ShardB, "30-31");
        await a.Host.StartAsync(CancellationToken.None);
        await b.Host.StartAsync(CancellationToken.None);

        Assert.Equal("worker:" + ShardA, a.Host.OwnershipScope);
        Assert.Equal(a.Host.OwnershipScope, a.World.CharacterOwnership!.Scope);
        Assert.Equal(b.Host.OwnershipScope, b.World.CharacterOwnership!.Scope);
        Assert.NotEqual(a.World.CharacterOwnership.Scope, b.World.CharacterOwnership.Scope);
        Assert.True(a.Guard.IsWorkerScope);
        Assert.Equal(ShardA, a.Guard.Shard);
        Assert.EndsWith(".lock", a.Guard.LockPath);
        Assert.NotEqual(a.Guard.LockPath, b.Guard.LockPath); // one sidecar per shard
        Assert.DoesNotContain(".world.lock", a.Guard.LockPath, StringComparison.Ordinal); // and never the realm sidecar

        Assert.Equal(a.World.CharacterOwnership, await db.CurrentAsync(a, a.Host.OwnershipScope));
        Assert.Equal(b.World.CharacterOwnership, await db.CurrentAsync(b, b.Host.OwnershipScope));
        Assert.Null(await db.CurrentAsync(a, "world")); // the realm row stays inactive: it is the gate
        Assert.Equal("0,1", (await db.RowAsync(a.Host.OwnershipScope)).OwnedMaps);
        Assert.Equal("30,31", (await db.RowAsync(b.Host.OwnershipScope)).OwnedMaps);
    }

    [Fact]
    public async Task TheSameShardTwice_IsRefusedByTheShardLock_AndTakesNoClaim()
    {
        using var directory = new TempDirectory("arcanecore-workerscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit first = db.NewWorker(ShardA, "0,1");
        await first.Host.StartAsync(CancellationToken.None);
        CharacterOwnershipToken owned = first.World.CharacterOwnership!;

        await using HostKit second = db.NewWorker(ShardA, "0,1");
        CharacterDatabaseAlreadyOwnedException refused = await Assert.ThrowsAsync<CharacterDatabaseAlreadyOwnedException>(
            () => second.Host.StartAsync(CancellationToken.None));

        Assert.Contains("already owned by another world process", refused.Message);
        Assert.False(second.Feature.Attached);
        Assert.Null(second.Guard.LockPath); // the refused start holds nothing
        Assert.Equal(owned, await db.CurrentAsync(first, owned.Scope)); // the first worker's claim is untouched
    }

    [Fact]
    public async Task AKilledWorker_IsReclaimedByItsRestart_AtEpochPlusOne_AndTheOldTokenIsRefused()
    {
        using var directory = new TempDirectory("arcanecore-workerscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);
        int id = await db.CreateCharacterAsync("Reclaimed");

        await using HostKit old = db.NewWorker(ShardA, "0,1");
        await old.Host.StartAsync(CancellationToken.None);
        CharacterOwnershipToken oldToken = old.World.CharacterOwnership!;
        await SaveAsync(old, State(id, 5, oldToken)); // the live owner writes

        // A hard kill: the process is gone (its shard lock is released by the operating system) and its row stays active. The old host object stays alive in this
        // process as the zombie.
        old.Guard.Dispose();

        await using HostKit restarted = db.NewWorker(ShardA, "0,1");
        await restarted.Host.StartAsync(CancellationToken.None);
        CharacterOwnershipToken newToken = restarted.World.CharacterOwnership!;

        Assert.Equal(oldToken.Scope, newToken.Scope);
        Assert.Equal(oldToken.Epoch + 1, newToken.Epoch);
        Assert.NotEqual(oldToken.Incarnation, newToken.Incarnation);
        Assert.Equal(restarted.Host.Incarnation, newToken.Incarnation);
        Assert.Equal(newToken, await db.CurrentAsync(restarted, newToken.Scope));

        await AssertRejectedAsync(() => SaveAsync(old, State(id, 6, oldToken))); // the zombie is fenced by the epoch
        Assert.Equal(5u, await db.MapOfAsync(id));
        await SaveAsync(restarted, State(id, 7, newToken));
        Assert.Equal(7u, await db.MapOfAsync(id));
    }

    [Fact]
    public async Task ARestartWithoutTheShardLock_CannotReclaimALiveShard()
    {
        using var directory = new TempDirectory("arcanecore-workerscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit live = db.NewWorker(ShardA, "0,1");
        await live.Host.StartAsync(CancellationToken.None);
        CharacterOwnershipToken owned = live.World.CharacterOwnership!;

        // The lock is held by the live worker, so the second start is refused before it reaches the row; holding the lock is the only liveness proof the claim accepts.
        await using HostKit rival = db.NewWorker(ShardA, "0,1");
        await Assert.ThrowsAsync<CharacterDatabaseAlreadyOwnedException>(() => rival.Host.StartAsync(CancellationToken.None));
        Assert.Equal(owned, await db.CurrentAsync(live, owned.Scope));
    }

    [Fact]
    public async Task ACombinedWorld_IsRefused_WhileAWorkerIsActive()
    {
        using var directory = new TempDirectory("arcanecore-workerscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit worker = db.NewWorker(ShardA, "0,1");
        await worker.Host.StartAsync(CancellationToken.None);

        await using HostKit combined = db.NewCombined();
        InvalidOperationException refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => combined.Host.StartAsync(CancellationToken.None));

        Assert.Contains("already owned by another world authority", refused.Message);
        Assert.False(combined.Feature.Attached);
        Assert.Null(await db.CurrentAsync(worker, "world"));
        Assert.Equal(worker.World.CharacterOwnership, await db.CurrentAsync(worker, worker.Host.OwnershipScope));
    }

    [Fact]
    public async Task AWorker_IsRefused_WhileACombinedWorldIsActive()
    {
        using var directory = new TempDirectory("arcanecore-workerscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit combined = db.NewCombined();
        await combined.Host.StartAsync(CancellationToken.None);
        CharacterOwnershipToken world = combined.World.CharacterOwnership!;

        await using HostKit worker = db.NewWorker(ShardA, "0,1");
        InvalidOperationException refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => worker.Host.StartAsync(CancellationToken.None));

        Assert.Contains("could not be claimed", refused.Message);
        Assert.False(worker.Feature.Attached);
        Assert.Equal(world, await db.CurrentAsync(combined, "world"));
        Assert.Null(await db.CurrentAsync(combined, "worker:" + ShardA));
    }

    [Fact]
    public async Task AWorker_BacksOut_WhenAnUnfencedCombinedWorldHoldsTheRealmLock()
    {
        using var directory = new TempDirectory("arcanecore-workerscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        // An unfenced Combined world takes no durable claim, only the realm lock: the claim-then-probe order is what the worker has to catch it with.
        using CharacterDatabaseWriterLock realm = CharacterDatabaseWriterLock.TryAcquire(db.Database.Value.Resolve(DatabaseComponent.Characters))!;
        Assert.NotNull(realm);

        await using HostKit worker = db.NewWorker(ShardA, "0,1");
        InvalidOperationException refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => worker.Host.StartAsync(CancellationToken.None));

        Assert.Contains("realm writer lock", refused.Message);
        Assert.False(worker.Feature.Attached);
        Assert.Null(await db.CurrentAsync(worker, "worker:" + ShardA)); // the claim was released again
    }

    [Fact]
    public async Task TheWorkerHeartbeat_AdvancesTheRowsHeartbeat_WhileTheHostRuns()
    {
        using var directory = new TempDirectory("arcanecore-workerscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit worker = db.NewWorker(ShardA, "0,1", heartbeat: TimeSpan.FromMilliseconds(40));
        await worker.Host.StartAsync(CancellationToken.None);
        long first = await db.HeartbeatAsync(worker.Host.OwnershipScope);
        Assert.True(first > 0);

        long latest = first;
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (latest <= first && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            latest = await db.HeartbeatAsync(worker.Host.OwnershipScope);
        }

        Assert.True(latest > first, "the heartbeat never advanced");
    }

    /// <summary>
    /// The heartbeat's refusal while a handoff is undecided (the queue is sealed) is no reason to stop, and the loop keeps beating: after the handoff aborts and the source resumes, the same refusal
    /// stops the queue. A loop that returned on the first refusal would never see the resume. The ownership row is moved on by a takeover, as a handoff's commit moves it.
    /// </summary>
    [Fact]
    public async Task TheWorkerHeartbeat_KeepsLoopingWhileTheQueueIsSealed_AndGoesTerminalAfterTheResume()
    {
        using var directory = new TempDirectory("arcanecore-workerscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);
        await using HostKit worker = db.NewWorker(ShardA, "0,1", heartbeat: TimeSpan.FromMilliseconds(25));
        var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.SaveQueue.FatalFailure += error => fatal.TrySetResult(error);
        await worker.Host.StartAsync(CancellationToken.None);
        CharacterOwnershipToken mine = worker.World.CharacterOwnership!;

        worker.SaveQueue.SealWrites(); // a handoff has flushed and not decided
        await TakeOverAsync(worker, mine);

        // Many intervals pass with the row refusing every beat: the queue is sealed, so nothing stops.
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        Assert.False(fatal.Task.IsCompleted);
        Assert.Null(worker.SaveQueue.TerminalFailure);

        worker.SaveQueue.UnsealWrites(); // the handoff aborted, the source resumed
        Exception failure = await fatal.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Contains("no longer belongs to this world", failure.Message);
        Assert.Same(failure, worker.SaveQueue.TerminalFailure);
    }

    /// <summary>A retired world is done: the heartbeat's refusal after the takeover never stops the queue, whatever happens to the loop.</summary>
    [Fact]
    public async Task TheWorkerHeartbeat_AfterTheRetirement_NeverStopsTheQueue()
    {
        using var directory = new TempDirectory("arcanecore-workerscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);
        await using HostKit worker = db.NewWorker(ShardA, "0,1", heartbeat: TimeSpan.FromMilliseconds(25));
        var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.SaveQueue.FatalFailure += error => fatal.TrySetResult(error);
        await worker.Host.StartAsync(CancellationToken.None);
        CharacterOwnershipToken mine = worker.World.CharacterOwnership!;

        worker.SaveQueue.RetireEveryCharacter();
        await TakeOverAsync(worker, mine);
        await Task.Delay(TimeSpan.FromMilliseconds(400));

        Assert.False(fatal.Task.IsCompleted);
        Assert.Null(worker.SaveQueue.TerminalFailure);
    }

    /// <summary>The takeover a handoff's commit performs on the shard's ownership row: the epoch moves on under another incarnation, so the old token's heartbeat is refused.</summary>
    private static async Task TakeOverAsync(HostKit worker, CharacterOwnershipToken observed)
    {
        await using AsyncServiceScope scope = worker.Scopes.CreateAsyncScope();
        CharacterOwnershipToken? taken = await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>()
            .TryTakeoverAsync(observed, "node-other", Guid.NewGuid());
        Assert.NotNull(taken);
        Assert.Equal(observed.Epoch + 1, taken.Epoch);
    }

    [Fact]
    public async Task SqliteWorkerScope_IsRefused_WithoutTheTestSwitch()
    {
        using var directory = new TempDirectory("arcanecore-workerscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit worker = db.NewWorker(ShardA, "0,1", allowSqlite: false);

        await Assert.ThrowsAsync<WorkerScopeConfigurationException>(() => worker.Host.StartAsync(CancellationToken.None));
        Assert.Null(worker.Guard.LockPath);
        Assert.False(await db.AnyActiveAsync());
    }

    [Fact]
    public async Task WorkerScope_NeedsAnExplicitMapList_TheMapWorkerRole_AndAValidShard()
    {
        using var directory = new TempDirectory("arcanecore-workerscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit noMaps = db.NewWorker(ShardA, ownedMaps: "");
        WorkerScopeConfigurationException emptyMaps = await Assert.ThrowsAsync<WorkerScopeConfigurationException>(() => noMaps.Host.StartAsync(CancellationToken.None));
        Assert.Contains("OwnedMaps", emptyMaps.Message);

        await using HostKit combinedRole = db.NewWorker(ShardA, "0,1", role: ClusterNodeRole.Combined);
        WorkerScopeConfigurationException wrongRole = await Assert.ThrowsAsync<WorkerScopeConfigurationException>(() => combinedRole.Host.StartAsync(CancellationToken.None));
        Assert.Contains("MapWorker", wrongRole.Message);

        await using HostKit badShard = db.NewWorker("bad shard!", "0,1");
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => badShard.Host.StartAsync(CancellationToken.None));
        Assert.False(await db.AnyActiveAsync());
    }

    [Fact]
    public void FencingOptions_ResolveTheShardFromTheNodeId_AndValidateTheWorkerKeys()
    {
        var options = new CharacterFencingOptions { Enabled = true, Scope = CharacterFencingScope.Worker };
        Assert.Equal("worker:node-1", options.ResolveScope("node-1"));
        options.Shard = "S9";
        Assert.Equal("worker:S9", options.ResolveScope("node-1"));
        Assert.Equal("world", new CharacterFencingOptions().ResolveScope("node-1")); // Realm scope never changes
        options.Validate();

        Assert.Throws<InvalidOperationException>(() => new CharacterFencingOptions { Scope = CharacterFencingScope.Worker }.Validate()); // needs fencing
        Assert.Throws<InvalidOperationException>(() => new CharacterFencingOptions { Enabled = true, Scope = CharacterFencingScope.Worker, Shard = "no good" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new CharacterFencingOptions { Enabled = true, Scope = CharacterFencingScope.Worker, HeartbeatSeconds = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new CharacterFencingOptions { Enabled = true, Scope = CharacterFencingScope.Worker, HeartbeatSeconds = 20, ReapAfterSeconds = 60 }.Validate()); // not more than three heartbeats
        // The heartbeat and reap ranges only apply in Worker scope: a Realm-scope world with them out of range still validates.
        new CharacterFencingOptions { HeartbeatSeconds = 0, ReapAfterSeconds = 1 }.Validate();
        Assert.Throws<WorkerScopeConfigurationException>(() => new CharacterFencingOptions { Enabled = true, Scope = CharacterFencingScope.Worker }.ResolveScope(new string('x', 58)));

        Assert.True(CharacterFencingOptions.TryGetOwnedMaps(new GatewayWorkerOptions { OwnedMaps = "1, 0-2 ,30" }, out IReadOnlyCollection<uint> maps, out _));
        Assert.Equal<uint>([0, 1, 2, 30], maps);
        Assert.False(CharacterFencingOptions.TryGetOwnedMaps(new GatewayWorkerOptions { OwnedMaps = "0-4000000000" }, out _, out string? tooMany));
        Assert.Contains("more than", tooMany);
    }

    [Fact]
    public async Task TheIdAllocators_ArePrimedBeforeTheFeaturesAttach_AndEconomyIsNotGatedOnItsEnabledFlag()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IIdRangeReservationStore>(new CountingReservations());
        services.AddSingleton<ItemsFeature>();
        services.AddSingleton<PetsFeature>();
        services.AddSingleton<EconomyFeature>();
        await using ServiceProvider provider = services.BuildServiceProvider();

        Action handOver = await WorldHost.PrepareDurableIdAllocatorsAsync(provider, provider.GetRequiredService<IServiceScopeFactory>(), blockSize: 10, lowWatermark: 2, requireDurableIds: true);

        // Nothing is attached yet, and the allocators are already in place (a feature that allocates while it attaches draws from a reserved block).
        Assert.True(provider.GetRequiredService<ItemsFeature>().GuidAllocator.RequireDurableIds);
        Assert.Equal(1u, provider.GetRequiredService<ItemsFeature>().GuidAllocator.Next());
        Assert.True(provider.GetRequiredService<PetsFeature>().Service.RequireDurableIds);
        Assert.Equal(1u, provider.GetRequiredService<PetsFeature>().Service.NextPetNumber());
        EconomyFeature economy = provider.GetRequiredService<EconomyFeature>();
        Assert.False(economy.Enabled); // set inside Attach, so it cannot be the condition
        Assert.NotNull(economy.MailIdAllocator);
        Assert.NotNull(economy.AuctionIdAllocator);
        Assert.NotNull(economy.ItemTextIdAllocator);
        handOver(); // nothing deferred without social and instance features: a no-op that must not throw
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandOver_SetsRequireDurableIds_OnGroupsGuildsPetitionsInstancesAndEconomy_OnlyInWorkerScope(bool workerScope)
    {
        // Groups, guilds, petitions and instances exist only once their feature attached, so the hand-over action carries the flag; the economy takes it at once.
        // A regression that stops wiring the flag lets a worker fall back to a local counter and hand out an id another worker already used.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IIdRangeReservationStore>(new CountingReservations());
        services.AddSingleton<CharacterDirectory>(); // SocialFeature's constructor takes it, with no default
        services.AddSingleton<ArcaneCore.World.Social.SocialFeature>();
        services.AddSingleton<ArcaneCore.World.Instances.InstanceFeature>();
        services.AddSingleton<EconomyFeature>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();

        Action handOver = await WorldHost.PrepareDurableIdAllocatorsAsync(provider, scopes, blockSize: 10, lowWatermark: 2, requireDurableIds: workerScope);
        Assert.Equal(workerScope, provider.GetRequiredService<EconomyFeature>().RequireDurableIds);

        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance), NullLogger<WorldRuntime>.Instance);
        var social = provider.GetRequiredService<ArcaneCore.World.Social.SocialFeature>();
        var instances = provider.GetRequiredService<ArcaneCore.World.Instances.InstanceFeature>();
        try
        {
            social.Attach(world);
            instances.Attach(world);
            handOver();

            Assert.Equal(workerScope, social.Context.Groups.RequireDurableIds);
            Assert.Equal(workerScope, social.Context.Guilds.RequireDurableIds);
            Assert.Equal(workerScope, social.Context.Petitions.RequireDurableIds);
            Assert.Equal(workerScope, instances.Instances.RequireDurableIds);
            Assert.NotNull(social.Context.Groups.IdBlockAllocator);
            Assert.NotNull(social.Context.Guilds.IdBlockAllocator);
            Assert.NotNull(instances.Instances.IdBlockAllocator);
        }
        finally
        {
            await social.StopAsync();
            await instances.StopAsync();
        }
    }

    [Fact]
    public async Task AWorkerHost_PrimesTheItemIdAllocator_BeforeTheFeaturesAttach_WithRequireDurableIdsSet()
    {
        using var directory = new TempDirectory("arcanecore-workerscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        // The feature takes an item guid inside Attach. With the allocator primed after the attach (or RequireDurableIds left false) this start fails or the flags below differ.
        await using HostKit worker = db.NewWorker(ShardA, "0,1", configure: services => services.AddSingleton<ItemsFeature>());
        await worker.Host.StartAsync(CancellationToken.None);

        Assert.True(worker.Feature.Attached);
        Assert.True(worker.Feature.ItemAllocatorHadBlocksAtAttach, "the item allocator had no durable block when the feature attached");
        Assert.True(worker.Feature.RequireDurableIdsAtAttach, "RequireDurableIds was not set for a Worker-scope host when the feature attached");
        Assert.NotNull(worker.Feature.ItemIdTakenAtAttach);
        ItemsFeature items = worker.Services.GetRequiredService<ItemsFeature>();
        Assert.True(items.GuidAllocator.RequireDurableIds);
        Assert.NotNull(items.GuidAllocator.BlockAllocator);
        Assert.True(items.GuidAllocator.BlockAllocator!.RemainingCount > 0); // the id came out of a reserved block, which is still being drawn from
    }

    [Fact]
    public async Task AWorkerHost_WithoutAnIdReservationStore_RefusesToStart()
    {
        using var directory = new TempDirectory("arcanecore-workerscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit worker = db.NewWorker(ShardA, "0,1", configure: services => services.RemoveAll<IIdRangeReservationStore>());
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.Host.StartAsync(CancellationToken.None));

        Assert.Contains("durable id range reservation store", refused.Message);
        Assert.False(worker.Feature.Attached);
    }

    private static async Task SaveAsync(HostKit host, CharacterState state)
    {
        await using AsyncServiceScope scope = host.Scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICharacterStore>().SaveStateAsync(state);
    }

    private static CharacterState State(int id, uint map, CharacterOwnershipToken? ownership)
        => new(id, map, 1, 1, 2, 3, 0, 1, 1, Ownership: ownership);

    /// <summary>The ownership rejection, found at any depth of the thrown exception (EF may wrap what an interceptor throws).</summary>
    private static async Task AssertRejectedAsync(Func<Task> write)
    {
        Exception? thrown = await Record.ExceptionAsync(write);
        Assert.NotNull(thrown);
        for (Exception? e = thrown; e is not null; e = e.InnerException)
        {
            if (e is CharacterOwnershipRejectedException)
                return;
        }

        Assert.Fail("expected a CharacterOwnershipRejectedException but got " + thrown);
    }

    /// <summary>One scratch SQLite characters file and the hosts that run against it.</summary>
    private sealed class Scratch : IAsyncDisposable
    {
        private readonly string _file;

        private Scratch(string file)
        {
            _file = file;
            Database = Options.Create(new DatabaseOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = $"Data Source={file}" });
        }

        public IOptions<DatabaseOptions> Database { get; }

        public static async Task<Scratch> CreateAsync(TempDirectory directory)
        {
            var scratch = new Scratch(Path.Combine(directory.Path, "characters.db"));
            await using ServiceProvider bootstrap = scratch.BuildServices(fenced: false);
            await bootstrap.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
            return scratch;
        }

        private CharacterDbContext PlainContext()
            => new(new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite($"Data Source={_file}").Options);

        public async Task<int> CreateCharacterAsync(string name)
        {
            await using CharacterDbContext db = PlainContext();
            return (await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = name, Race = 1, Class = 1, Level = 1 })).Id;
        }

        public async Task<uint> MapOfAsync(int id)
        {
            await using CharacterDbContext db = PlainContext();
            return (await new EfCharacterStore(db).GetByIdAsync(id))!.MapId;
        }

        public async Task<CharacterOwnershipRow> RowAsync(string scope)
        {
            await using CharacterDbContext db = PlainContext();
            return await db.Set<CharacterOwnershipRow>().AsNoTracking().SingleAsync(r => r.Scope == scope);
        }

        /// <summary>The shard's last heartbeat (characters schema 75 keeps it in its own table); 0 before the first beat.</summary>
        public async Task<long> HeartbeatAsync(string scope)
        {
            await using CharacterDbContext db = PlainContext();
            return await db.Set<CharacterOwnershipHeartbeatRow>().AsNoTracking().Where(r => r.Scope == scope).Select(r => r.HeartbeatAtMs).SingleOrDefaultAsync();
        }

        public async Task<bool> AnyActiveAsync()
        {
            await using CharacterDbContext db = PlainContext();
            return await db.Set<CharacterOwnershipRow>().AsNoTracking().AnyAsync(r => r.Active);
        }

        public async Task<CharacterOwnershipToken?> CurrentAsync(HostKit host, string scope)
        {
            await using AsyncServiceScope serviceScope = host.Scopes.CreateAsyncScope();
            return await serviceScope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().GetCurrentAsync(scope);
        }

        public HostKit NewWorker(string shard, string ownedMaps, TimeSpan? heartbeat = null, bool allowSqlite = true, ClusterNodeRole role = ClusterNodeRole.MapWorker, Action<IServiceCollection>? configure = null)
        {
            var fencing = new CharacterFencingOptions
            {
                Enabled = true,
                OwnerNode = "node-" + shard,
                Scope = CharacterFencingScope.Worker,
                Shard = shard,
                AllowSqliteWorkerScope = allowSqlite,
            };
            var worker = new GatewayWorkerOptions { Enabled = true, NodeId = "node-" + shard, OwnedMaps = ownedMaps };
            return HostKit.Create(BuildServices(fenced: true, configure), Database, fencing, worker, new ClusterNodeOptions { Role = role }, heartbeat);
        }

        public HostKit NewCombined()
            => HostKit.Create(BuildServices(fenced: true), Database, new CharacterFencingOptions { Enabled = true, OwnerNode = "combined" }, null, null, null);

        private ServiceProvider BuildServices(bool fenced, Action<IServiceCollection>? configure = null)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = $"Data Source={_file}",
            };
            if (fenced)
                settings[DataServiceCollectionExtensions.FencingEnabledKey] = "true";
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCharacterDatabase(configuration);
            services.AddSingleton<IWorldFeature, Feature>();
            configure?.Invoke(services);
            return services.BuildServiceProvider();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>One world host with its own container (so its own connections), writer guard and ownership context.</summary>
    private sealed class HostKit : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private HostKit(ServiceProvider services, WorldRuntime world, WorldHost host, WorldWriterGuard guard, Feature feature, CharacterSaveQueue saveQueue)
        {
            _services = services;
            SaveQueue = saveQueue;
            World = world;
            Host = host;
            Guard = guard;
            Feature = feature;
            Scopes = services.GetRequiredService<IServiceScopeFactory>();
        }

        public WorldRuntime World { get; }
        public CharacterSaveQueue SaveQueue { get; }
        public WorldHost Host { get; }
        public WorldWriterGuard Guard { get; }
        public Feature Feature { get; }
        public IServiceScopeFactory Scopes { get; }
        public IServiceProvider Services => _services;

        public static HostKit Create(ServiceProvider services, IOptions<DatabaseOptions> database, CharacterFencingOptions fencing, GatewayWorkerOptions? worker, ClusterNodeOptions? node, TimeSpan? heartbeat)
        {
            IServiceScopeFactory scopes = services.GetRequiredService<IServiceScopeFactory>();
            var saveQueue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
            var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saveQueue, NullLogger<WorldRuntime>.Instance);
            IOptions<CharacterFencingOptions> fencingOptions = Options.Create(fencing);
            IOptions<GatewayWorkerOptions>? workerOptions = worker is null ? null : Options.Create(worker);
            var guard = new WorldWriterGuard(database, fencingOptions: fencingOptions, workerOptions: workerOptions);
            var host = new WorldHost(world, saveQueue, new CharacterDirectory(), scopes, NullLogger<WorldHost>.Instance, fencingOptions,
                ownershipContext: services.GetRequiredService<CharacterOwnershipContext>(), writerGuard: guard, workerOptions: workerOptions,
                nodeOptions: node is null ? null : Options.Create(node)) { ListenerExpected = false, HeartbeatInterval = heartbeat };
            return new HostKit(services, world, host, guard, (Feature)services.GetServices<IWorldFeature>().Single(), saveQueue);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Host.StopAsync(CancellationToken.None); }
            catch (Exception) { } // a refused or stale host may fail its orderly stop; the proof is in the assertions
            Guard.Dispose();
            World.Dispose();
            await _services.DisposeAsync();
        }
    }

    /// <summary>Records what the item id allocator looked like at the moment the host attached it, and takes an item guid there when an <see cref="ItemsFeature"/> is registered.</summary>
    private sealed class Feature(IServiceProvider services) : IWorldFeature
    {
        public bool Attached { get; private set; }
        public bool ItemAllocatorHadBlocksAtAttach { get; private set; }
        public bool RequireDurableIdsAtAttach { get; private set; }
        public uint? ItemIdTakenAtAttach { get; private set; }

        public void Attach(WorldRuntime world)
        {
            Attached = true;
            if (services.GetService<ItemsFeature>() is { } items)
            {
                ItemAllocatorHadBlocksAtAttach = items.GuidAllocator.BlockAllocator is not null;
                RequireDurableIdsAtAttach = items.GuidAllocator.RequireDurableIds;
                ItemIdTakenAtAttach = items.GuidAllocator.Next(); // throws under RequireDurableIds when no block allocator is configured
            }
        }
    }

    /// <summary>Hands out consecutive blocks per scope, in memory.</summary>
    private sealed class CountingReservations : IIdRangeReservationStore
    {
        private readonly ConcurrentDictionary<string, uint> _next = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public Task<IdRangeBlock> ReserveBlockAsync(string scope, uint count, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                uint start = _next.GetOrAdd(scope, IdRangeScopes.GetMinFirstId);
                _next[scope] = start + count;
                return Task.FromResult(new IdRangeBlock(scope, start, start + count - 1u));
            }
        }
    }
}

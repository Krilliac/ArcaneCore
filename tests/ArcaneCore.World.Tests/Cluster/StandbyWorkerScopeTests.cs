using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>
/// Step C7 of the per-worker character writer lock (_wf/writer-lock-DESIGN.md): a standby world in <c>Cluster:Fencing:Scope=Worker</c> takes over one worker shard
/// (<c>worker:&lt;shard&gt;</c>) by a handoff. Several in-process hosts share one SQLite characters file (allowed only through <c>AllowSqliteWorkerScope</c>), each with its own
/// container and writer guard. The shard sidecar locks, the durable ownership rows and the handoff journal are the real ones. SQLite-only; the MariaDB and PostgreSQL behaviour of
/// the same paths is the integration gate's <c>-WithDbProviders</c> run.
/// </summary>
public sealed class StandbyWorkerScopeTests
{
    private const string ShardA = "shard-a";
    private const string ShardB = "shard-b";

    [Fact]
    public async Task APromotedWorkerStandby_TakesTheShardLock_ChecksTheRealmLock_ReadsTheToken_AndStartsTheHeartbeat()
    {
        using var directory = new TempDirectory("arcanecore-standbyscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit active = db.NewActive(ShardA, "0,1");
        await active.Host.StartAsync(CancellationToken.None);
        await using HostKit standby = db.NewStandby(ShardA, "0,1,2", heartbeat: TimeSpan.FromMilliseconds(40));
        await standby.Host.StartAsync(CancellationToken.None);

        Assert.True(standby.Host.IsStandby);
        Assert.Equal("worker:" + ShardA, standby.Host.OwnershipScope);
        Assert.Null(standby.Guard.LockPath); // a standby holds nothing until it is promoted
        CharacterOwnershipToken oldToken = active.World.CharacterOwnership!;

        Assert.Equal("0,1", (await db.RowAsync(oldToken.Scope)).OwnedMaps);

        // The real handoff (journal, then IWorldHandoffStore.CommitEpochTakeoverAsync with the standby's configured maps, as WorldHandoffCoordinator calls it) hands the shard row to the standby's incarnation.
        CharacterOwnershipToken takenOver = await db.HandoffAsync(active, standby);
        Assert.Equal(oldToken.Epoch + 1, takenOver.Epoch);
        Assert.Equal(oldToken.Scope, takenOver.Scope);

        // The old world still holds the shard lock: the promotion is refused and the standby stays a standby, retryable.
        InvalidOperationException held = await Assert.ThrowsAsync<InvalidOperationException>(() => standby.Host.PromoteAsync(takenOver));
        Assert.Contains("writer lock is held elsewhere", held.Message);
        Assert.True(standby.Host.IsStandby);
        Assert.False(standby.Guard.HoldsLock);

        active.Guard.Dispose(); // what LocalWorldHandoffSource.ReleaseWriterLockAsync does after the takeover
        await standby.Host.PromoteAsync(takenOver);

        Assert.False(standby.Host.IsStandby);
        Assert.True(standby.Guard.IsWorkerScope);
        Assert.True(standby.Guard.HoldsLock);
        Assert.EndsWith(".lock", standby.Guard.LockPath);
        Assert.DoesNotContain(".world.lock", standby.Guard.LockPath, StringComparison.Ordinal); // the shard sidecar, never the realm's
        Assert.Equal(takenOver, standby.World.CharacterOwnership);
        Assert.Equal(takenOver, await db.CurrentAsync(standby, takenOver.Scope));
        Assert.True(standby.Feature.Attached);

        // The handoff's takeover wrote the new owner's maps: the handed-over shard does not keep the old owner's set.
        CharacterOwnershipRow row = await db.RowAsync(takenOver.Scope);
        Assert.Equal("0,1,2", row.OwnedMaps);
        Assert.Equal(standby.Host.OwnerNode, row.OwnerNode);

        long first = row.HeartbeatAtMs ?? 0;
        Assert.True(first > 0);
        long latest = first;
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (latest <= first && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            latest = (await db.RowAsync(takenOver.Scope)).HeartbeatAtMs ?? 0;
        }

        Assert.True(latest > first, "the promoted worker's heartbeat never advanced");
    }

    [Fact]
    public async Task AHandoffTakeover_WhoseStandbyMapsOverlapAnotherActiveShard_IsRefused_AndChangesNothing()
    {
        using var directory = new TempDirectory("arcanecore-standbyscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit activeA = db.NewActive(ShardA, "0,1");
        await using HostKit activeB = db.NewActive(ShardB, "30,31");
        await activeA.Host.StartAsync(CancellationToken.None);
        await activeB.Host.StartAsync(CancellationToken.None);
        await using HostKit standby = db.NewStandby(ShardA, "0,1,30"); // map 30 belongs to the other active shard
        await standby.Host.StartAsync(CancellationToken.None);
        CharacterOwnershipToken before = activeA.World.CharacterOwnership!;

        IWorldHandoffStore store = activeA.Services.GetRequiredService<IWorldHandoffStore>();
        var id = Guid.NewGuid();
        await store.BeginAsync(id, new WorkerNodeId("node-" + ShardA), new WorkerNodeId("standby-" + ShardA), before);
        foreach (WorldHandoffStage stage in new[] { WorldHandoffStage.Warm, WorldHandoffStage.AdmissionsStopped, WorldHandoffStage.Frozen, WorldHandoffStage.Flushed })
            await store.AdvancePreCommitAsync(id, stage);

        Assert.Null(await store.CommitEpochTakeoverAsync(id, standby.Host.OwnerNode, standby.Host.Incarnation, standby.Host.WorkerOwnedMaps, CancellationToken.None));

        CharacterOwnershipRow row = await db.RowAsync(before.Scope);
        Assert.Equal("0,1", row.OwnedMaps);
        Assert.Equal(before, await db.CurrentAsync(activeA, before.Scope));
        WorldHandoffRecord record = (await store.GetAsync(id))!;
        Assert.False(record.IsCommittedOrBeyond); // still abortable
        await store.AbortAsync(id, "test");
    }

    [Fact]
    public async Task AWorkerStandby_IsRefused_WhileACombinedWorldHoldsTheRealmLock_AndPromotesOnceItIsFree()
    {
        using var directory = new TempDirectory("arcanecore-standbyscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit active = db.NewActive(ShardA, "0,1");
        await active.Host.StartAsync(CancellationToken.None);
        await using HostKit standby = db.NewStandby(ShardA, "0,1");
        await standby.Host.StartAsync(CancellationToken.None);
        CharacterOwnershipToken takenOver = await db.HandoffAsync(active, standby);
        active.Guard.Dispose();

        // An unfenced Combined world takes the realm lock only; the shard lock is taken first and then the realm lock is probed, so the promotion backs out holding nothing.
        CharacterDatabaseWriterLock realm = CharacterDatabaseWriterLock.TryAcquire(db.Database.Value.Resolve(DatabaseComponent.Characters))!;
        Assert.NotNull(realm);
        try
        {
            InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => standby.Host.PromoteAsync(takenOver));
            Assert.Contains("realm writer lock", refused.Message);
            Assert.True(standby.Host.IsStandby);
            Assert.False(standby.Guard.HoldsLock);
            Assert.False(standby.Feature.Attached);
        }
        finally
        {
            realm.Dispose();
        }

        await standby.Host.PromoteAsync(takenOver);
        Assert.False(standby.Host.IsStandby);
        Assert.True(standby.Guard.HoldsLock);
    }

    [Fact]
    public async Task AWorkerScopeConfigurationFailure_AtPromotion_IsWrappedLikeTheOtherStandbyFailures()
    {
        using var directory = new TempDirectory("arcanecore-standbyscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        // SQLite without AllowSqliteWorkerScope: the shard lock cannot be taken, and that is a configuration failure of the worker scope.
        await using HostKit standby = db.NewStandby(ShardA, "0,1", allowSqlite: false);
        await standby.Host.StartAsync(CancellationToken.None);
        var token = new CharacterOwnershipToken(standby.Host.OwnershipScope, 2, standby.Host.Incarnation);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => standby.Host.PromoteAsync(token));

        Assert.IsNotType<WorkerScopeConfigurationException>(refused);
        Assert.IsType<WorkerScopeConfigurationException>(refused.InnerException);
        Assert.Contains("the standby stays a standby", refused.Message);
        Assert.True(standby.Host.IsStandby);
        Assert.False(standby.Guard.HoldsLock);
    }

    [Fact]
    public async Task AWorkerStandby_NeedsAnExplicitShard()
    {
        using var directory = new TempDirectory("arcanecore-standbyscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit standby = db.NewStandby(shard: null, "0,1");

        WorkerScopeConfigurationException refused = await Assert.ThrowsAsync<WorkerScopeConfigurationException>(() => standby.Host.StartAsync(CancellationToken.None));
        Assert.Contains("Cluster:Fencing:Shard", refused.Message);
    }

    [Fact]
    public async Task TwoShards_HandOffAtTheSameTime_EachUnderItsOwnScope_AndTheSameShardTwiceIsRefused()
    {
        using var directory = new TempDirectory("arcanecore-standbyscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit activeA = db.NewActive(ShardA, "0,1");
        await using HostKit activeB = db.NewActive(ShardB, "30,31");
        await activeA.Host.StartAsync(CancellationToken.None);
        await activeB.Host.StartAsync(CancellationToken.None);
        await using HostKit standbyA = db.NewStandby(ShardA, "0,1");
        await standbyA.Host.StartAsync(CancellationToken.None);
        IWorldHandoffStore store = activeA.Services.GetRequiredService<IWorldHandoffStore>();
        CharacterOwnershipToken tokenA = activeA.World.CharacterOwnership!;
        CharacterOwnershipToken tokenB = activeB.World.CharacterOwnership!;

        var handoffA = Guid.NewGuid();
        var handoffB = Guid.NewGuid();
        await store.BeginAsync(handoffA, new WorkerNodeId("node-" + ShardA), new WorkerNodeId("standby-a"), tokenA);
        await store.BeginAsync(handoffB, new WorkerNodeId("node-" + ShardB), new WorkerNodeId("standby-b"), tokenB); // a second open handoff, of another scope
        Assert.Equal(2, (await store.GetUnresolvedAsync()).Count);

        // The same scope twice is refused, whichever handoff id is used.
        InvalidOperationException twice = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.BeginAsync(Guid.NewGuid(), new WorkerNodeId("node-" + ShardA), new WorkerNodeId("standby-c"), tokenA));
        Assert.Contains(tokenA.Scope, twice.Message);

        foreach (Guid id in new[] { handoffA, handoffB })
        {
            foreach (WorldHandoffStage stage in new[] { WorldHandoffStage.Warm, WorldHandoffStage.AdmissionsStopped, WorldHandoffStage.Frozen, WorldHandoffStage.Flushed })
                await store.AdvancePreCommitAsync(id, stage);
        }

        // Shard A commits to its standby; shard B's handoff is still open and its ownership untouched.
        WorldHandoffRecord committed = (await store.CommitEpochTakeoverAsync(handoffA, standbyA.Host.OwnerNode, standbyA.Host.Incarnation, standbyA.Host.WorkerOwnedMaps, CancellationToken.None))!;
        CharacterOwnershipToken takenOver = committed.TargetOwnershipToken!;
        Assert.Equal(tokenA.Scope, takenOver.Scope);
        Assert.Equal(tokenA.Epoch + 1, takenOver.Epoch);
        Assert.Equal(tokenB, await db.CurrentAsync(activeB, tokenB.Scope));
        Assert.Equal(takenOver, await db.CurrentAsync(activeA, tokenA.Scope));

        activeA.Guard.Dispose();
        await standbyA.Host.PromoteAsync(takenOver);
        Assert.Equal(takenOver, standbyA.World.CharacterOwnership);
        await store.MarkReroutedAsync(handoffA);
        await store.MarkRetiredAsync(handoffA);

        // Shard B is still its own handoff: abort it, and a new one of that scope opens, while shard A (resolved) can hand off again too.
        await store.AbortAsync(handoffB, "test");
        await store.BeginAsync(Guid.NewGuid(), new WorkerNodeId("node-" + ShardB), new WorkerNodeId("standby-b"), tokenB);
        await store.BeginAsync(Guid.NewGuid(), new WorkerNodeId("standby-a"), new WorkerNodeId("standby-a2"), takenOver);
        Assert.Equal(2, (await store.GetUnresolvedAsync()).Count);
    }

    [Fact]
    public async Task TheRealmModeHandoff_IsUnchanged_OneOpenHandoffAtATime_OnTheWorldScope()
    {
        using var directory = new TempDirectory("arcanecore-standbyscope-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit combined = db.NewCombined();
        await combined.Host.StartAsync(CancellationToken.None);
        IWorldHandoffStore store = combined.Services.GetRequiredService<IWorldHandoffStore>();
        CharacterOwnershipToken world = combined.World.CharacterOwnership!;
        Assert.Equal("world", world.Scope);

        var first = Guid.NewGuid();
        await store.BeginAsync(first, new WorkerNodeId("a"), new WorkerNodeId("b"), world);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginAsync(Guid.NewGuid(), new WorkerNodeId("a"), new WorkerNodeId("c"), world));

        await store.AbortAsync(first, "test");
        await store.BeginAsync(Guid.NewGuid(), new WorkerNodeId("a"), new WorkerNodeId("c"), world);
        Assert.Single(await store.GetUnresolvedAsync());
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

        public async Task<CharacterOwnershipRow> RowAsync(string scope)
        {
            await using CharacterDbContext db = PlainContext();
            return await db.Set<CharacterOwnershipRow>().AsNoTracking().SingleAsync(r => r.Scope == scope);
        }

        public async Task<CharacterOwnershipToken?> CurrentAsync(HostKit host, string scope)
        {
            await using AsyncServiceScope serviceScope = host.Scopes.CreateAsyncScope();
            return await serviceScope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().GetCurrentAsync(scope);
        }

        /// <summary>
        /// The production handoff path up to the epoch takeover: journal a handoff of <paramref name="source"/>'s shard to <paramref name="standby"/>, advance it to Flushed, and commit
        /// the takeover with the standby's configured maps (what <c>WorldHandoffCoordinator</c> does through <c>IWorldHandoffTarget.WorkerOwnedMaps</c>). Returns the taken-over token.
        /// </summary>
        public async Task<CharacterOwnershipToken> HandoffAsync(HostKit source, HostKit standby)
        {
            IWorldHandoffStore store = source.Services.GetRequiredService<IWorldHandoffStore>();
            var id = Guid.NewGuid();
            await store.BeginAsync(id, new WorkerNodeId(source.Host.OwnerNode), new WorkerNodeId(standby.Host.OwnerNode), source.World.CharacterOwnership!);
            foreach (WorldHandoffStage stage in new[] { WorldHandoffStage.Warm, WorldHandoffStage.AdmissionsStopped, WorldHandoffStage.Frozen, WorldHandoffStage.Flushed })
                await store.AdvancePreCommitAsync(id, stage);
            WorldHandoffRecord committed = (await store.CommitEpochTakeoverAsync(id, standby.Host.OwnerNode, standby.Host.Incarnation, standby.Host.WorkerOwnedMaps, CancellationToken.None))!;
            return committed.TargetOwnershipToken!;
        }

        public HostKit NewActive(string shard, string ownedMaps)
            => NewHost(shard, ownedMaps, standby: false, heartbeat: null, allowSqlite: true, explicitShard: true);

        public HostKit NewStandby(string? shard, string ownedMaps, TimeSpan? heartbeat = null, bool allowSqlite = true)
            => NewHost(shard ?? "unset", ownedMaps, standby: true, heartbeat, allowSqlite, explicitShard: shard is not null);

        private HostKit NewHost(string shard, string ownedMaps, bool standby, TimeSpan? heartbeat, bool allowSqlite, bool explicitShard)
        {
            // The standby's node id differs from the active worker's; it takes the active worker's shard through the explicit Shard key.
            string node = (standby ? "standby-" : "node-") + shard;
            var fencing = new CharacterFencingOptions
            {
                Enabled = true,
                OwnerNode = node,
                Scope = CharacterFencingScope.Worker,
                Shard = explicitShard ? shard : null,
                AllowSqliteWorkerScope = allowSqlite,
            };
            var worker = new GatewayWorkerOptions { Enabled = true, Standby = standby, NodeId = node, OwnedMaps = ownedMaps };
            return HostKit.Create(BuildServices(fenced: true), Database, fencing, worker, new ClusterNodeOptions { Role = ClusterNodeRole.MapWorker }, heartbeat, standby);
        }

        public HostKit NewCombined()
            => HostKit.Create(BuildServices(fenced: true), Database, new CharacterFencingOptions { Enabled = true, OwnerNode = "combined" }, null, null, null, standby: false);

        private ServiceProvider BuildServices(bool fenced)
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
            return services.BuildServiceProvider();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>One world host with its own container (so its own connections), writer guard and ownership context.</summary>
    private sealed class HostKit : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private HostKit(ServiceProvider services, WorldRuntime world, WorldHost host, WorldWriterGuard guard, Feature feature)
        {
            _services = services;
            World = world;
            Host = host;
            Guard = guard;
            Feature = feature;
            Scopes = services.GetRequiredService<IServiceScopeFactory>();
        }

        public WorldRuntime World { get; }
        public WorldHost Host { get; }
        public WorldWriterGuard Guard { get; }
        public Feature Feature { get; }
        public IServiceScopeFactory Scopes { get; }
        public IServiceProvider Services => _services;

        public static HostKit Create(ServiceProvider services, IOptions<DatabaseOptions> database, CharacterFencingOptions fencing, GatewayWorkerOptions? worker, ClusterNodeOptions? node, TimeSpan? heartbeat, bool standby)
        {
            IServiceScopeFactory scopes = services.GetRequiredService<IServiceScopeFactory>();
            var saveQueue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
            var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saveQueue, NullLogger<WorldRuntime>.Instance);
            IOptions<CharacterFencingOptions> fencingOptions = Options.Create(fencing);
            IOptions<GatewayWorkerOptions>? workerOptions = worker is null ? null : Options.Create(worker);
            var guard = new WorldWriterGuard(database, deferUntilPromotion: standby, fencingOptions: fencingOptions, workerOptions: workerOptions);
            var host = new WorldHost(world, saveQueue, new CharacterDirectory(), scopes, NullLogger<WorldHost>.Instance, fencingOptions,
                ownershipContext: services.GetRequiredService<CharacterOwnershipContext>(), writerGuard: guard, workerOptions: workerOptions,
                nodeOptions: node is null ? null : Options.Create(node)) { ListenerExpected = false, HeartbeatInterval = heartbeat };
            return new HostKit(services, world, host, guard, (Feature)services.GetServices<IWorldFeature>().Single());
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

    private sealed class Feature : IWorldFeature
    {
        public bool Attached { get; private set; }

        public void Attach(WorldRuntime world) => Attached = true;
    }
}

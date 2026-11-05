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

namespace ArcaneCore.World.Tests.Cluster.Handoff;

/// <summary>
/// Commit C12 of the handoff control design (_wf/handoff-control-DESIGN.md section 8): the worker-scope reclaim guard. A relaunched copy of a worker that died between a handoff's epoch takeover
/// and the promotion of its standby holds the shard lock (the dead process released it) and sees a row under another incarnation, which the reclaim path reads as "a dead owner". While the
/// committed handoff is unresolved that row belongs to the handoff's target, so the relaunch must be refused; every other reclaim stays as before. Several in-process hosts share one SQLite
/// characters file (allowed only through <c>AllowSqliteWorkerScope</c>); the shard sidecar locks, the ownership rows and the handoff journal are the real ones.
/// </summary>
public sealed class HandoffReclaimGuardTests
{
    private const string Shard = "shard-a";

    [Fact]
    public async Task ARelaunchedSource_BetweenCommitAndPromotion_CannotReclaimTheShard()
    {
        using var directory = new TempDirectory("arcanecore-reclaimguard-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit active = db.NewActive(Shard, "0,1");
        await active.Host.StartAsync(CancellationToken.None);
        await using HostKit standby = db.NewStandby(Shard, "0,1");
        CharacterOwnershipToken sourceToken = active.World.CharacterOwnership!;
        (Guid handoffId, CharacterOwnershipToken takenOver) = await db.CommitHandoffAsync(active, standby);

        active.Guard.Dispose(); // the source process dies after the commit: its shard lock is gone

        await using HostKit relaunched = db.NewActive(Shard, "0,1");
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => relaunched.Host.StartAsync(CancellationToken.None));

        Assert.Contains(handoffId.ToString(), refused.Message);
        Assert.Contains("has not been promoted yet", refused.Message);
        Assert.False(relaunched.Feature.Attached);
        Assert.Equal(takenOver, await db.CurrentAsync(relaunched, sourceToken.Scope)); // the row still names the handoff's target, untouched
        Assert.Equal(sourceToken.Epoch + 1, takenOver.Epoch);
        Assert.Equal(standby.Host.Incarnation, takenOver.Incarnation);
    }

    [Fact]
    public async Task TheStandby_StillPromotes_WhileTheCommittedHandoffIsUnresolved()
    {
        using var directory = new TempDirectory("arcanecore-reclaimguard-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit active = db.NewActive(Shard, "0,1");
        await active.Host.StartAsync(CancellationToken.None);
        await using HostKit standby = db.NewStandby(Shard, "0,1");
        await standby.Host.StartAsync(CancellationToken.None);
        (_, CharacterOwnershipToken takenOver) = await db.CommitHandoffAsync(active, standby);
        active.Guard.Dispose();

        await standby.Host.PromoteAsync(takenOver); // the guard sits on the reclaim path only; the takeover target never goes through it

        Assert.False(standby.Host.IsStandby);
        Assert.Equal(takenOver, standby.World.CharacterOwnership);
    }

    [Fact]
    public async Task OnceTheHandoffIsResolved_ARelaunchedWorkerReclaimsAsBefore()
    {
        using var directory = new TempDirectory("arcanecore-reclaimguard-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit active = db.NewActive(Shard, "0,1");
        await active.Host.StartAsync(CancellationToken.None);
        await using HostKit standby = db.NewStandby(Shard, "0,1");
        (Guid handoffId, CharacterOwnershipToken takenOver) = await db.CommitHandoffAsync(active, standby);
        active.Guard.Dispose();

        IWorldHandoffStore store = active.Services.GetRequiredService<IWorldHandoffStore>();
        await store.MarkReroutedAsync(handoffId);
        await store.MarkRetiredAsync(handoffId); // recovery finished the handoff (the standby then died, so the row is a dead incarnation's)

        await using HostKit relaunched = db.NewActive(Shard, "0,1");
        await relaunched.Host.StartAsync(CancellationToken.None);

        CharacterOwnershipToken reclaimed = relaunched.World.CharacterOwnership!;
        Assert.Equal(takenOver.Epoch + 1, reclaimed.Epoch);
        Assert.Equal(relaunched.Host.Incarnation, reclaimed.Incarnation);
    }

    [Fact]
    public async Task APreCommitOrAbortedHandoff_DoesNotBlockTheReclaim()
    {
        using var directory = new TempDirectory("arcanecore-reclaimguard-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit active = db.NewActive(Shard, "0,1");
        await active.Host.StartAsync(CancellationToken.None);
        CharacterOwnershipToken owned = active.World.CharacterOwnership!;

        // A handoff that never reached the takeover leaves the row alone: it is unresolved but still PreCommit.
        IWorldHandoffStore store = active.Services.GetRequiredService<IWorldHandoffStore>();
        var preCommit = Guid.NewGuid();
        await store.BeginAsync(preCommit, new WorkerNodeId(active.Host.OwnerNode), new WorkerNodeId("standby-" + Shard), owned);
        await store.AdvancePreCommitAsync(preCommit, WorldHandoffStage.Warm);
        active.Guard.Dispose();

        await using HostKit first = db.NewActive(Shard, "0,1");
        await first.Host.StartAsync(CancellationToken.None);
        Assert.Equal(owned.Epoch + 1, first.World.CharacterOwnership!.Epoch);

        await store.AbortAsync(preCommit, "test");
        first.Guard.Dispose();

        await using HostKit second = db.NewActive(Shard, "0,1");
        await second.Host.StartAsync(CancellationToken.None);
        Assert.Equal(owned.Epoch + 2, second.World.CharacterOwnership!.Epoch);
    }

    [Fact]
    public async Task ANormalReclaim_WithNoHandoffAtAll_IsUnchanged()
    {
        using var directory = new TempDirectory("arcanecore-reclaimguard-", sqlite: true);
        await using Scratch db = await Scratch.CreateAsync(directory);

        await using HostKit old = db.NewActive(Shard, "0,1");
        await old.Host.StartAsync(CancellationToken.None);
        CharacterOwnershipToken oldToken = old.World.CharacterOwnership!;
        old.Guard.Dispose(); // a hard kill

        await using HostKit restarted = db.NewActive(Shard, "0,1");
        await restarted.Host.StartAsync(CancellationToken.None);

        Assert.Equal(oldToken.Epoch + 1, restarted.World.CharacterOwnership!.Epoch);
        Assert.Equal(restarted.Host.Incarnation, restarted.World.CharacterOwnership.Incarnation);
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

        public async Task<CharacterOwnershipToken?> CurrentAsync(HostKit host, string scope)
        {
            await using AsyncServiceScope serviceScope = host.Scopes.CreateAsyncScope();
            return await serviceScope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().GetCurrentAsync(scope);
        }

        /// <summary>The production handoff path up to the epoch takeover: journal a handoff of the source's shard to the standby, advance it to Flushed and commit the takeover with the standby's maps.</summary>
        public async Task<(Guid HandoffId, CharacterOwnershipToken TakenOver)> CommitHandoffAsync(HostKit source, HostKit standby)
        {
            IWorldHandoffStore store = source.Services.GetRequiredService<IWorldHandoffStore>();
            var id = Guid.NewGuid();
            await store.BeginAsync(id, new WorkerNodeId(source.Host.OwnerNode), new WorkerNodeId(standby.Host.OwnerNode), source.World.CharacterOwnership!);
            foreach (WorldHandoffStage stage in new[] { WorldHandoffStage.Warm, WorldHandoffStage.AdmissionsStopped, WorldHandoffStage.Frozen, WorldHandoffStage.Flushed })
                await store.AdvancePreCommitAsync(id, stage);
            WorldHandoffRecord committed = (await store.CommitEpochTakeoverAsync(id, standby.Host.OwnerNode, standby.Host.Incarnation, standby.Host.WorkerOwnedMaps, CancellationToken.None))!;
            return (id, committed.TargetOwnershipToken!);
        }

        public HostKit NewActive(string shard, string ownedMaps)
            => NewHost(shard, ownedMaps, standby: false);

        public HostKit NewStandby(string shard, string ownedMaps)
            => NewHost(shard, ownedMaps, standby: true);

        private HostKit NewHost(string shard, string ownedMaps, bool standby)
        {
            // Every active copy of the shard shares one node id (a relaunch); the standby has its own and takes the active worker's shard through the explicit Shard key.
            string node = (standby ? "standby-" : "node-") + shard;
            var fencing = new CharacterFencingOptions
            {
                Enabled = true,
                OwnerNode = node,
                Scope = CharacterFencingScope.Worker,
                Shard = shard,
                AllowSqliteWorkerScope = true,
            };
            var worker = new GatewayWorkerOptions { Enabled = true, Standby = standby, NodeId = node, OwnedMaps = ownedMaps };
            return HostKit.Create(BuildServices(fenced: true), Database, fencing, worker, new ClusterNodeOptions { Role = ClusterNodeRole.MapWorker }, standby);
        }

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

        public static HostKit Create(ServiceProvider services, IOptions<DatabaseOptions> database, CharacterFencingOptions fencing, GatewayWorkerOptions? worker, ClusterNodeOptions? node, bool standby)
        {
            IServiceScopeFactory scopes = services.GetRequiredService<IServiceScopeFactory>();
            var saveQueue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
            var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saveQueue, NullLogger<WorldRuntime>.Instance);
            IOptions<CharacterFencingOptions> fencingOptions = Options.Create(fencing);
            IOptions<GatewayWorkerOptions>? workerOptions = worker is null ? null : Options.Create(worker);
            var guard = new WorldWriterGuard(database, deferUntilPromotion: standby, fencingOptions: fencingOptions, workerOptions: workerOptions);
            var host = new WorldHost(world, saveQueue, new CharacterDirectory(), scopes, NullLogger<WorldHost>.Instance, fencingOptions,
                ownershipContext: services.GetRequiredService<CharacterOwnershipContext>(), writerGuard: guard, workerOptions: workerOptions,
                nodeOptions: node is null ? null : Options.Create(node)) { ListenerExpected = false };
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

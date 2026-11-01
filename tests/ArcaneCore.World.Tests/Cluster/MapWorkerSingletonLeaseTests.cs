using ArcaneCore.World.Persistence;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Clustering;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Features;
using ArcaneCore.World.Instances;
using ArcaneCore.World.ServerMail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>
/// The wiring of a map worker: <see cref="WorldHost"/> takes the realm singleton leases from the characters database before it attaches features,
/// attaches the auction bot and AutoBroadcast only for a lease it holds, and gives the leases back at stop. Combined touches no lease row.
/// </summary>
public sealed class MapWorkerSingletonLeaseTests
{
    private const string Node = "worker-1";
    private const uint RaidMap = 409;

    /// <summary>2026-10-01 00:00:00 UTC: the instance clock of the schedule tests (the keeper has its own).</summary>
    private static readonly DateTimeOffset ScheduleStart = DateTimeOffset.FromUnixTimeSeconds(1_790_812_800);

    private static readonly MapContent RaidContent = new(
        [new MapTemplate(RaidMap, 0, MapType.Raid, 0, 40, 7, 0, -7510f, -1036f, "Molten Core", "")], [], [], [], []);

    [Fact]
    public async Task MapWorker_WithEconomyLeaseHeldByAnotherNode_DoesNotAttachAuctionBot_ButAttachesWorldFeatures()
    {
        using var directory = new TempDirectory("arcanecore-leases-", sqlite: true);
        string database = Path.Combine(directory.Path, "characters.db");
        await EnsureSchemaAsync(database);
        await using Kit kit = Kit.Create(database, ClusterNodeRole.MapWorker);
        kit.Store.TryAcquire(RealmAuthorityScopes.Economy, "other-node", Guid.NewGuid(), TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow);
        // A crashed previous incarnation of this node still holds the social lease for a second: the start waits it out.
        kit.Store.TryAcquire(RealmAuthorityScopes.Social, Node, Guid.NewGuid(), TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow);

        await kit.Host.StartAsync(CancellationToken.None);

        string[] attached = [.. kit.Features.List().Select(f => f.Name)];
        Assert.Contains(nameof(OrdinaryFeature), attached);
        Assert.Contains(nameof(AutoBroadcastFeature), attached);
        Assert.DoesNotContain(nameof(AuctionBotFeature), attached);
        Assert.True(kit.Ordinary.Attached);

        RealmAuthorityLeaseRow social = await ReadAsync(database, RealmAuthorityScopes.Social);
        RealmAuthorityLeaseRow economy = await ReadAsync(database, RealmAuthorityScopes.Economy);
        Assert.True(social.Active);
        Assert.Equal(Node, social.OwnerNode);
        Assert.Equal(2, social.FencingToken); // above the dead incarnation's token 1
        Assert.Equal("other-node", economy.OwnerNode); // untouched

        await kit.Host.StopAsync(CancellationToken.None);

        Assert.True(kit.Ordinary.Stopped);
        social = await ReadAsync(database, RealmAuthorityScopes.Social);
        economy = await ReadAsync(database, RealmAuthorityScopes.Economy);
        Assert.False(social.Active);
        Assert.Null(social.OwnerNode);
        Assert.Equal(2, social.FencingToken); // the high-water mark stays
        Assert.True(economy.Active);
        Assert.Equal("other-node", economy.OwnerNode); // another node's lease is not ours to release
    }

    [Fact]
    public async Task MapWorker_WithFreeLeases_AttachesBothSingletons_AndReleasesOnStop()
    {
        using var directory = new TempDirectory("arcanecore-leases-", sqlite: true);
        string database = Path.Combine(directory.Path, "characters.db");
        await EnsureSchemaAsync(database);
        await using Kit kit = Kit.Create(database, ClusterNodeRole.MapWorker);

        await kit.Host.StartAsync(CancellationToken.None);

        string[] attached = [.. kit.Features.List().Select(f => f.Name)];
        Assert.Contains(nameof(AutoBroadcastFeature), attached);
        Assert.Contains(nameof(AuctionBotFeature), attached);
        Assert.Contains(nameof(OrdinaryFeature), attached);
        foreach (string scope in new[] { RealmAuthorityScopes.Social, RealmAuthorityScopes.Economy, RealmAuthorityScopes.Instances })
        {
            RealmAuthorityLeaseRow held = await ReadAsync(database, scope);
            Assert.True(held.Active);
            Assert.Equal(Node, held.OwnerNode);
            Assert.Equal(1, held.FencingToken);
        }

        await kit.Host.StopAsync(CancellationToken.None);

        foreach (string scope in new[] { RealmAuthorityScopes.Social, RealmAuthorityScopes.Economy, RealmAuthorityScopes.Instances })
        {
            RealmAuthorityLeaseRow released = await ReadAsync(database, scope);
            Assert.False(released.Active);
            Assert.Null(released.OwnerNode);
            Assert.Equal(1, released.FencingToken);
        }

    }

    [Fact]
    public async Task MapWorker_WhoseAfterStartWorkThrows_KeepsTheLeasesHeld_UntilItsFeaturesHaveStopped()
    {
        using var directory = new TempDirectory("arcanecore-leases-", sqlite: true);
        string database = Path.Combine(directory.Path, "characters.db");
        await EnsureSchemaAsync(database);
        await using Kit kit = Kit.Create(database, ClusterNodeRole.MapWorker, startupWork: _ => throw new InvalidOperationException("after-start work failed"));
        var heldWhileStopping = new List<(bool Social, bool Economy)>();
        kit.Ordinary.OnStop = async () =>
            heldWhileStopping.Add(((await ReadAsync(database, RealmAuthorityScopes.Social)).Active, (await ReadAsync(database, RealmAuthorityScopes.Economy)).Active));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Host.StartAsync(CancellationToken.None));

        Assert.Equal("after-start work failed", failure.Message);
        // The auction bot (a timer started by Attach) may still tick until its feature stops: the lease must still be ours when the features stop.
        Assert.Equal([(true, true)], heldWhileStopping);
        Assert.True(kit.Ordinary.Stopped);
        Assert.False((await ReadAsync(database, RealmAuthorityScopes.Social)).Active); // and given back afterwards
        Assert.False((await ReadAsync(database, RealmAuthorityScopes.Economy)).Active);
    }

    [Fact]
    public async Task Combined_AttachesEverything_AndTouchesNoLeaseRow()
    {
        using var directory = new TempDirectory("arcanecore-leases-", sqlite: true);
        string database = Path.Combine(directory.Path, "characters.db");
        await EnsureSchemaAsync(database);
        await using Kit kit = Kit.Create(database, ClusterNodeRole.Combined);

        await kit.Host.StartAsync(CancellationToken.None);

        string[] attached = [.. kit.Features.List().Select(f => f.Name)];
        Assert.Contains(nameof(AutoBroadcastFeature), attached);
        Assert.Contains(nameof(AuctionBotFeature), attached);
        Assert.Contains(nameof(OrdinaryFeature), attached);

        await kit.Host.StopAsync(CancellationToken.None);

        Assert.True(kit.Ordinary.Stopped);
        foreach (string scope in RealmAuthorityLeaseDataModule.Scopes)
        {
            RealmAuthorityLeaseRow row = await ReadAsync(database, scope);
            Assert.False(row.Active);
            Assert.Equal(0, row.FencingToken);
            Assert.Null(row.OwnerNode);
        }
    }

    [Fact]
    public async Task TwoMapWorkers_BothRunTheResetSchedule_ButOnlyTheInstancesLeaseHolderWritesRaidResetTimes()
    {
        using var directory = new TempDirectory("arcanecore-leases-", sqlite: true);
        string database = Path.Combine(directory.Path, "characters.db");
        await EnsureSchemaAsync(database);
        var firstStore = new RecordingInstanceStore();
        var secondStore = new RecordingInstanceStore();
        var firstClock = new ArcaneCore.World.Tests.Cluster.Social.ManualTimeProvider(ScheduleStart);
        var secondClock = new ArcaneCore.World.Tests.Cluster.Social.ManualTimeProvider(ScheduleStart);
        await using Kit first = Kit.Create(database, ClusterNodeRole.MapWorker, withInstances: true, instanceStore: firstStore, instanceClock: firstClock);
        await using Kit second = Kit.Create(database, ClusterNodeRole.MapWorker, withInstances: true, node: "worker-2", instanceStore: secondStore, instanceClock: secondClock);
        await first.Host.StartAsync(CancellationToken.None);
        await second.Host.StartAsync(CancellationToken.None);

        Assert.True(first.Instances!.HoldsScheduleAuthority);
        Assert.False(second.Instances!.HoldsScheduleAuthority);
        Assert.Equal(Node, (await ReadAsync(database, RealmAuthorityScopes.Instances)).OwnerNode);
        await first.LoadedAsync();
        await second.LoadedAsync();
        await first.Instances.FlushAsync();
        await second.Instances.FlushAsync();
        long scheduled = firstStore.ResetTimes[RaidMap];
        Assert.Empty(secondStore.ResetTimes); // the non-holder writes no row at load either

        firstClock.Advance(TimeSpan.FromDays(8));
        secondClock.Advance(TimeSpan.FromDays(8));
        Assert.True(await first.Instances.RunScheduleTickAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(await second.Instances.RunScheduleTickAsync().WaitAsync(TimeSpan.FromSeconds(30))); // the non-holder runs its local schedule too
        await first.Instances.FlushAsync();
        await second.Instances.FlushAsync();

        long next = first.Instances.Instances.GetRaidResetTime(RaidMap);
        Assert.True(next > scheduled);
        Assert.Equal(next, firstStore.ResetTimes[RaidMap]);
        Assert.Equal(1, first.Instances.ScheduleRuns);
        Assert.Equal(1, second.Instances.ScheduleRuns);
        Assert.Empty(secondStore.ResetTimes); // no durable write from the non-holder
        long secondNext = second.Instances.Instances.GetRaidResetTime(RaidMap);
        Assert.True(secondNext > scheduled); // its in-memory schedule advanced past the global reset
        Assert.True(secondNext > secondClock.GetUtcNow().ToUnixTimeSeconds()); // so new raid saves and raid info on that node see a future reset
    }

    [Theory]
    [InlineData(ClusterNodeRole.Social)]
    [InlineData(ClusterNodeRole.Economy)]
    [InlineData(ClusterNodeRole.Gateway)]
    public async Task NonMapWorkerNodeStartingFirst_DoesNotTakeTheInstancesLease_SoTheMapWorkerRunsTheSchedule(ClusterNodeRole other)
    {
        using var directory = new TempDirectory("arcanecore-leases-", sqlite: true);
        string database = Path.Combine(directory.Path, "characters.db");
        await EnsureSchemaAsync(database);
        var store = new RecordingInstanceStore();
        var clock = new ArcaneCore.World.Tests.Cluster.Social.ManualTimeProvider(ScheduleStart);
        await using Kit early = Kit.Create(database, other, node: "early-node");
        await using Kit worker = Kit.Create(database, ClusterNodeRole.MapWorker, withInstances: true, node: "worker-2", instanceStore: store, instanceClock: clock);
        await early.Host.StartAsync(CancellationToken.None);
        await worker.Host.StartAsync(CancellationToken.None);

        Assert.Equal("worker-2", (await ReadAsync(database, RealmAuthorityScopes.Instances)).OwnerNode);
        Assert.True(worker.Instances!.HoldsScheduleAuthority);
        await worker.LoadedAsync();
        await worker.Instances.FlushAsync();
        Assert.True(store.ResetTimes.ContainsKey(RaidMap));
    }
    [Fact]
    public async Task MapWorker_LosingTheInstancesLease_StopsTheResetSchedule()
    {
        using var directory = new TempDirectory("arcanecore-leases-", sqlite: true);
        string database = Path.Combine(directory.Path, "characters.db");
        await EnsureSchemaAsync(database);
        var time = new ArcaneCore.World.Tests.Cluster.Social.ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new RejectingRenewalStore();
        var instanceStore = new RecordingInstanceStore();
        var clock = new ArcaneCore.World.Tests.Cluster.Social.ManualTimeProvider(ScheduleStart);
        await using Kit kit = Kit.Create(database, ClusterNodeRole.MapWorker, withInstances: true, keeperStore: store, time: time, instanceStore: instanceStore, instanceClock: clock);
        await kit.Host.StartAsync(CancellationToken.None);
        Assert.True(await kit.Instances!.RunScheduleTickAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, kit.Instances.ScheduleRuns);

        store.RejectRenewals = true;
        time.Advance(TimeSpan.FromSeconds(10)); // the renewal is refused: fail-stop
        Assert.True(kit.Instances.Halted);
        Assert.False(kit.Instances.HoldsScheduleAuthority);

        await kit.LoadedAsync();
        await kit.Instances.FlushAsync();
        long written = instanceStore.ResetTimes[RaidMap];
        clock.Advance(TimeSpan.FromDays(8));
        Assert.False(await kit.Instances.RunScheduleTickAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        await kit.Instances.FlushAsync();

        Assert.Equal(1, kit.Instances.ScheduleRuns);
        Assert.Equal(written, instanceStore.ResetTimes[RaidMap]); // the schedule did not run again
        Assert.Equal(written, kit.Instances.Instances.GetRaidResetTime(RaidMap));
    }

    [Fact]
    public async Task Combined_RunsTheResetSchedule_AndWritesRaidResetTimes_WithoutALease()
    {
        using var directory = new TempDirectory("arcanecore-leases-", sqlite: true);
        string database = Path.Combine(directory.Path, "characters.db");
        await EnsureSchemaAsync(database);
        var instanceStore = new RecordingInstanceStore();
        var clock = new ArcaneCore.World.Tests.Cluster.Social.ManualTimeProvider(ScheduleStart);
        await using Kit kit = Kit.Create(database, ClusterNodeRole.Combined, withInstances: true, instanceStore: instanceStore, instanceClock: clock);
        await kit.Host.StartAsync(CancellationToken.None);
        Assert.True(kit.Instances!.HoldsScheduleAuthority);
        await kit.LoadedAsync();
        await kit.Instances.FlushAsync();
        long scheduled = instanceStore.ResetTimes[RaidMap];

        clock.Advance(TimeSpan.FromDays(8));
        Assert.True(await kit.Instances.RunScheduleTickAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        await kit.Instances.FlushAsync();

        Assert.True(instanceStore.ResetTimes[RaidMap] > scheduled);
        Assert.Equal(0, (await ReadAsync(database, RealmAuthorityScopes.Instances)).FencingToken);
    }

    [Fact]
    public void LeaseScopeFor_DoesNotDecideAttachmentOfTheInstanceFeature()
    {
        Assert.Null(ClusterRoleFeatureFilter.LeaseScopeFor(typeof(InstanceFeature)));
    }

    /// <summary>Instance store that records the raid reset times written to it (one per map: the last write).</summary>
    private sealed class RecordingInstanceStore : ArcaneCore.Kernel.Instances.IInstanceStore
    {
        public System.Collections.Concurrent.ConcurrentDictionary<uint, long> ResetTimes { get; } = new();

        public Task<ArcaneCore.Kernel.Instances.InstanceStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ArcaneCore.Kernel.Instances.InstanceStoreSnapshot.Empty);

        public Task SaveResetTimeAsync(ArcaneCore.Kernel.Instances.InstanceResetRecord reset, CancellationToken cancellationToken = default)
        {
            ResetTimes[reset.MapId] = reset.ResetTime;
            return Task.CompletedTask;
        }

        public Task SaveInstanceAsync(ArcaneCore.Kernel.Instances.InstanceRecord instance, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveInstanceDataAsync(uint instanceId, string data, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveBindAsync(ArcaneCore.Kernel.Instances.CharacterInstanceBindRecord bind, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteBindAsync(int characterId, uint instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveLastInstanceAsync(ArcaneCore.Kernel.Instances.CharacterLastInstanceRecord last, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RejectingRenewalStore : IRealmAuthorityLeaseStore
    {
        private readonly InMemoryRealmAuthorityLeaseStore _inner = new();
        public volatile bool RejectRenewals;

        public AuthorityLeaseResult TryAcquire(string scope, string ownerNodeId, Guid ownerIncarnation, TimeSpan ttl, DateTimeOffset nowUtc)
            => _inner.TryAcquire(scope, ownerNodeId, ownerIncarnation, ttl, nowUtc);

        public AuthorityLeaseResult TryRenew(string scope, string ownerNodeId, Guid ownerIncarnation, long fencingToken, TimeSpan ttl, DateTimeOffset nowUtc)
            => RejectRenewals
                ? new AuthorityLeaseResult(AuthorityLeaseOutcome.RejectedStaleToken, null)
                : _inner.TryRenew(scope, ownerNodeId, ownerIncarnation, fencingToken, ttl, nowUtc);

        public AuthorityLeaseResult TryRelease(string scope, string ownerNodeId, Guid ownerIncarnation, long fencingToken)
            => _inner.TryRelease(scope, ownerNodeId, ownerIncarnation, fencingToken);

        public bool ValidateFencingToken(string scope, string ownerNodeId, long fencingToken, DateTimeOffset nowUtc)
            => _inner.ValidateFencingToken(scope, ownerNodeId, fencingToken, nowUtc);

        public bool ValidateFencingToken(string scope, string ownerNodeId, Guid ownerIncarnation, long fencingToken, DateTimeOffset nowUtc)
            => _inner.ValidateFencingToken(scope, ownerNodeId, ownerIncarnation, fencingToken, nowUtc);

        public RealmAuthorityLease? GetCurrent(string scope, DateTimeOffset nowUtc) => _inner.GetCurrent(scope, nowUtc);

        public long GetHighWaterToken(string scope) => _inner.GetHighWaterToken(scope);
    }

    private static CharacterDbContext Context(string database)
        => new(new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite($"Data Source={database}").Options);

    private static async Task EnsureSchemaAsync(string database)
    {
        await using CharacterDbContext db = Context(database);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
    }

    private static async Task<RealmAuthorityLeaseRow> ReadAsync(string database, string scope)
    {
        await using CharacterDbContext db = Context(database);
        return await db.Set<RealmAuthorityLeaseRow>().AsNoTracking().SingleAsync(r => r.Scope == scope);
    }

    private sealed class Kit : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly WorldRuntime _world;

        private Kit(ServiceProvider provider, WorldRuntime world, WorldHost host, EfRealmAuthorityLeaseStore store, FeatureHost features, OrdinaryFeature ordinary, InstanceFeature? instances)
        {
            Instances = instances;
            _provider = provider;
            _world = world;
            Host = host;
            Store = store;
            Features = features;
            Ordinary = ordinary;
        }

        public WorldHost Host { get; }
        public EfRealmAuthorityLeaseStore Store { get; }
        public FeatureHost Features { get; }
        public OrdinaryFeature Ordinary { get; }
        public InstanceFeature? Instances { get; }

        /// <summary>Wait until the world thread ran the commands queued so far (the instance feature loads its state there).</summary>
        public Task LoadedAsync() => _world.InvokeAsync(() => true);

        public static Kit Create(string database, ClusterNodeRole role, Func<CancellationToken, Task>? startupWork = null,
            bool withInstances = false, string node = Node, IRealmAuthorityLeaseStore? keeperStore = null, TimeProvider? time = null,
            ArcaneCore.Kernel.Instances.IInstanceStore? instanceStore = null, TimeProvider? instanceClock = null)
        {
            var ordinary = new OrdinaryFeature();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped(_ => Context(database));
            services.AddSingleton<ICharacterStore>(new InMemoryCharacterStore());
            services.AddSingleton<EfRealmAuthorityLeaseStore>();
            services.AddSingleton<FeatureHost>();
            services.AddSingleton<IWorldFeature>(ordinary);
            services.AddSingleton<AutoBroadcastFeature>();
            services.AddSingleton<IWorldFeature>(sp => sp.GetRequiredService<AutoBroadcastFeature>());
            services.AddSingleton<AuctionBotFeature>();
            services.AddSingleton<IWorldFeature>(sp => sp.GetRequiredService<AuctionBotFeature>());
            IOptions<ClusterNodeOptions> nodeOptions = Options.Create(new ClusterNodeOptions { Role = role, AuthorityLeaseTtlSeconds = 30, AuthorityLeaseRenewSeconds = 10 });
            if (withInstances)
            {
                // The 5 s schedule timer is off: the tests drive every tick, so no real timer races them.
                services.AddSingleton(sp => new InstanceFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>())
                { ScheduleTimerEnabled = false });
                if (instanceStore is not null)
                {
                    services.AddScoped(_ => instanceStore);
                }

                if (instanceClock is not null)
                {
                    services.AddSingleton(instanceClock);
                }

                services.AddSingleton<IWorldFeature>(sp => sp.GetRequiredService<InstanceFeature>());
                services.AddSingleton<IRealmSingletonHalt>(sp => sp.GetRequiredService<InstanceFeature>());
            }

            if (role != ClusterNodeRole.Combined)
            {
                services.AddSingleton(sp => new RealmAuthorityLeaseKeeper(keeperStore ?? sp.GetRequiredService<EfRealmAuthorityLeaseStore>(), nodeOptions,
                    Options.Create(new GatewayWorkerOptions { Enabled = true, NodeId = node }), NullLogger<RealmAuthorityLeaseKeeper>.Instance,
                    time: time, setExitCode: _ => { }, halts: sp.GetServices<IRealmSingletonHalt>()));
            }

            ServiceProvider provider = services.BuildServiceProvider();
            IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var saveQueue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
            var runtime = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saveQueue, NullLogger<WorldRuntime>.Instance);
            if (withInstances)
            {
                WorldMaps.Of(runtime).Load(RaidContent);
            }

            EfRealmAuthorityLeaseStore store = provider.GetRequiredService<EfRealmAuthorityLeaseStore>();
            RealmAuthorityLeaseKeeper? keeper = role == ClusterNodeRole.Combined ? null : provider.GetRequiredService<RealmAuthorityLeaseKeeper>();
            ClusterRoleFeatureFilter? filter = role == ClusterNodeRole.Combined ? null : new ClusterRoleFeatureFilter(store);
            var host = new WorldHost(runtime, saveQueue, new CharacterDirectory(), scopes, NullLogger<WorldHost>.Instance,
                Options.Create(new CharacterFencingOptions { Enabled = false, OwnerNode = node }),
                roleFilter: filter, leaseKeeper: keeper, nodeOptions: nodeOptions) { ListenerExpected = false, StartupWorkForTesting = startupWork };
            return new Kit(provider, runtime, host, store, provider.GetRequiredService<FeatureHost>(), ordinary, withInstances ? provider.GetRequiredService<InstanceFeature>() : null);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Host.StopAsync(CancellationToken.None); }
            catch (Exception) { }
            _world.Dispose();
            await _provider.DisposeAsync();
        }
    }

    /// <summary>An ordinary world feature (spells, creatures and so on stand for it): every role runs it.</summary>
    private sealed class OrdinaryFeature : IWorldFeature
    {
        public bool Attached { get; private set; }
        public bool Stopped { get; private set; }
        public Func<Task>? OnStop { get; set; }

        public void Attach(WorldRuntime world) => Attached = true;

        public async Task StopAsync()
        {
            if (OnStop is { } onStop)
            {
                await onStop();
            }

            Stopped = true;
        }
    }
}

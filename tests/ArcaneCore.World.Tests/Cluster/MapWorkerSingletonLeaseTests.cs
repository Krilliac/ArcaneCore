using ArcaneCore.World.Persistence;
using ArcaneCore.Game.Maps;
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
        foreach (string scope in new[] { RealmAuthorityScopes.Social, RealmAuthorityScopes.Economy })
        {
            RealmAuthorityLeaseRow held = await ReadAsync(database, scope);
            Assert.True(held.Active);
            Assert.Equal(Node, held.OwnerNode);
            Assert.Equal(1, held.FencingToken);
        }

        await kit.Host.StopAsync(CancellationToken.None);

        foreach (string scope in new[] { RealmAuthorityScopes.Social, RealmAuthorityScopes.Economy })
        {
            RealmAuthorityLeaseRow released = await ReadAsync(database, scope);
            Assert.False(released.Active);
            Assert.Null(released.OwnerNode);
            Assert.Equal(1, released.FencingToken);
        }

        Assert.Equal(0, (await ReadAsync(database, RealmAuthorityScopes.Instances)).FencingToken); // not a singleton this node runs
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

        private Kit(ServiceProvider provider, WorldRuntime world, WorldHost host, EfRealmAuthorityLeaseStore store, FeatureHost features, OrdinaryFeature ordinary)
        {
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

        public static Kit Create(string database, ClusterNodeRole role, Func<CancellationToken, Task>? startupWork = null)
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
            ServiceProvider provider = services.BuildServiceProvider();
            IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var saveQueue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
            var runtime = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saveQueue, NullLogger<WorldRuntime>.Instance);
            EfRealmAuthorityLeaseStore store = provider.GetRequiredService<EfRealmAuthorityLeaseStore>();
            IOptions<ClusterNodeOptions> node = Options.Create(new ClusterNodeOptions { Role = role, AuthorityLeaseTtlSeconds = 30, AuthorityLeaseRenewSeconds = 10 });
            RealmAuthorityLeaseKeeper? keeper = role == ClusterNodeRole.Combined
                ? null
                : new RealmAuthorityLeaseKeeper(store, node, Options.Create(new GatewayWorkerOptions { Enabled = true, NodeId = Node }),
                    NullLogger<RealmAuthorityLeaseKeeper>.Instance, setExitCode: _ => { });
            ClusterRoleFeatureFilter? filter = role == ClusterNodeRole.Combined ? null : new ClusterRoleFeatureFilter(store);
            var host = new WorldHost(runtime, saveQueue, new CharacterDirectory(), scopes, NullLogger<WorldHost>.Instance,
                Options.Create(new CharacterFencingOptions { Enabled = false, OwnerNode = Node }),
                roleFilter: filter, leaseKeeper: keeper, nodeOptions: node) { ListenerExpected = false, StartupWorkForTesting = startupWork };
            return new Kit(provider, runtime, host, store, provider.GetRequiredService<FeatureHost>(), ordinary);
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

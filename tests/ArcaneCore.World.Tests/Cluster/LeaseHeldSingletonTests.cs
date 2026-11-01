using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Economy;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Tests.Cluster.Social;
using ArcaneCore.World.Tests.Honor;
using ArcaneCore.World.WorldState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>
/// Writer-lock design step C6 (sections 2b and 4): the realm sweeps that must run once per realm are gated on the lease of their scope. For each site: a node that does not hold the
/// lease skips or refuses, the holder runs, and a lost or released lease stops it. Sites covered here: the new <c>maintenance</c> and <c>worldstate</c> scopes in the keeper, the
/// economy expiry sweep, the weekly honor calculation, the game event status writer, and <see cref="LeaseFencedWrite.RequireHeld"/>.
/// </summary>
public sealed class LeaseHeldSingletonTests
{
    private const string Node = "worker-a";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryRealmAuthorityLeaseStore _leases = new();

    private RealmAuthorityLeaseKeeper CreateKeeper(IRealmAuthorityLeaseStore? store = null, string node = Node)
        => new(store ?? _leases,
            Options.Create(new ClusterNodeOptions { Role = ClusterNodeRole.MapWorker, AuthorityLeaseTtlSeconds = 30, AuthorityLeaseRenewSeconds = 10 }),
            Options.Create(new GatewayWorkerOptions { Enabled = true, NodeId = node }),
            NullLogger<RealmAuthorityLeaseKeeper>.Instance,
            lifetime: null,
            time: _time,
            setExitCode: _ => { }); // not the process-global ExitCodes.Current, which other tests assert on

    private static string[] All => [RealmAuthorityScopes.Social, RealmAuthorityScopes.Economy, RealmAuthorityScopes.Instances, RealmAuthorityScopes.Maintenance, RealmAuthorityScopes.WorldState];

    // ---- the scopes and the keeper

    [Fact]
    public void ScopeConstants_MatchTheSeededRows()
    {
        Assert.Equal(CharacterOwnershipShardDataModule.MaintenanceLeaseScope, RealmAuthorityScopes.Maintenance);
        Assert.Equal(CharacterOwnershipShardDataModule.WorldStateLeaseScope, RealmAuthorityScopes.WorldState);
    }

    [Fact]
    public async Task MapWorkerKeeper_TakesMaintenanceAndWorldState_AndGivesThemBack()
    {
        await using RealmAuthorityLeaseKeeper keeper = CreateKeeper();
        await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);

        foreach (string scope in All)
        {
            Assert.True(keeper.Holds(scope), scope);
            Assert.NotNull(keeper.TokenFor(scope));
        }

        await keeper.ReleaseAllAsync();

        foreach (string scope in All)
        {
            Assert.False(keeper.Holds(scope), scope);
            Assert.Null(_leases.GetCurrent(scope, _time.GetUtcNow()));
        }
    }

    [Fact]
    public async Task Keeper_WithTheMaintenanceLeaseHeldByAnotherNode_IsNotItsHolder_ButHoldsTheRest()
    {
        _leases.TryAcquire(RealmAuthorityScopes.Maintenance, "other-node", Guid.NewGuid(), TimeSpan.FromMinutes(10), _time.GetUtcNow());
        await using RealmAuthorityLeaseKeeper keeper = CreateKeeper();
        await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(keeper.Holds(RealmAuthorityScopes.Maintenance));
        Assert.Null(keeper.FenceFor(RealmAuthorityScopes.Maintenance));
        Assert.True(keeper.Holds(RealmAuthorityScopes.WorldState));
        Assert.True(keeper.Holds(RealmAuthorityScopes.Economy));
    }

    [Fact]
    public async Task Keeper_WhenALeaseIsTakenOver_NoLongerHoldsAnyScope()
    {
        Guid incarnation = Guid.NewGuid();
        await using RealmAuthorityLeaseKeeper keeper = CreateKeeper();
        await keeper.AcquireAtStartAsync(incarnation, CancellationToken.None);
        long token = keeper.TokenFor(RealmAuthorityScopes.Maintenance)!.Value;
        Assert.True(keeper.Holds(RealmAuthorityScopes.Maintenance));

        // Another node takes the scope over (the row was given up, and the new owner was granted).
        _leases.TryRelease(RealmAuthorityScopes.Maintenance, Node, incarnation, token);
        Assert.Equal(AuthorityLeaseOutcome.Granted,
            _leases.TryAcquire(RealmAuthorityScopes.Maintenance, "other-node", Guid.NewGuid(), TimeSpan.FromMinutes(10), _time.GetUtcNow()).Outcome);
        _time.Advance(TimeSpan.FromSeconds(10)); // one renewal tick finds the loss: the node is fail-stop

        Assert.False(keeper.Holds(RealmAuthorityScopes.Maintenance));
        Assert.False(keeper.Holds(RealmAuthorityScopes.Economy)); // a lost lease stops the whole node's singletons
    }

    [Fact]
    public async Task Keeper_OnADatabaseWithoutTheNewLeaseRows_StartsAndSimplyDoesNotHoldThem()
    {
        // EnsureCreated seeds only the three released scopes (HasData); schema 72 adds the other two rows through an EnsureRowChange.
        string path = Path.Combine(Path.GetTempPath(), $"arcanecore-c6-leases-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using (var db = new CharacterDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                Assert.DoesNotContain(db.Set<RealmAuthorityLeaseRow>().AsNoTracking().ToList(), r => r.Scope is "maintenance" or "worldstate");
            }

            var services = new ServiceCollection();
            services.AddScoped(_ => new CharacterDbContext(options));
            await using ServiceProvider provider = services.BuildServiceProvider();
            var store = new EfRealmAuthorityLeaseStore(provider.GetRequiredService<IServiceScopeFactory>());
            await using RealmAuthorityLeaseKeeper keeper = CreateKeeper(store);

            await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);

            Assert.True(keeper.Holds(RealmAuthorityScopes.Social));
            Assert.True(keeper.Holds(RealmAuthorityScopes.Economy));
            Assert.True(keeper.Holds(RealmAuthorityScopes.Instances));
            Assert.False(keeper.Holds(RealmAuthorityScopes.Maintenance));
            Assert.False(keeper.Holds(RealmAuthorityScopes.WorldState));
            Assert.Null(keeper.TokenFor(RealmAuthorityScopes.Maintenance));
        }
        finally
        {
            TryDelete(path);
        }
    }

    // ---- LeaseFencedWrite.RequireHeld

    [Fact]
    public void RequireHeld_WithNoFenceSource_DoesNothing()
        => LeaseFencedWrite.RequireHeld(null, RealmAuthorityScopes.Maintenance);

    [Fact]
    public async Task RequireHeld_RefusesANonHolder_AndPassesTheHolder()
    {
        _leases.TryAcquire(RealmAuthorityScopes.Maintenance, "other-node", Guid.NewGuid(), TimeSpan.FromMinutes(10), _time.GetUtcNow());
        await using RealmAuthorityLeaseKeeper keeper = CreateKeeper();
        await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);

        RealmLeaseNotHeldException refused = Assert.Throws<RealmLeaseNotHeldException>(() => LeaseFencedWrite.RequireHeld(keeper, RealmAuthorityScopes.Maintenance));
        Assert.Equal(RealmAuthorityScopes.Maintenance, refused.Scope);
        Assert.IsAssignableFrom<CharacterOwnershipRejectedException>(refused);
        LeaseFencedWrite.RequireHeld(keeper, RealmAuthorityScopes.WorldState); // held: no throw
    }

    [Fact]
    public async Task RequireHeld_RefusesAfterTheLeaseWasGivenBack()
    {
        await using RealmAuthorityLeaseKeeper keeper = CreateKeeper();
        await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);
        LeaseFencedWrite.RequireHeld(keeper, RealmAuthorityScopes.Economy);

        await keeper.ReleaseAllAsync();

        Assert.Throws<RealmLeaseNotHeldException>(() => LeaseFencedWrite.RequireHeld(keeper, RealmAuthorityScopes.Economy));
    }

    // ---- weekly honor calculation (maintenance lease)

    private sealed class HonorRig(MemoryHonorStore store, ServiceProvider provider, HonorMaintenanceRunner runner) : IAsyncDisposable
    {
        public MemoryHonorStore Store { get; } = store;

        public HonorMaintenanceRunner Runner { get; } = runner;

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    private static HonorRig CreateHonor(RealmAuthorityLeaseKeeper? keeper)
    {
        var store = new MemoryHonorStore();
        store.SaveMaintenanceAsync(new HonorMaintenanceState(100, 107, false)).GetAwaiter().GetResult();
        store.AddCharacter(1, 1);
        store.Seed(1, store.State(1), new HonorCpRecord(4, 1000, 10f, 103, (byte)HonorKind.Honorable));
        IServiceCollection services = new ServiceCollection()
            .AddSingleton<IHonorStore>(store)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        if (keeper is not null)
        {
            services.AddSingleton(keeper);
        }

        ServiceProvider provider = services.BuildServiceProvider();
        var honor = new HonorFeature(provider, provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
        return new HonorRig(store, provider, new HonorMaintenanceRunner(honor, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance));
    }

    [Fact]
    public async Task HonorMaintenance_RunsOnTheMaintenanceHolder()
    {
        await using RealmAuthorityLeaseKeeper keeper = CreateKeeper();
        await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);
        await using HonorRig rig = CreateHonor(keeper);

        Assert.Equal(1, await rig.Runner.RunDueAsync(107, null));

        Assert.NotEqual(new HonorMaintenanceState(100, 107, false), rig.Store.Maintenance); // the days moved on
    }

    [Fact]
    public async Task HonorMaintenance_WithNoKeeper_RunsLikeACombinedWorld()
    {
        await using HonorRig rig = CreateHonor(null);

        Assert.Equal(1, await rig.Runner.RunDueAsync(107, null));
    }

    [Fact]
    public async Task HonorMaintenance_SkipsOnANodeThatDoesNotHoldTheLease_AndTouchesNothing()
    {
        _leases.TryAcquire(RealmAuthorityScopes.Maintenance, "other-node", Guid.NewGuid(), TimeSpan.FromMinutes(10), _time.GetUtcNow());
        await using RealmAuthorityLeaseKeeper keeper = CreateKeeper();
        await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);
        await using HonorRig rig = CreateHonor(keeper);

        Assert.Equal(0, await rig.Runner.RunDueAsync(107, null));

        Assert.Equal(new HonorMaintenanceState(100, 107, false), rig.Store.Maintenance);
    }

    [Fact]
    public async Task HonorMaintenance_HaltsWhenTheLeaseIsLost()
    {
        Guid incarnation = Guid.NewGuid();
        await using RealmAuthorityLeaseKeeper keeper = CreateKeeper();
        await keeper.AcquireAtStartAsync(incarnation, CancellationToken.None);
        await using HonorRig rig = CreateHonor(keeper);
        long token = keeper.TokenFor(RealmAuthorityScopes.Maintenance)!.Value;
        _leases.TryRelease(RealmAuthorityScopes.Maintenance, Node, incarnation, token);
        _leases.TryAcquire(RealmAuthorityScopes.Maintenance, "other-node", Guid.NewGuid(), TimeSpan.FromMinutes(10), _time.GetUtcNow());
        _time.Advance(TimeSpan.FromSeconds(10)); // the renewal tick finds the loss

        Assert.Equal(0, await rig.Runner.RunDueAsync(107, null));

        Assert.Equal(new HonorMaintenanceState(100, 107, false), rig.Store.Maintenance);
    }

    // ---- game event status writer (worldstate lease)

    private sealed class CountingStatusStore : IGameEventStatusStore
    {
        public int Writes;

        public Task<IReadOnlyList<int>> LoadActiveAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<int>>([]);

        public Task ReplaceActiveAsync(IReadOnlyCollection<int> events, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Writes);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task GameEventStatusWriter_WritesOnlyForTheWorldStateHolder(bool holds, int expectedWrites)
    {
        var store = new CountingStatusStore();
        await using ServiceProvider provider = new ServiceCollection().AddSingleton<IGameEventStatusStore>(store).BuildServiceProvider();
        var writer = new GameEventStatusWriter(provider, NullLogger.Instance, () => holds);

        writer.Changed([1, 2]);
        await writer.FlushAsync();

        Assert.Equal(expectedWrites, store.Writes);
    }

    [Fact]
    public async Task GameEventStatusWriter_StopsWritingOnceTheLeaseIsGone()
    {
        var store = new CountingStatusStore();
        bool holds = true;
        await using ServiceProvider provider = new ServiceCollection().AddSingleton<IGameEventStatusStore>(store).BuildServiceProvider();
        var writer = new GameEventStatusWriter(provider, NullLogger.Instance, () => holds);

        writer.Changed([1]);
        await writer.FlushAsync();
        holds = false;
        writer.Changed([1, 2]);
        await writer.FlushAsync();

        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task GameEventStatusWriter_WithoutAGate_WritesAsBefore()
    {
        var store = new CountingStatusStore();
        await using ServiceProvider provider = new ServiceCollection().AddSingleton<IGameEventStatusStore>(store).BuildServiceProvider();
        var writer = new GameEventStatusWriter(provider, NullLogger.Instance);

        writer.Changed([3]);
        await writer.FlushAsync();

        Assert.Equal(1, store.Writes);
    }

    // ---- economy expiry sweep (economy lease)

    private sealed class EconomyNode : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly CharacterSaveQueue _saves;

        public EconomyNode(DbContextOptions<CharacterDbContext> options, RealmAuthorityLeaseKeeper? keeper)
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => new CharacterDbContext(options));
            services.AddScoped<IEconomyStore>(provider => new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()));
            services.AddSingleton(provider => new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance));
            if (keeper is not null)
            {
                services.AddSingleton(keeper);
            }

            _services = services.BuildServiceProvider();
            _saves = _services.GetRequiredService<CharacterSaveQueue>();
            World = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 }, _saves, NullLogger<WorldRuntime>.Instance);
            Feature = new EconomyFeature(_services, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<EconomyFeature>.Instance);
            Feature.Options.ExpirySweepSeconds = 0; // the test drives the sweep itself
            Feature.Attach(World);
            World.Start();
        }

        public WorldRuntime World { get; }

        public EconomyFeature Feature { get; }

        public Task<T> OnWorld<T>(Func<T> action) => World.InvokeAsync(action).WaitAsync(Budget);

        public async ValueTask DisposeAsync()
        {
            await Feature.DisposeAsync();
            World.Dispose();
            await _saves.StopAsync();
            await _services.DisposeAsync();
        }
    }

    private static async Task<DbContextOptions<CharacterDbContext>> CreateEconomyDatabaseAsync(string path)
    {
        var options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // A money-only letter past its expiry: the holder sweep deletes it (vmangos ReturnOrDeleteOldMails).
        var expired = new MailRecord
        {
            Id = 600, MessageType = MailMessageType.Normal, SenderId = 10, ReceiverId = 6, Subject = "old", Money = 10,
            DeliverTime = now - 7200, ExpireTime = now - 10,
        };
        await using var db = new CharacterDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var row = new MailRow();
        row.CopyFrom(expired);
        db.Add(row);
        await db.SaveChangesAsync();
        return options;
    }

    private static async Task<bool> ExpiredMailExistsAsync(DbContextOptions<CharacterDbContext> options)
    {
        await using var db = new CharacterDbContext(options);
        return await db.Set<MailRow>().AnyAsync(m => m.Id == 600);
    }

    [Fact]
    public async Task EconomySweep_RunsOnTheEconomyHolder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"arcanecore-c6-economy-{Guid.NewGuid():N}.db");
        try
        {
            DbContextOptions<CharacterDbContext> options = await CreateEconomyDatabaseAsync(path);
            await using RealmAuthorityLeaseKeeper keeper = CreateKeeper();
            await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);
            await using var node = new EconomyNode(options, keeper);
            Assert.True(node.Feature.HoldsEconomyLease);

            await node.OnWorld(() =>
            {
                node.Feature.RunExpirySweep();
                return true;
            });

            using var timeout = new CancellationTokenSource(Budget);
            while (await ExpiredMailExistsAsync(options))
            {
                await Task.Delay(20, timeout.Token);
            }
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task EconomySweep_IsSkippedOnAWorkerThatDoesNotHoldTheEconomyLease()
    {
        string path = Path.Combine(Path.GetTempPath(), $"arcanecore-c6-economy-{Guid.NewGuid():N}.db");
        try
        {
            DbContextOptions<CharacterDbContext> options = await CreateEconomyDatabaseAsync(path);
            _leases.TryAcquire(RealmAuthorityScopes.Economy, "other-node", Guid.NewGuid(), TimeSpan.FromMinutes(10), _time.GetUtcNow());
            await using RealmAuthorityLeaseKeeper keeper = CreateKeeper();
            await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);
            await using var node = new EconomyNode(options, keeper);
            Assert.False(node.Feature.HoldsEconomyLease);
            Assert.True(node.Feature.IsEconomyHolder); // no forwarding link: only the lease says it is not the holder

            await node.OnWorld(() =>
            {
                node.Feature.RunExpirySweep();
                return true;
            });
            await Task.Delay(500); // the sweep reads off the world thread; give a wrongly started one time to delete

            Assert.True(await ExpiredMailExistsAsync(options), "a worker without the economy lease must not expire letters");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task EconomySweep_StopsOnceTheLeaseIsGivenBack()
    {
        string path = Path.Combine(Path.GetTempPath(), $"arcanecore-c6-economy-{Guid.NewGuid():N}.db");
        try
        {
            DbContextOptions<CharacterDbContext> options = await CreateEconomyDatabaseAsync(path);
            await using RealmAuthorityLeaseKeeper keeper = CreateKeeper();
            await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);
            await using var node = new EconomyNode(options, keeper);
            await keeper.ReleaseAllAsync();
            Assert.False(node.Feature.HoldsEconomyLease);

            await node.OnWorld(() =>
            {
                node.Feature.RunExpirySweep();
                return true;
            });
            await Task.Delay(500);

            Assert.True(await ExpiredMailExistsAsync(options));
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

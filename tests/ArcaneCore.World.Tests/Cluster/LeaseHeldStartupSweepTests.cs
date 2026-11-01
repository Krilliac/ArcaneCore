using System.Collections.Concurrent;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Cluster.Social;
using ArcaneCore.World.Tests.Honor;
using ArcaneCore.World.Tests.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Population;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>
/// Writer-lock design step C6, feature level: the playerbot startup sweeps (provisioning reconcile and orphan registry rows, section 4 row 7), the playerbot population pass, and the honor
/// first-start seed (row 6) each run on the maintenance lease holder, skip on a node that does not hold it, and stop when the lease is lost.
/// </summary>
public sealed class LeaseHeldStartupSweepTests
{
    private const string Node = "worker-a";

    public enum Holding
    {
        Holder,
        NonHolder,
        Lost,
    }

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryRealmAuthorityLeaseStore _leases = new();

    private RealmAuthorityLeaseKeeper CreateKeeper()
        => new(_leases,
            Options.Create(new ClusterNodeOptions { Role = ClusterNodeRole.MapWorker, AuthorityLeaseTtlSeconds = 30, AuthorityLeaseRenewSeconds = 10 }),
            Options.Create(new GatewayWorkerOptions { Enabled = true, NodeId = Node }),
            NullLogger<RealmAuthorityLeaseKeeper>.Instance,
            lifetime: null,
            time: _time,
            setExitCode: _ => { }); // not the process-global ExitCodes.Current, which other tests assert on

    /// <summary>A keeper in the requested state for the maintenance scope: holding it, never granted it (another node has it), or granted it and then lost it to a takeover.</summary>
    private async Task<RealmAuthorityLeaseKeeper> KeeperAsync(Holding holding)
    {
        Guid incarnation = Guid.NewGuid();
        if (holding == Holding.NonHolder)
        {
            _leases.TryAcquire(RealmAuthorityScopes.Maintenance, "other-node", Guid.NewGuid(), TimeSpan.FromMinutes(10), _time.GetUtcNow());
        }

        RealmAuthorityLeaseKeeper keeper = CreateKeeper();
        await keeper.AcquireAtStartAsync(incarnation, CancellationToken.None);
        if (holding == Holding.Lost)
        {
            long token = keeper.TokenFor(RealmAuthorityScopes.Maintenance)!.Value;
            _leases.TryRelease(RealmAuthorityScopes.Maintenance, Node, incarnation, token);
            _leases.TryAcquire(RealmAuthorityScopes.Maintenance, "other-node", Guid.NewGuid(), TimeSpan.FromMinutes(10), _time.GetUtcNow());
            _time.Advance(TimeSpan.FromSeconds(10)); // the renewal tick finds the loss
        }

        Assert.Equal(holding == Holding.Holder, keeper.Holds(RealmAuthorityScopes.Maintenance));
        return keeper;
    }

    // ---- honor first-start seed

    private static (HonorFeature Feature, MemoryHonorStore Store, ServiceProvider Provider) CreateHonor(RealmAuthorityLeaseKeeper keeper)
    {
        var store = new MemoryHonorStore(); // no maintenance row: a first start
        IServiceCollection services = new ServiceCollection()
            .AddSingleton<IHonorStore>(store)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton(keeper);
        ServiceProvider provider = services.BuildServiceProvider();
        var feature = new HonorFeature(provider, provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
        return (feature, store, provider);
    }

    [Fact]
    public async Task HonorFirstStartSeed_IsWrittenByTheMaintenanceHolder()
    {
        await using RealmAuthorityLeaseKeeper keeper = await KeeperAsync(Holding.Holder);
        (HonorFeature feature, MemoryHonorStore store, ServiceProvider provider) = CreateHonor(keeper);
        await using ServiceProvider providerScope = provider;
        await using HonorFeature featureScope = feature;

        _ = feature.Service;

        Assert.NotNull(store.Maintenance);
        Assert.NotEqual(0u, feature.WeekBeginDay);
    }

    [Theory]
    [InlineData(Holding.NonHolder)]
    [InlineData(Holding.Lost)]
    public async Task HonorFirstStartSeed_IsSkippedWithoutTheLease_AndTheAttachDoesNotThrow(Holding holding)
    {
        await using RealmAuthorityLeaseKeeper keeper = await KeeperAsync(holding);
        (HonorFeature feature, MemoryHonorStore store, ServiceProvider provider) = CreateHonor(keeper);
        await using ServiceProvider providerScope = provider;
        await using HonorFeature featureScope = feature;

        _ = feature.Service; // would throw out of GetAwaiter().GetResult() if the seed were not gated

        Assert.Null(store.Maintenance);
        Assert.Equal(0, store.WriteAttempts);
        Assert.NotEqual(0u, feature.WeekBeginDay); // the same week is still computed from the clock
    }

    // ---- playerbot startup sweeps and the population pass

    private WorldTestHost StartBotHost(RealmAuthorityLeaseKeeper keeper, InMemoryAccountStore accounts, InMemoryCharacterStore characters,
        ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore owners, ManagedPlayerbotLifecycleTests.MemoryProvisionStore provisions)
        => WorldTestHost.Start(configureServices: services =>
        {
            var options = new PlayerbotOptions
            {
                Enabled = true, RestoreOnStartup = true, MaxBots = 4, ThinkIntervalMs = 100, MaxActionsPerTick = 2, MaxPathPoints = 32,
                MaxRouteYards = 100, AllowedMaps = [0, 1],
            };
            services.AddSingleton<IAccountStore>(accounts);
            services.AddSingleton<IAccountAdmin>(accounts);
            services.AddSingleton<ICharacterStore>(characters);
            services.AddSingleton<ICharacterLifeStore>(characters);
            services.AddSingleton<IManagedPlayerbotStore>(owners);
            services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(options));
            services.AddSingleton<IManagedPlayerbotProvisionStore>(provisions);
            services.AddSingleton(keeper);
        });

    [Theory]
    [InlineData(Holding.Holder, true)]
    [InlineData(Holding.NonHolder, false)]
    [InlineData(Holding.Lost, false)]
    public async Task BotStartup_DeletesAnOrphanRegistryRow_OnlyOnTheMaintenanceHolder(Holding holding, bool deleted)
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var provisions = new ManagedPlayerbotLifecycleTests.MemoryProvisionStore(accounts);
        Account owner = await accounts.CreateAsync(new Account { Username = "PBLEASEORPHAN", Salt = new byte[32], Verifier = new byte[32], Security = AccountSecurity.Player });
        var bot = new ManagedPlayerbot(Guid.NewGuid(), owner.Id, 4242, owner.Username, true,
            ManagedPlayerbotState.Stopped, PlayerbotGoalKind.Explore, 0, 0, 0, 1, 1); // its character 4242 does not exist
        await owners.CreateAsync(bot);
        await using RealmAuthorityLeaseKeeper keeper = await KeeperAsync(holding);
        await using WorldTestHost host = StartBotHost(keeper, accounts, characters, owners, provisions);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();

        await feature.StartupAsync(default);

        Assert.Equal(deleted ? 0 : 1, (await owners.LoadAllAsync()).Count);
        Assert.DoesNotContain(feature.Snapshot(), b => b.BotId == bot.BotId); // never restored either way
    }

    [Theory]
    [InlineData(Holding.Holder, 1)]
    [InlineData(Holding.NonHolder, 0)]
    [InlineData(Holding.Lost, 0)]
    public async Task BotStartup_ReconcilesAPendingProvision_OnlyOnTheMaintenanceHolder(Holding holding, int rollbacks)
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var provisions = new ManagedPlayerbotLifecycleTests.MemoryProvisionStore(accounts);
        await provisions.CreateAsync(Guid.NewGuid(), new Account { Username = "PBLEASEPENDING", Salt = new byte[32], Verifier = new byte[32], Security = AccountSecurity.Player });
        await using RealmAuthorityLeaseKeeper keeper = await KeeperAsync(holding);
        await using WorldTestHost host = StartBotHost(keeper, accounts, characters, owners, provisions);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();

        await feature.StartupAsync(default);

        Assert.Equal(rollbacks, provisions.RollbackCount);
        Assert.Equal(1 - rollbacks, (await provisions.LoadPendingAsync()).Count);
    }

    [Theory]
    [InlineData(Holding.Holder, true)]
    [InlineData(Holding.NonHolder, false)]
    [InlineData(Holding.Lost, false)]
    public async Task BotFeature_HoldsTheMaintenanceLease_OnlyOnTheHolder(Holding holding, bool expected)
    {
        var accounts = new InMemoryAccountStore();
        await using RealmAuthorityLeaseKeeper keeper = await KeeperAsync(holding);
        await using WorldTestHost host = StartBotHost(keeper, accounts, new InMemoryCharacterStore(),
            new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore(), new ManagedPlayerbotLifecycleTests.MemoryProvisionStore(accounts));

        Assert.Equal(expected, host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>().HoldsMaintenanceLease);
    }

    [Theory]
    [InlineData(Holding.NonHolder)]
    [InlineData(Holding.Lost)]
    public async Task PopulationPass_OnANodeWithoutTheMaintenanceLease_CreatesNothing(Holding holding)
    {
        await using RealmAuthorityLeaseKeeper keeper = await KeeperAsync(holding);
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(configureServices: s => s.AddSingleton(keeper));
        world.Ticks.Set(200, 10); // a full window under the budget: a holder would create bots up to the target

        await world.PassAsync();
        await world.PassAsync();

        Assert.Equal(0, world.Feature.PassesStarted);            // no pass ran, so no bot was created and no membership write was attempted
        Assert.Empty(await world.Owners.LoadAllAsync());          // no registry row and so no orphan bot character, however many passes were asked for
        Assert.Empty(world.Members.Snapshot);
        Assert.Equal(0, world.Bots.Snapshot().Count);
    }

    [Fact]
    public async Task PopulationPass_OnTheMaintenanceHolder_CreatesTheBotsAndTheirMembership()
    {
        await using RealmAuthorityLeaseKeeper keeper = await KeeperAsync(Holding.Holder);
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(configureServices: s => s.AddSingleton(keeper));
        world.Ticks.Set(200, 10);

        await world.PassAsync();

        Assert.Equal(2, world.Members.Snapshot.Count);
        Assert.Equal(2, (await world.Owners.LoadAllAsync()).Count);
    }
}

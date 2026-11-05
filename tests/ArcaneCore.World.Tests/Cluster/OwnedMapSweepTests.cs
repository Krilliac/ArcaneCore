using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Data.Creatures;
using ArcaneCore.Data.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Cluster.Social;
using ArcaneCore.World.Tests.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Population;
using ArcaneCore.World.WorldState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>
/// Writer-lock design step C6b (section 4 rows 3 and 7, decision Q2): a worker's per-map sweeps are limited to the maps it owns. The expired creature and gameobject respawn deletes touch only
/// owned-map rows, the playerbot restore logs in only the bots whose character stands on an owned map, and a shed-only population pass still parks bots on a node that does not hold the
/// maintenance lease. Also the feature-level lease tests C6 left open: a population pass that loses the lease mid-pass, and the war effort and Scourge invasion world-state skips.
/// </summary>
public sealed class OwnedMapSweepTests
{
    private const string Node = "worker-a";
    private const long Now = 2_000_000_000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryRealmAuthorityLeaseStore _leases = new();

    public enum Holding
    {
        Holder,
        NonHolder,
        Lost,
    }

    private RealmAuthorityLeaseKeeper CreateKeeper(string ownedMaps = "", bool allowInstanceMaps = true)
        => new(_leases,
            Options.Create(new ClusterNodeOptions { Role = ClusterNodeRole.MapWorker, AuthorityLeaseTtlSeconds = 30, AuthorityLeaseRenewSeconds = 10 }),
            Options.Create(new GatewayWorkerOptions { Enabled = true, NodeId = Node, OwnedMaps = ownedMaps, AllowInstanceMaps = allowInstanceMaps }),
            NullLogger<RealmAuthorityLeaseKeeper>.Instance,
            lifetime: null,
            time: _time,
            setExitCode: _ => { }); // not the process-global ExitCodes.Current, which other tests assert on

    /// <summary>A keeper that holds every scope, never gets <paramref name="scope"/> (another node has it), or gets every scope and then loses <paramref name="scope"/> to a takeover.</summary>
    private async Task<RealmAuthorityLeaseKeeper> KeeperAsync(Holding holding, string scope)
    {
        Guid incarnation = Guid.NewGuid();
        if (holding == Holding.NonHolder)
            _leases.TryAcquire(scope, "other-node", Guid.NewGuid(), TimeSpan.FromMinutes(10), _time.GetUtcNow());

        RealmAuthorityLeaseKeeper keeper = CreateKeeper();
        await keeper.AcquireAtStartAsync(incarnation, CancellationToken.None);
        if (holding == Holding.Lost)
            LoseLease(keeper, scope, incarnation);

        Assert.Equal(holding == Holding.Holder, keeper.Holds(scope));
        return keeper;
    }

    /// <summary>Another node takes <paramref name="scope"/> over and the renewal tick finds it, so the keeper no longer holds it.</summary>
    private void LoseLease(RealmAuthorityLeaseKeeper keeper, string scope, Guid incarnation)
    {
        long token = keeper.TokenFor(scope)!.Value;
        _leases.TryRelease(scope, Node, incarnation, token);
        _leases.TryAcquire(scope, "other-node", Guid.NewGuid(), TimeSpan.FromMinutes(10), _time.GetUtcNow());
        _time.Advance(TimeSpan.FromSeconds(10)); // timers fire synchronously on this thread
        SpinWait.SpinUntil(() => !keeper.Holds(scope), TimeSpan.FromSeconds(5));
    }

    // ---- the owned map set

    [Fact]
    public async Task TheKeeper_ExposesTheWorkersOwnedMaps_AndEveryMapWhenTheListIsEmpty()
    {
        await using RealmAuthorityLeaseKeeper everything = CreateKeeper();
        Assert.True(everything.OwnedMaps.OwnsEverything);

        await using RealmAuthorityLeaseKeeper worker = CreateKeeper("0,30-40", allowInstanceMaps: false);
        Assert.False(worker.OwnedMaps.OwnsEverything);
        Assert.True(worker.OwnedMaps.Owns(0, 0));
        Assert.True(worker.OwnedMaps.Owns(35, 0));
        Assert.False(worker.OwnedMaps.Owns(1, 0));
        Assert.False(worker.OwnedMaps.Owns(409, 7)); // an instance copy while AllowInstanceMaps is off

        await using RealmAuthorityLeaseKeeper withInstances = CreateKeeper("0", allowInstanceMaps: true);
        Assert.True(withInstances.OwnedMaps.Owns(409, 7));
        Assert.False(withInstances.OwnedMaps.Owns(409, 0));
    }

    [Fact]
    public void AFenceSourceThatSaysNothing_OwnsEveryMap()
    {
        IRealmLeaseFenceSource source = new BareSource();

        Assert.True(source.OwnedMaps.OwnsEverything);
        Assert.True(source.OwnedMaps.Owns(1, 0));
    }

    private sealed class BareSource : IRealmLeaseFenceSource
    {
        public RealmLeaseFence? FenceFor(string scope) => null;
    }

    private sealed class OwnedSource(OwnedMapSet maps) : IRealmLeaseFenceSource
    {
        public RealmLeaseFence? FenceFor(string scope) => null;

        public bool Holds(string scope) => false;

        public OwnedMapSet OwnedMaps => maps;
    }

    // ---- respawn expired deletes

    private static void TryDelete(string path)
    {
        foreach (string file in new[] { path, path + "-wal", path + "-shm" })
        {
            try { File.Delete(file); }
            catch (IOException) { /* a leftover temp file is harmless */ }
        }
    }

    private static async Task<List<(uint MapId, int InstanceId)>> SeedAndLoadAsync(string path, IRealmLeaseFenceSource? source, bool creatures)
    {
        var options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        await using (var db = new CharacterDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            (uint Map, int Instance)[] rows = [(0, 0), (1, 0), (30, 0), (33, 9)];
            uint guid = 1;
            foreach ((uint map, int instance) in rows)
            {
                if (creatures)
                    db.Set<CreatureRespawnRow>().Add(new CreatureRespawnRow { InstanceId = instance, SpawnGuid = guid++, MapId = map, RespawnTime = Now - 100 });
                else
                    db.Set<GameObjectRespawnRow>().Add(new GameObjectRespawnRow { InstanceId = instance, SpawnGuid = guid++, MapId = map, RespawnTime = Now - 100 });
            }

            await db.SaveChangesAsync();
        }

        await using (var db = new CharacterDbContext(options))
        {
            if (creatures)
                await new EfCreatureRespawnStore(db, source).LoadAsync(Now);
            else
                await new EfGameObjectRespawnStore(db, source).LoadAsync(Now);
        }

        await using var read = new CharacterDbContext(options);
        return creatures
            ? [.. (await read.Set<CreatureRespawnRow>().AsNoTracking().OrderBy(r => r.SpawnGuid).ToListAsync()).Select(r => (r.MapId, r.InstanceId))]
            : [.. (await read.Set<GameObjectRespawnRow>().AsNoTracking().OrderBy(r => r.SpawnGuid).ToListAsync()).Select(r => (r.MapId, r.InstanceId))];
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARealmNode_DeletesEveryExpiredRespawnRow(bool creatures)
    {
        string path = Path.Combine(Path.GetTempPath(), $"arcanecore-c6b-respawn-{Guid.NewGuid():N}.db");
        try
        {
            Assert.Empty(await SeedAndLoadAsync(path, null, creatures));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task AWorker_DeletesOnlyTheExpiredRespawnRowsOfItsOwnedMaps(bool creatures, bool allowInstanceMaps)
    {
        string path = Path.Combine(Path.GetTempPath(), $"arcanecore-c6b-respawn-{Guid.NewGuid():N}.db");
        try
        {
            var owned = new OwnedMapSet([(0u, 1u)], allowInstanceMaps);

            List<(uint MapId, int InstanceId)> left = await SeedAndLoadAsync(path, new OwnedSource(owned), creatures);

            // Maps 0 and 1 are the worker's: gone. Map 30 is another worker's: kept. The instance copy of map 33 is the worker's only while instance maps are allowed.
            List<(uint MapId, int InstanceId)> expected = allowInstanceMaps ? [(30u, 0)] : [(30u, 0), (33u, 9)];
            Assert.Equal(expected, left);
        }
        finally
        {
            TryDelete(path);
        }
    }

    // ---- bot restore by map

    private static WorldTestHost StartRestoreHost(InMemoryAccountStore accounts, InMemoryCharacterStore characters,
        ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore owners)
        => WorldTestHost.Start(configureServices: services =>
        {
            var options = new PlayerbotOptions
            {
                Enabled = true, RestoreOnStartup = true, MaxBots = 4, ThinkIntervalMs = 100, MaxActionsPerTick = 2, MaxPathPoints = 32, MaxRouteYards = 100, AllowedMaps = [0, 1],
            };
            services.AddSingleton<IAccountStore>(accounts);
            services.AddSingleton<IAccountAdmin>(accounts);
            services.AddSingleton<ICharacterStore>(characters);
            services.AddSingleton<ICharacterLifeStore>(characters);
            services.AddSingleton<IManagedPlayerbotStore>(owners);
            services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(options));
            services.AddSingleton<IManagedPlayerbotProvisionStore>(new ManagedPlayerbotLifecycleTests.MemoryProvisionStore(accounts));
        });

    [Fact]
    public async Task ABotRestore_StartsOnlyTheBotsWhoseCharacterStandsOnAnOwnedMap()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        Guid mine;
        Guid theirs;
        await using (WorldTestHost first = StartRestoreHost(accounts, characters, owners))
        {
            ManagedPlayerbotFeature creator = first.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
            await creator.StartupAsync(default);
            mine = Assert.IsType<Guid>((await creator.CreateAsync("Ownedmapone", 1, 1)).BotId);
            theirs = Assert.IsType<Guid>((await creator.CreateAsync("Othermapone", 1, 1)).BotId);
            await creator.ShutdownBeforeWorldStopAsync();
        }

        // Both bots are desired-enabled; the second one's character stands on map 1, which another worker owns.
        owners.ChangeOutOfBand(mine, b => b with { DesiredEnabled = true });
        owners.ChangeOutOfBand(theirs, b => b with { DesiredEnabled = true });
        CharacterRecord mineCharacter = (await characters.GetByIdAsync((await owners.FindAsync(mine))!.CharacterId))!;
        CharacterRecord theirCharacter = (await characters.GetByIdAsync((await owners.FindAsync(theirs))!.CharacterId))!;
        mineCharacter.MapId = 0;
        theirCharacter.MapId = 1;

        await using WorldTestHost second = StartRestoreHost(accounts, characters, owners);
        second.World.Ownership = new MapOwnership([new MapOwnership.Range(0, 0)], allowInstanceMaps: false);
        ManagedPlayerbotFeature feature = second.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);

        // Read on the world thread: off it, Snapshot() is the copy the last tick published, which may predate the restore's start of the owned bot.
        PlayerbotStatus[] snapshot = [.. await second.OnWorldAsync(() => feature.Snapshot())];
        Assert.NotEqual(ManagedPlayerbotState.Stopped, Assert.Single(snapshot, s => s.BotId == mine).State);
        Assert.Equal(ManagedPlayerbotState.Stopped, Assert.Single(snapshot, s => s.BotId == theirs).State);
        Assert.Null(feature.FindSession(theirs));
        await feature.ShutdownBeforeWorldStopAsync();
    }

    // ---- a shed-only pass on a non-holder, and a mid-pass lease loss

    [Fact]
    public async Task AShedOnlyPass_OnANodeThatLostTheMaintenanceLease_StillParksBotsForLoad()
    {
        Guid incarnation = Guid.NewGuid();
        await using RealmAuthorityLeaseKeeper keeper = CreateKeeper();
        await keeper.AcquireAtStartAsync(incarnation, CancellationToken.None);
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(configureServices: s => s.AddSingleton(keeper));
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(2);
        int membersBefore = world.Members.Snapshot.Count;

        LoseLease(keeper, RealmAuthorityScopes.Maintenance, incarnation);
        Assert.False(world.Bots.HoldsMaintenanceLease);
        world.Ticks.Set(200, 500); // far over the 60 ms budget
        await world.Feature.RunPassNowAsync(shedOnly: true);

        Assert.True(world.RunningCount < 2, "the shed-only pass parked no bot although only the maintenance lease was lost");
        Assert.Equal(membersBefore, world.Members.Snapshot.Count); // parking keeps the membership rows
    }

    [Fact]
    public async Task APassThatLosesTheMaintenanceLeaseMidPass_CreatesNoBotWithoutAMembershipRow()
    {
        Guid incarnation = Guid.NewGuid();
        await using RealmAuthorityLeaseKeeper keeper = CreateKeeper();
        await keeper.AcquireAtStartAsync(incarnation, CancellationToken.None);
        // The lease is taken over while the pass builds its race and class cache, which it does after the pass's own lease check and inside the first creation.
        var store = new LeaseLosingStore(() => LoseLease(keeper, RealmAuthorityScopes.Maintenance, incarnation));
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(
            configureServices: s => s.AddSingleton(keeper), dataStore: () => store);
        world.Ticks.Set(200, 10); // a holder would create two bots

        await world.PassAsync();

        Assert.True(store.Lost, "the lease was never lost during the pass");
        IReadOnlyList<ManagedPlayerbot> registry = await world.Owners.LoadAllAsync();
        Assert.True(registry.Count < 2, "the pass kept creating bots after the lease was lost");
        Assert.Equal(registry.Select(b => b.BotId).Order(), world.Members.Snapshot.Select(m => m.BotId).Order()); // every registry row has its membership row: no orphan
    }

    /// <summary>A world data store that runs <paramref name="onFirstAsk"/> once, the first time it is asked whether a race and class pair is valid.</summary>
    private sealed class LeaseLosingStore(Action onFirstAsk) : IWorldDataStore
    {
        private readonly InMemoryWorldDataStore _inner = new();
        private int _asked;

        public bool Lost => Volatile.Read(ref _asked) != 0;

        public Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default) => _inner.GetStartPositionAsync(race, cls, cancellationToken);

        public Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default) => _inner.GetRaceInfoAsync(race, gender, cancellationToken);

        public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default) => _inner.GetClassInfoAsync(cls, cancellationToken);

        public Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _asked, 1) == 0) onFirstAsk();
            return _inner.IsValidRaceClassAsync(race, cls, cancellationToken);
        }
    }

    // ---- world-state features skip their durable writes without the worldstate lease

    private sealed class WarStore : IWarEffortStateStore
    {
        private int _phaseWrites;
        private int _bossMarks;

        public int PhaseWrites => Volatile.Read(ref _phaseWrites);

        public int BossMarks => Volatile.Read(ref _bossMarks);

        public Task<WarEffortSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(WarEffortSnapshot.Disabled);

        public Task SetPhaseAsync(WarEffortPhase phase, long phaseEndsAtUnix, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _phaseWrites);
            return Task.CompletedTask;
        }

        public Task<bool> MarkBossKilledAsync(int bossIndex, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _bossMarks);
            return Task.FromResult(true);
        }
    }

    private sealed class ScourgeStore : IScourgeInvasionStateStore
    {
        private int _starts;

        public int Starts => Volatile.Read(ref _starts);

        public Task<ScourgeInvasionSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(ScourgeInvasionSnapshot.Disabled);

        public Task<bool> StartAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _starts);
            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> NecropolisDestroyedAsync(uint zoneId, uint spawnGuid, long nowUnix, int nextAttackSeconds, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> RestartZoneAsync(uint zoneId, long nowUnix, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private static WorldRuntime NewWorld(ServiceProvider provider)
        => new(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);

    [Theory]
    [InlineData(Holding.Holder, 1)]
    [InlineData(Holding.NonHolder, 0)]
    [InlineData(Holding.Lost, 0)]
    public async Task WarEffortFeature_WritesItsPhase_OnlyOnTheWorldStateHolder(Holding holding, int phaseWrites)
    {
        await using RealmAuthorityLeaseKeeper keeper = await KeeperAsync(holding, RealmAuthorityScopes.WorldState);
        var store = new WarStore();
        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<IWarEffortStateStore>(store).AddSingleton(keeper).BuildServiceProvider();
        var events = new GameEventFeature(provider, NullLogger<GameEventFeature>.Instance);
        var war = new WarEffortFeature(provider.GetRequiredService<IServiceScopeFactory>(), events, NullLogger<WarEffortFeature>.Instance);
        using WorldRuntime world = NewWorld(provider);
        war.Attach(world);
        Assert.Equal(holding == Holding.Holder, war.HoldsWorldStateLease);

        war.OnEventChanged(WarEffortCatalog.GatheringEvent, active: true, resume: false); // .event start 120: queues the phase write
        await war.StoreWork!.InFlight.WaitAsync(Budget);

        Assert.Equal(phaseWrites, store.PhaseWrites);
        Assert.Equal(0, store.BossMarks);
    }

    [Theory]
    [InlineData(Holding.Holder, 1)]
    [InlineData(Holding.NonHolder, 0)]
    [InlineData(Holding.Lost, 0)]
    public async Task ScourgeInvasionFeature_WritesItsQueuedStart_OnlyOnTheWorldStateHolder(Holding holding, int starts)
    {
        await using RealmAuthorityLeaseKeeper keeper = await KeeperAsync(holding, RealmAuthorityScopes.WorldState);
        var store = new ScourgeStore();
        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<IScourgeInvasionStateStore>(store).AddSingleton(keeper).BuildServiceProvider();
        var events = new GameEventFeature(provider, NullLogger<GameEventFeature>.Instance);
        var scourge = new ScourgeInvasionFeature(provider.GetRequiredService<IServiceScopeFactory>(), events, NullLogger<ScourgeInvasionFeature>.Instance);
        using WorldRuntime world = NewWorld(provider);
        scourge.Attach(world);
        Assert.Equal(holding == Holding.Holder, scourge.HoldsWorldStateLease);

        scourge.OnEventChanged(ScourgeInvasionCatalog.MainEvent, active: true, resume: false); // queues the start write
        await scourge.StoreWork!.InFlight.WaitAsync(Budget);

        Assert.Equal(starts, store.Starts);
    }
}

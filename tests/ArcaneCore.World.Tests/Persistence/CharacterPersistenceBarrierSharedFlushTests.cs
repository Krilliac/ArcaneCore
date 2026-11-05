using System.Reflection;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.OutdoorPvP;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Bans;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.OutdoorPvP;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Social;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Persistence;

/// <summary>
/// <see cref="CharacterPersistenceBarrier.FlushSharedWritesAsync"/> itself, over the queues a handoff added to it (group, GM audit, account data, account address, both respawn queues,
/// and the four world-state save queues): every queue is resolved and attempted even when another one faults, one failure is thrown as it is, several become one
/// <see cref="InvalidOperationException"/> wrapping an <see cref="AggregateException"/>, and a store's lease refusal does not refuse the handoff.
/// </summary>
public sealed class CharacterPersistenceBarrierSharedFlushTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    /// <summary>A store that is only ever resolved, never called: the tests save a write that carries its own verdict.</summary>
    public class UnusedStore : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new NotSupportedException("the test's writes never call the store");
    }

    private sealed class DownCreatureStore : ICreatureRespawnStore
    {
        public Task<IReadOnlyList<CreatureRespawnRecord>> LoadAsync(long nowUnixSeconds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CreatureRespawnRecord>>([]);

        public Task SaveAsync(IReadOnlyCollection<CreatureRespawnRecord> upserts, IReadOnlyCollection<CreatureRespawnKey> deletes, CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("storage is down"));

        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DownObjectStore : IGameObjectRespawnStore
    {
        public Task<IReadOnlyList<GameObjectRespawnRecord>> LoadAsync(long nowUnixSeconds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameObjectRespawnRecord>>([]);

        public Task SaveAsync(IReadOnlyCollection<GameObjectRespawnRecord> upserts, IReadOnlyCollection<GameObjectRespawnKey> deletes, CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("storage is down"));

        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DownFishingStore : IFishingExtravaganzaStore
    {
        public Task<FishingExtravaganzaState?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<FishingExtravaganzaState?>(null);

        public Task SaveAsync(FishingExtravaganzaState state, CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("storage is down"));
    }

    private sealed class DownDragonStore : INightmareDragonsStore
    {
        public Task<NightmareDragonsState?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<NightmareDragonsState?>(null);

        public Task SaveAsync(NightmareDragonsState state, CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("storage is down"));
    }

    private sealed class DownInvasionStore : IElementalInvasionStore
    {
        public Task<IReadOnlyList<ElementalInvasionState>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ElementalInvasionState>>([]);

        public Task SaveAsync(ElementalInvasionState state, CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("storage is down"));
    }

    private sealed class DownSilithystStore : ISilithystStore
    {
        public Task<SilithystState?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<SilithystState?>(null);

        public Task SaveAsync(SilithystState state, CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("storage is down"));
    }

    /// <summary>The four keyed-queue features, attached over stores that are only resolved.</summary>
    private sealed class KeyedRig : IAsyncDisposable
    {
        public ServiceProvider Services { get; }

        public GmAuditFeature Audit { get; }

        public AccountDataFeature AccountData { get; }

        public AccountAddressFeature Address { get; }

        public SocialGroupPersistenceFeature Group { get; }

        public KeyedRig(Action<IServiceCollection>? more = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(new SessionRegistry());
            services.AddScoped(_ => DispatchProxy.Create<IGmAuditStore, UnusedStore>());
            services.AddScoped(_ => DispatchProxy.Create<IAccountDataStore, UnusedStore>());
            services.AddScoped(_ => DispatchProxy.Create<IAccountAddressStore, UnusedStore>());
            services.AddScoped(_ => DispatchProxy.Create<IGroupStore, UnusedStore>());
            more?.Invoke(services);
            Services = services.BuildServiceProvider();
            var scopes = Services.GetRequiredService<IServiceScopeFactory>();
            var loggers = Services.GetRequiredService<ILoggerFactory>();

            Audit = new GmAuditFeature(scopes, loggers);
            Audit.Writes.Start();

            AccountData = new AccountDataFeature(Services, scopes, loggers);
            AccountData.Attach(null!); // the world is not used by this feature's attach

            Address = new AccountAddressFeature(Services, scopes, loggers);
            Address.Attach(null!); // likewise

            // The group feature attaches against a social feature and a database; its queue is the shared one, put in place directly.
            Group = new SocialGroupPersistenceFeature(Services, scopes, loggers);
            var groupWrites = new KeyedStoreWriteQueue<IGroupStore>(scopes, NullLogger.Instance, "group");
            groupWrites.Start();
            typeof(SocialGroupPersistenceFeature).GetProperty(nameof(SocialGroupPersistenceFeature.Writes))!.SetValue(Group, groupWrites);
        }

        public static Task Down(string key) => Task.FromException(new InvalidOperationException("storage is down for " + key));

        public CharacterPersistenceBarrier Barrier(params object[] extra)
        {
            var services = new ServiceCollection();
            services.AddSingleton(Audit);
            services.AddSingleton(AccountData);
            services.AddSingleton(Address);
            services.AddSingleton(Group);
            foreach (object feature in extra)
            {
                services.AddSingleton(feature.GetType(), feature);
            }

            return new CharacterPersistenceBarrier(services.BuildServiceProvider());
        }

        public async ValueTask DisposeAsync()
        {
            foreach (Func<Task> stop in new Func<Task>[] { Audit.Writes.StopAsync, () => AccountData.Writes!.StopAsync(), () => Address.Writes!.StopAsync(), () => Group.Writes!.StopAsync() })
            {
                try
                {
                    await stop();
                }
                catch (InvalidOperationException)
                {
                    // a test that ends with a retained write
                }
            }

            await Services.DisposeAsync();
        }
    }

    private static WorldRuntime NewWorld(IServiceProvider sp)
        => new(
            new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);

    [Fact]
    public async Task EveryAddedQueueIsResolvedAndAttempted_AndSeveralFailuresBecomeOneAggregate()
    {
        await using var rig = new KeyedRig(s =>
        {
            s.AddSingleton<ICreatureRespawnStore>(new DownCreatureStore());
            s.AddSingleton<IGameObjectRespawnStore>(new DownObjectStore());
            s.AddSingleton<IFishingExtravaganzaStore>(new DownFishingStore());
            s.AddSingleton<INightmareDragonsStore>(new DownDragonStore());
            s.AddSingleton<IElementalInvasionStore>(new DownInvasionStore());
            s.AddSingleton<ISilithystStore>(new DownSilithystStore());
        });
        var scopes = rig.Services.GetRequiredService<IServiceScopeFactory>();

        rig.Audit.Writes.Save("mute:1", _ => KeyedRig.Down("mute:1"));
        rig.AccountData.Writes!.Save("account:1", _ => KeyedRig.Down("account:1"));
        rig.Address.Writes!.Save("address:1", _ => KeyedRig.Down("address:1"));
        rig.Group.Writes!.Save("group:1", _ => KeyedRig.Down("group:1"));

        using WorldRuntime world = NewWorld(rig.Services);
        var creatures = new CreatureRespawnFeature(rig.Services, rig.Services.GetRequiredService<ILogger<CreatureRespawnFeature>>());
        creatures.Attach(world);
        var objects = new GameObjectRespawnFeature(rig.Services, rig.Services.GetRequiredService<ILogger<GameObjectRespawnFeature>>());
        objects.Attach(world);
        ((CreatureRespawnQueue)creatures.Persistence!).Save(0, 0, 1, 4_000_000_000);
        ((GameObjectRespawnQueue)objects.Persistence!).Save(0, 0, 1, 4_000_000_000);

        var events = new GameEventFeature(rig.Services, NullLogger<GameEventFeature>.Instance);
        var fishing = new FishingExtravaganzaFeature(scopes, events);
        fishing.PreviousWinTime = fishing.NowUnix() - (2 * 86400);
        fishing.ResetIfStale();

        var dragons = new NightmareDragonsFeature(scopes)
        {
            NowUnix = () => 1_000_000,
            StatusOverride = _ => NightmareDragonsFeature.DragonStatus.Alive,
            SpawnOverride = (_, _) => { },
            DespawnOverride = _ => { },
        };
        dragons.Update();

        var holiday = new HolidayObjectsFeature(scopes, new GameEventFeature(rig.Services, NullLogger<GameEventFeature>.Instance));
        holiday.Attach(world);
        Assert.True(holiday.Invasion.TryAdvance(0, hourElapsed: true));

        var outdoorPvP = new OutdoorPvPFeature(rig.Services, NullLogger<OutdoorPvPFeature>.Instance);
        outdoorPvP.Attach(world);
        world.RunTick(1);
        world.GetMap(1);
        SilithusZone zone = Assert.IsType<SilithusZone>(outdoorPvP.Silithus);
        zone.Saved!(3, 4, zone.MaxResources);

        await creatures.FlushAsync().WaitAsync(HangGuard);
        await objects.FlushAsync().WaitAsync(HangGuard);

        CharacterPersistenceBarrier barrier = rig.Barrier(creatures, objects, fishing, dragons, holiday, outdoorPvP);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => barrier.FlushSharedWritesAsync());

        AggregateException all = Assert.IsType<AggregateException>(ex.InnerException);
        Assert.Equal("10 shared write queues are not durable", ex.Message);
        Assert.Equal(10, all.InnerExceptions.Count); // a branch that resolved nothing would be skipped silently and leave fewer
        string joined = string.Join(" | ", all.InnerExceptions.Select(e => e.Message));
        Assert.Contains("group writes are not durable: group:1", joined);
        Assert.Contains("GM audit writes are not durable: mute:1", joined);
        Assert.Contains("account data writes are not durable: account:1", joined);
        Assert.Contains("account address writes are not durable: address:1", joined);

        await creatures.StopAsync().ContinueWith(_ => { }); // the respawn queues end with retained writes
        await objects.StopAsync().ContinueWith(_ => { });
    }

    [Fact]
    public async Task ALaterQueueIsStillAttemptedAfterAnEarlierOneFaults_AndASingleFailureIsThrownAsItIs()
    {
        await using var rig = new KeyedRig();
        int addressAttempts = 0;
        bool addressLanded = false;
        rig.Audit.Writes.Save("mute:1", _ => KeyedRig.Down("mute:1")); // the earlier queue never lands
        rig.Address.Writes!.Save("address:1", _ =>
        {
            // fails the queue's own three attempts, then lands on the handoff's retry
            if (Interlocked.Increment(ref addressAttempts) <= 3)
            {
                return KeyedRig.Down("address:1");
            }

            addressLanded = true;
            return Task.CompletedTask;
        });
        await rig.Audit.Writes.FlushAsync().WaitAsync(HangGuard);
        await rig.Address.Writes.FlushAsync().WaitAsync(HangGuard);
        Assert.Equal(["address:1"], rig.Address.Writes.RetainedKeys);

        CharacterPersistenceBarrier barrier = rig.Barrier();
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => barrier.FlushSharedWritesAsync());

        Assert.Equal("GM audit writes are not durable: mute:1", ex.Message); // the one failure, not wrapped in an aggregate
        Assert.IsNotType<AggregateException>(ex.InnerException);
        Assert.True(Volatile.Read(ref addressLanded), "the queue after the failing one was not attempted");
        Assert.Empty(rig.Address.Writes.RetainedKeys);
    }

    [Fact]
    public async Task TwoFailingKeyedQueuesAreWrappedInOneAggregate()
    {
        await using var rig = new KeyedRig();
        rig.Audit.Writes.Save("mute:1", _ => KeyedRig.Down("mute:1"));
        rig.AccountData.Writes!.Save("account:1", _ => KeyedRig.Down("account:1"));
        await rig.Audit.Writes.FlushAsync().WaitAsync(HangGuard);
        await rig.AccountData.Writes.FlushAsync().WaitAsync(HangGuard);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Barrier().FlushSharedWritesAsync());

        Assert.Equal("2 shared write queues are not durable", ex.Message);
        AggregateException all = Assert.IsType<AggregateException>(ex.InnerException);
        Assert.Equal(2, all.InnerExceptions.Count);
    }

    [Fact]
    public async Task ALeaseRefusalOfANonHolder_DoesNotRefuseTheHandoff_ButAGenuineFailureStillDoes()
    {
        await using var rig = new KeyedRig();
        rig.Audit.Writes.Save("ticket:7", _ => Task.FromException(new RealmLeaseNotHeldException("social"))); // a ticket on a worker without the Social lease
        rig.Group.Writes!.Save("group:3", _ => Task.FromException(new RealmLeaseNotHeldException("social")));
        await rig.Audit.Writes.FlushAsync().WaitAsync(HangGuard);
        await rig.Group.Writes.FlushAsync().WaitAsync(HangGuard);
        Assert.Equal(["ticket:7"], rig.Audit.Writes.RetainedKeys);

        CharacterPersistenceBarrier barrier = rig.Barrier();
        await barrier.FlushSharedWritesAsync().WaitAsync(HangGuard); // nothing is owed: the holder writes the stored value

        Assert.Equal(["ticket:7"], rig.Audit.Writes.RetainedKeys); // still retained: stop names it, a regained lease lands it
        Assert.Equal(["group:3"], rig.Group.Writes.RetainedKeys);

        rig.Audit.Writes.Save("mute:2", _ => KeyedRig.Down("mute:2"));
        await rig.Audit.Writes.FlushAsync().WaitAsync(HangGuard);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => barrier.FlushSharedWritesAsync());
        Assert.Equal("GM audit writes are not durable: mute:2", ex.Message); // names the genuine key only
    }
}

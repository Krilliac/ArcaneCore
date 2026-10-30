using ArcaneCore.Game;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Tests.Instances;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// The wiring that makes game object respawn persistence work in the daemon (the real <see cref="GameObjectLootFeature"/> plus
/// <see cref="GameObjectRespawnFeature"/> in a <see cref="WorldTestHost"/>): a despawned chest is written to the store, and a restarted host
/// loads it still despawned until its time passes (vmangos GameObject::SaveRespawnTime, Objects/GameObject.cpp:1118-1122, and
/// GameObject::LoadFromDB :1002-1011). These fail if the feature stops passing its persistence and clock to the object systems, or stops
/// restoring at grid load. The creature counterpart is <c>CreatureRespawnHostedTests</c>.
/// </summary>
public sealed class GameObjectRespawnHostedTests
{
    private const uint ChestEntry = 180001;
    private const uint SpawnGuid = 77101;

    private sealed class SharedStore : IGameObjectRespawnStore
    {
        private readonly Dictionary<(uint Instance, uint Guid), GameObjectRespawnRecord> _rows = [];

        public IReadOnlyList<GameObjectRespawnRecord> Rows
        {
            get
            {
                lock (_rows)
                {
                    return [.. _rows.Values];
                }
            }
        }

        public Task<IReadOnlyList<GameObjectRespawnRecord>> LoadAsync(long nowUnixSeconds, CancellationToken cancellationToken = default)
        {
            lock (_rows)
            {
                return Task.FromResult<IReadOnlyList<GameObjectRespawnRecord>>([.. _rows.Values.Where(r => r.RespawnTime > nowUnixSeconds)]);
            }
        }

        public Task SaveAsync(IReadOnlyCollection<GameObjectRespawnRecord> upserts, IReadOnlyCollection<GameObjectRespawnKey> deletes, CancellationToken cancellationToken = default)
        {
            lock (_rows)
            {
                foreach (GameObjectRespawnRecord upsert in upserts)
                {
                    _rows[(upsert.InstanceId, upsert.SpawnGuid)] = upsert;
                }

                foreach (GameObjectRespawnKey delete in deletes)
                {
                    _rows.Remove((delete.InstanceId, delete.SpawnGuid));
                }
            }

            return Task.CompletedTask;
        }

        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default)
        {
            lock (_rows)
            {
                foreach ((uint Instance, uint Guid) key in _rows.Keys.Where(k => k.Instance == instanceId).ToArray())
                {
                    _rows.Remove(key);
                }
            }

            return Task.CompletedTask;
        }

        public void Add(GameObjectRespawnRecord record)
        {
            lock (_rows)
            {
                _rows[(record.InstanceId, record.SpawnGuid)] = record;
            }
        }
    }

    private static WorldTestHost Start(SharedStore store, out GameObjectTestContext context)
    {
        var chest = new GameObjectTemplate { Entry = ChestEntry, Type = (uint)GameObjectType.Chest, DisplayId = 259, Name = "Mined Node", Data = new uint[GameObjectTemplate.DataCount] };
        var spawn = new GameObjectSpawn { Guid = SpawnGuid, Entry = ChestEntry, MapId = 0, X = -8948f, Y = -132.5f, Z = 83.5f, SpawnTimeSeconds = 600 };
        context = new GameObjectTestContext(new GameObjectContent([chest], [spawn], [], [], []), LootContent.Empty);
        GameObjectTestStore.Current.Value = context;
        try
        {
            return WorldTestHost.Start(configureServices: services => services.AddSingleton<IGameObjectRespawnStore>(store));
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
        }
    }

    private static GameObject? Chest(GameObjectLootFeature feature)
        => feature.FindSystem(0)?.Find(new ObjectGuid(ObjectGuid.WithEntry(HighGuid.GameObject, ChestEntry, SpawnGuid).Value));

    [Fact]
    public async Task ADespawnedChest_ReachesTheStore_AndARestartedHostLoadsItStillDespawned()
    {
        var store = new SharedStore();
        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using (WorldTestHost first = Start(store, out GameObjectTestContext context))
        {
            await using WorldTestClient client = await first.EnterWorldAsync("GORSPA", "Gorspa");
            GameObjectLootFeature feature = context.Feature!;
            await first.WaitForWorldAsync(() => Chest(feature) is not null, "the chest loads");

            await first.OnWorldAsync(() => feature.FindSystem(0)!.Despawn(Chest(feature)!));
            await first.WorldServices.GetRequiredService<GameObjectRespawnFeature>().FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));

            GameObjectRespawnRecord row = Assert.Single(store.Rows);
            Assert.Equal((0u, 0u, SpawnGuid), (row.MapId, row.InstanceId, row.SpawnGuid));
            Assert.InRange(row.RespawnTime, before + 590, before + 700); // the 600 s spawn delay, as a wall-clock time
        }

        await using WorldTestHost second = Start(store, out GameObjectTestContext secondContext);
        await using WorldTestClient again = await second.EnterWorldAsync("GORSPB", "Gorspb");
        GameObjectLootFeature restarted = secondContext.Feature!;
        await second.WaitForWorldAsync(() => Chest(restarted) is not null, "the chest loads again");

        (bool spawned, long? remaining) = await second.OnWorldAsync(() =>
        {
            GameObject go = Chest(restarted)!;
            return (go.IsSpawned, restarted.FindSystem(0)!.RespawnRemainingMs(go));
        });
        Assert.False(spawned);
        Assert.InRange(remaining ?? -1, 1, 600_000);
    }

    [Fact]
    public async Task AStoredFutureTime_KeepsTheChestDown_AndARespawnDeletesTheRow()
    {
        var store = new SharedStore();
        store.Add(new GameObjectRespawnRecord(0, 0, SpawnGuid, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1000));

        await using WorldTestHost host = Start(store, out GameObjectTestContext context);
        await using WorldTestClient client = await host.EnterWorldAsync("GORSPC", "Gorspc");
        GameObjectLootFeature feature = context.Feature!;
        await host.WaitForWorldAsync(() => Chest(feature) is not null, "the chest loads");
        Assert.False(await host.OnWorldAsync(() => Chest(feature)!.IsSpawned));

        await host.OnWorldAsync(() => feature.FindSystem(0)!.ForceRespawn(Chest(feature)!));
        await host.WorldServices.GetRequiredService<GameObjectRespawnFeature>().FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(await host.OnWorldAsync(() => Chest(feature)!.IsSpawned));
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task AnExpiredRow_IsNotLoaded_SoTheChestSpawns()
    {
        var store = new SharedStore();
        store.Add(new GameObjectRespawnRecord(0, 0, SpawnGuid, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5));

        await using WorldTestHost host = Start(store, out GameObjectTestContext context);
        await using WorldTestClient client = await host.EnterWorldAsync("GORSPD", "Gorspd");
        GameObjectLootFeature feature = context.Feature!;
        await host.WaitForWorldAsync(() => Chest(feature) is not null, "the chest loads");

        Assert.True(await host.OnWorldAsync(() => Chest(feature)!.IsSpawned));
    }

    [Fact]
    public async Task ADeletedInstance_TakesItsGameObjectRespawnTimesWithIt_ThroughTheQueue()
    {
        // The stored instance 5 has no binds and an unknown map, so the instance manager drops it while it loads; that deletion must reach the
        // game object respawn queue, which forgets the times of the instance in memory and removes its rows behind any save of it still queued.
        var store = new SharedStore();
        long future = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 100_000;
        store.Add(new GameObjectRespawnRecord(99999, 5, 77, future));
        store.Add(new GameObjectRespawnRecord(0, 0, 78, future));
        InMemoryInstanceStore.Seed.Value = new InstanceStoreSnapshot([new InstanceRecord(5, 99999, 0)], [], [], []);
        try
        {
            await using WorldTestHost host = Start(store, out _);
            GameObjectRespawnFeature feature = host.WorldServices.GetRequiredService<GameObjectRespawnFeature>();
            await host.WaitForWorldAsync(() => feature.Persistence!.GetPending(99999, 5).Count == 0, "the deleted instance times are forgotten");
            await feature.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));

            GameObjectRespawnRecord remaining = Assert.Single(store.Rows);
            Assert.Equal((0u, 0u, 78u), (remaining.MapId, remaining.InstanceId, remaining.SpawnGuid)); // the shared copy of a map stays
        }
        finally
        {
            InMemoryInstanceStore.Seed.Value = null;
        }
    }
}

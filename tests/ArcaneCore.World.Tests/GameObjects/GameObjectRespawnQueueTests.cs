using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// The game object respawn queue shares its body with the creature queue (<see cref="RespawnWriteQueue{TRecord, TKey, TStore}"/>); the creature queue's
/// own tests cover batching and retries, these pin that the game object instantiation reaches its own store type, retains a failed write and
/// throws at stop while one is still not durable.
/// </summary>
public sealed class GameObjectRespawnQueueTests
{
    private const long Now = 1_700_000_000;

    private sealed class FakeStore : IGameObjectRespawnStore
    {
        public volatile bool Down;

        public List<GameObjectRespawnRecord> Saved { get; } = [];

        public List<GameObjectRespawnKey> Deleted { get; } = [];

        /// <summary>Every call in the order it reached the store: <c>save:GUID</c>, <c>delete:GUID</c> or <c>instance:ID</c>.</summary>
        public List<string> Calls { get; } = [];

        public List<uint> InstancesDeleted { get; } = [];

        public Task<IReadOnlyList<GameObjectRespawnRecord>> LoadAsync(long nowUnixSeconds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameObjectRespawnRecord>>([]);

        public Task SaveAsync(
            IReadOnlyCollection<GameObjectRespawnRecord> upserts, IReadOnlyCollection<GameObjectRespawnKey> deletes, CancellationToken cancellationToken = default)
        {
            if (Down)
            {
                throw new InvalidOperationException("store down");
            }

            lock (Saved)
            {
                Saved.AddRange(upserts);
                Deleted.AddRange(deletes);
                Calls.AddRange(upserts.Select(r => $"save:{r.SpawnGuid}"));
                Calls.AddRange(deletes.Select(k => $"delete:{k.SpawnGuid}"));
            }

            return Task.CompletedTask;
        }

        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default)
        {
            lock (Saved)
            {
                InstancesDeleted.Add(instanceId);
                Calls.Add($"instance:{instanceId}");
            }

            return Task.CompletedTask;
        }
    }

    private static long FaultCount(string site) => FaultLedger.Shared.Snapshot().Where(s => s.Site == site).Sum(s => s.Count);

    private static ServiceProvider Build(FakeStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGameObjectRespawnStore>(store);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SaveAndDelete_ReachTheGameObjectStore_AndReadsAnswerFromMemory()
    {
        var store = new FakeStore();
        await using ServiceProvider sp = Build(store);
        var queue = new GameObjectRespawnQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<GameObjectRespawnQueue>.Instance);

        queue.Save(0, 0, 5, Now + 100);
        queue.Save(409, 7, 6, Now + 200);
        queue.Delete(409, 7, 6);
        queue.Start();

        Assert.True(queue.TryGetPending(0, 0, 5, out long at));
        Assert.Equal(Now + 100, at);
        Assert.False(queue.TryGetPending(409, 7, 6, out _));

        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await queue.StopAsync();
        Assert.Equal([new GameObjectRespawnRecord(0, 0, 5, Now + 100)], store.Saved);
        Assert.Equal([new GameObjectRespawnKey(7, 6)], store.Deleted);
    }

    [Fact]
    public async Task AFailingStore_RetainsTheWrite_AndStopThrowsNamingIt()
    {
        var store = new FakeStore { Down = true };
        await using ServiceProvider sp = Build(store);
        var queue = new GameObjectRespawnQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<GameObjectRespawnQueue>.Instance);
        queue.Start();

        long writeFaults = FaultCount("writequeue.gameobject-respawn");
        long attemptFaults = FaultCount("writequeue.gameobject-respawn-attempt");
        queue.Save(0, 0, 9, Now + 100);
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(60));

        // The fault slug is part of the ledger's contract (the game object queue must not record under the creature queue's site): the final failure and the two earlier attempts.
        Assert.True(FaultCount("writequeue.gameobject-respawn") > writeFaults, "the failed write was not recorded under writequeue.gameobject-respawn");
        Assert.True(FaultCount("writequeue.gameobject-respawn-attempt") >= attemptFaults + 2, "the two retried attempts were not recorded under writequeue.gameobject-respawn-attempt");
        Assert.Equal(1, queue.RetainedCount);
        Assert.Equal(["spawn:0/9"], queue.RetainedKeys);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(queue.StopAsync);
        Assert.Contains("game object respawn writes did not drain for spawn 9 (instance 0)", ex.Message);
    }

    [Fact]
    public async Task ForgetInstance_DropsItsTimesFromMemory_AndQueuesTheStoreDeleteBehindTheEarlierSaves()
    {
        var store = new FakeStore();
        await using ServiceProvider sp = Build(store);
        var queue = new GameObjectRespawnQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<GameObjectRespawnQueue>.Instance);
        queue.Start();

        queue.Save(409, 7, 6, Now + 200);
        queue.Save(0, 0, 5, Now + 100);
        queue.ForgetInstance(7);
        queue.ForgetInstance(0); // the shared copy of a map is never forgotten
        queue.Save(409, 8, 4, Now + 300); // another instance is untouched

        Assert.False(queue.TryGetPending(409, 7, 6, out _));
        Assert.True(queue.TryGetPending(0, 0, 5, out _));
        Assert.True(queue.TryGetPending(409, 8, 4, out _));

        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await queue.StopAsync();

        Assert.Equal([7u], store.InstancesDeleted); // instance 0 queued nothing
        lock (store.Saved)
        {
            Assert.True(store.Calls.IndexOf("save:6") < store.Calls.IndexOf("instance:7"), "the instance delete ran before the earlier save of that instance: " + string.Join(", ", store.Calls));
        }
    }

    [Fact]
    public async Task RetryRetainedAsync_FaultsNamingTheKeyWhileTheStoreIsDown_AndCompletesOnceItLands()
    {
        var store = new FakeStore { Down = true };
        await using ServiceProvider sp = Build(store);
        var queue = new GameObjectRespawnQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<GameObjectRespawnQueue>.Instance)
        {
            RetainedRetryInterval = TimeSpan.FromHours(1),
        };
        await queue.RetryRetainedAsync().WaitAsync(TimeSpan.FromSeconds(30)); // not started and nothing retained: nothing is owed
        queue.Start();

        queue.Save(0, 0, 9, Now + 100);
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(["spawn:0/9"], queue.RetainedKeys);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RetryRetainedAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("game object respawn writes are not durable: spawn:0/9", ex.Message);
        Assert.Equal(1, queue.RetainedCount); // still retained: a failed retry keeps it

        store.Down = false;
        await queue.RetryRetainedAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, queue.RetainedCount);
        Assert.Equal([new GameObjectRespawnRecord(0, 0, 9, Now + 100)], store.Saved);
        await queue.StopAsync(); // nothing retained: does not throw
    }

    [Fact]
    public async Task RetryRetainedAsync_OnAStoppedQueue_FaultsWhileAWriteIsRetained()
    {
        var store = new FakeStore { Down = true };
        await using ServiceProvider sp = Build(store);
        var queue = new GameObjectRespawnQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<GameObjectRespawnQueue>.Instance);
        queue.Start();
        queue.Save(0, 0, 9, Now + 100);
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(60));
        await Assert.ThrowsAsync<InvalidOperationException>(queue.StopAsync);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RetryRetainedAsync());
        Assert.Contains("spawn:0/9", ex.Message);
    }
}

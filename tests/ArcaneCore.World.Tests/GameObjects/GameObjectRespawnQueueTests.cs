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
            }

            return Task.CompletedTask;
        }

        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

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

        queue.Save(0, 0, 9, Now + 100);
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(1, queue.RetainedCount);
        Assert.Equal(["spawn:0/9"], queue.RetainedKeys);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(queue.StopAsync);
        Assert.Contains("game object respawn writes did not drain for spawn 9 (instance 0)", ex.Message);
    }
}

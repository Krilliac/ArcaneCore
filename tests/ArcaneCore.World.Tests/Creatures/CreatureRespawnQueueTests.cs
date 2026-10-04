using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Creatures;

/// <summary>
/// The world's respawn persistence: reads from memory, writes queued and applied in order off the world thread, retried, drained at shutdown
/// (<see cref="CreatureRespawnQueue"/>, <see cref="CreatureRespawnFeature"/>). Waits are on conditions (a flush), never on a fixed delay.
/// </summary>
public sealed class CreatureRespawnQueueTests
{
    private const long Now = 1_700_000_000;

    private sealed class FakeStore : ICreatureRespawnStore
    {
        private int _failuresLeft;

        public FakeStore(IReadOnlyList<CreatureRespawnRecord>? stored = null, int failFirst = 0)
        {
            Stored = stored ?? [];
            _failuresLeft = failFirst;
        }

        public IReadOnlyList<CreatureRespawnRecord> Stored { get; }

        public List<string> Log { get; } = [];

        public long LoadedAt { get; private set; }

        public Task<IReadOnlyList<CreatureRespawnRecord>> LoadAsync(long nowUnixSeconds, CancellationToken cancellationToken = default)
        {
            LoadedAt = nowUnixSeconds;
            return Task.FromResult(Stored);
        }

        /// <summary>While set, every write fails (a lasting outage); cleared, writes land again.</summary>
        public volatile bool Down;

        /// <summary>When set, a save waits for it before it lands (a write still in flight).</summary>
        public TaskCompletionSource? HoldSaves { get; set; }

        /// <summary>Signalled when a save started waiting on <see cref="HoldSaves"/>.</summary>
        public TaskCompletionSource SaveHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The "table": (instance, spawn guid) to respawn time.</summary>
        public Dictionary<(uint Instance, uint Guid), long> Rows { get; } = [];

        public async Task SaveAsync(IReadOnlyCollection<CreatureRespawnRecord> upserts, IReadOnlyCollection<CreatureRespawnKey> deletes, CancellationToken cancellationToken = default)
        {
            if (HoldSaves is { } hold)
            {
                SaveHeld.TrySetResult();
                await hold.Task.ConfigureAwait(false);
            }

            if (Down || Interlocked.Decrement(ref _failuresLeft) >= 0)
            {
                throw new InvalidOperationException("simulated outage");
            }

            lock (Log)
            {
                Log.AddRange(upserts.Select(u => $"save:{u.MapId}:{u.InstanceId}:{u.SpawnGuid}:{u.RespawnTime}"));
                Log.AddRange(deletes.Select(d => $"delete:{d.InstanceId}:{d.SpawnGuid}"));
                foreach (CreatureRespawnRecord u in upserts)
                {
                    Rows[(u.InstanceId, u.SpawnGuid)] = u.RespawnTime;
                }

                foreach (CreatureRespawnKey d in deletes)
                {
                    Rows.Remove((d.InstanceId, d.SpawnGuid));
                }
            }
        }

        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default)
        {
            if (Down)
            {
                throw new InvalidOperationException("simulated outage");
            }

            lock (Log)
            {
                Log.Add($"deleteinstance:{instanceId}");
                foreach ((uint Instance, uint Guid) key in Rows.Keys.Where(k => k.Instance == instanceId).ToArray())
                {
                    Rows.Remove(key);
                }
            }

            return Task.CompletedTask;
        }
    }

    private static ServiceProvider Build(FakeStore? store, Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (store is not null)
        {
            services.AddSingleton<ICreatureRespawnStore>(store); // one shared fake: the queue opens a scope per write
        }

        more?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static CreatureRespawnQueue NewQueue(ServiceProvider sp)
        => new(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CreatureRespawnQueue>.Instance);

    [Fact]
    public async Task WritesReachTheStoreInOrder_AndReadsAnswerFromMemory()
    {
        var store = new FakeStore();
        await using ServiceProvider sp = Build(store);
        CreatureRespawnQueue queue = NewQueue(sp);
        queue.Start();

        queue.Save(0, 0, 1, Now + 100);
        queue.Save(409, 7, 2, Now + 200);
        queue.Save(0, 0, 1, Now + 300); // replaces the first
        queue.Delete(409, 7, 2);

        // Memory answers at once, before any write has landed.
        Assert.Equal(new Dictionary<uint, long> { [1] = Now + 300 }, queue.GetPending(0, 0));
        Assert.Empty(queue.GetPending(409, 7));
        Assert.Empty(queue.GetPending(1, 1));

        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["save:0:0:1:" + (Now + 100), "save:409:7:2:" + (Now + 200), "save:0:0:1:" + (Now + 300), "delete:7:2"], store.Log);
        await queue.StopAsync();
    }

    [Fact]
    public async Task TheInitialRowsAreServedAndGetPendingReturnsACopy()
    {
        await using ServiceProvider sp = Build(new FakeStore());
        CreatureRespawnQueue queue = NewQueue(sp);
        queue.LoadInitial([new CreatureRespawnRecord(0, 0, 9, Now + 50), new CreatureRespawnRecord(409, 3, 9, Now + 60)]);

        IReadOnlyDictionary<uint, long> world = queue.GetPending(0, 0);
        Assert.Equal(Now + 50, world[9]);
        Assert.Equal(Now + 60, queue.GetPending(409, 3)[9]); // the same guid in another instance
        queue.Save(0, 0, 10, Now + 70);
        Assert.DoesNotContain(10u, world.Keys); // a snapshot, not a live view
        await queue.StopAsync();
    }

    [Fact]
    public async Task AFailingWriteIsRetried_AndTheLaterWritesStillLand()
    {
        var store = new FakeStore(failFirst: 2);
        await using ServiceProvider sp = Build(store);
        CreatureRespawnQueue queue = NewQueue(sp);
        queue.Start();

        queue.Save(0, 0, 1, Now + 100);
        queue.Save(0, 0, 2, Now + 100);

        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30)); // two retries: 200 ms + 400 ms of back-off
        Assert.Equal(["save:0:0:1:" + (Now + 100), "save:0:0:2:" + (Now + 100)], store.Log);
        await queue.StopAsync();
    }

    [Fact]
    public async Task StopDrainsWhatIsQueued_AndALateWriteDoesNotThrow()
    {
        var store = new FakeStore();
        await using ServiceProvider sp = Build(store);
        CreatureRespawnQueue queue = NewQueue(sp);
        queue.Start();
        for (uint guid = 1; guid <= 50; guid++)
        {
            queue.Save(0, 0, guid, Now + 100);
        }

        await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(50, store.Log.Count);
        queue.Save(0, 0, 99, Now + 1); // the channel is closed: logged, the process keeps running
        Assert.Equal(0, queue.Pending);
    }

    [Fact]
    public async Task WithoutAStore_WritesAreDiscarded()
    {
        await using ServiceProvider sp = Build(store: null);
        CreatureRespawnQueue queue = NewQueue(sp);
        queue.Start();

        queue.Save(0, 0, 1, Now + 100);
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(Now + 100, queue.GetPending(0, 0)[1]); // memory still answers
        await queue.StopAsync();
    }

    [Fact]
    public async Task AWriteThatFailsAllAttempts_IsRetained_NotDropped_AndStopRetriesItAndReportsIt()
    {
        var store = new FakeStore { Down = true };
        await using ServiceProvider sp = Build(store);
        CreatureRespawnQueue queue = NewQueue(sp);
        queue.Start();

        queue.Save(0, 0, 1, Now + 100);
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30)); // three attempts, then retained
        Assert.Equal(1, queue.RetainedCount);
        Assert.Empty(store.Log);

        // Still down at shutdown: the final retry fails too and StopAsync names the spawn.
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("spawn 1 (instance 0)", ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.Equal(1, queue.RetainedCount);
    }

    [Fact]
    public async Task ARetainedWrite_LandsAtStop_WhenTheStoreIsBack_AndANewerWriteOfTheSameSpawnSupersedesIt()
    {
        var store = new FakeStore { Down = true };
        await using ServiceProvider sp = Build(store);
        CreatureRespawnQueue queue = NewQueue(sp);
        queue.Start();

        queue.Save(0, 0, 1, Now + 100);
        queue.Save(0, 0, 2, Now + 100);
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, queue.RetainedCount);

        store.Down = false;
        queue.Save(0, 0, 2, Now + 500); // newer than the retained one: it lands and clears it
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, queue.RetainedCount);

        await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(30)); // retries spawn 1, throws nothing
        Assert.Equal(0, queue.RetainedCount);
        Assert.Equal(new Dictionary<(uint, uint), long> { [(0, 1)] = Now + 100, [(0, 2)] = Now + 500 }, store.Rows);
    }

    [Fact]
    public async Task ForgetInstance_RemovesTheMemoryCopy_AndTheRowsEvenWhenASaveOfItWasStillInFlight()
    {
        // The instance store deletes creature_respawn rows on its own consumer. A save still waiting in this queue would put a row back after
        // that delete, so the forget is queued here, behind the save, and the order of the two consumers no longer matters.
        var store = new FakeStore { HoldSaves = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using ServiceProvider sp = Build(store);
        CreatureRespawnQueue queue = NewQueue(sp);
        queue.Start();

        queue.Save(409, 7, 1, Now + 100);
        queue.Save(0, 0, 2, Now + 100); // the shared copy of a map is not an instance's
        await store.SaveHeld.Task.WaitAsync(TimeSpan.FromSeconds(30)); // the first save is now in flight

        store.Rows.Remove((7, 1)); // the instance store's DeleteInstanceAsync ran first: nothing to remove yet
        queue.ForgetInstance(7);
        Assert.Empty(queue.GetPending(409, 7));
        Assert.Equal(Now + 100, queue.GetPending(0, 0)[2]);

        store.HoldSaves.SetResult(); // the in-flight save lands and re-creates the row ...
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));

        // ... and the forget, queued behind it, removes it again.
        Assert.Equal(["save:409:7:1:" + (Now + 100), "save:0:0:2:" + (Now + 100), "deleteinstance:7"], store.Log);
        Assert.Equal(new Dictionary<(uint, uint), long> { [(0, 2)] = Now + 100 }, store.Rows);
        await queue.StopAsync();
    }

    [Fact]
    public async Task ForgetInstance_DropsRetainedWritesOfThatInstance_AndDoesNotRetainAnInFlightFailure()
    {
        var store = new FakeStore { Down = true };
        await using ServiceProvider sp = Build(store);
        CreatureRespawnQueue queue = NewQueue(sp);
        queue.Start();

        queue.Save(409, 7, 1, Now + 100);
        queue.Save(409, 8, 1, Now + 100);
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, queue.RetainedCount);

        queue.ForgetInstance(7); // the retained save of instance 7 is dropped; the delete itself fails and is retained
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, queue.RetainedCount); // save of instance 8, delete of instance 7

        store.Down = false;
        await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["deleteinstance:7", "save:409:8:1:" + (Now + 100)], store.Log);
        Assert.Equal(new Dictionary<(uint, uint), long> { [(8, 1)] = Now + 100 }, store.Rows);
    }

    [Fact]
    public async Task ForgetInstance_IgnoresInstanceZero()
    {
        var store = new FakeStore();
        await using ServiceProvider sp = Build(store);
        CreatureRespawnQueue queue = NewQueue(sp);
        queue.Start();
        queue.Save(0, 0, 1, Now + 100);

        queue.ForgetInstance(0);

        Assert.Equal(Now + 100, queue.GetPending(0, 0)[1]);
        await queue.StopAsync();
        Assert.Equal(["save:0:0:1:" + (Now + 100)], store.Log);
    }

    // --- the feature --------------------------------------------------------------------------

    private static WorldRuntime NewWorld(ServiceProvider sp)
    {
        var saves = new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        return new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
    }

    [Fact]
    public void IsDiscoveredAsAWorldFeature()
        => Assert.Contains(typeof(CreatureRespawnFeature), WorldFeatures.FeatureTypes);

    private sealed class FixedClock(long now) : IRespawnClock
    {
        public long UnixSeconds => now;
    }

    [Fact]
    public async Task Attach_LoadsTheStoredTimes_AtTheClockTime_AndServesThem()
    {
        var store = new FakeStore([new CreatureRespawnRecord(0, 0, 5, Now + 500)]);
        await using ServiceProvider sp = Build(store, s => s.AddSingleton<IRespawnClock>(new FixedClock(Now)));
        using WorldRuntime world = NewWorld(sp);
        var feature = new CreatureRespawnFeature(sp, sp.GetRequiredService<ILogger<CreatureRespawnFeature>>());

        feature.Attach(world);

        Assert.Equal(Now, store.LoadedAt);
        Assert.NotNull(feature.Persistence);
        Assert.Equal(Now + 500, feature.Persistence!.GetPending(0, 0)[5]);
        feature.Persistence.Delete(0, 0, 5);
        await feature.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["delete:0:5"], store.Log);
        await feature.StopAsync();
    }

    [Fact]
    public async Task Attach_IsInactive_WithoutAStore_OrWithPersistOff()
    {
        await using ServiceProvider none = Build(store: null);
        using WorldRuntime world1 = NewWorld(none);
        var withoutStore = new CreatureRespawnFeature(none, none.GetRequiredService<ILogger<CreatureRespawnFeature>>());
        withoutStore.Attach(world1);
        Assert.Null(withoutStore.Persistence);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Creatures:Respawn:Persist"] = "false" }).Build();
        var store = new FakeStore([new CreatureRespawnRecord(0, 0, 5, Now + 500)]);
        await using ServiceProvider off = Build(store, s => s.AddSingleton<IConfiguration>(config));
        using WorldRuntime world2 = NewWorld(off);
        var disabled = new CreatureRespawnFeature(off, off.GetRequiredService<ILogger<CreatureRespawnFeature>>());
        disabled.Attach(world2);
        Assert.Null(disabled.Persistence);
        Assert.Equal(0, store.LoadedAt); // the store was not even read
    }
}

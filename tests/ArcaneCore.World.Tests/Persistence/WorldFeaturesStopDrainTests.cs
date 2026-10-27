using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Progression;
using ArcaneCore.World.WorldState;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Persistence;

/// <summary>The feature write queues share one stop-drain lane: once one of them has stalled it, the others make no attempt, so the stall does not add up across queues.</summary>
public sealed class WorldFeaturesStopDrainTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private sealed class SlowLockedStore : ICharacterRestStore, IExploredZonesStore
    {
        private int _attempts;
        private int _inFlight;

        public volatile bool Fail;
        public volatile bool Locked;

        /// <summary>The rest queue's saves wait this long, then land (a healthy but slow write).</summary>
        public volatile int SlowSuccessMs;

        public int Attempts => Volatile.Read(ref _attempts);

        /// <summary>Saves inside the store right now (a write that has not reached its verdict).</summary>
        public int InFlight => Volatile.Read(ref _inFlight);

        public Task<CharacterRestState?> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<CharacterRestState?>(null);

        public Task SaveAsync(int characterId, CharacterRestState state, CancellationToken cancellationToken = default) => Step(SlowSuccessMs);

        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default) => Step();

        Task<uint[]?> IExploredZonesStore.LoadAsync(int characterId, CancellationToken cancellationToken) => Task.FromResult<uint[]?>(null);

        public Task SaveAsync(int characterId, IReadOnlyList<uint> words, CancellationToken cancellationToken = default) => Step();

        private async Task Step(int slowSuccessMs = 0)
        {
            Interlocked.Increment(ref _attempts);
            Interlocked.Increment(ref _inFlight);
            try
            {
                if (Locked)
                {
                    await Task.Delay(400).ConfigureAwait(false);
                    throw new SqliteException("SQLite Error 5: 'database is locked'.", 5);
                }

                if (Fail)
                {
                    throw new IOException("store down");
                }

                if (slowSuccessMs > 0)
                {
                    await Task.Delay(slowSuccessMs).ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    [Fact]
    public async Task StopWorldFeatures_SharesOneLaneAcrossFeatureQueues()
    {
        var drain = new ShutdownDrain { Features = new StopDrainBudget { StallBudget = TimeSpan.FromMilliseconds(300) } };
        var store = new SlowLockedStore();
        await using ServiceProvider services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(drain)
            .AddSingleton<ICharacterRestStore>(store)
            .AddSingleton<IExploredZonesStore>(store)
            .AddSingleton<RestFeature>()
            .AddSingleton<IWorldFeature>(sp => sp.GetRequiredService<RestFeature>())
            .AddSingleton<ExploredZonesPersistence>()
            .AddSingleton<IWorldFeature>(sp => sp.GetRequiredService<ExploredZonesPersistence>())
            .BuildServiceProvider();
        RestFeature rest = services.GetRequiredService<RestFeature>();
        ExploredZonesPersistence explored = services.GetRequiredService<ExploredZonesPersistence>();
        rest.Writes.Start(); // the features start their queues when they attach to a world; these are driven without one
        explored.Writes.Start();
        store.Fail = true;
        rest.Writes.Save(1, new CharacterRestState(50f, 1000, true));
        explored.Writes.Save(1, new uint[ExploredZonesText.WordCount]);
        await rest.Writes.FlushAsync().WaitAsync(Wait);
        await explored.Writes.FlushAsync().WaitAsync(Wait);
        Assert.True(rest.Writes.HasRetainedFailure(1));
        Assert.True(explored.Writes.HasRetainedFailure(1));
        int before = store.Attempts;
        store.Fail = false;
        store.Locked = true;

        await Assert.ThrowsAsync<AggregateException>(services.StopWorldFeaturesAsync);

        Assert.True(drain.Features.Refused > 0, drain.Features.Describe());
        Assert.Equal(1, store.Attempts - before); // the first queue's one attempt stalled the shared lane; the second queue made none
        Assert.False(drain.Saves.Started); // the save queue's lane is begun by the host, not by the feature queues
    }

    [Fact]
    public async Task StopWorldFeatures_ASlowSuccessInANotYetStoppedQueue_DoesNotRefuseTheStoppingQueue()
    {
        var drain = new ShutdownDrain { Features = new StopDrainBudget { StallBudget = TimeSpan.FromMilliseconds(300) } };
        var store = new SlowLockedStore();
        await using ServiceProvider services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(drain)
            .AddSingleton<ICharacterRestStore>(store)
            .AddSingleton<IExploredZonesStore>(store)
            .AddSingleton<RestFeature>()
            .AddSingleton<IWorldFeature>(sp => sp.GetRequiredService<RestFeature>())
            .AddSingleton<ExploredZonesPersistence>()
            .AddSingleton<IWorldFeature>(sp => sp.GetRequiredService<ExploredZonesPersistence>())
            .BuildServiceProvider();
        RestFeature rest = services.GetRequiredService<RestFeature>();
        ExploredZonesPersistence explored = services.GetRequiredService<ExploredZonesPersistence>();
        rest.Writes.Start();
        explored.Writes.Start();
        store.Fail = true;
        for (int id = 1; id <= 5; id++)
        {
            explored.Writes.Save(id, new uint[ExploredZonesText.WordCount]);
        }

        await explored.Writes.FlushAsync().WaitAsync(Wait); // five retained writes, their failed attempts before the lane began
        int before = store.Attempts;
        store.Fail = false;

        store.SlowSuccessMs = 1500;
        rest.Writes.Save(1, new CharacterRestState(50f, 1000, true)); // the rest queue is stopped after explored: its healthy write is merely slow
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (store.InFlight != 1)
        {
            Assert.True(DateTime.UtcNow < deadline, "the slow write never started");
            await Task.Delay(5);
        }

        drain.Features.Begin(); // the host begins the lane before the features stop
        await Task.Delay(450); // past the 300 ms budget, with the slow write still running and going to succeed

        await services.StopWorldFeaturesAsync().WaitAsync(Wait); // explored stops first and must not be refused

        Assert.Equal(6, store.Attempts - before); // the five explored writes and the slow rest write
        Assert.Equal(0, drain.Features.Refused);
        Assert.Equal(6, drain.Features.Succeeded);
        Assert.False(explored.Writes.HasRetainedFailure(1));
    }
}
